using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WreckfestController.Models;
using WreckfestController.Services.Hook;
using WreckfestController.Services.Publishing;
using WreckfestController.Services.Tracking;

namespace WreckfestController.Services.ServerControl;

public class ServerManager
{
    /// <summary>Only the build-tied <c>WreckfestServer:SupportedBuild</c>; everything a person edits comes from the settings sections.</summary>
    private readonly IConfiguration _configuration;
    private readonly IOptionsMonitor<WreckfestServerSettings> _server;
    private readonly IOptionsMonitor<SteamCmdSettings> _steamCmd;
    /// <summary>
    /// Event raised when console output is received from the server
    /// </summary>
    public event Action<string>? ConsoleOutput;

    /// <summary>
    /// Event raised when experimental injected console hook output is received.
    /// </summary>
    public event Action<string>? ConsoleHookOutput;

    /// <summary>
    /// Event raised when a player sends a chat command (message starting with !), with
    /// the attachment session it arrived under. A handler acts on the server only through
    /// that session, so its replies cannot reach a server attached since.
    /// </summary>
    public event Action<AttachmentSession, string, bool, string>? ChatCommandReceived;

    /// <summary>
    /// Raised once per finished race, with every car's result as the injected hook read
    /// it when the results screen opened.
    /// </summary>
    /// <remarks>
    /// Raised on the thread draining the hook pipe, so a handler must only hand the
    /// record on, as to a queue, and return. Anything that waits - on the database, or
    /// on server output - stalls every hook line behind it, and the hook drops lines
    /// once its own queue fills.
    /// </remarks>
    public event Action<HookRaceRecord>? RaceFinished;

    private readonly object _lock = new();
    private DateTime? _startTime;
    private int? _actualServerPid;
    // A handle on the attached process, held for the whole attachment (#40). Windows
    // does not reuse a PID while any handle to its process is open, so every lookup by
    // _actualServerPid reaches this process or finds it gone - never a later process
    // that inherited the number. Owned by SetAttachedProcess; nothing else uses it.
    private Process? _attachedPin;
    private readonly ILogger<ServerManager> _logger;
    private readonly System.Collections.Concurrent.ConcurrentQueue<(DateTime Timestamp, string Message)> _outputBuffer = new();
    private const int MaxBufferSize = 500;
    private readonly IServerInputWriter _serverInputWriter;
    private readonly IInjectedHookOutputReader _injectedHookOutputReader;

    // Null in tests that construct a ServerManager by hand: no marker is added, and any
    // marker found is another controller's.
    private readonly ControllerInstance? _instance;
    private readonly SemaphoreSlim _commandSendLock = new(1, 1);
    private readonly PlayerTracker _playerTracker;
    private readonly TrackChangeTracker _trackChangeTracker;
    private readonly ServerInfoTracker _serverInfoTracker;
    private readonly IServerEventPublisher _events;
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
    private readonly System.Threading.Channels.Channel<(string Player, bool IsBot, string Message, AttachmentSession Session)> _chatCommands =
        System.Threading.Channels.Channel.CreateUnbounded<(string, bool, string, AttachmentSession)>(
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

    // The current attachment (#40); see AttachmentSession. Replaced, under _lock, only
    // by SetAttachedProcess. Session ids only ever increase, so a session is never
    // mistaken for a later one - a PID cannot serve, since attach A, switch to B and
    // back to A matches again.
    //
    // Deliberately separate from _serverEventGeneration: that is an I/O epoch which
    // also advances when monitoring restarts or the hook is reinjected, so gating
    // dispatch on it would refuse commands after an ordinary reinject.
    private AttachmentSession? _session;
    private CancellationTokenSource? _sessionEnd;
    private long _lastSessionId;
    // Automatic cleanup of an exited process must not cancel its replacement
    // search, but any explicit attach/start/stop must invalidate that search.
    private long _attachmentSelectionId;
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

    /// <summary>
    /// The attachment as it is now, or null with nothing attached. An action captures
    /// this when it begins and passes it to <see cref="SendCommandAsync"/>; deferred work
    /// must use the session it was given, never read this again later.
    /// </summary>
    public virtual AttachmentSession? CurrentSession
    {
        get
        {
            // Notices an exit first, which ends the session, so a dead server's session
            // is never handed out.
            if (GetActualServerPid() == null)
            {
                return null;
            }

            lock (_lock)
            {
                return _session;
            }
        }
    }

    public int? AttachedProcessId => GetActualServerPid();

    /// <summary>
    /// Injection is only allowed into the process that is already attached, so the
    /// tracked PID and the hooked process cannot diverge. A null candidate never
    /// qualifies - comparing two nulls would otherwise read as a match when nothing
    /// is selected and nothing is attached. Never while another injection is still
    /// in progress: it can wait many seconds for a starting server's window.
    /// </summary>
    public bool CanInjectInto(int? candidateProcessId) =>
        candidateProcessId.HasValue && candidateProcessId == AttachedProcessId && !IsInjectionInProgress;

    private int _injectionInProgress;

    /// <summary>True while <see cref="InjectConsoleHookAsync"/> is running.</summary>
    public bool IsInjectionInProgress => Volatile.Read(ref _injectionInProgress) != 0;

    public ServerManager(
        IConfiguration configuration,
        IOptionsMonitor<WreckfestServerSettings> server,
        IOptionsMonitor<SteamCmdSettings> steamCmd,
        ILogger<ServerManager> logger,
        PlayerTracker playerTracker,
        TrackChangeTracker trackChangeTracker,
        ServerInfoTracker serverInfoTracker,
        IServerEventPublisher events)
        : this(
            configuration,
            server,
            steamCmd,
            logger,
            playerTracker,
            trackChangeTracker,
            serverInfoTracker,
            events,
            new InjectedHookInputWriter(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<InjectedHookInputWriter>.Instance),
            new InjectedHookOutputReader(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<InjectedHookOutputReader>.Instance))
    {
    }

    public ServerManager(
        IConfiguration configuration,
        IOptionsMonitor<WreckfestServerSettings> server,
        IOptionsMonitor<SteamCmdSettings> steamCmd,
        ILogger<ServerManager> logger,
        PlayerTracker playerTracker,
        TrackChangeTracker trackChangeTracker,
        ServerInfoTracker serverInfoTracker,
        IServerEventPublisher events,
        IServerInputWriter serverInputWriter)
        : this(
            configuration,
            server,
            steamCmd,
            logger,
            playerTracker,
            trackChangeTracker,
            serverInfoTracker,
            events,
            serverInputWriter,
            new InjectedHookOutputReader(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<InjectedHookOutputReader>.Instance))
    {
    }

    public ServerManager(
        IConfiguration configuration,
        IOptionsMonitor<WreckfestServerSettings> server,
        IOptionsMonitor<SteamCmdSettings> steamCmd,
        ILogger<ServerManager> logger,
        PlayerTracker playerTracker,
        TrackChangeTracker trackChangeTracker,
        ServerInfoTracker serverInfoTracker,
        IServerEventPublisher events,
        IServerInputWriter serverInputWriter,
        IInjectedHookOutputReader injectedHookOutputReader,
        ControllerInstance? instance = null)
    {
        _instance = instance;
        _configuration = configuration;
        _server = server;
        _steamCmd = steamCmd;
        _logger = logger;
        _playerTracker = playerTracker;
        _trackChangeTracker = trackChangeTracker;
        _serverInfoTracker = serverInfoTracker;
        _serverInputWriter = serverInputWriter;
        _injectedHookOutputReader = injectedHookOutputReader;
        _events = events;

        _injectedHookOutputReader.OutputReceivedFrom += OnInjectedHookOutputReceived;
        _injectedHookOutputReader.HookOutputReceived += output =>
        {
            ConsoleHookOutput?.Invoke(output);

            // The hook's own status and warning lines went only to the Process
            // Manager tab, so a diagnostic it printed was invisible to the log
            // everything else is diagnosed from. Mirror just those - not the
            // game's console text, which would double every line.
            if (output.Contains("WreckfestConsoleHook", StringComparison.Ordinal))
            {
                _logger.LogInformation("Hook: {Line}", output);
            }
        };

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
        lock (_lock)
        {
            // Only track by PID - we always start the server through the API
            if (_actualServerPid.HasValue)
            {
                try
                {
                    var process = Process.GetProcessById(_actualServerPid.Value);
                    // Opened while _lock is held and the pin keeps the PID ours, and kept
                    // by the returned instance: a caller acting on it after the lock is
                    // released - a force stop's Kill - still reaches this process, even
                    // if attachment moves and the pin is released meanwhile (#40).
                    _ = process.SafeHandle;
                    if (!process.HasExited)
                    {
                        return process;
                    }
                    else
                    {
                        process.Dispose();
                        _logger.LogWarning("Tracked server process (PID: {PID}) has exited", _actualServerPid.Value);
                        SetAttachedProcess(null, processExited: true);
                        _startTime = null;
                        return null;
                    }
                }
                catch (ArgumentException)
                {
                    // Process doesn't exist
                    _logger.LogWarning("Tracked server process (PID: {PID}) no longer exists", _actualServerPid!.Value);
                    SetAttachedProcess(null, processExited: true);
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
    }

    public virtual async Task<(bool Success, string Message)> StartServerAsync()
    {
        Process? process = null;

        // Starting attaches the new process: not while an injection or another attach runs.
        if (!await _attachmentGate.WaitAsync(AttachmentGateWait))
        {
            return (false, BusyMessage);
        }

        try
        {
            lock (_lock)
            {
                if (IsRunning)
                {
                    return (false, "Server is already running");
                }

                try
                {
                    var server = _server.CurrentValue;
                    var serverPath = server.ServerPath;
                    // This controller's marker goes on every server it starts, so the server can
                    // be told apart from other controllers' in the process list (#201).
                    var serverArguments = server.ServerArguments ?? "";
                    if (_instance is not null)
                    {
                        if (!_instance.TryAddTo(serverArguments, out var marked))
                        {
                            return (false, "The Server Arguments end inside an unclosed quote. Fix them in the desktop app's Configuration.");
                        }

                        serverArguments = marked;
                    }
                    var workingDirectory = server.WorkingDirectory;

                    if (string.IsNullOrEmpty(serverPath) || !File.Exists(serverPath))
                    {
                        // The path stays in the log: the web never sees local paths (#153).
                        _logger.LogWarning("Server executable not found at {ServerPath}", serverPath);
                        return (false, "The server executable was not found. Check the server path in the desktop app's settings.");
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

                    // Not held open means it has already gone; the immediate-exit check
                    // below reports that.
                    if (SetAttachedProcess(process.Id))
                    {
                        _startTime = DateTime.UtcNow;
                        ProcessIdChanged?.Invoke(process.Id);

                        // Start monitoring the server output (console or log file)
                        StartOutputMonitoring();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to start server");
                    return (false, "The server could not be started. The desktop app's log has the details.");
                }
            }
        }
        finally
        {
            _attachmentGate.Release();
        }

        // Check if process exits immediately — done outside the lock to avoid blocking callers during the wait
        if (process.WaitForExit(500))
        {
            var exitCode = process.ExitCode;
            _logger.LogError("Server process exited immediately with code: {ExitCode}", exitCode);
            lock (_lock)
            {
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

            // Notify the web UI
            _ = Task.Run(async () =>
            {
                try
                {
                    await _events.ServerStartedAsync(new Models.ServerStartedEvent
                    {
                        ProcessId = processId,
                        ProcessName = processName,
                        StartTime = _startTime ?? DateTime.UtcNow
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to publish server started event");
                }
            });

            var hook = await InjectAutomaticallyAsync(processId);
            return (true, $"Server started successfully. Process: {processName} (PID: {processId}). {hook}");
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
            var session = CurrentSession;
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
            var commandResult = await SendCommandAsync(session, "exit");
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

                    // Notify the web UI before cleanup
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _events.ServerStoppedAsync(new Models.ServerStoppedEvent
                            {
                                ProcessId = currentPid ?? 0,
                                StopMethod = "Graceful"
                            });
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to publish server stopped event");
                        }
                    });

                    // Clean up
                    lock (_lock)
                    {
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

            // Notify the web UI before cleanup
            _ = Task.Run(async () =>
            {
                try
                {
                    await _events.ServerStoppedAsync(new Models.ServerStoppedEvent
                    {
                        ProcessId = currentPid,
                        StopMethod = "Force"
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to publish server stopped event");
                }
            });

            // Only the attached process is stopped. A second "started" process used to be
            // killed here as well, and after attaching elsewhere that was a different
            // server altogether (#40).
            lock (_lock)
            {
                // Stop output monitoring before cleanup (frees console attachment)
                StopOutputMonitoring();

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
            return (false, "The server could not be stopped. The desktop app's log has the details.");
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
    /// This is faster than stop+start but requires PID detection logic. Restarts the
    /// server attached under <paramref name="session"/> only: a restart asked for
    /// minutes ago must not restart a server attached since (#40).
    /// </summary>
    public virtual async Task<(bool Success, string Message)> RestartServerViaCommandAsync(AttachmentSession? session)
    {
        if (session == null)
            return (false, "Server is not running");

        try
        {
            using var originalProcess = GetActualServerProcess();
            if (originalProcess == null)
                return (false, "Server is not running");
            // Early and cheap; the session is checked again under _lock below.
            if (originalProcess.Id != session.ProcessId)
                return (false, "Attachment changed before restart.");

            // Hold the original process handle so its exit time remains available
            // after /restart, even when normal status polling clears the attachment.
            _ = originalProcess.Handle;
            var oldPid = originalProcess.Id;
            var original = ReadRestartProcessIdentity(oldPid);
            if (original == null || !original.IsComplete)
                return (false, "Cannot restart safely: original server identity is unavailable.");
            if (originalProcess.HasExited ||
                Math.Abs((original.CreatedUtc - originalProcess.StartTime.ToUniversalTime()).TotalMilliseconds) > 1)
                return (false, "Cannot restart safely: original process changed during identity capture.");

            long selectionId;
            lock (_lock)
            {
                if (_actualServerPid != oldPid || _session?.Id != session.Id)
                    return (false, "Attachment changed before restart.");
                selectionId = _attachmentSelectionId;
            }

            _logger.LogInformation("Starting server restart via /restart command");

            // Step 1: Get all current Wreckfest*.exe PIDs
            var oldPids = GetAllWreckfestPids();
            _logger.LogInformation("Current Wreckfest PIDs before restart: {PIDs}", string.Join(", ", oldPids));

            // Step 2: Stop output monitoring before restart (old process will be killed)
            _logger.LogDebug("Stopping output monitoring before restart");
            lock (_lock)
            {
                if (_attachmentSelectionId != selectionId)
                    return (false, "Attachment changed before restart.");
                StopOutputMonitoring();
            }

            // Step 3: Send /restart command
            var requestedUtc = DateTime.UtcNow;
            var commandResult = await SendCommandAsync(session, "/restart");
            if (!commandResult.Success)
            {
                return (false, $"Failed to send restart command: {commandResult.Message}");
            }

            _logger.LogInformation("Restart command sent, waiting for server to restart...");

            // Step 4: Wait for the old process to exit and its replacement to appear. Not for
            // console text: the replacement has no hook yet, so it cannot send any (#201).
            var newProcessPids = await WaitForReplacementAsync(originalProcess, oldPids, RestartReplacementTimeout);

            if (newProcessPids.Count == 0)
            {
                _logger.LogError("No new Wreckfest process detected after restart");
                return (false, "Server restart failed: No new process detected. The server may have failed to restart.");
            }

            if (!originalProcess.HasExited)
                return (false, "Server restart failed: original process is still running.");

            var candidates = newProcessPids.Select(ReadRestartProcessIdentity).ToList();
            var selection = RestartProcessIdentity.SelectReplacement(
                original, oldPids, candidates, requestedUtc, originalProcess.ExitTime.ToUniversalTime());
            if (selection.Process is not { } replacement)
                return (false, $"Server restart failed: {selection.Error}");

            // Re-read before attachment so an exited/reused PID cannot inherit the
            // selected process's identity. Keep the handle open through the change.
            using var replacementProcess = Process.GetProcessById(replacement.ProcessId);
            _ = replacementProcess.Handle;
            if (ReadRestartProcessIdentity(replacement.ProcessId) != replacement || replacementProcess.HasExited)
                return (false, "Server restart failed: replacement process changed during detection.");

            var newPid = replacement.ProcessId;

            // A manual INJECT into the old process may still be running; let it finish
            // rather than switch the attachment under it.
            if (!await _attachmentGate.WaitAsync(AttachmentGateWait))
                return (false, "Server restart failed: a hook injection did not finish; replacement was not attached.");
            try
            {
                lock (_lock)
                {
                    if (_attachmentSelectionId != selectionId)
                        return (false, "Attachment changed during restart; replacement was not attached.");
                    StopHookOutputListener();
                    ClearProcessScopedState();
                    if (!SetAttachedProcess(newPid))
                        return (false, "Server restart failed: the replacement process exited before it could be attached.");
                }
            }
            finally
            {
                _attachmentGate.Release();
            }
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

            _logger.LogInformation("Server restarted successfully via /restart command. New PID: {PID} (was {OldPID})",
                newPid, oldPid != 0 ? oldPid : (int?)null);

            // Restart console monitoring for the new process
            _logger.LogDebug("Restarting output monitoring for new process");
            StartOutputMonitoring();

            var hook = await InjectAutomaticallyAsync(newPid);

            // Notify the web UI
            _ = Task.Run(async () =>
            {
                try
                {
                    await _events.ServerRestartedAsync(new Models.ServerRestartedEvent
                    {
                        OldProcessId = oldPid != 0 ? oldPid : null,
                        NewProcessId = newPid,
                        RestartMethod = "Command"
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to publish server restarted event");
                }
            });

            return (true, $"Server restarted successfully via /restart command. New PID: {newPid}. {hook}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restart server via /restart command");
            return (false, "The server could not be restarted. The desktop app's log has the details.");
        }
    }

    /// <summary>
    /// At controller startup (#201): attaches to this controller's own running server, found
    /// by its marker, and injects the hook. Exactly one server must carry the marker; with
    /// none or several, nothing is attached, as before, and the reason is logged.
    /// </summary>
    public virtual async Task<(bool Success, string Message)> ReattachOwnServerAsync()
    {
        if (_instance is null)
        {
            return (false, "This controller has no id, so it cannot recognise its server.");
        }

        // Read before the scan, which takes a while: an admin who attaches or starts a server
        // meanwhile wins, and this attach then backs off.
        var selection = CurrentSelectionId;
        if (AttachedProcessId is { } attached)
        {
            return (false, $"Already attached to process {attached}.");
        }

        var own = GetRunningWreckfestServers()
            .Where(p => p.IsConfiguredServer && p.Owner == Models.ServerOwner.ThisController)
            .Select(p => p.ProcessId)
            .ToList();
        if (own.Count != 1)
        {
            var reason = own.Count == 0
                ? $"No running server carries this controller's id ({_instance.Id}); nothing to reattach to."
                : $"{own.Count} running servers carry this controller's id ({_instance.Id}): processes {string.Join(", ", own)}. Attach to one by hand.";
            _logger.LogInformation("{Reason}", reason);
            return (false, reason);
        }

        var pid = own[0];
        var attach = AttachToConfiguredServer(pid, selection);
        if (!attach.Success)
        {
            _logger.LogWarning("Could not reattach to this controller's server, process {ProcessId}: {Message}", pid, attach.Message);
            return attach;
        }

        var hook = await InjectAutomaticallyAsync(pid);
        _logger.LogInformation("Reattached to this controller's server, process {ProcessId}. {Hook}", pid, hook);
        return (true, $"Reattached to this controller's server, process {pid}. {hook}");
    }

    /// <summary>How long <c>/restart</c> may take to replace the process.</summary>
    protected virtual TimeSpan RestartReplacementTimeout => TimeSpan.FromSeconds(30);

    /// <summary>
    /// Polls until <paramref name="original"/> has exited and a Wreckfest process not in
    /// <paramref name="oldPids"/> exists, or <paramref name="timeout"/> passes. Returns the new
    /// PIDs, empty on timeout; the caller still checks which of them is the replacement.
    /// </summary>
    private async Task<List<int>> WaitForReplacementAsync(Process original, List<int> oldPids, TimeSpan timeout)
    {
        var waited = Stopwatch.StartNew();
        var newPids = new List<int>();
        while (waited.Elapsed < timeout)
        {
            await Task.Delay(500);
            if (!original.HasExited)
            {
                continue;
            }

            newPids = GetAllWreckfestPids().Except(oldPids).ToList();
            if (newPids.Count > 0)
            {
                break;
            }
        }

        _logger.LogInformation(
            "New Wreckfest PIDs after restart: {PIDs} ({Seconds:0.0}s)",
            newPids.Count == 0 ? "none" : string.Join(", ", newPids),
            waited.Elapsed.TotalSeconds);
        return newPids;
    }

    /// <summary>
    /// Injects the hook into a server this controller has just started or restarted into
    /// (#201): nothing works without it, and a cup's scheduled restart has nobody at hand to
    /// press INJECT. One retry, because an inject can report failure although the DLL did
    /// load, and the retry then finds it loaded. A failure never fails the start: the
    /// returned text says the hook is missing, and the manual INJECT stays available.
    /// </summary>
    /// <summary>Ends a start or restart message when the automatic inject failed.</summary>
    public const string HookMissingNote = "The hook could not be injected automatically; use INJECT.";

    private async Task<string> InjectAutomaticallyAsync(int processId)
    {
        const int attempts = 2;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            var (success, message) = await InjectConsoleHookAsync(processId);
            if (success)
            {
                return "Hook injected.";
            }

            _logger.LogWarning(
                "Automatic hook injection into process {ProcessId} failed (attempt {Attempt} of {Attempts}): {Message}",
                processId,
                attempt,
                attempts,
                message);
        }

        return HookMissingNote;
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

    private RestartProcessIdentity? ReadRestartProcessIdentity(int pid)
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                $"SELECT ProcessId, ParentProcessId, ExecutablePath, CommandLine, CreationDate FROM Win32_Process WHERE ProcessId = {pid}");
            using var results = searcher.Get();
            foreach (System.Management.ManagementObject process in results)
            {
                using (process)
                {
                    var creationDate = process["CreationDate"]?.ToString();
                    if (string.IsNullOrWhiteSpace(creationDate))
                        return null;
                    return new RestartProcessIdentity(
                        Convert.ToInt32(process["ProcessId"]), Convert.ToInt32(process["ParentProcessId"]),
                        process["ExecutablePath"]?.ToString() ?? string.Empty,
                        process["CommandLine"]?.ToString() ?? string.Empty,
                        System.Management.ManagementDateTimeConverter.ToDateTime(creationDate).ToUniversalTime());
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not verify restart identity for PID {Pid}", pid);
        }
        return null;
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
        var steamCmd = _steamCmd.CurrentValue;
        var steamCmdPath = steamCmd.SteamCmdPath;
        var appId = steamCmd.WreckfestAppId;
        var installDir = _server.CurrentValue.WorkingDirectory;

        if (string.IsNullOrEmpty(steamCmdPath) || !File.Exists(steamCmdPath))
        {
            _logger.LogWarning("SteamCMD not found at {SteamCmdPath}", steamCmdPath);
            return (false, "SteamCMD was not found. Check the SteamCMD path in the desktop app's settings.");
        }

        if (string.IsNullOrEmpty(appId))
        {
            return (false, "SteamCmd Wreckfest App ID not configured in settings.");
        }

        if (string.IsNullOrEmpty(installDir) || !Directory.Exists(installDir))
        {
            _logger.LogWarning("Working directory for the update not found: {InstallDir}", installDir);
            return (false, "The server's working directory was not found. Check it in the desktop app's settings.");
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
            return (false, "The server could not be updated. The desktop app's log has the details.");
        }
    }

    /// <summary>
    /// Sends a console command through the hook to the server attached under
    /// <paramref name="session"/>, and to no other. There is deliberately no overload
    /// that picks the current attachment: an action captures
    /// <see cref="CurrentSession"/> when it begins, and deferred work passes the session
    /// it was given. A null session - nothing was attached - is refused.
    /// </summary>
    public virtual async Task<(bool Success, string Message)> SendCommandAsync(AttachmentSession? session, string command)
    {
        command = command.TrimEnd('\r', '\n');
        if (string.IsNullOrWhiteSpace(command))
        {
            return (false, "Command cannot be empty");
        }

        // IsRunning notices an exit, which ends the session, so the check below refuses
        // a session whose process has gone.
        if (session == null || !IsRunning)
        {
            return (false, "Server is not running");
        }

        await _commandSendLock.WaitAsync();
        try
        {
            // Checked after the semaphore, which is the last moment before the command
            // goes out - and the moment that matters, because waiting for this lock is
            // exactly when attachment can move underneath a caller that already
            // validated. The PID comes from the session, so it is the session's process
            // or nothing, however the attachment moves after this.
            int processId;
            lock (_lock)
            {
                if (_session?.Id != session.Id)
                {
                    _logger.LogWarning(
                        "Refused to send {Command}: accepted under attachment session {Expected}, now {Current}",
                        command, session.Id, _session?.Id.ToString() ?? "none");
                    return (false,
                        $"Attachment moved before the command could be sent (session {session.Id}, now {_session?.Id.ToString() ?? "none"})");
                }

                processId = session.ProcessId;
            }

            var result = await _serverInputWriter.SendCommandAsync(command, processId);

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
            return (false, "The command could not be sent. The desktop app's log has the details.");
        }
        finally
        {
            _commandSendLock.Release();
        }
    }

    /// <summary>
    /// Reads the server's session state through the hook, from the process attached
    /// under <paramref name="attachment"/>. Null when the hook is unavailable, the read
    /// fails, or that attachment has been replaced - before the read or while it was in
    /// flight - so callers fail open and never act on another server's state (#40).
    /// </summary>
    public virtual async Task<HookSessionState?> ReadHookSessionAsync(AttachmentSession? attachment)
    {
        if (!IsCurrentSession(attachment) || _serverInputWriter is not IHookSessionReader reader)
        {
            return null;
        }

        try
        {
            var result = await reader.ReadSessionStateAsync(attachment!.ProcessId);
            return result.Success && IsCurrentSession(attachment) ? result.Session : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Hook session read failed");
            return null;
        }
    }

    /// <summary>
    /// Reads module-relative memory through the hook, from the process attached under
    /// <paramref name="attachment"/>. Returns null when the hook is unavailable or that
    /// attachment has been replaced, so callers can fail open rather than treating
    /// "cannot read" as a definite state.
    /// </summary>
    public virtual async Task<byte[]?> ReadHookMemoryAsync(AttachmentSession? attachment, uint rva, int size)
    {
        if (!IsCurrentSession(attachment) || _serverInputWriter is not IHookMemoryReader reader)
        {
            return null;
        }

        try
        {
            var result = await reader.ReadModuleMemoryAsync(attachment!.ProcessId, rva, size);
            return result.Success && IsCurrentSession(attachment) ? result.Data : null;
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
        // Held open from here, so the process checked is the process attached.
        if (OpenHeld(pid) is not { } process)
        {
            return (false, $"Process {pid} does not exist");
        }

        using (process)
        {
            return AttachToExistingProcess(process);
        }
    }

    /// <summary>
    /// Attaches, from the web API, only to the configured dedicated server
    /// (<see cref="CheckConfiguredServerProcess"/>). The check and the attach hold one open
    /// native handle to the process: while it is open Windows cannot reuse the PID, so the
    /// process that passed the check is the one attached - not another that took its PID
    /// in between.
    /// </summary>
    public virtual (bool Success, string Message) AttachToConfiguredServer(int pid) =>
        AttachToConfiguredServer(pid, onlyIfSelection: null);

    /// <param name="onlyIfSelection">
    /// When set, attach only if no attachment was chosen since this
    /// <see cref="CurrentSelectionId"/> was read - checked under the lock that every
    /// attachment change takes - so a background attach never overrides an admin's choice.
    /// </param>
    public virtual (bool Success, string Message) AttachToConfiguredServer(int pid, long? onlyIfSelection)
    {
        if (OpenHeld(pid) is not { } process)
        {
            return (false, NotAnInspectableServer(pid));
        }

        using (process)
        {
            var check = CheckConfiguredServer(process);
            return check.Allowed ? AttachToExistingProcess(process, onlyIfSelection) : (false, check.Reason);
        }
    }

    /// <summary>Changes whenever an attachment is chosen: an attach, a start or a restart.</summary>
    public long CurrentSelectionId => Interlocked.Read(ref _attachmentSelectionId);

    /// <summary>
    /// One at a time: an injection, or a change of attachment (attach, start, restart), from
    /// the moment it begins until it is done - side effects included. An injection checks its
    /// target and loads the DLL inside it, so the process it checked is still the attached one
    /// when the DLL goes in (#201).
    /// </summary>
    private readonly SemaphoreSlim _attachmentGate = new(1, 1);

    private const string BusyMessage = "A hook injection is in progress, or another attach; try again in a moment.";

    /// <summary>
    /// How long a start or restart waits for the gate. An injection takes at most about 30
    /// seconds: up to 20 for the server's window, 10 for the DLL to load.
    /// </summary>
    protected virtual TimeSpan AttachmentGateWait => TimeSpan.FromSeconds(60);

    /// <summary>
    /// The process with a native handle opened and held (Process.SafeHandle keeps it until
    /// the Process is disposed). Process.GetProcessById alone holds nothing: each property
    /// would open and close its own handle, and the PID could change owner in between.
    /// Null when the process is gone or cannot be opened - another user's, say.
    /// </summary>
    private static Process? OpenHeld(int pid)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(pid);
            _ = process.SafeHandle;
            return process;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            process?.Dispose();
            return null;
        }
    }

    private static string NotAnInspectableServer(int pid) =>
        $"Process {pid} is not a running Wreckfest dedicated server that the controller can inspect.";

    private (bool Success, string Message) AttachToExistingProcess(Process process, long? onlyIfSelection = null)
    {
        var pid = process.Id;
        if (!_attachmentGate.Wait(0))
        {
            return (false, BusyMessage);
        }

        // The server attached until now, if it keeps running after this attach.
        Process? released = null;
        try
        {
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
                if (onlyIfSelection is { } expected && _attachmentSelectionId != expected)
                {
                    return (false, "The attachment changed meanwhile; left as it is.");
                }

                // Pinned before anything is torn down: an attach that cannot be held
                // open fails here and leaves the current attachment as it was.
                var pin = OpenHeld(pid);
                if (pin == null || pin.HasExited)
                {
                    pin?.Dispose();
                    return (false, $"Process {pid} has already exited");
                }

                if (_actualServerPid is int previous && previous != pid)
                {
                    released = OpenHeld(previous);
                }

                StopOutputMonitoring();
                StopHookOutputListener();
                ClearProcessScopedState();

                SetAttachedProcess(pid, pin: pin);

                _startTime = process.StartTime.ToUniversalTime();
            }

            ProcessIdChanged?.Invoke(pid);
            _logger.LogInformation("Attached to existing server process (PID: {PID}, Name: {Name})", pid, process.ProcessName);

            // Start monitoring the attached process
            StartOutputMonitoring();

            if (released != null)
            {
                lock (_lock)
                {
                    var releasedPid = released.Id;
                    var previous = _hookUnloads.GetValueOrDefault(releasedPid) ?? Task.CompletedTask;
                    var unload = UnloadReleasedHookAsync(previous, released);
                    _hookUnloads[releasedPid] = unload;

                    // The unload holds the process open until it ends; after that the PID
                    // can be reused, so the entry must go with it.
                    _ = unload.ContinueWith(done =>
                    {
                        lock (_lock)
                        {
                            if (_hookUnloads.TryGetValue(releasedPid, out var current) && current == done)
                            {
                                _hookUnloads.Remove(releasedPid);
                            }
                        }
                    }, TaskScheduler.Default);
                }

                released = null;
            }

            return (true, $"Attached to process {pid} ({process.ProcessName})");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to attach to process {PID}", pid);
            return (false, $"Process {pid} could not be attached to; see the desktop app's log.");
        }
        finally
        {
            released?.Dispose();
            _attachmentGate.Release();
        }
    }

    /// <summary>
    /// The hook unload running or last run for each released process, by PID. An injection
    /// into that process waits for it, so the hook is never loaded into a process while it
    /// is being taken out. While an unload runs it holds its process open, so the PID
    /// still names that process. Guarded by _lock.
    /// </summary>
    private readonly Dictionary<int, Task> _hookUnloads = new();

    /// <summary>
    /// Takes the hook out of a server the controller let go of while it keeps running: an
    /// attach to another process does not stop the one attached before. Left in, its
    /// patches stay in the game with nothing reading them, and a later injection would
    /// reuse that copy instead of loading the controller's own.
    /// </summary>
    /// <remarks>
    /// Not under the attachment gate: an unload takes seconds, and attaching back meanwhile
    /// must not be refused as busy. <paramref name="released"/> is held open throughout, so
    /// the PID cannot change owner. Skipped when the process has exited or has been
    /// attached again by the time it runs. Never throws, so the chain keeps going.
    /// </remarks>
    private async Task UnloadReleasedHookAsync(Task previous, Process released)
    {
        using (released)
        {
            var pid = released.Id;
            try
            {
                // An unload of this process released earlier, and attached again since.
                await previous;
                // Off the caller, which holds _lock.
                await Task.Yield();

                if (released.HasExited || AttachedProcessId == pid)
                {
                    return;
                }

                var (unloaded, message) = await _injectedHookOutputReader.UnloadAsync(pid);
                if (unloaded)
                {
                    _logger.LogInformation("Console hook taken out of released server process {PID}: {Message}", pid, message);
                }
                else
                {
                    _logger.LogWarning("Console hook left in released server process {PID}: {Message}", pid, message);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not take the console hook out of released server process {PID}", pid);
            }
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
    //
    // Each call ends the current attachment session and begins the next: attach,
    // detach and process replacement all come through here, while a reinject or a
    // monitoring restart does not, and so keeps the session.
    //
    // Returns false, changing nothing, when the process to attach cannot be held open
    // - it has exited, or cannot be opened. An attachment is never made without its pin.
    //
    // A caller that must not tear anything down for an attach that will fail opens the
    // pin itself first (OpenHeld) and hands it over in pin; it is owned here from then.
    private bool SetAttachedProcess(int? pid, bool processExited = false, Process? pin = null)
    {
        CancellationTokenSource? ended;
        Process? unpinned;
        lock (_lock)
        {
            // Every attach path still holds its own handle on the process here, so the
            // PID cannot have changed owner before this pin takes over.
            if (pid is int pinned && pin == null && (pin = OpenHeld(pinned)) == null)
            {
                _logger.LogWarning("Process {PID} could not be held open, so it was not attached", pinned);
                return false;
            }

            unpinned = _attachedPin;
            _attachedPin = pin;
            _actualServerPid = pid;
            if (!processExited)
                Interlocked.Increment(ref _attachmentSelectionId);

            ended = _sessionEnd;
            if (pid is int processId)
            {
                _sessionEnd = new CancellationTokenSource();
                _session = new AttachmentSession(++_lastSessionId, processId, _sessionEnd.Token);
            }
            else
            {
                _sessionEnd = null;
                _session = null;
            }
        }

        // Every caller holds _lock, and cancellation runs callbacks, so they run on the
        // thread pool rather than here. IsCancellationRequested is set before this
        // returns. The source is never disposed: a token from a disposed source can
        // throw, and late work still reads it.
        _ = ended?.CancelAsync();
        unpinned?.Dispose();
        return true;
    }

    // True while attachment is still the current one. Cheap enough for every hook read:
    // it opens no process handle. A read from a process that has exited fails on its own.
    private bool IsCurrentSession(AttachmentSession? attachment)
    {
        if (attachment == null || attachment.Ended.IsCancellationRequested)
        {
            return false;
        }

        lock (_lock)
        {
            return _session?.Id == attachment.Id;
        }
    }

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
            var server = _server.CurrentValue;
            var serverArgs = server.ServerArguments ?? "";
            var workingDir = server.WorkingDirectory;

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
                        // Log file path is relative to working directory, and must stay
                        // in it: server_config.cfg is a file other code writes, so an
                        // absolute or ../ path here must not make the log viewer return
                        // any file on the PC.
                        var root = Path.GetFullPath(workingDir);
                        var logPath = Path.GetFullPath(Path.Combine(root, logFileName));
                        var rootWithSeparator = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
                        if (!logPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogWarning(
                                "Ignoring log={LogFile} in {Config}: the log file must be inside the server's working directory",
                                logFileName,
                                configFilePath);
                            return null;
                        }

                        return logPath;
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
            logFilePath = _server.CurrentValue.LogFilePath;
        }

        if (string.IsNullOrEmpty(logFilePath))
        {
            return (false, "LogFilePath not found in server_config.cfg or appsettings.json", null, null);
        }

        if (!File.Exists(logFilePath))
        {
            _logger.LogWarning("Log file not found at {LogFilePath}", logFilePath);
            return (false, "The log file was not found. Check the log file path in the desktop app's settings.", logFilePath, null);
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
            return (false, "The log file could not be read. The desktop app's log has the details.", logFilePath, null);
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
        // One step under the lock attachment changes take. The session, the reader
        // bound to it and the generation the timer carries must all describe the same
        // attachment: a restart starting monitoring late, after another attach, would
        // otherwise pair the old session's reader with the new generation, and every
        // poll would then pass its generation check yet read nothing.
        lock (_lock)
        {
            StopServerEventPolling();

            // Polling reads only from the attachment it was started for: a poll still
            // in flight after a switch reads nothing rather than the next server's ring.
            var attachment = _session;
            _serverEventReader = new ServerEventReader(
                (rva, size) => ReadHookMemoryAsync(attachment, rva, size),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ServerEventReader>.Instance);

            // Every poll carries the generation it was started under. Disposing the
            // timer does not cancel a poll already awaiting a hook read, so the
            // generation is what lets that poll notice its results belong to a process
            // we have since stopped watching, and drop them instead of feeding another
            // process's tracker.
            var generation = Volatile.Read(ref _serverEventGeneration);

            _serverEventTimer = new System.Threading.Timer(
                _ => _ = PollServerEventsAsync(generation),
                null,
                ServerEventPollInterval,
                ServerEventPollInterval);
        }
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
    internal int CurrentAttachmentGeneration => Volatile.Read(ref _serverEventGeneration);

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
            // dispatcher and the SignalR hub, and holding a controller lock across
            // either invites a deadlock.
            NotifyConsoleOutput(line);
            _events.AddConsoleLog(line);
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
    private void EnqueueChatCommand(string playerName, bool isBot, string chatMessage, AttachmentSession session)
    {
        EnsureChatCommandWorker();
        if (!_chatCommands.Writer.TryWrite((playerName, isBot, chatMessage, session)))
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
        await foreach (var (player, isBot, message, session) in _chatCommands.Reader.ReadAllAsync())
        {
            // A command accepted before an attachment switch would otherwise run
            // against the new server: the queue is the longest delay between
            // accepting output and acting on it, and a chat command acts. Checked
            // against the session rather than the I/O generation, so a reinject while
            // the command waited does not lose it.
            if (session.Ended.IsCancellationRequested)
            {
                _logger.LogDebug(
                    "Discarded queued chat command from {Player}; attachment moved before it ran",
                    player);
                continue;
            }

            // Passing the dequeue check is not enough on its own: the handler can
            // block - on the command semaphore, on a vote - while attachment moves.
            // It acts only through this session, so SendCommandAsync refuses at the
            // last moment instead.
            try
            {
                ChatCommandReceived?.Invoke(session, player, isBot, message);
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
        AttachmentSession? session;
        lock (_lock)
        {
            attachedPid = _actualServerPid;
            generation = CurrentAttachmentGeneration;
            session = _session;
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
        // output: it must not reach the output buffer, the web UI console or the
        // chat regex, and it is consumed whether or not it parsed.
        if (TryProcessHookChatRecord(output, session) || TryProcessHookRaceRecord(output, generation))
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
    private bool TryProcessHookChatRecord(string output, AttachmentSession? session)
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

        // Nothing is attached, so there is no server a reply could go to.
        if (session == null)
        {
            _logger.LogDebug("Dropped chat command from {Player}: nothing is attached", record.PlayerName);
            return true;
        }

        // No duplicate suppression needed: the hook emits one record per message,
        // where the console echo the old path had to undo did not exist.
        EnqueueChatCommand(record.PlayerName, record.IsBot, record.Message, session);
        return true;
    }

    /// <summary>
    /// Handles one race results record from the injected hook. Returns true when the
    /// line was a record, including a malformed one, which is dropped rather than leaked
    /// into the console output fanout.
    /// </summary>
    internal bool TryProcessHookRaceRecord(string output, int generation)
    {
        if (!HookRaceRecord.LooksLikeRecord(output))
        {
            return false;
        }

        var record = HookRaceRecord.TryParse(output);
        if (record == null)
        {
            // The raw line is the evidence for what changed, so keep it.
            _logger.LogWarning("Discarded a malformed race results record from the injected hook: {Record}", output);
            return true;
        }

        var winner = record.Cars.Where(car => car.Position != null).MinBy(car => car.Position);
        _logger.LogInformation(
            "Race finished on {Track}: {Cars} cars, winner {Winner} ({Time} ms) in {Vehicle}",
            record.TrackId,
            record.Cars.Count,
            winner?.Name,
            winner?.TimeMs,
            winner?.VehicleName);

        // Checked as late as possible, as chat does: a record read from the previous
        // server must not be reported as a race on the one attached now.
        if (!IsCurrentAttachmentGeneration(generation))
        {
            _logger.LogInformation("Discarded a race result from the injected hook; attachment moved before it was reported");
            return true;
        }

        try
        {
            RaceFinished?.Invoke(record);
        }
        catch (Exception ex)
        {
            // A failing subscriber must not take down the pipe reader.
            _logger.LogError(ex, "A race results subscriber failed");
        }

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
            var commandResult = await SendCommandAsync(CurrentSession, "?");
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
            return (false, "The server info could not be read. The desktop app's log has the details.", null);
        }
    }

    /// <summary>
    /// Whether <paramref name="executable"/> is the server <paramref name="configured"/>
    /// names: the same file, compared as full paths, ignoring case as Windows does. False
    /// when either is missing or not a valid path.
    /// </summary>
    public static bool IsConfiguredServerPath(string? executable, string? configured)
    {
        if (string.IsNullOrWhiteSpace(executable) || string.IsNullOrWhiteSpace(configured))
        {
            return false;
        }

        try
        {
            return string.Equals(Path.GetFullPath(executable), Path.GetFullPath(configured), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether <paramref name="pid"/> may be attached to or injected into from the web API:
    /// only a running Wreckfest dedicated server whose executable is the configured
    /// ServerPath - not the game client, another install's server, or any other process.
    /// The reason never includes a path.
    /// </summary>
    public virtual (bool Allowed, string Reason) CheckConfiguredServerProcess(int pid)
    {
        using var process = OpenHeld(pid);
        return process is null ? (false, NotAnInspectableServer(pid)) : CheckConfiguredServer(process);
    }

    /// <summary>
    /// The check behind <see cref="CheckConfiguredServerProcess"/>, on a process whose handle
    /// is held (<see cref="OpenHeld"/>): the lookups by PID below - the module, the command
    /// line - then reach this same process.
    /// </summary>
    private (bool Allowed, string Reason) CheckConfiguredServer(Process process)
    {
        var pid = process.Id;
        var notAServer = NotAnInspectableServer(pid);
        if (string.IsNullOrWhiteSpace(_server.CurrentValue.ServerPath))
        {
            return (false, "No server path is set in the desktop app's Configuration, so no process can be confirmed as the server.");
        }

        string executable;
        try
        {
            if (process.HasExited || !IsWreckfestProcessName(process.ProcessName))
            {
                return (false, notAServer);
            }

            // Read through the open handle: another user's or an elevated process cannot be
            // inspected, and is refused rather than guessed at.
            executable = process.MainModule?.FileName ?? string.Empty;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return (false, notAServer);
        }

        var commandLine = CommandLineOf(pid);
        if (!WindowsCommandLine.HasServerFlag(commandLine))
        {
            return (false, notAServer);
        }

        if (!IsConfiguredServerPath(executable, _server.CurrentValue.ServerPath))
        {
            return (false, $"Process {pid} is a Wreckfest server from another install, not the one set in the desktop app's Configuration.");
        }

        return OwnerOf(commandLine) == Models.ServerOwner.OtherController
            ? (false, $"Process {pid} belongs to another controller on this PC.")
            : (true, string.Empty);
    }

    /// <summary>Whose server a command line starts, by its <c>-wfc_controller</c> marker.</summary>
    private Models.ServerOwner OwnerOf(string? commandLine) =>
        WindowsCommandLine.ControllerMarker(commandLine) switch
        {
            null => Models.ServerOwner.Unmarked,
            var id when string.Equals(id, _instance?.Id, StringComparison.OrdinalIgnoreCase) => Models.ServerOwner.ThisController,
            _ => Models.ServerOwner.OtherController,
        };

    private static bool IsWreckfestProcessName(string name) =>
        name.Equals("Wreckfest_x64", StringComparison.OrdinalIgnoreCase) || name.Equals("Wreckfest", StringComparison.OrdinalIgnoreCase);

    /// <summary>A process's command line, from WMI; null when it cannot be read.</summary>
    private string? CommandLineOf(int pid)
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {pid}");
            foreach (System.Management.ManagementObject result in searcher.Get())
            {
                using (result)
                {
                    return result["CommandLine"]?.ToString();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Could not read the command line of process {PID}", pid);
        }

        return null;
    }

    /// <summary>
    /// Scans for running Wreckfest server processes
    /// </summary>
    public virtual List<Models.ServerProcessInfo> GetRunningWreckfestServers()
    {
        var servers = new List<Models.ServerProcessInfo>();
        var configuredPath = _server.CurrentValue.ServerPath;

        try
        {
            var processes = Process.GetProcesses();
            // The pid attachment sets: attach from the API or the desktop, or a start.
            var currentPid = _actualServerPid;

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
                                if (WindowsCommandLine.HasServerFlag(commandLine))
                                {
                                    var executable = process.MainModule?.FileName ?? string.Empty;
                                    var serverInfo = new Models.ServerProcessInfo
                                    {
                                        ProcessId = process.Id,
                                        StartTime = process.StartTime,
                                        ExecutablePath = executable,
                                        IsConfiguredServer = IsConfiguredServerPath(executable, configuredPath),
                                        Owner = OwnerOf(commandLine),
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
    /// The desktop app's attach. Attaching to a process other than the attached one
    /// stops the attached server first, so the caller confirms that with the user and
    /// passes the attachment they confirmed, <paramref name="confirmed"/> (null when
    /// nothing was attached). If the attachment has moved since, nothing is stopped
    /// and nothing is attached (#40).
    /// </summary>
    public async Task<(bool Success, string Message)> AttachToProcessAsync(int processId, AttachmentSession? confirmed)
    {
        // Held for the whole attach: it stops monitoring, and maybe the attached server,
        // before the attachment itself changes.
        if (!_attachmentGate.Wait(0))
        {
            return (false, BusyMessage);
        }

        try
        {
            _logger.LogInformation($"Attempting to attach to process {processId}");

            if (CurrentSession?.Id != confirmed?.Id)
            {
                return (false, "The attached server changed while you were confirming. Nothing was stopped; try again.");
            }

            // Check if process exists and is a Wreckfest server. The handle is held
            // through the attach, so the PID cannot change owner before it is pinned.
            using var process = Process.GetProcessById(processId);
            _ = process.SafeHandle;
            var processName = process.ProcessName;
            if (process.HasExited)
            {
                return (false, $"Process {processId} not found or has exited");
            }

            if (!processName.Equals("Wreckfest_x64", StringComparison.OrdinalIgnoreCase) &&
                !processName.Equals("Wreckfest", StringComparison.OrdinalIgnoreCase))
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

            // Stop the attached server if it is a different process; the user confirmed
            // this, naming it.
            if (confirmed != null && confirmed.ProcessId != processId)
            {
                _logger.LogInformation("Stopping attached server {CurrentPid} before attaching to {NewPid}",
                    confirmed.ProcessId, processId);
                var stopped = await StopServerAsync();
                if (!stopped.Success && IsRunning)
                {
                    _logger.LogWarning("Attached server {Pid} could not be stopped ({Message}); nothing was attached",
                        confirmed.ProcessId, stopped.Message);
                    // Monitoring was stopped above for the switch; the server is still
                    // attached, so give it back.
                    StartOutputMonitoring();
                    return (false, $"Process {confirmed.ProcessId} could not be stopped, so nothing was attached.");
                }
            }

            lock (_lock)
            {
                if (!SetAttachedProcess(processId))
                {
                    return (false, $"Process {processId} has exited");
                }

                _startTime = process.StartTime.ToUniversalTime();
            }
            ProcessIdChanged?.Invoke(processId);

            // Initialize trackers
            _playerTracker.Clear();
            _trackChangeTracker.Clear();

            StartOutputMonitoring();

            _logger.LogInformation($"Successfully attached to process {processId}");

            // Notify the web UI
            _ = Task.Run(async () =>
            {
                try
                {
                    await _events.ServerAttachedAsync(new Models.ServerAttachedEvent
                    {
                        ProcessId = processId,
                        ProcessName = processName,
                        StartTime = _startTime ?? DateTime.UtcNow
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to publish server attached event");
                }
            });

            return (true, $"Attached to process {processId}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to attach to process {processId}");
            return (false, "Could not attach to the server. The desktop app's log has the details.");
        }
        finally
        {
            _attachmentGate.Release();
        }
    }

    /// <summary>
    /// Injects the experimental console hook into an existing Wreckfest server process.
    /// </summary>
    public virtual async Task<(bool Success, string Message)> InjectConsoleHookAsync(int processId)
    {
        _logger.LogInformation("Console hook injection requested for process {ProcessId}", processId);

        // One at a time, from the button and the API alike: a second injection would restart
        // the hook listener under the first one. And no attachment change meanwhile, so the
        // process the target check passes is the one the DLL goes into.
        if (!_attachmentGate.Wait(0))
        {
            _logger.LogWarning("Console hook injection into process {ProcessId} refused: another injection or an attach is in progress", processId);
            return (false, "Injection refused: another injection or an attach is already in progress.");
        }

        Volatile.Write(ref _injectionInProgress, 1);
        try
        {
            return await InjectConsoleHookCoreAsync(processId);
        }
        finally
        {
            Volatile.Write(ref _injectionInProgress, 0);
            _attachmentGate.Release();
        }
    }

    private async Task<(bool Success, string Message)> InjectConsoleHookCoreAsync(int processId)
    {
        // First, so everything below is checked after it: a hook still being taken out of
        // this process would be found loaded and reused, then unloaded under the new
        // attachment.
        Task? unload;
        lock (_lock)
        {
            unload = _hookUnloads.GetValueOrDefault(processId);
        }

        if (unload != null)
        {
            await unload;
        }

        try
        {
            var process = Process.GetProcessById(processId);
            var targetCheck = CheckInjectionTarget(process);
            if (!targetCheck.Success)
            {
                return targetCheck;
            }

            var windowCheck = await WaitForServerWindowAsync(process);
            if (!windowCheck.Success)
            {
                return windowCheck;
            }

            // The wait can take seconds, long enough for the server to exit or the
            // controller to attach elsewhere.
            targetCheck = CheckInjectionTarget(process);
            if (!targetCheck.Success)
            {
                return targetCheck;
            }

            var buildCheck = EnsureSupportedBuild(process);
            if (!buildCheck.Success)
            {
                return buildCheck;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to validate target process {ProcessId}", processId);
            return (false, $"Could not check process {processId}. The desktop app's log has the details.");
        }

        return await _injectedHookOutputReader.InjectAsync(processId);
    }

    private (bool Success, string Message) CheckInjectionTarget(Process process)
    {
        if (process.HasExited)
        {
            return (false, $"Process {process.Id} has exited");
        }

        var attachedProcessId = AttachedProcessId;
        if (!attachedProcessId.HasValue)
        {
            return (false, $"Injection refused: no process is attached; requested process is {process.Id}.");
        }

        if (attachedProcessId.Value != process.Id)
        {
            return (false,
                $"Injection refused: attached process is {attachedProcessId.Value}; requested process is {process.Id}.");
        }

        return (true, string.Empty);
    }

    /// <summary>
    /// How many times injection re-checks for the console window of a server that is
    /// still starting. With <see cref="ServerWindowRetryDelay"/> that is 20 seconds:
    /// a cold start measured 10.9 s before its window appeared, warm ones about 5 s.
    /// </summary>
    public const int ServerWindowRetries = 40;

    /// <summary>The pause between those checks.</summary>
    protected virtual TimeSpan ServerWindowRetryDelay => TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// A freshly started server has no console window at all until it has loaded -
    /// about 11 seconds on the dev machine - and the build is read from that window's
    /// title. So "no window yet" means "still starting" and is worth waiting out,
    /// while a window whose title does not parse is a real <c>&lt;unreadable&gt;</c>
    /// and is refused at once by <see cref="EnsureSupportedBuild"/>.
    /// </summary>
    private async Task<(bool Success, string Message)> WaitForServerWindowAsync(Process process)
    {
        if (HasServerWindow(process))
        {
            return (true, string.Empty);
        }

        var maxWait = ServerWindowRetryDelay * ServerWindowRetries;
        _logger.LogInformation(
            "Server process {ProcessId} is still starting (no console window yet); waiting up to {Seconds:0}s before injecting",
            process.Id,
            maxWait.TotalSeconds);
        var waited = Stopwatch.StartNew();

        for (var retry = 1; retry <= ServerWindowRetries; retry++)
        {
            await Task.Delay(ServerWindowRetryDelay);

            if (process.HasExited)
            {
                return (false, $"Process {process.Id} has exited");
            }

            if (HasServerWindow(process))
            {
                _logger.LogInformation(
                    "Server process {ProcessId} console window appeared after {Seconds:0.0}s",
                    process.Id,
                    waited.Elapsed.TotalSeconds);
                return (true, string.Empty);
            }
        }

        _logger.LogWarning(
            "Server process {ProcessId} still has no console window after {Seconds:0}s",
            process.Id,
            maxWait.TotalSeconds);
        return (false,
            $"Injection refused: server process {process.Id} is still starting (no console window after {maxWait.TotalSeconds:0}s). Try again shortly.");
    }

    /// <summary>True once the process has a console window, which is when its title carries the build.</summary>
    protected virtual bool HasServerWindow(Process process)
    {
        try
        {
            // Process caches the window handle and title from the first read.
            process.Refresh();
            return process.MainWindowHandle != IntPtr.Zero;
        }
        catch
        {
            return false;
        }
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
            process.Refresh();
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
            var pid = _actualServerPid;
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
