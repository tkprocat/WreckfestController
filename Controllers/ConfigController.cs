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
    public IActionResult UpdateEventLoopTracks([FromBody] List<EventLoopTrack> tracks)
    {
        try
        {
            _logger.LogInformation("Received request to update event loop tracks");
            _configService.WriteEventLoopTracks(tracks);
            return Ok(new { message = "Event loop tracks updated successfully", count = tracks.Count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update event loop tracks");
            return BadRequest(new { message = $"Failed to update event loop tracks: {ex.Message}" });
        }
    }

    /// <summary>
    /// Add a new track to the event loop
    /// </summary>
    [HttpPost("tracks")]
    public IActionResult AddEventLoopTrack([FromBody] EventLoopTrack track)
    {
        try
        {
            _logger.LogInformation("Received request to add event loop track");
            var tracks = _configService.ReadEventLoopTracks();
            tracks.Add(track);
            _configService.WriteEventLoopTracks(tracks);
            return Ok(new { message = "Track added successfully", index = tracks.Count - 1 });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add event loop track");
            return BadRequest(new { message = $"Failed to add event loop track: {ex.Message}" });
        }
    }

    /// <summary>
    /// Update a specific track in the event loop by index
    /// </summary>
    [HttpPut("tracks/{index}")]
    public IActionResult UpdateEventLoopTrack(int index, [FromBody] EventLoopTrack track)
    {
        try
        {
            _logger.LogInformation("Received request to update event loop track at index {Index}", index);
            var tracks = _configService.ReadEventLoopTracks();

            if (index < 0 || index >= tracks.Count)
            {
                return BadRequest(new { message = $"Invalid index {index}. Valid range: 0-{tracks.Count - 1}" });
            }

            tracks[index] = track;
            _configService.WriteEventLoopTracks(tracks);
            return Ok(new { message = $"Track at index {index} updated successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update event loop track at index {Index}", index);
            return BadRequest(new { message = $"Failed to update event loop track: {ex.Message}" });
        }
    }

    /// <summary>
    /// Delete a specific track from the event loop by index
    /// </summary>
    [HttpDelete("tracks/{index}")]
    public IActionResult DeleteEventLoopTrack(int index)
    {
        try
        {
            _logger.LogInformation("Received request to delete event loop track at index {Index}", index);
            var tracks = _configService.ReadEventLoopTracks();

            if (index < 0 || index >= tracks.Count)
            {
                return BadRequest(new { message = $"Invalid index {index}. Valid range: 0-{tracks.Count - 1}" });
            }

            tracks.RemoveAt(index);
            _configService.WriteEventLoopTracks(tracks);
            return Ok(new { message = $"Track at index {index} deleted successfully", remainingCount = tracks.Count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete event loop track at index {Index}", index);
            return BadRequest(new { message = $"Failed to delete event loop track: {ex.Message}" });
        }
    }
}
