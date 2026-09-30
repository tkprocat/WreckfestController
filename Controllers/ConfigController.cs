using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using WreckfestController.Models;
using WreckfestController.Services.Auth;
using WreckfestController.Services.Config;
using WreckfestController.Services.ServerControl;

namespace WreckfestController.Controllers;

/// <summary>
/// The server's own config file, server_config.cfg: its settings and its event loop. A
/// file that cannot be read or written answers 409 with the reason; a bad request answers
/// 400 with the field at fault.
/// </summary>
[ApiController]
[Authorize(Policy = ApiAuthentication.AdminPolicy)]
[Route("api/config")]
// Declaring any response type stops ASP.NET Core inferring the 200 from ActionResult<T>,
// so it is declared too; with no type given, each action's own return type is used.
[ProducesResponseType(StatusCodes.Status200OK)]
[ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
public class ConfigController : ControllerBase
{
    private readonly ConfigService _configService;
    private readonly ServerManager _serverManager;
    private readonly ILogger<ConfigController> _logger;

    public ConfigController(ConfigService configService, ServerManager serverManager, ILogger<ConfigController> logger)
    {
        _configService = configService;
        _serverManager = serverManager;
        _logger = logger;
    }

    /// <summary>
    /// Which settings a PUT basic can change: each field, its server_config.cfg key, and
    /// whether the file has an active line for it. A missing or commented-out key (say
    /// admin_steam_ids, commented out by default), or one set again below the event loop,
    /// cannot be saved; the reason says what to fix in the file.
    /// </summary>
    [HttpGet("basic/fields")]
    public ActionResult<IReadOnlyList<ConfigFieldResponse>> GetBasicConfigFields()
    {
        try
        {
            var fields = ServerConfigPatch.Fields.Order().ToList();
            var problems = _configService.BasicKeyProblems(fields.Select(ServerConfigPatch.KeyOf));
            var active = _configService.ActiveBasicKeys(fields.Select(ServerConfigPatch.KeyOf));
            return fields
                .Select(field => ServerConfigPatch.KeyOf(field))
                .Zip(fields, (key, field) => new ConfigFieldResponse(
                    JsonNamingPolicy.CamelCase.ConvertName(field),
                    key,
                    !problems.ContainsKey(key),
                    problems.GetValueOrDefault(key),
                    active.Contains(key)))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read the basic config's keys");
            return this.Refused("server_config.cfg could not be read. The desktop app's log has the details.");
        }
    }

    /// <summary>The server settings in server_config.cfg.</summary>
    [HttpGet("basic")]
    public ActionResult<ServerConfig> GetBasicConfig()
    {
        try
        {
            return _configService.ReadBasicConfig();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read basic config");
            return this.Refused("server_config.cfg could not be read. The desktop app's log has the details.");
        }
    }

    /// <summary>
    /// Changes server settings. Only the fields present in the body change; everything
    /// else keeps its current value. Returns the settings as read back from the file.
    /// A field whose key has no active line in server_config.cfg (missing, or commented
    /// out), or is set again below the event loop, cannot be saved: that is a 409 naming
    /// the key, and nothing is written.
    /// </summary>
    [HttpPut("basic")]
    public ActionResult<ServerConfig> UpdateBasicConfig([FromBody] JsonElement patch)
    {
        ServerConfig config;
        try
        {
            config = _configService.ReadBasicConfig();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read basic config");
            return this.Refused("server_config.cfg could not be read. The desktop app's log has the details.");
        }

        if (!ServerConfigPatch.TryApply(config, patch, out var error, out var applied))
        {
            return this.Invalid(error!.Field, error.Message);
        }

        ServerConfig saved;
        try
        {
            var unsavable = _configService.BasicKeysThatCannotBeSaved(applied.Select(ServerConfigPatch.KeyOf));
            if (unsavable.Count > 0)
            {
                return this.Refused(
                    $"server_config.cfg cannot take this change: {string.Join("; ", unsavable)}. " +
                    "Fix server_config.cfg, then try again. Nothing was changed.");
            }

            // Only what the patch changed, each by its key: a key the file lacks is added, one
            // commented out is uncommented. Every other line stays as it is.
            _configService.WriteSettings(applied
                .Select(ServerConfigPatch.KeyOf)
                .ToDictionary(key => key, key => ConfigService.ValueOf(config, key) ?? string.Empty));
            saved = _configService.ReadBasicConfig();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update basic config");
            return WriteRefused(ex);
        }

        _logger.LogInformation("{Caller} updated the basic config", this.Caller());
        return saved;
    }

    /// <summary>The event loop's <c>#CollectionName</c>.</summary>
    [HttpGet("tracks/collection-name")]
    public ActionResult<CollectionNameResponse> GetTrackCollectionName()
    {
        try
        {
            return new CollectionNameResponse(_configService.GetCurrentCollectionName());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read track collection name");
            return this.Refused("server_config.cfg could not be read. The desktop app's log has the details.");
        }
    }

    /// <summary>
    /// The event loop: the tracks the server rotates through, its <c>#CollectionName</c>,
    /// and its version (also the ETag), for a PUT that must not overwrite a change made since.
    /// </summary>
    [HttpGet("tracks")]
    public ActionResult<EventLoopResponse> GetEventLoopTracks()
    {
        try
        {
            return WithETag(ReadEventLoop());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read event loop tracks");
            return this.Refused("server_config.cfg could not be read. The desktop app's log has the details.");
        }
    }

    /// <summary>
    /// Replaces the event loop. Returns it as read back from the file. A file without a
    /// <c># Event Loop</c> heading has nowhere to put it: that is a 409. If-Match is
    /// optional: with the version a GET gave, a rotation changed since (by another admin, a
    /// cup starting, a deploy or an edit of the file) answers 409 with the rotation as it is.
    /// </summary>
    [HttpPut("tracks")]
    public ActionResult<EventLoopResponse> UpdateEventLoopTracks(UpdateEventLoopTracksRequest request)
    {
        if (EventLoopTrackRules.Validate(request.CollectionName, request.Tracks) is { } error)
        {
            return this.Invalid(error.Field, error.Message);
        }

        var expected = Request.Headers.IfMatch.ToString().Trim().Trim('"');
        EventLoopResponse saved;
        try
        {
            if (expected.Length == 0)
            {
                _configService.WriteEventLoopTracks(request.CollectionName, request.Tracks);
            }
            else if (!_configService.TryWriteEventLoopTracks(request.CollectionName, request.Tracks, expected, out var current))
            {
                var now = ToResponse(current.Name, current.Tracks);
                Response.Headers.ETag = $"\"{now.Version}\"";
                return Conflict(now);
            }

            saved = ReadEventLoop();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update event loop tracks");
            return WriteRefused(ex);
        }

        _logger.LogInformation("{Caller} replaced the event loop ({Count} tracks)", this.Caller(), request.Tracks.Count);
        return WithETag(saved);
    }

    private EventLoopResponse ReadEventLoop() =>
        ToResponse(_configService.GetCurrentCollectionName(), _configService.ReadEventLoopTracks());

    private static EventLoopResponse ToResponse(string name, List<EventLoopTrack> tracks) =>
        new(tracks.Count, tracks, name, ConfigService.EventLoopVersion(name, tracks));

    private EventLoopResponse WithETag(EventLoopResponse loop)
    {
        Response.Headers.ETag = $"\"{loop.Version}\"";
        return loop;
    }

    /// <summary>Live server info, asked of the running server with its <c>?</c> command.</summary>
    [HttpGet("serverinfo")]
    public async Task<ActionResult<ServerConfig>> GetServerInfo()
    {
        try
        {
            var result = await _serverManager.GetServerInfoAsync();
            return result.Success && result.Config is { } config ? config : this.Refused(result.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve server info");
            return this.Refused("The server info could not be read. The desktop app's log has the details.");
        }
    }

    /// <summary>A failed write: why, when the reason is known, else a fixed message. Never the exception's text.</summary>
    private ActionResult WriteRefused(Exception ex) =>
        ConfigWriteFailure.From(ex) is { } failure
            ? this.Refused(failure.Message, ("reason", failure.Reason))
            : this.Refused("server_config.cfg could not be written. The desktop app's log has the details.");
}

public sealed record CollectionNameResponse(string CollectionName);

/// <summary>
/// The event loop as server_config.cfg holds it. <see cref="Version"/> is
/// <see cref="ConfigService.EventLoopVersion"/>: any edit, from anywhere, changes it.
/// </summary>
public sealed record EventLoopResponse(int Count, IReadOnlyList<EventLoopTrack> Tracks, string CollectionName, string Version);

/// <summary>
/// A server setting as PUT basic sees it: the field, its server_config.cfg key, and whether
/// a change to it can be saved - with why not, when it cannot.
/// </summary>
/// <param name="Active">
/// The file sets it (an active line above the event loop). When false, the server runs on
/// its default, and saving a change adds or uncomments the line.
/// </param>
public sealed record ConfigFieldResponse(string Field, string Key, bool Savable, string? Reason, bool Active);
