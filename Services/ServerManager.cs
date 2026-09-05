using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace WreckfestController.Services;

public class ServerManager
{
    private Process? _serverProcess;
    private readonly IConfiguration _configuration;
    /// <summary>
    /// Event raised when console output is received from the server
    /// </summary>
    public event Action<string>? ConsoleOutput;

    /// <summary>
    /// Event raised when experimental injected console hook output is received.
    /// </summary>
    public event Action<string>? ConsoleHookOutput;

    /// <summary>
    /// Event raised when a player sends a chat command (message starting with !).
    /// </summary>
    public event Action<string, bool, string>? ChatCommandReceived;

    private readonly object _lock = new();
    private DateTime? _startTime;
    private int? _actualServerPid;
    private readonly ILogger<ServerManager> _logger;
    private readonly System.Collections.Concurrent.ConcurrentQueue<(DateTime Timestamp, string Message)> _outputBuffer = new();
    private const int MaxBufferSize = 500;
    private readonly IServerInputWriter _serverInputWriter;
    private readonly IInjectedHookOutputReader _injectedHookOutputReader;
    private readonly SemaphoreSlim _commandSendLock = new(1, 1);
    private readonly PlayerTracker _playerTracker;
    private readonly TrackChangeTracker _trackChangeTracker;
    private readonly ServerInfoTracker _serverInfoTracker;
    private readonly WreckfestWebWebhookService _webhookService;
    private readonly ConsoleLogWebhookSender _consoleLogSender;
    private string _currentTrack = string.Empty;

    /// <summary>
    /// The message from the last record handled. The hook emits a record ahead of the
    /// console line it pairs with, so this suppresses the report for that line.
    /// Compared by containment because the record carried the line before colour
    /// codes were stripped, while the console line arrives after.
    /// </summary>
    private string? _lastRecordMessage;

    /// <summary>Recognises a chat line well enough to notice one that produced no record.</summary>
    private static readonly Regex ChatLineShape =
        new(@"^(?:\*\s*)?\d{2}:\d{2}:\d{2}\s+(?:-\s+)?\*?[^:]+:\s*!", RegexOptions.Compiled);

    // Chat commands are handled on their own single-consumer worker rather than
    // inline on the hook's output-reading thread. Handlers block (VotingService
    // waits on a hook round-trip), and a blocked reader stops draining the output
    // pipe - which makes the hook's own WriteHookLine/FlushFileBuffers block, so
    // neither side can progress until a timeout fires. One consumer preserves the
    // strict command ordering that !yes / !no / !confirm rely on.
    private readonly System.Threading.Channels.Channel<(string Player, bool IsBot, string Message, int Generation, long AttachmentId)> _chatCommands =
        System.Threading.Channels.Channel.CreateUnbounded<(string, bool, string, int, long)>(
            new System.Threading.Channels.UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });
    private Task? _chatCommandWorker;
    private readonly object _chatWorkerLock = new();
    private bool _useInjectedHookAsPrimaryOutput;

    // Server events come from the game's own ring buffer rather than parsed console
    // text; see ServerEventReader. Polled rather than pushed, which is why the reader
    // reports overflow so we can fall back to a full snapshot.
    private ServerEventReader? _serverEventReader;

    // The attachment a chat command was accepted under, made ambient for the
    // duration of the handler. Passing it explicitly would mean threading a PID
    // through ChatCommandReceived and every SendCommandAsync call in VotingService;
    // AsyncLocal flows across the awaits a handler makes without that churn.
    //
    // It carries the PID rather than the generation on purpose: the generation is
    // an I/O epoch that also advances when monitoring restarts, so gating dispatch
    // on it would refuse commands after an ordinary reinject.
    // Monotonic identity for one continuous attachment. A PID cannot serve: attach
    // A, switch to B, switch back to A and the PID matches again, so work queued
    // under the first A attachment would be accepted by the second. This only ever
    // increases, so an attachment is never mistaken for a later one.
    private long _attachmentId;

    // The attachment a chat command was accepted under, made ambient for the
    // duration of the handler. Passing it explicitly would mean threading it
    // through ChatCommandReceived and every SendCommandAsync call in
    // VotingService; AsyncLocal flows across the awaits a handler makes without
    // that churn. An explicit session parameter is the better end state - see the
    // follow-up issue - but that is a wider change than this fix.
    private static readonly AsyncLocal<long?> _dispatchAttachmentId = new();
    private int _serverEventGeneration;
    private System.Threading.Timer? _serverEventTimer;
    private int _serverEventPollBusy;
    private bool _serverEventsSeeded;
    private static readonly TimeSpan ServerEventPollInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Raised when the server process ID changes (after restart or attach)
    /// </summary>
    public event Action<int?>? ProcessIdChanged;

    public bool IsRunning => GetActualServerPid() != null;

    public int? AttachedProcessId => GetActualServerPid();

    /// <summary>
    /// Injection is only allowed into the process that is already attached, so the
    /// tracked PID and the hooked process cannot diverge. A null candidate never
    /// qualifies - comparing two nulls would otherwise read as a match when nothing
    /// is selected and nothing is attached.
    /// </summary>
    public bool CanInjectInto(int? candidateProcessId) =>
        candidateProcessId.HasValue && candidateProcessId == AttachedProcessId;

    public ServerManager(
        IConfiguration configuration,
        ILogger<ServerManager> logger,
        PlayerTracker playerTracker,
        TrackChangeTracker trackChangeTracker,
        ServerInfoTracker serverInfoTracker,
        WreckfestWebWebhookService webhookService,
        ConsoleLogWebhookSender consoleLogSender)
        : this(
            configuration,
            logger,
            playerTracker,
            trackChangeTracker,
            serverInfoTracker,
            webhookService,
            consoleLogSender,
            new InjectedHookInputWriter(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<InjectedHookInputWriter>.Instance),
            new InjectedHookOutputReader(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<InjectedHookOutputReader>.Instance))
    {
    }

    public ServerManager(
        IConfiguration configuration,
        ILogger<ServerManager> logger,
        PlayerTracker playerTracker,
        TrackChangeTracker trackChangeTracker,
        ServerInfoTracker serverInfoTracker,
        WreckfestWebWebhookService webhookService,
        ConsoleLogWebhookSender consoleLogSender,
        IServerInputWriter serverInputWriter)
        : this(
            configuration,
            logger,
            playerTracker,
            trackChangeTracker,
            serverInfoTracker,
            webhookService,
            consoleLogSender,
            serverInputWriter,
            new InjectedHookOutputReader(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<InjectedHookOutputReader>.Instance))
    {
    }

    public ServerManager(
        IConfiguration configuration,
        ILogger<ServerManager> logger,
        PlayerTracker playerTracker,
        TrackChangeTracker trackChangeTracker,
        ServerInfoTracker serverInfoTracker,
        WreckfestWebWebhookService webhookService,
        ConsoleLogWebhookSender consoleLogSender,
        IServerInputWriter serverInputWriter,
        IInjectedHookOutputReader injectedHookOutputReader)
    {
        _configuration = configuration;
        _logger = logger;
        _playerTracker = playerTracker;
        _trackChangeTracker = trackChangeTracker;
        _serverInfoTracker = serverInfoTracker;
        _serverInputWriter = serverInputWriter;
        _injectedHookOutputReader = injectedHookOutputReader;
        _webhookService = webhookService;
        _consoleLogSender = consoleLogSender;

        _injectedHookOutputReader.OutputReceivedFrom += OnInjectedHookOutputReceived;
        _injectedHookOutputReader.HookOutputReceived += output => ConsoleHookOutput?.Invoke(output);

    }

    /// <summary>
    /// The attached PID if that process is still alive. Every caller that only needs
    /// the identity goes through here: <see cref="GetActualServerProcess"/> hands out
    /// an owned <see cref="Process"/>, and the once-a-second status poll would
    /// otherwise open a fresh OS handle per tick and hold it until finalisation.
    /// </summary>
    private int? GetActualServerPid()
    {
        using var process = GetActualServerProcess();
        return process?.Id;
    }

    /// <summary>
    /// The attached process, or null when nothing is attached or it has exited.
    /// The returned instance is owned by the caller and must be disposed.
    /// </summary>
    private Process? GetActualServerProcess()
    {
        // Only track by PID - we always start the server through the API
        if (_actualServerPid.HasValue)
        {
            try
            {
                var process = Process.GetProcessById(_actualServerPid.Value);
                if (!process.HasExited)
                {
                    return process;
                }
                else
                {
                    _logger.LogWarning("Tracked server process (PID: {PID}) has exited", _actualServerPid.Value);
                    SetAttachedProcess(null);
                    _startTime = null;
                    return null;
                }
            }
            catch (ArgumentException)
            {
                // Process doesn't exist
                _logger.LogWarning("Tracked server process (PID: {PID}) no longer exists", _actualServerPid!.Value);
                SetAttachedProcess(null);
                _startTime = null;
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error accessing tracked server process (PID: {PID})", _actualServerPid!.Value);
                return null;
            }
        }

        // No tracked PID means server is not running
        return null;
    }

    public virtual async Task<(bool Success, string Message)> StartServerAsync()
    {
        Process? process = null;

        lock (_lock)
        {
            if (IsRunning)
            {
                return (false, "Server is already running");
            }

            try
            {
                var serverPath = _configuration["WreckfestServer:ServerPath"];
                var serverArguments = _configuration["WreckfestServer:ServerArguments"] ?? "";
                var workingDirectory = _configuration["WreckfestServer:WorkingDirectory"];

                if (string.IsNullOrEmpty(serverPath) || !File.Exists(serverPath))
                {
                    return (false, $"Server executable not found at: {serverPath}");
                }

                // Resolve config file path if it contains server_config reference
                if (!string.IsNullOrEmpty(serverArguments) && serverArguments.Contains("server_config="))
                {
                    var configMatch = System.Text.RegularExpressions.Regex.Match(serverArguments, @"server_config=([^\s]+)");
                    if (configMatch.Success)
                    {
                        var configPath = configMatch.Groups[1].Value;
                        // If not an absolute path, make it relative to working directory
                        if (!Path.IsPathRooted(configPath) && !string.IsNullOrEmpty(workingDirectory))
                        {
                            var fullConfigPath = Path.Combine(workingDirectory, configPath);
                            if (File.Exists(fullConfigPath))
                            {
                                _logger.LogInformation("Using config file: {ConfigPath}", fullConfigPath);
                            }
                            else
                            {
                                _logger.LogWarning("Config file not found at: {ConfigPath}", fullConfigPath);
                            }
                        }
                    }
                }

                _logger.LogInformation("Starting server: {Path} {Args} in directory {WorkingDir}",
                    serverPath, serverArguments, workingDirectory ?? "(default)");

                process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = serverPath,
                        Arguments = serverArguments,
                        WorkingDirectory = workingDirectory,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                process.Start();

                _logger.LogInformation("Process started with PID: {PID}", process.Id);

                // Monitor process exit
                process.EnableRaisingEvents = true;
                process.Exited += (sender, e) =>
                {
                    _logger.LogWarning("Server process exited. Exit code: {ExitCode}", process.ExitCode);
                };

                _serverProcess = process;
                SetAttachedProcess(process.Id);
                _startTime = DateTime.UtcNow;
                ProcessIdChanged?.Invoke(process.Id);

                // Start monitoring the server output (console or log file)
                StartOutputMonitoring();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start server");
                return (false, $"Failed to start server: {ex.Message}");
            }
        }

        // Check if process exits immediately — done outside the lock to avoid blocking callers during the wait
        if (process.WaitForExit(500))
        {
            var exitCode = process.ExitCode;
            _logger.LogError("Server process exited immediately with code: {ExitCode}", exitCode);
            lock (_lock)
            {
                _serverProcess = null;
                _startTime = null;
            }
            return (false, $"Server process exited immediately with code: {exitCode}. Check server arguments and config file.");
        }

        // Wait outside the lock, check multiple times
        for (int i = 0; i < 5; i++)
        {
            await Task.Delay(1000);

            string processName;
            int processId;
            using (var actualProcess = GetActualServerProcess())
            {
                if (actualProcess == null)
                {
                    continue;
                }

                processName = actualProcess.ProcessName;
                processId = actualProcess.Id;
            }

            _logger.LogInformation("Server started successfully. Process: {ProcessName} (PID: {ProcessId})", processName, processId);

            // Send webhook notification
            _ = Task.Run(async () =>
            {
                try
                {
                    await _webhookService.SendServerStartedAsync(new Models.ServerStartedEvent
                    {
                        ProcessId = processId,
                        ProcessName = processName,
                        StartTime = _startTime ?? DateTime.UtcNow
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to send server started webhook");
                }
            });

            return (true, $"Server started successfully. Process: {processName} (PID: {processId})");
        }

        // Process is running but not detected by GetActualServerProcess (shouldn't happen with PID tracking)
        _logger.LogWarning("Server process started (PID: {PID}) but not confirmed after 5 seconds", _actualServerPid);
        return (true, $"Server process started (PID: {_actualServerPid}) but not confirmed. Check logs.");
    }

    /// <summary>
    /// Stops the server gracefully using the built-in "exit" command.
    /// </summary>
    public virtual async Task<(bool Success, string Message)> StopServerViaCommandAsync()
    {
        if (!IsRunning)
        {
            return (false, "Server is not running");
        }

        try
        {
            var currentPid = _actualServerPid;
            _logger.LogInformation("Stopping server gracefully via 'exit' command (PID: {PID})", currentPid);

            // Stop reading server output before sending exit. I/O is hook-only, so this
            // only clears the primary-output flag and stops event polling - it does not
            // close the input pipe the command below travels on.
            StopOutputMonitoring();

            // The hook reports success only when the game writes back an "OK" line, and
            // "exit" is the one command that cannot reliably produce one: the game starts
            // shutting down the moment it is dispatched, so the acknowledgement is racing
            // a process that is going away. Treating a missing "OK" as failure is what
            // made every graceful stop force-kill a server that was already exiting.
            //
            var commandResult = await SendCommandAsync("exit");
            if (!commandResult.Success && !IsExpectedExitSilence(commandResult.Message))
            {
                _logger.LogWarning("Exit command was rejected ({Message}), falling back to force stop", commandResult.Message);
                return await StopServerAsync();
            }

            if (commandResult.Success)
            {
                _logger.LogInformation("Exit command acknowledged, waiting for server to shut down...");
            }
            else
            {
                _logger.LogInformation("Exit command returned no hook response; waiting for server to shut down...");
            }

            // A missing response is expected for exit; only the process state can
            // determine whether the dispatched command actually shut the server down.
            var timeout = TimeSpan.FromSeconds(30);
            var startTime = DateTime.Now;
            var checkInterval = TimeSpan.FromMilliseconds(500);

            while (DateTime.Now - startTime < timeout)
            {
                await Task.Delay(checkInterval);

                // Check if process has exited
                if (GetActualServerPid() == null)
                {
                    _logger.LogInformation("Server process exited gracefully");

                    // Send webhook notification before cleanup
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _webhookService.SendServerStoppedAsync(new Models.ServerStoppedEvent
                            {
                                ProcessId = currentPid ?? 0,
                                StopMethod = "Graceful"
                            });
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to send server stopped webhook");
                        }
                    });

                    // Clean up
                    lock (_lock)
                    {
                        _serverProcess = null;
                        _startTime = null;
                        SetAttachedProcess(null);

                        // Clear player tracking
                        _playerTracker.Clear();
                    }

                    return (true, commandResult.Success
                        ? $"Server stopped gracefully (was PID: {currentPid})"
                        : $"Server stopped gracefully, though the exit command was never acknowledged (was PID: {currentPid})");
                }
            }

            // Still alive after the grace period, so the exit genuinely did not take.
            _logger.LogWarning(
                "Server still running {Seconds}s after the exit command, forcing shutdown",
                timeout.TotalSeconds);
            return await StopServerAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop server gracefully, falling back to force stop");
            return await StopServerAsync();
        }
    }

    // "exit" is dispatched and then the game goes away, so the hook has nothing left
    // to acknowledge with. Two results mean that silence: no response at all, and a
    // response timeout after the command was already delivered. Both leave a server
    // that may well be shutting down, so both earn the wait.
    //
    // Anything else - a refused command, or a timeout before delivery - is a real
    // failure and still falls back immediately.
    private static bool IsExpectedExitSilence(string message) =>
        string.Equals(message, InjectedHookInputWriter.NoResponseMessage, StringComparison.Ordinal)
        || string.Equals(message, InjectedHookInputWriter.DispatchedWithoutResponseMessage, StringComparison.Ordinal);

    /// <summary>
    /// Force stops the server by killing the process tree.
    /// </summary>
    public virtual async Task<(bool Success, string Message)> StopServerAsync()
    {
        Process? actualProcess;
        int currentPid;

        lock (_lock)
        {
            actualProcess = GetActualServerProcess();
            if (actualProcess == null)
            {
                return (false, "Server is not running");
            }
            currentPid = actualProcess.Id;
        }

        try
        {
            _logger.LogInformation("Force stopping server process {ProcessId}", currentPid);

            // Kill and wait outside the lock to avoid blocking status checks
            using (actualProcess)
            {
                actualProcess.Kill(entireProcessTree: true);
                actualProcess.WaitForExit(10000);
            }

            // Send webhook notification before cleanup
            _ = Task.Run(async () =>
            {
                try
                {
                    await _webhookService.SendServerStoppedAsync(new Models.ServerStoppedEvent
                    {
                        ProcessId = currentPid,
                        StopMethod = "Force"
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to send server stopped webhook");
                }
            });

            lock (_lock)
            {
                // Clean up the launcher process if it's still around
                if (_serverProcess != null && !_serverProcess.HasExited)
                {
                    try
                    {
                        _serverProcess.Kill();
                        _serverProcess.Dispose();
                    }
                    catch
                    {
                        // Ignore cleanup errors
                    }
                }

                // Stop output monitoring before cleanup (frees console attachment)
                StopOutputMonitoring();

                _serverProcess = null;
                _startTime = null;
                SetAttachedProcess(null);

                // Clear player tracking
                _playerTracker.Clear();
            }

            return (true, "Server stopped successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop server");
            return (false, $"Failed to stop server: {ex.Message}");
        }
    }

    public virtual async Task<(bool Success, string Message)> RestartServerAsync()
    {
        var stopResult = await StopServerAsync();
        if (!stopResult.Success && IsRunning)
        {
            return (false, $"Failed to restart: {stopResult.Message}");
        }

        // Wait a moment before restarting
        await Task.Delay(2000);

        return await StartServerAsync();
    }

    /// <summary>
    /// Restarts the server using the built-in /restart command and tracks the new PID.
    /// This is faster than stop+start but requires PID detection logic.
    /// </summary>
    public virtual async Task<(bool Success, string Message)> RestartServerViaCommandAsync()
    {
        if (!IsRunning)
        {
            return (false, "Server is not running");
        }

        try
        {
            _logger.LogInformation("Starting server restart via /restart command");

            // Step 1: Get all current Wreckfest*.exe PIDs
            var oldPids = GetAllWreckfestPids();
            _logger.LogInformation("Current Wreckfest PIDs before restart: {PIDs}", string.Join(", ", oldPids));

            // Step 2: Stop output monitoring before restart (old process will be killed)
            _logger.LogDebug("Stopping output monitoring before restart");
            StopOutputMonitoring();

            // Step 3: Send /restart command
            var commandResult = await SendCommandAsync("/restart");
            if (!commandResult.Success)
            {
                return (false, $"Failed to send restart command: {commandResult.Message}");
            }

            _logger.LogInformation("Restart command sent, waiting for server to restart...");

            // Step 3: Wait for restart to complete
            // We'll wait for either:
            // - A log message indicating restart (e.g., "Server connected.", "Current track loaded!")
            // - Or a timeout (30 seconds max)

            var restartDetected = false;
            var timeout = TimeSpan.FromSeconds(30);
            var startTime = DateTime.Now;
            var logCheckInterval = TimeSpan.FromMilliseconds(500);

            while (DateTime.Now - startTime < timeout)
            {
                await Task.Delay(logCheckInterval);

                // Look for restart indicators in hook output received since the command was sent
                {
                    var recentMessages = _outputBuffer
                        .Where(m => m.Timestamp > startTime)
                        .Select(m => m.Message)
                        .ToList();

                    // Check for common restart indicators
                    if (recentMessages.Any(m =>
                        m.Contains("Server connected", StringComparison.OrdinalIgnoreCase) ||
                        m.Contains("Current track loaded!", StringComparison.OrdinalIgnoreCase) ||
                        m.Contains("Server started", StringComparison.OrdinalIgnoreCase)))
                    {
                        _logger.LogInformation("Restart detected via log message");
                        restartDetected = true;
                        break;
                    }
                }
            }

            if (!restartDetected)
            {
                _logger.LogWarning("Restart completion not detected via logs within timeout, proceeding with PID detection anyway");
            }

            // Wait a bit more to ensure new process is fully started
            await Task.Delay(2000);

            // Step 4: Get all current Wreckfest*.exe PIDs again
            var newPids = GetAllWreckfestPids();
            _logger.LogInformation("Wreckfest PIDs after restart: {PIDs}", string.Join(", ", newPids));

            // Step 5: Find the new PID (PIDs in newPids that aren't in oldPids)
            var newProcessPids = newPids.Except(oldPids).ToList();

            if (newProcessPids.Count == 0)
            {
                _logger.LogError("No new Wreckfest process detected after restart");
                return (false, "Server restart failed: No new process detected. The server may have failed to restart.");
            }

            if (newProcessPids.Count > 1)
            {
                _logger.LogWarning("Multiple new Wreckfest processes detected: {PIDs}. Using the first one.", string.Join(", ", newProcessPids));
            }

            // Update to track the new PID
            var newPid = newProcessPids.First();
            SetAttachedProcess(newPid);
            ProcessIdChanged?.Invoke(newPid);

            // Update start time
            try
            {
                var process = Process.GetProcessById(newPid);
                _startTime = process.StartTime.ToUniversalTime();
            }
            catch
            {
                _startTime = DateTime.UtcNow;
            }

            var oldPid = oldPids.FirstOrDefault();
            _logger.LogInformation("Server restarted successfully via /restart command. New PID: {PID} (was {OldPID})",
                newPid, oldPid != 0 ? oldPid : (int?)null);

            // Restart console monitoring for the new process
            _logger.LogDebug("Restarting output monitoring for new process");
            StartOutputMonitoring();

            // Send webhook notification
            _ = Task.Run(async () =>
            {
                try
                {
                    await _webhookService.SendServerRestartedAsync(new Models.ServerRestartedEvent
                    {
                        OldProcessId = oldPid != 0 ? oldPid : null,
                        NewProcessId = newPid,
                        RestartMethod = "Command"
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to send server restarted webhook");
                }
            });

            return (true, $"Server restarted successfully via /restart command. New PID: {newPid}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restart server via /restart command");
            return (false, $"Failed to restart server: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets all running Wreckfest*.exe process IDs
    /// </summary>
    private List<int> GetAllWreckfestPids()
    {
        try
        {
            var processes = Process.GetProcesses()
                .Where(p => p.ProcessName.StartsWith("Wreckfest", StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Id)
                .ToList();

            return processes;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting Wreckfest process IDs");
            return new List<int>();
        }
    }

    public virtual async Task<(bool Success, string Message)> UpdateServerAsync()
    {
        _logger.LogInformation("Starting server update process");

        // Stop the server if it's running
        if (IsRunning)
        {
            _logger.LogInformation("Stopping server before update");
            var stopResult = await StopServerAsync();
            if (!stopResult.Success)
            {
                return (false, $"Failed to stop server for update: {stopResult.Message}");
            }

            // Wait a moment to ensure server is fully stopped
            await Task.Delay(2000);
        }

        // Get steamcmd configuration
        var steamCmdPath = _configuration["SteamCmd:SteamCmdPath"];
        var appId = _configuration["SteamCmd:WreckfestAppId"];
        var installDir = _configuration["WreckfestServer:WorkingDirectory"];

        if (string.IsNullOrEmpty(steamCmdPath) || !File.Exists(steamCmdPath))
        {
            return (false, $"SteamCmd executable not found at: {steamCmdPath}. Please configure SteamCmd Path in settings.");
        }

        if (string.IsNullOrEmpty(appId))
        {
            return (false, "SteamCmd Wreckfest App ID not configured in settings.");
        }

        if (string.IsNullOrEmpty(installDir) || !Directory.Exists(installDir))
        {
            return (false, $"Wreckfest Working Directory not found: {installDir}. Please configure in settings.");
        }

        try
        {
            _logger.LogInformation("Running SteamCmd to update Wreckfest server (AppId: {AppId})", appId);

            // Build steamcmd arguments for anonymous login and update
            // +login anonymous - login anonymously
            // +force_install_dir - set install directory
            // +app_update - update the app
            // +quit - exit steamcmd after update
            var arguments = $"+login anonymous +force_install_dir \"{installDir}\" +app_update {appId} validate +quit";

            var processStartInfo = new ProcessStartInfo
            {
                FileName = steamCmdPath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = new Process { StartInfo = processStartInfo };

            var outputBuilder = new System.Text.StringBuilder();
            var errorBuilder = new System.Text.StringBuilder();

            process.OutputDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    _logger.LogInformation("SteamCmd: {Output}", e.Data);
                    outputBuilder.AppendLine(e.Data);
                }
            };

            process.ErrorDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    _logger.LogWarning("SteamCmd Error: {Error}", e.Data);
                    errorBuilder.AppendLine(e.Data);
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Wait for steamcmd to complete (with a timeout of 30 minutes)
            var completed = await Task.Run(() => process.WaitForExit(1800000)); // 30 minutes timeout

            if (!completed)
            {
                process.Kill(entireProcessTree: true);
                return (false, "SteamCmd update timed out after 30 minutes");
            }

            if (process.ExitCode != 0)
            {
                var errorOutput = errorBuilder.ToString();
                return (false, $"SteamCmd update failed with exit code {process.ExitCode}. Check logs for details.");
            }

            _logger.LogInformation("SteamCmd update completed successfully");

            // Wait a moment before restarting
            await Task.Delay(2000);

            // Start the server again
            _logger.LogInformation("Starting server after update");
            var startResult = await StartServerAsync();

            if (startResult.Success)
            {
                return (true, "Server updated and restarted successfully");
            }
            else
            {
                return (false, $"Server updated successfully but failed to start: {startResult.Message}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update server via SteamCmd");
            return (false, $"Failed to update server: {ex.Message}");
        }
    }

    public virtual async Task<(bool Success, string Message)> SendCommandAsync(string command)
    {
        command = command.TrimEnd('\r', '\n');
        if (string.IsNullOrWhiteSpace(command))
        {
            return (false, "Command cannot be empty");
        }

        if (!IsRunning)
        {
            return (false, "Server is not running");
        }

        await _commandSendLock.WaitAsync();
        try
        {
            // Resolved after the semaphore, which is the last moment before the
            // command goes out - and the moment that matters, because waiting for
            // this lock is exactly when attachment can move underneath a caller
            // that already validated.
            int? processId;
            var expectedAttachment = _dispatchAttachmentId.Value;
            lock (_lock)
            {
                processId = _actualServerPid ?? GetActualServerPid();
                if (processId == null)
                {
                    return (false, "Server is not running");
                }

                if (expectedAttachment != null && expectedAttachment.Value != CurrentAttachmentId)
                {
                    _logger.LogWarning(
                        "Refused to send {Command}: accepted under attachment {Expected}, now on {Current} (process {Pid})",
                        command, expectedAttachment.Value, CurrentAttachmentId, processId.Value);
                    return (false,
                        $"Attachment moved before the command could be sent (was {expectedAttachment.Value}, now {CurrentAttachmentId})");
                }
            }

            var result = await _serverInputWriter.SendCommandAsync(command, processId.Value);

            if (result.Success)
            {
                _logger.LogInformation("Successfully sent command to console: {Command}", command);
                return result;
            }
            else
            {
                return result;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending command: {Command}", command);
            return (false, $"Error sending command: {ex.Message}");
        }
        finally
        {
            _commandSendLock.Release();
        }
    }

    /// <summary>
    /// Reads module-relative memory from the running server through the hook.
    /// Returns null when the hook is unavailable, so callers can fail open rather
    /// than treating "cannot read" as a definite state.
    /// </summary>
    public virtual async Task<byte[]?> ReadHookMemoryAsync(uint rva, int size)
    {
        var processId = GetActualServerPid();
        if (processId == null || _serverInputWriter is not IHookMemoryReader reader)
        {
            return null;
        }

        try
        {
            var result = await reader.ReadModuleMemoryAsync(processId.Value, rva, size);
            return result.Success ? result.Data : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Hook memory read failed at rva 0x{Rva:X8}", rva);
            return null;
        }
    }

    public virtual async Task<bool> TryRefreshPlayersFromHookAsync()
    {
        if (_serverInputWriter is not IPlayerSnapshotReader playerSnapshotReader)
        {
            return false;
        }

        // PID and generation are captured together under the switch's own lock.
        // Read separately, a switch landing between them would stamp the old
        // process's snapshot with the new process's generation, and the commit check
        // below would wave it through.
        int pid;
        int generation;
        lock (_lock)
        {
            var attachedPid = GetActualServerPid();
            if (attachedPid == null)
            {
                return false;
            }

            pid = attachedPid.Value;
            generation = CurrentAttachmentGeneration;
        }

        try
        {
            var snapshot = await playerSnapshotReader.ReadPlayerSnapshotAsync(pid);
            if (!snapshot.Success)
            {
                _logger.LogDebug("Injected hook player snapshot refresh skipped: {Message}", snapshot.Message);
                return false;
            }

            // Commit under the same lock the switch uses, so the generation cannot
            // change between the check and the mutation.
            lock (_lock)
            {
                if (!IsCurrentAttachmentGeneration(generation))
                {
                    _logger.LogDebug(
                        "Discarded a player snapshot from process {Pid}; attachment moved while it was in flight",
                        pid);
                    return false;
                }

                _playerTracker.ProcessHookPlayerSnapshot(snapshot.Players);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Injected hook player snapshot refresh failed");
            return false;
        }
    }

    private void NotifyConsoleOutput(string message)
    {
        ConsoleOutput?.Invoke(message);
    }

    public virtual ServerStatus GetStatus()
    {
        var actualPid = GetActualServerPid();

        return new ServerStatus
        {
            IsRunning = actualPid != null,
            ProcessId = actualPid,
            Uptime = _startTime.HasValue && actualPid != null
                ? DateTime.UtcNow - _startTime.Value
                : null,
            CurrentTrack = _currentTrack
        };
    }

    public (bool Success, string Message) AttachToExistingProcess(int pid)
    {
        try
        {
            var process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                return (false, $"Process {pid} has already exited");
            }

            // Switch attachment as one step. Hook output carries no PID, so anything
            // still arriving from the previous process - buffered output, an
            // in-flight event poll, the roster it built - would otherwise be applied
            // to this one.
            lock (_lock)
            {
                StopOutputMonitoring();
                StopHookOutputListener();
                ClearProcessScopedState();

                SetAttachedProcess(pid);
                _startTime = process.StartTime;
            }

            ProcessIdChanged?.Invoke(pid);
            _logger.LogInformation("Attached to existing server process (PID: {PID}, Name: {Name})", pid, process.ProcessName);

            // Start monitoring the attached process
            StartOutputMonitoring();

            return (true, $"Attached to process {pid} ({process.ProcessName})");
        }
        catch (ArgumentException)
        {
            return (false, $"Process {pid} does not exist");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to attach to process {PID}", pid);
            return (false, $"Failed to attach to process: {ex.Message}");
        }
    }

    // StopOutputMonitoring only clears the primary-output flag and stops event
    // polling; the reader's pipe listeners keep running and keep delivering the old
    // process's output. Stopping it here is deliberately scoped to attachment
    // switching rather than folded into StopOutputMonitoring, which the graceful
    // stop path also calls and which must keep behaving as it does today.
    //
    // Synchronous in practice - StopAsync closes the listeners and returns a
    // completed task - so this cannot deadlock under _lock.
    private void StopHookOutputListener()
    {
        try
        {
            _injectedHookOutputReader.StopAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to stop the hook output listener while switching attachment");
        }
    }

    // Every attachment change goes through here. Scattered assignments were how
    // identity drifted: eight sites moved the PID and only one advanced the
    // guard that work is validated against.
    private void SetAttachedProcess(int? pid)
    {
        _actualServerPid = pid;
        Interlocked.Increment(ref _attachmentId);
    }

    private long CurrentAttachmentId => Interlocked.Read(ref _attachmentId);

    // Everything here describes one attached process and means nothing about the
    // next one. Called while holding _lock.
    private void ClearProcessScopedState()
    {
        _playerTracker.Clear();
        _trackChangeTracker.Clear();
        _outputBuffer.Clear();
    }

    private void AddToOutputBuffer(string message)
    {
        _outputBuffer.Enqueue((DateTime.Now, message));

        // Keep buffer size limited
        while (_outputBuffer.Count > MaxBufferSize)
        {
            _outputBuffer.TryDequeue(out _);
        }
    }

    private string? GetLogFilePathFromConfig()
    {
        try
        {
            var serverArgs = _configuration["WreckfestServer:ServerArguments"] ?? "";
            var workingDir = _configuration["WreckfestServer:WorkingDirectory"];

            if (string.IsNullOrEmpty(workingDir))
            {
                return null;
            }

            // Extract server_config file path from arguments like: "-s server_config=server_config.cfg"
            var match = System.Text.RegularExpressions.Regex.Match(serverArgs, @"server_config=([^\s]+)");
            if (!match.Success)
            {
                return null;
            }

            var configFileName = match.Groups[1].Value;
            var configFilePath = Path.IsPathRooted(configFileName)
                ? configFileName
                : Path.Combine(workingDir, configFileName);

            if (!File.Exists(configFilePath))
            {
                return null;
            }

            // Parse the config file to find the log= setting
            var configLines = File.ReadAllLines(configFilePath);
            foreach (var line in configLines)
            {
                var trimmedLine = line.Trim();
                if (trimmedLine.StartsWith("log=") && !trimmedLine.StartsWith("#"))
                {
                    var logFileName = trimmedLine.Substring(4).Trim();
                    if (!string.IsNullOrEmpty(logFileName))
                    {
                        // Log file path is relative to working directory
                        return Path.Combine(workingDir, logFileName);
                    }
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse log file path from server config");
            return null;
        }
    }

    /// <summary>
    /// Tails the server's log file from disk. This is the one deliberate exception to
    /// the hook-only I/O contract in CLAUDE.md: WreckfestWeb's log viewer depends on
    /// it, and it is the only way to see output from before attachment. It is
    /// read-only history - it answers with no hook injected, and can return lines that
    /// predate the attached process - so nothing that tracks live state may be fed
    /// from it.
    /// </summary>
    public (bool Success, string Message, string? LogFilePath, List<string>? Lines) GetLogFileContent(int lines = 100)
    {
        // Try to get log file path from server config first
        var logFilePath = GetLogFilePathFromConfig();

        // Fall back to appsettings.json if not found in server config
        if (string.IsNullOrEmpty(logFilePath))
        {
            logFilePath = _configuration["WreckfestServer:LogFilePath"];
        }

        if (string.IsNullOrEmpty(logFilePath))
        {
            return (false, "LogFilePath not found in server_config.cfg or appsettings.json", null, null);
        }

        if (!File.Exists(logFilePath))
        {
            return (false, $"Log file not found at: {logFilePath}", logFilePath, null);
        }

        try
        {
            // Read last N lines from log file with FileShare.ReadWrite to allow reading while server is writing
            var allLines = new List<string>();
            using (var fileStream = new FileStream(logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(fileStream))
            {
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    allLines.Add(line);
                }
            }

            var lastLines = allLines
                .TakeLast(Math.Min(lines, allLines.Count))
                .ToList();

            return (true, "Success", logFilePath, lastLines);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read log file: {Path}", logFilePath);
            return (false, $"Failed to read log file: {ex.Message}", logFilePath, null);
        }
    }

    /// <summary>
    /// Starts output monitoring. Output only flows once the console hook has been
    /// injected into the target process (Process Manager -> INJECT).
    /// </summary>
    private void StartOutputMonitoring()
    {
        _useInjectedHookAsPrimaryOutput = true;
        StartServerEventPolling();
        _logger.LogInformation("Injected hook output active; waiting for manual hook injection");
        NotifyConsoleOutput("[Controller] Use Process Manager -> INJECT to start output capture.");
    }

    private void StartServerEventPolling()
    {
        StopServerEventPolling();

        _serverEventReader = new ServerEventReader(
            ReadHookMemoryAsync,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ServerEventReader>.Instance);

        // Every poll carries the generation it was started under. Disposing the timer
        // does not cancel a poll already awaiting a hook read, so the generation is
        // what lets that poll notice its results belong to a process we have since
        // stopped watching, and drop them instead of feeding another process's
        // tracker.
        var generation = Volatile.Read(ref _serverEventGeneration);

        _serverEventTimer = new System.Threading.Timer(
            _ => _ = PollServerEventsAsync(generation),
            null,
            ServerEventPollInterval,
            ServerEventPollInterval);
    }

    private void StopServerEventPolling()
    {
        // Retire the generation first: an in-flight poll checks this the moment its
        // await resumes.
        Interlocked.Increment(ref _serverEventGeneration);
        _serverEventTimer?.Dispose();
        _serverEventTimer = null;
        _serverEventReader = null;
        _serverEventsSeeded = false;
    }

    private bool IsCurrentEventGeneration(int generation) =>
        Volatile.Read(ref _serverEventGeneration) == generation;

    // Attachment and polling share one counter: every attachment switch stops
    // polling, so retiring the generation covers both, and a single value avoids
    // two counters that could disagree about which process is current.
    private int CurrentAttachmentGeneration => Volatile.Read(ref _serverEventGeneration);

    private bool IsCurrentAttachmentGeneration(int generation) =>
        Volatile.Read(ref _serverEventGeneration) == generation;

    private async Task PollServerEventsAsync(int generation)
    {
        var reader = _serverEventReader;
        if (reader == null || !IsCurrentEventGeneration(generation))
        {
            return;
        }

        // A slow poll must not stack up behind itself.
        if (Interlocked.Exchange(ref _serverEventPollBusy, 1) == 1)
        {
            return;
        }

        try
        {
            var (events, overflowed) = await reader.PollAsync();

            // Attachment may have moved while that read was outstanding. These events
            // belong to the old process; applying them would corrupt the new one.
            if (!IsCurrentEventGeneration(generation))
            {
                return;
            }

            // The first successful poll only adopts the cursor - it deliberately does
            // not replay history - so anyone already connected produced no event. Seed
            // the roster from a snapshot once, then let events maintain it.
            if (!_serverEventsSeeded && reader.HasSynced && IsCurrentEventGeneration(generation))
            {
                _serverEventsSeeded = true;
                await TryRefreshPlayersFromHookAsync();
            }

            lock (_lock)
            {
                // Re-checked inside the lock: without it the switch can land between
                // the check above and these mutations.
                if (!IsCurrentEventGeneration(generation))
                {
                    return;
                }

                foreach (var serverEvent in events)
                {
                    _playerTracker.ProcessServerEvent(serverEvent);
                }
            }

            if (events.Count > 0 && IsCurrentEventGeneration(generation))
            {
                await TryRefreshPlayersFromHookAsync();
            }

            if (overflowed && IsCurrentEventGeneration(generation))
            {
                _logger.LogWarning("Server event ring overflowed; resyncing from a full player snapshot");
                await TryRefreshPlayersFromHookAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Server event poll failed");
        }
        finally
        {
            Interlocked.Exchange(ref _serverEventPollBusy, 0);
        }
    }

    /// <summary>
    /// Stops output monitoring.
    /// </summary>
    private void StopOutputMonitoring()
    {
        _useInjectedHookAsPrimaryOutput = false;
        StopServerEventPolling();
    }

    /// <summary>
    /// Callback for console monitor output.
    /// </summary>
    private void OnConsoleOutputReceived(string output) =>
        ProcessConsoleLines(output, CurrentAttachmentGeneration);

    // Named apart from the one-argument entry point rather than overloading it:
    // the tests reach OnConsoleOutputReceived by reflection, and an overload makes
    // that lookup ambiguous.
    private void ProcessConsoleLines(string output, int generation)
    {
        if (string.IsNullOrWhiteSpace(output))
            return;

        // Console monitor may send multi-line output, so split by newlines
        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            // Everything that mutates controller state happens under the same lock
            // the attachment switch takes, re-checking the generation first, so a
            // switch cannot land between the check and the mutation.
            lock (_lock)
            {
                if (!IsCurrentAttachmentGeneration(generation))
                {
                    return;
                }

                _outputBuffer.Enqueue((DateTime.Now, line));
                while (_outputBuffer.Count > MaxBufferSize)
                {
                    _outputBuffer.TryDequeue(out _);
                }

                ReportChatLineWithoutRecord(line);
                _trackChangeTracker.ProcessLogLine(line);
                _serverInfoTracker.ProcessLogLine(line);
            }

            // Fan-out to subscribers stays outside the lock: these reach the UI
            // dispatcher and an HTTP sender, and holding a controller lock across
            // either invites a deadlock.
            NotifyConsoleOutput(line);
            _consoleLogSender.AddLog(line);
        }
    }

    /// <summary>
    /// Chat arrives as a structured record from the injected hook, never by reading
    /// the console line back. The old regex guessed where the sender ended with
    /// [^:]+, so a player whose name contained a colon could never trigger a command.
    ///
    /// This only reports. Console text and records travel the same hook pipe, so a
    /// text fallback could never cover the hook being down - it only ever covered the
    /// record extraction failing, and it is exactly then that a silently dropped
    /// command is most expensive. A line that looks like chat and had no record is
    /// therefore logged, not parsed.
    /// </summary>
    private void ReportChatLineWithoutRecord(string line)
    {
        if (_lastRecordMessage != null && line.Contains(_lastRecordMessage, StringComparison.Ordinal))
        {
            _lastRecordMessage = null;
            return;
        }

        if (!ChatLineShape.IsMatch(line))
        {
            return;
        }

        _logger.LogWarning(
            "A chat line arrived with no structured record from the hook, so no command was raised: {Line}",
            line);
    }

    /// <summary>
    /// Hands a chat command to the worker. Never blocks the caller - the caller is
    /// the thread draining the hook output pipe.
    /// </summary>
    private void EnqueueChatCommand(string playerName, bool isBot, string chatMessage, int generation, long attachmentId)
    {
        EnsureChatCommandWorker();
        if (!_chatCommands.Writer.TryWrite((playerName, isBot, chatMessage, generation, attachmentId)))
        {
            _logger.LogWarning("Dropped chat command from {Player}: queue closed", playerName);
        }
    }

    private void EnsureChatCommandWorker()
    {
        if (_chatCommandWorker != null)
            return;

        lock (_chatWorkerLock)
        {
            _chatCommandWorker ??= Task.Run(ProcessChatCommandQueueAsync);
        }
    }

    private async Task ProcessChatCommandQueueAsync()
    {
        await foreach (var (player, isBot, message, generation, attachmentId) in _chatCommands.Reader.ReadAllAsync())
        {
            // A command accepted before an attachment switch would otherwise run
            // against the new server: the queue is the longest delay between
            // accepting output and acting on it, and a chat command acts.
            if (!IsCurrentAttachmentGeneration(generation))
            {
                _logger.LogDebug(
                    "Discarded queued chat command from {Player}; attachment moved before it ran",
                    player);
                continue;
            }

            // Passing the dequeue check is not enough on its own: the handler can
            // block - on the command semaphore, on a vote - while attachment moves.
            // Publishing it here lets SendCommandAsync refuse at the last moment,
            // when it picks the target PID.
            _dispatchAttachmentId.Value = attachmentId;

            try
            {
                ChatCommandReceived?.Invoke(player, isBot, message);
            }
            catch (Exception ex)
            {
                // One bad command must not kill the worker and silently stop all
                // further chat handling.
                _logger.LogError(ex, "Chat command handler failed for {Player}: {Message}", player, message);
            }
        }
    }

    private void OnInjectedHookOutputReceived(int sourcePid, string output)
    {
        // The line carries the process it was read from, which is the only reliable
        // discriminator: stopping a listener does not await its in-flight callbacks,
        // and the reader's TargetProcessId is cleared on stop and retargeted on the
        // next inject, so it describes the reader's present state rather than this
        // line's origin.
        //
        // This must gate the chat demux below as well as the text fanout: a chat
        // command is the one piece of output that acts on the server rather than
        // merely being displayed.
        // The generation is taken with the PID, in one lock acquisition, and then
        // re-checked at every point that mutates state. Validating here and acting
        // later is not enough: the switch can land in between, and this line would
        // still be applied to the process we have just moved to.
        int? attachedPid;
        int generation;
        long attachmentId;
        lock (_lock)
        {
            attachedPid = _actualServerPid;
            generation = CurrentAttachmentGeneration;
            attachmentId = CurrentAttachmentId;
        }

        // Only drop what can be proved stale. With nothing attached there is no
        // other attachment to confuse this with, and silently discarding output
        // then would hide it in exactly the case it is most needed.
        if (attachedPid != null && sourcePid != attachedPid.Value)
        {
            _logger.LogDebug(
                "Dropped hook output from process {Source}; attached to {Current}",
                sourcePid, attachedPid.Value);
            return;
        }

        // Demuxed ahead of the text fanout. A structured record is not console
        // output: it must not reach the output buffer, the console webhook or the
        // chat regex, and it is consumed whether or not it parsed.
        if (TryProcessHookChatRecord(output, generation, attachmentId))
        {
            return;
        }

        if (ProcessConsoleHookOutput)
        {
            ProcessConsoleLines(output, generation);
        }
    }

    /// <summary>
    /// Handles one structured chat record from the injected hook. Returns true when
    /// the line was a record - including a malformed one, which is dropped rather
    /// than leaked into the console output fanout.
    /// </summary>
    private bool TryProcessHookChatRecord(string output, int generation, long attachmentId)
    {
        if (!HookChatRecord.LooksLikeRecord(output))
        {
            return false;
        }

        var record = HookChatRecord.TryParse(output);
        if (record == null)
        {
            _logger.LogWarning("Discarded a malformed structured chat record from the injected hook");
            return true;
        }

        // Remembered so the console line this record pairs with is not reported as
        // having arrived without one.
        _lastRecordMessage = record.Message;

        // The hook's length caps are byte counts while the game limits chat by
        // characters, so a multi-byte message can be cut mid-sequence. Report the two
        // counts side by side when they disagree, and flag any replacement character
        // that survived decoding - both are things we want to see before deciding
        // whether the caps need raising.
        var messageBytes = System.Text.Encoding.UTF8.GetByteCount(record.Message);
        var nameBytes = System.Text.Encoding.UTF8.GetByteCount(record.PlayerName);
        if (messageBytes != record.Message.Length || nameBytes != record.PlayerName.Length)
        {
            _logger.LogInformation(
                "Non-ASCII chat record: name=[{Name}] ({NameChars} chars / {NameBytes} bytes) " +
                "message=[{Message}] ({MessageChars} chars / {MessageBytes} bytes) replacementChars={Replacements}",
                record.PlayerName,
                record.PlayerName.Length,
                nameBytes,
                record.Message,
                record.Message.Length,
                messageBytes,
                record.PlayerName.Count(c => c == '�') + record.Message.Count(c => c == '�'));
        }

        // Brackets so leading or trailing whitespace is visible: both bugs found
        // during live testing were invisible characters on these two fields.
        _logger.LogDebug(
            "Hook chat record parsed: ring={RingIndex} bot={IsBot} name=[{Name}] message=[{Message}]",
            record.RingIndex,
            record.IsBot,
            record.PlayerName,
            record.Message);

        if (!record.Message.StartsWith('!'))
        {
            return true;
        }

        // No duplicate suppression needed: the hook emits one record per message,
        // where the console echo the old path had to undo did not exist.
        EnqueueChatCommand(record.PlayerName, record.IsBot, record.Message, generation, attachmentId);
        return true;
    }

    public virtual Models.PlayerListResponse GetPlayerList()
    {
        var onlinePlayers = _playerTracker.GetPlayers();
        var (onlineCount, totalCount) = _playerTracker.GetPlayerCount();

        return new Models.PlayerListResponse
        {
            TotalPlayers = onlineCount,
            MaxPlayers = 24, // TODO: Get from config or server query
            Players = onlinePlayers,
            LastUpdated = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Request server info by sending ? command and waiting for response
    /// </summary>
    public virtual async Task<(bool Success, string Message, Models.ServerConfig? Config)> GetServerInfoAsync()
    {
        if (!IsRunning)
        {
            return (false, "Server is not running", null);
        }

        try
        {
            // Send ? command
            var commandResult = await SendCommandAsync("?");
            if (!commandResult.Success)
            {
                return (false, $"Failed to send ? command: {commandResult.Message}", null);
            }

            // Wait for response (max 5 seconds)
            var config = await _serverInfoTracker.RequestServerInfoAsync(TimeSpan.FromSeconds(5));

            return (true, "Server info retrieved successfully", config);
        }
        catch (TimeoutException)
        {
            return (false, "Server info request timed out - the server may not support the ? command", null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve server info");
            return (false, $"Failed to retrieve server info: {ex.Message}", null);
        }
    }

    /// <summary>
    /// Scans for running Wreckfest server processes
    /// </summary>
    public List<Models.ServerProcessInfo> GetRunningWreckfestServers()
    {
        var servers = new List<Models.ServerProcessInfo>();

        try
        {
            var processes = Process.GetProcesses();
            var currentPid = _serverProcess?.Id;

            foreach (var process in processes)
            {
                try
                {
                    // Look for Wreckfest_x64.exe or Wreckfest.exe
                    if (process.ProcessName.Equals("Wreckfest_x64", StringComparison.OrdinalIgnoreCase) ||
                        process.ProcessName.Equals("Wreckfest", StringComparison.OrdinalIgnoreCase))
                    {
                        // Get command line using WMI
                        using (var searcher = new System.Management.ManagementObjectSearcher(
                            $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {process.Id}"))
                        {
                            var results = searcher.Get();
                            foreach (System.Management.ManagementObject obj in results)
                            {
                                var commandLine = obj["CommandLine"]?.ToString() ?? string.Empty;

                                // Only include servers started with -s parameter
                                if (commandLine.Contains(" -s ", StringComparison.OrdinalIgnoreCase))
                                {
                                    var serverInfo = new Models.ServerProcessInfo
                                    {
                                        ProcessId = process.Id,
                                        StartTime = process.StartTime,
                                        ExecutablePath = process.MainModule?.FileName ?? string.Empty,
                                        MemoryUsageMB = process.WorkingSet64 / 1024 / 1024,
                                        IsAttached = process.Id == currentPid,
                                        ConfigFile = ExtractConfigFileName(commandLine)
                                    };

                                    servers.Add(serverInfo);
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Skip processes we can't access
                    _logger.LogTrace(ex, $"Could not access process {process.Id}");
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to scan for Wreckfest servers");
        }

        return servers.OrderBy(s => s.StartTime).ToList();
    }

    /// <summary>
    /// Extracts the config file name from command line arguments
    /// </summary>
    private string ExtractConfigFileName(string commandLine)
    {
        try
        {
            // Look for pattern: -s server_config=xxx.cfg
            var match = System.Text.RegularExpressions.Regex.Match(
                commandLine,
                @"-s\s+server_config=([^\s]+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Failed to extract config file name from command line");
        }

        return "Unknown";
    }

    /// <summary>
    /// Attaches to an existing Wreckfest server process
    /// </summary>
    public async Task<(bool Success, string Message)> AttachToProcessAsync(int processId)
    {
        try
        {
            _logger.LogInformation($"Attempting to attach to process {processId}");

            // Check if process exists and is a Wreckfest server
            var process = Process.GetProcessById(processId);
            if (process == null || process.HasExited)
            {
                return (false, $"Process {processId} not found or has exited");
            }

            if (!process.ProcessName.Equals("Wreckfest_x64", StringComparison.OrdinalIgnoreCase) &&
                !process.ProcessName.Equals("Wreckfest", StringComparison.OrdinalIgnoreCase))
            {
                return (false, $"Process {processId} is not a Wreckfest server");
            }

            // Stop monitoring if we're already monitoring a different process
            // IMPORTANT: Must stop monitoring BEFORE killing the process, otherwise
            // we'll detach from the target process's console and crash it
            if (_actualServerPid.HasValue && _actualServerPid.Value != processId)
            {
                _logger.LogInformation("Stopping monitoring of current process {CurrentPid} before attaching to {NewPid}",
                    _actualServerPid, processId);
                StopOutputMonitoring();
            }

            // Stop any currently running server if it's a different process
            if (_serverProcess != null && !_serverProcess.HasExited && _serverProcess.Id != processId)
            {
                _logger.LogInformation("Stopping current server {CurrentPid} before attaching to {NewPid}",
                    _serverProcess.Id, processId);
                await StopServerAsync();
            }

            lock (_lock)
            {
                _serverProcess = process;
                SetAttachedProcess(processId);
                _startTime = process.StartTime;
            }
            ProcessIdChanged?.Invoke(processId);

            // Initialize trackers
            _playerTracker.Clear();
            _trackChangeTracker.Clear();

            StartOutputMonitoring();

            _logger.LogInformation($"Successfully attached to process {processId}");

            // Send webhook notification
            _ = Task.Run(async () =>
            {
                try
                {
                    await _webhookService.SendServerAttachedAsync(new Models.ServerAttachedEvent
                    {
                        ProcessId = processId,
                        ProcessName = process.ProcessName,
                        StartTime = _startTime ?? DateTime.UtcNow
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to send server attached webhook");
                }
            });

            return (true, $"Attached to process {processId}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to attach to process {processId}");
            return (false, $"Failed to attach: {ex.Message}");
        }
    }

    /// <summary>
    /// Injects the experimental console hook into an existing Wreckfest server process.
    /// </summary>
    public virtual Task<(bool Success, string Message)> InjectConsoleHookAsync(int processId)
    {
        _logger.LogInformation("Console hook injection requested for process {ProcessId}", processId);

        try
        {
            var process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                return Task.FromResult((false, $"Process {processId} has exited"));
            }

            var attachedProcessId = AttachedProcessId;
            if (!attachedProcessId.HasValue)
            {
                return Task.FromResult((false,
                    $"Injection refused: no process is attached; requested process is {processId}."));
            }

            if (attachedProcessId.Value != processId)
            {
                return Task.FromResult((false,
                    $"Injection refused: attached process is {attachedProcessId.Value}; requested process is {processId}."));
            }

            var buildCheck = EnsureSupportedBuild(process);
            if (!buildCheck.Success)
            {
                return Task.FromResult(buildCheck);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to validate target process {ProcessId}", processId);
            return Task.FromResult((false, $"Failed to validate target process {processId}: {ex.Message}"));
        }

        return _injectedHookOutputReader.InjectAsync(processId);
    }

    /// <summary>
    /// Wreckfest reports its build in the console window title, e.g.
    /// "Wreckfest 1.308438 64bit - Dedicated Server". The hook's offsets are
    /// derived against one specific build, so refuse injection when the target
    /// build cannot be verified to match.
    /// </summary>
    private (bool Success, string Message) EnsureSupportedBuild(Process process)
    {
        var build = GetServerBuild(process);
        var supported = _configuration["WreckfestServer:SupportedBuild"]?.Trim();

        if (build == null)
        {
            _logger.LogWarning("Could not read Wreckfest build from process {ProcessId} window title", process.Id);
            return (false,
                $"Injection refused: detected Wreckfest build <unreadable>; supported build is {FormatSupportedBuild(supported)}.");
        }

        if (string.IsNullOrWhiteSpace(supported))
        {
            _logger.LogWarning("Wreckfest build {Build} cannot be verified because no SupportedBuild is configured", build);
            return (false,
                $"Injection refused: detected Wreckfest build {build}; supported build is <not configured>.");
        }

        if (string.Equals(build, supported, StringComparison.Ordinal))
        {
            _logger.LogInformation("Wreckfest build {Build} matches supported build", build);
            return (true, string.Empty);
        }

        var message = $"Injection refused: detected Wreckfest build {build} does not match supported build {supported}.";
        _logger.LogWarning(
            "Wreckfest build {Build} does not match supported build {Supported}; refusing injection because hook offsets may be unsafe",
            build,
            supported);
        return (false, message);
    }

    private static string FormatSupportedBuild(string? supportedBuild) =>
        string.IsNullOrWhiteSpace(supportedBuild) ? "<not configured>" : supportedBuild;

    /// <summary>
    /// Extracts the build number from a Wreckfest console window title.
    /// Returns null when the title is unavailable or does not match.
    /// </summary>
    protected virtual string? GetServerBuild(Process process)
    {
        try
        {
            return ParseServerBuild(process.MainWindowTitle);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Parses the build number out of a Wreckfest console window title, e.g.
    /// "Wreckfest 1.308438 64bit - Dedicated Server" -> "1.308438".
    /// </summary>
    public static string? ParseServerBuild(string? windowTitle)
    {
        if (string.IsNullOrWhiteSpace(windowTitle))
        {
            return null;
        }

        var match = Regex.Match(windowTitle, @"Wreckfest\s+([0-9]+(?:\.[0-9]+)+)");
        return match.Success ? match.Groups[1].Value : null;
    }

    public bool ProcessConsoleHookOutput
    {
        get => _useInjectedHookAsPrimaryOutput;
        set => _useInjectedHookAsPrimaryOutput = value;
    }

    public virtual bool IsConsoleHookConnected => _injectedHookOutputReader.IsHookConnected;

    public static string NormalizeConsoleHookLine(string line)
    {
        return InjectedHookOutputReader.NormalizeLine(line);
    }

    /// <summary>
    /// Gets the config file name for the currently attached server
    /// </summary>
    public string GetCurrentConfigFileName()
    {
        try
        {
            var pid = _serverProcess?.Id;
            if (pid == null)
                return string.Empty;

            using (var searcher = new System.Management.ManagementObjectSearcher(
                $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {pid}"))
            {
                var results = searcher.Get();
                foreach (System.Management.ManagementObject obj in results)
                {
                    var commandLine = obj["CommandLine"]?.ToString() ?? string.Empty;
                    return ExtractConfigFileName(commandLine);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Failed to get config file name for current process");
        }

        return string.Empty;
    }
}

public class ServerStatus
{
    public bool IsRunning { get; set; }
    public int? ProcessId { get; set; }
    public TimeSpan? Uptime { get; set; }
    public string CurrentTrack { get; set; } = string.Empty;
}
