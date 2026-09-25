using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using WreckfestController.Models;
using WreckfestController.Services;

namespace WreckfestController.Controllers;

[ApiController]
[Route("api/[controller]")]
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
    /// Get all basic server configuration settings
    /// </summary>
    [HttpGet("basic")]
    public IActionResult GetBasicConfig()
    {
        try
        {
            _logger.LogInformation("Received request to get basic config");
            var config = _configService.ReadBasicConfig();
            return Ok(config);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read basic config");
            return BadRequest(new { message = $"Failed to read basic config: {ex.Message}" });
        }
    }

    /// <summary>
    /// Update basic server configuration settings. Only the fields present in the body
    /// change; everything else keeps its current value.
    /// </summary>
    [HttpPut("basic")]
    public IActionResult UpdateBasicConfig([FromBody] JsonElement patch)
    {
        try
        {
            _logger.LogInformation("Received request to update basic config");
            var config = _configService.ReadBasicConfig();
            if (!ServerConfigPatch.TryApply(config, patch, out var error))
            {
                return BadRequest(new { message = error });
            }

            _configService.WriteBasicConfig(config);
            return Ok(new { message = "Basic config updated successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update basic config");
            return BadRequest(new { message = $"Failed to update basic config: {ex.Message}" });
        }
    }

    /// <summary>
    /// Get the name of the track collection for event loops
    /// </summary>
    [HttpGet("tracks/collection-name")]
    public IActionResult GetTrackCollectionName()
    {
        try
        {
            _logger.LogInformation("Received request to get track collection name");
            var collectionName = _configService.GetCurrentCollectionName();
            return Ok(new { collectionName });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read track collection name");
            return BadRequest(new { message = $"Failed to read track collection name: {ex.Message}" });
        }
    }


    /// <summary>
    /// Get all event loop tracks
    /// </summary>
    [HttpGet("tracks")]
    public IActionResult GetEventLoopTracks()
    {
        try
        {
            _logger.LogInformation("Received request to get event loop tracks");
            var tracks = _configService.ReadEventLoopTracks();
            return Ok(new { count = tracks.Count, tracks });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read event loop tracks");
            return BadRequest(new { message = $"Failed to read event loop tracks: {ex.Message}" });
        }
    }

    /// <summary>
    /// Set all event loop tracks (replaces existing tracks)
    /// </summary>
    [HttpPut("tracks")]
    public IActionResult UpdateEventLoopTracks([FromBody] UpdateEventLoopTracksRequest request)
    {
        try
        {
            _logger.LogInformation("Received request to update event loop tracks");
            if (ValidateEventLoopTracks(request) is { } error)
            {
                return BadRequest(new { message = error });
            }

            _configService.WriteEventLoopTracks(request.CollectionName, request.Tracks);
            return Ok(new { message = "Event loop tracks updated successfully", count = request.Tracks.Count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update event loop tracks");
            return BadRequest(new { message = $"Failed to update event loop tracks: {ex.Message}" });
        }
    }

    /// <summary>
    /// Rejects a request that would write an event loop the server cannot load. Every
    /// value becomes a line of server_config.cfg, so line breaks are refused too.
    /// </summary>
    private static string? ValidateEventLoopTracks(UpdateEventLoopTracksRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.CollectionName))
        {
            return "collectionName is required.";
        }

        if (HasLineBreak(request.CollectionName))
        {
            return "collectionName must not contain line breaks.";
        }

        if (request.Tracks is null)
        {
            return "tracks is required.";
        }

        for (var i = 0; i < request.Tracks.Count; i++)
        {
            var track = request.Tracks[i];
            if (track is null || string.IsNullOrWhiteSpace(track.Track))
            {
                return $"tracks[{i}].track is required.";
            }

            string?[] values = [track.Track, track.Gamemode, track.CarClassRestriction, track.CarRestriction, track.Weather];
            if (values.Any(HasLineBreak))
            {
                return $"tracks[{i}] must not contain line breaks.";
            }
        }

        return null;
    }

    private static bool HasLineBreak(string? value) =>
        value is not null && value.AsSpan().IndexOfAny('\r', '\n') >= 0;

    /// <summary>
    /// Get live server info by sending ? command to the running server
    /// </summary>
    [HttpGet("serverinfo")]
    public async Task<IActionResult> GetServerInfo()
    {
        try
        {
            _logger.LogInformation("Received request to get live server info");
            var result = await _serverManager.GetServerInfoAsync();

            if (!result.Success)
            {
                return BadRequest(new { message = result.Message });
            }

            return Ok(result.Config);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve server info");
            return BadRequest(new { message = $"Failed to retrieve server info: {ex.Message}" });
        }
    }
}
