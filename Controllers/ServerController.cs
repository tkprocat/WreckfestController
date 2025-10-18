using Microsoft.AspNetCore.Mvc;
using WreckfestController.Services;

namespace WreckfestController.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ServerController : ControllerBase
{
    private readonly ServerManager _serverManager;
    private readonly ILogger<ServerController> _logger;

    public ServerController(ServerManager serverManager, ILogger<ServerController> logger)
    {
        _serverManager = serverManager;
        _logger = logger;
    }

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        var status = _serverManager.GetStatus();
        return Ok(status);
    }

    [HttpPost("start")]
    public async Task<IActionResult> StartServer()
    {
        _logger.LogInformation("Received request to start server");
        var result = await _serverManager.StartServerAsync();

        if (result.Success)
        {
            return Ok(new { message = result.Message });
        }

        return BadRequest(new { message = result.Message });
    }

    [HttpPost("stop")]
    public async Task<IActionResult> StopServer()
    {
        _logger.LogInformation("Received request to stop server");
        var result = await _serverManager.StopServerAsync();

        if (result.Success)
        {
            return Ok(new { message = result.Message });
        }

        return BadRequest(new { message = result.Message });
    }

    [HttpPost("restart")]
    public async Task<IActionResult> RestartServer()
    {
        _logger.LogInformation("Received request to restart server");
        var result = await _serverManager.RestartServerAsync();

        if (result.Success)
        {
            return Ok(new { message = result.Message });
        }

        return BadRequest(new { message = result.Message });
    }
   
    [HttpPost("attach/{pid}")]
    public IActionResult AttachToProcess(int pid)
    {
        _logger.LogInformation("Received request to attach to process {PID}", pid);
        var result = _serverManager.AttachToExistingProcess(pid);

        if (result.Success)
        {
            return Ok(new { message = result.Message });
        }

        return BadRequest(new { message = result.Message });
    }

    [HttpGet("logfile")]
    public IActionResult GetLogFile([FromQuery] int lines = 100)
    {
        var result = _serverManager.GetLogFileContent(lines);

        if (!result.Success)
        {
            return BadRequest(new { message = result.Message });
        }

        return Ok(new
        {
            Lines = result.Lines?.Count ?? 0,
            Source = "logfile",
            LogFilePath = result.LogFilePath,
            Output = result.Lines
        });
    }

    [HttpGet("players")]
    public IActionResult GetPlayers()
    {
        _logger.LogInformation("Received request to get player list");
        var playerList = _serverManager.GetPlayerList();
        return Ok(playerList);
    }
}

public class ServerCommandRequest
{
    public string Command { get; set; } = string.Empty;
}
