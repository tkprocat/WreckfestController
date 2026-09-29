using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using WreckfestController.Models;
using WreckfestController.Services.Auth;
using WreckfestController.Services.ServerControl;

namespace WreckfestController.Controllers;

/// <summary>
/// Controls the dedicated server: start, stop, restart, update, commands and the hook.
/// An action the server's state does not allow (not running, already running, no hook)
/// answers 409 with the reason as the problem's title.
/// </summary>
[ApiController]
[Authorize(Policy = ApiAuthentication.AdminPolicy)]
[Route("api/server")]
// Declaring any response type stops ASP.NET Core inferring the 200 from ActionResult<T>,
// so it is declared too; with no type given, each action's own return type is used.
[ProducesResponseType(StatusCodes.Status200OK)]
[ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
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
    public VersionResponse GetVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version;
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        return new VersionResponse(
            informationalVersion ?? version?.ToString() ?? "Unknown",
            version?.ToString() ?? "Unknown",
            "WreckfestController");
    }

    [HttpGet("status")]
    public ServerStatusResponse GetStatus() => ServerStatusResponse.From(_serverManager.GetStatus());

    [HttpPost("start")]
    public async Task<ActionResult<ServerActionResponse>> StartServer()
    {
        _logger.LogInformation("Received request to start server");
        return Answer(await _serverManager.StartServerAsync());
    }

    /// <summary>Stops the server with its own <c>exit</c> command.</summary>
    [HttpPost("stop")]
    public async Task<ActionResult<ServerActionResponse>> StopServer()
    {
        _logger.LogInformation("Received request to stop server (using graceful 'exit' command)");
        return Answer(await _serverManager.StopServerViaCommandAsync());
    }

    /// <summary>Kills the server process.</summary>
    [HttpPost("forcestop")]
    public async Task<ActionResult<ServerActionResponse>> ForceStopServer()
    {
        _logger.LogInformation("Received request to force stop server (kill process)");
        return Answer(await _serverManager.StopServerAsync());
    }

    /// <summary>Restarts with the in-game <c>/restart</c> command.</summary>
    [HttpPost("restart")]
    public async Task<ActionResult<ServerActionResponse>> RestartServer()
    {
        _logger.LogInformation("Received request to restart server (using in-game /restart command)");
        return Answer(await _serverManager.RestartServerViaCommandAsync());
    }

    /// <summary>Stops the process and starts it again.</summary>
    [HttpPost("forcerestart")]
    public async Task<ActionResult<ServerActionResponse>> ForceRestartServer()
    {
        _logger.LogInformation("Received request to force restart server (stop + start)");
        return Answer(await _serverManager.RestartServerAsync());
    }

    /// <summary>Updates the server with SteamCMD.</summary>
    [HttpPost("update")]
    public async Task<ActionResult<ServerActionResponse>> UpdateServer()
    {
        _logger.LogInformation("Received request to update server");
        return Answer(await _serverManager.UpdateServerAsync());
    }

    /// <summary>Sends a console command through the hook.</summary>
    [HttpPost("command")]
    public async Task<ActionResult<ServerActionResponse>> SendCommand(ServerCommandRequest request)
    {
        _logger.LogInformation("Received request to send command: {Command}", request.Command);
        return Answer(await _serverManager.SendCommandAsync(request.Command));
    }

    /// <summary>
    /// The running dedicated servers that attach and inject accept: those whose executable
    /// is the configured ServerPath. A remote admin picks the restarted server from here.
    /// No paths: the web never sees where things are on the PC.
    /// </summary>
    [HttpGet("processes")]
    public IReadOnlyList<ServerProcessResponse> GetServerProcesses() =>
        _serverManager.GetRunningWreckfestServers()
            .Where(p => p.IsConfiguredServer)
            .Select(p => new ServerProcessResponse(p.ProcessId, p.StartTime.ToUniversalTime(), p.IsAttached))
            .ToList();

    // No :int route constraint: a pid that is not a number is a 400 naming pid, not a 404.
    // Only the configured server may be targeted: attach decides what Force stop kills and
    // what inject loads the hook into. AttachToConfiguredServer checks and attaches on one
    // process handle.
    [HttpPost("attach/{pid}")]
    public ActionResult<ServerActionResponse> AttachToProcess(int pid)
    {
        if (InvalidPid(pid) is { } refused)
        {
            return refused;
        }

        _logger.LogInformation("Received request to attach to process {PID}", pid);
        return Answer(_serverManager.AttachToConfiguredServer(pid));
    }

    /// <summary>
    /// Injects the console hook into a running Wreckfest process and routes its
    /// output into the controller. Mirrors the Process Manager INJECT button so
    /// the full start -> inject cycle can be driven without the GUI.
    /// </summary>
    [HttpPost("inject/{pid}")]
    public async Task<ActionResult<InjectResponse>> InjectConsoleHook(int pid)
    {
        if ((InvalidPid(pid) ?? NotTheServer(pid)) is { } refused)
        {
            return refused;
        }

        _logger.LogInformation("Received request to inject console hook into process {PID}", pid);
        var result = await _serverManager.InjectConsoleHookAsync(pid);
        if (!result.Success)
        {
            return this.Refused(result.Message);
        }

        _serverManager.ProcessConsoleHookOutput = true;
        return new InjectResponse(result.Message, pid);
    }

    /// <summary>Injects the console hook into the currently tracked server process.</summary>
    [HttpPost("inject")]
    public async Task<ActionResult<InjectResponse>> InjectConsoleHookIntoTrackedProcess()
    {
        var status = _serverManager.GetStatus();
        if (!status.IsRunning || status.ProcessId is not int pid)
        {
            return this.Refused("No tracked server process to inject into.");
        }

        return await InjectConsoleHook(pid);
    }

    /// <summary>The last lines of the server's log file, read from disk.</summary>
    [HttpGet("logfile")]
    public ActionResult<LogFileResponse> GetLogFile([FromQuery, Range(1, 10_000)] int lines = 100)
    {
        var result = _serverManager.GetLogFileContent(lines);
        if (!result.Success)
        {
            // Its messages can name the log file's path ("not found at C:\..."); the web
            // never sees paths, so a failure that involved one is summarised.
            if (result.LogFilePath is null)
            {
                return this.Refused(result.Message);
            }

            _logger.LogWarning("Log file tail for the web API failed: {Message}", result.Message);
            return this.Refused("The server's log file could not be read. The desktop app's log has the details.");
        }

        var output = result.Lines ?? [];
        return new LogFileResponse(output.Count, "logfile", output);
    }

    [HttpGet("players")]
    public async Task<PlayerListResponse> GetPlayers()
    {
        _logger.LogInformation("Received request to get player list");

        // Refresh from the hook's structured snapshot first. Hook output only carries
        // lines printed after injection, so players who joined beforehand are absent
        // from the tracker; the snapshot reads current state directly instead of
        // relying on scrollback or on a "list" echo the hook never produces.
        await _serverManager.TryRefreshPlayersFromHookAsync();

        return _serverManager.GetPlayerList();
    }

    private ActionResult<ServerActionResponse> Answer((bool Success, string Message) result) =>
        result.Success ? new ServerActionResponse(result.Message) : this.Refused(result.Message);

    private ActionResult? InvalidPid(int pid) =>
        pid > 0 ? null : this.Invalid("pid", "pid must be a process id: a positive number.");

    private ActionResult? NotTheServer(int pid) =>
        _serverManager.CheckConfiguredServerProcess(pid) is (false, var reason) ? this.Refused(reason) : null;
}

public class ServerCommandRequest
{
    /// <summary>A console command, such as <c>/message Hello</c>.</summary>
    [Required]
    public string Command { get; set; } = string.Empty;
}

public sealed record VersionResponse(string Version, string AssemblyVersion, string Product);

/// <summary>A server action that went through, and what the server manager said.</summary>
public sealed record ServerActionResponse(string Message);

public sealed record InjectResponse(string Message, int ProcessId);

// No logFilePath: the web never sees where files are on the PC.
public sealed record LogFileResponse(int Lines, string Source, IReadOnlyList<string> Output);

/// <summary>A running dedicated server the API may attach to, and whether it already is.</summary>
public sealed record ServerProcessResponse(int ProcessId, DateTime StartTime, bool IsAttached);

/// <summary>Whether the server runs, and for how long, in whole seconds.</summary>
public sealed record ServerStatusResponse(bool IsRunning, int? ProcessId, long? UptimeSeconds, string? CurrentTrack)
{
    public static ServerStatusResponse From(ServerStatus status) => new(
        status.IsRunning,
        status.ProcessId,
        status.Uptime is { } uptime ? (long)uptime.TotalSeconds : null,
        string.IsNullOrEmpty(status.CurrentTrack) ? null : status.CurrentTrack);
}
