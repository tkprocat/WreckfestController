using Microsoft.AspNetCore.Mvc;
using WreckfestController.Models;
using WreckfestController.Services;

namespace WreckfestController.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ConfigController : ControllerBase
{
    private readonly ConfigService _configService;
    private readonly ILogger<ConfigController> _logger;

    public ConfigController(ConfigService configService, ILogger<ConfigController> logger)
    {
        _configService = configService;
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
    /// Update basic server configuration settings
    /// </summary>
    [HttpPut("basic")]
    public IActionResult UpdateBasicConfig([FromBody] ServerConfig config)
    {
        try
        {
            _logger.LogInformation("Received request to update basic config");
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
            _configService.WriteEventLoopTracks(request.CollectionName, request.Tracks);
            return Ok(new { message = "Event loop tracks updated successfully", count = request.Tracks.Count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update event loop tracks");
            return BadRequest(new { message = $"Failed to update event loop tracks: {ex.Message}" });
        }
    }
}
