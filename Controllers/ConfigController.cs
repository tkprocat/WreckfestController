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
            return this.Refused($"Failed to read basic config: {ex.Message}");
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
            return this.Refused($"Failed to read basic config: {ex.Message}");
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

            _configService.WriteBasicConfig(config);
            saved = _configService.ReadBasicConfig();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update basic config");
            return this.Refused($"Failed to update basic config: {ex.Message}");
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
            return this.Refused($"Failed to read track collection name: {ex.Message}");
        }
    }

    /// <summary>The event loop: the tracks the server rotates through.</summary>
    [HttpGet("tracks")]
    public ActionResult<EventLoopResponse> GetEventLoopTracks()
    {
        try
        {
            var tracks = _configService.ReadEventLoopTracks();
            return new EventLoopResponse(tracks.Count, tracks);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read event loop tracks");
            return this.Refused($"Failed to read event loop tracks: {ex.Message}");
        }
    }

    /// <summary>
    /// Replaces the event loop. Returns it as read back from the file. A file without a
    /// <c># Event Loop</c> heading has nowhere to put it: that is a 409.
    /// </summary>
    [HttpPut("tracks")]
    public ActionResult<EventLoopResponse> UpdateEventLoopTracks(UpdateEventLoopTracksRequest request)
    {
        if (EventLoopTrackRules.Validate(request.CollectionName, request.Tracks) is { } error)
        {
            return this.Invalid(error.Field, error.Message);
        }

        List<EventLoopTrack> saved;
        try
        {
            _configService.WriteEventLoopTracks(request.CollectionName, request.Tracks);
            saved = _configService.ReadEventLoopTracks();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update event loop tracks");
            return this.Refused($"Failed to update event loop tracks: {ex.Message}");
        }

        _logger.LogInformation("{Caller} replaced the event loop ({Count} tracks)", this.Caller(), request.Tracks.Count);
        return new EventLoopResponse(saved.Count, saved);
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
            return this.Refused($"Failed to retrieve server info: {ex.Message}");
        }
    }
}

public sealed record CollectionNameResponse(string CollectionName);

public sealed record EventLoopResponse(int Count, IReadOnlyList<EventLoopTrack> Tracks);
