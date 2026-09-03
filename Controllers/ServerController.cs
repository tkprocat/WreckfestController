using Microsoft.AspNetCore.Mvc;
using System.Reflection;
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

    [HttpGet("version")]
    public IActionResult GetVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version;
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        return Ok(new
        {
            version = informationalVersion ?? version?.ToString() ?? "Unknown",
            assemblyVersion = version?.ToString() ?? "Unknown",
            product = "WreckfestController"
        });
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
        _logger.LogInformation("Received request to stop server (using graceful 'exit' command)");
        var result = await _serverManager.StopServerViaCommandAsync();

        if (result.Success)
        {
            return Ok(new { message = result.Message });
        }

        return BadRequest(new { message = result.Message });
    }

    [HttpPost("forcestop")]
    public async Task<IActionResult> ForceStopServer()
    {
        _logger.LogInformation("Received request to force stop server (kill process)");
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
        _logger.LogInformation("Received request to restart server (using in-game /restart command)");
        var result = await _serverManager.RestartServerViaCommandAsync();

        if (result.Success)
        {
            return Ok(new { message = result.Message });
        }

        return BadRequest(new { message = result.Message });
    }

    [HttpPost("forcerestart")]
    public async Task<IActionResult> ForceRestartServer()
    {
        _logger.LogInformation("Received request to force restart server (stop + start)");
        var result = await _serverManager.RestartServerAsync();

        if (result.Success)
        {
            return Ok(new { message = result.Message });
        }

        return BadRequest(new { message = result.Message });
    }

    [HttpPost("update")]
    public async Task<IActionResult> UpdateServer()
    {
        _logger.LogInformation("Received request to update server");
        var result = await _serverManager.UpdateServerAsync();

        if (result.Success)
        {
            return Ok(new { message = result.Message });
        }

        return BadRequest(new { message = result.Message });
    }

    [HttpPost("command")]
    public async Task<IActionResult> SendCommand([FromBody] ServerCommandRequest request)
    {
        _logger.LogInformation("Received request to send command: {Command}", request.Command);
        var result = await _serverManager.SendCommandAsync(request.Command);

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

    /// <summary>
    /// Injects the console hook into a running Wreckfest process and routes its
    /// output into the controller. Mirrors the Process Manager INJECT button so
    /// the full start -> inject cycle can be driven without the GUI.
    /// </summary>
    [HttpPost("inject/{pid}")]
    public async Task<IActionResult> InjectConsoleHook(int pid)
    {
        _logger.LogInformation("Received request to inject console hook into process {PID}", pid);
        var result = await _serverManager.InjectConsoleHookAsync(pid);

        if (!result.Success)
        {
            return BadRequest(new { message = result.Message });
        }

        _serverManager.ProcessConsoleHookOutput = true;
        return Ok(new { message = result.Message, processId = pid });
    }

    /// <summary>
    /// Injects the console hook into the currently tracked server process.
    /// </summary>
    [HttpPost("inject")]
    public async Task<IActionResult> InjectConsoleHookIntoTrackedProcess()
    {
        var status = _serverManager.GetStatus();
        if (!status.IsRunning || status.ProcessId is not int pid)
        {
            return BadRequest(new { message = "No tracked server process to inject into" });
        }

        return await InjectConsoleHook(pid);
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
    public async Task<IActionResult> GetPlayers()
    {
        _logger.LogInformation("Received request to get player list");

        // Refresh from the hook's structured snapshot first. Hook output only carries
        // lines printed after injection, so players who joined beforehand are absent
        // from the tracker; the snapshot reads current state directly instead of
        // relying on scrollback or on a "list" echo the hook never produces.
        await _serverManager.TryRefreshPlayersFromHookAsync();

        var playerList = _serverManager.GetPlayerList();
        return Ok(playerList);
    }
}

public class ServerCommandRequest
{
    public string Command { get; set; } = string.Empty;
}
