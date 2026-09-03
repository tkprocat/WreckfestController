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
    private static readonly TimeSpan DuplicateChatCommandWindow = TimeSpan.FromSeconds(2);
    private readonly object _chatCommandDedupLock = new();

    // Chat commands are handled on their own single-consumer worker rather than
    // inline on the hook's output-reading thread. Handlers block (VotingService
    // waits on a hook round-trip), and a blocked reader stops draining the output
    // pipe - which makes the hook's own WriteHookLine/FlushFileBuffers block, so
    // neither side can progress until a timeout fires. One consumer preserves the
    // strict command ordering that !yes / !no / !confirm rely on.
    private readonly System.Threading.Channels.Channel<(string Player, bool IsBot, string Message)> _chatCommands =
        System.Threading.Channels.Channel.CreateUnbounded<(string, bool, string)>(
            new System.Threading.Channels.UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });
    private Task? _chatCommandWorker;
    private readonly object _chatWorkerLock = new();
    private string? _lastChatCommandKey;
    private DateTime _lastChatCommandAtUtc;
    private bool _useInjectedHookAsPrimaryOutput;

    // Server events come from the game's own ring buffer rather than parsed console
    // text; see ServerEventReader. Polled rather than pushed, which is why the reader
    // reports overflow so we can fall back to a full snapshot.
    private ServerEventReader? _serverEventReader;
    private System.Threading.Timer? _serverEventTimer;
    private int _serverEventPollBusy;
    private bool _serverEventsSeeded;
    private static readonly TimeSpan ServerEventPollInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Raised when the server process ID changes (after restart or attach)
    /// </summary>
    public event Action<int?>? ProcessIdChanged;

    public bool IsRunning => GetActualServerProcess() != null;

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

        _injectedHookOutputReader.OutputReceived += OnInjectedHookOutputReceived;
        _injectedHookOutputReader.HookOutputReceived += output => ConsoleHookOutput?.Invoke(output);

        // Subscribe to player tracker list command requests
        _playerTracker.OnListCommandRequested += OnListCommandRequested;
    }

    /// <summary>
    /// Handles player tracker requests to send a list command
    /// </summary>
    private void OnListCommandRequested()
    {
        if (!IsRunning)
        {
            _logger.LogDebug("Ignoring list command request - server is not running");
            return;
        }

        // Fire and forget - we don't want to block the player tracker
        _ = Task.Run(async () =>
        {
            try
            {
                var process = GetActualServerProcess();
                if (process != null && _serverInputWriter is IPlayerSnapshotReader playerSnapshotReader)
                {
                    var snapshot = await playerSnapshotReader.ReadPlayerSnapshotAsync(process.Id);
                    if (snapshot.Success)
                    {
                        _playerTracker.ProcessHookPlayerSnapshot(snapshot.Players);
                        return;
                    }

                    _logger.LogWarning("Failed to read injected hook player snapshot, falling back to list command: {Message}", snapshot.Message);
                }

                var result = await SendCommandAsync("list");
                if (!result.Success)
                {
                    _logger.LogWarning("Failed to send list command: {Message}", result.Message);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending list command");
            }
        });
    }

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
                    _actualServerPid = null;
                    _startTime = null;
                    return null;
                }
            }
            catch (ArgumentException)
            {
                // Process doesn't exist
                _logger.LogWarning("Tracked server process (PID: {PID}) no longer exists", _actualServerPid!.Value);
                _actualServerPid = null;
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
                _actualServerPid = process.Id;
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

            var actualProcess = GetActualServerProcess();
            if (actualProcess != null)
            {
                _logger.LogInformation("Server started successfully. Process: {ProcessName} (PID: {ProcessId})", actualProcess.ProcessName, actualProcess.Id);

                // Send webhook notification
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _webhookService.SendServerStartedAsync(new Models.ServerStartedEvent
                        {
                            ProcessId = actualProcess.Id,
                            ProcessName = actualProcess.ProcessName,
                            StartTime = _startTime ?? DateTime.UtcNow
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to send server started webhook");
                    }
                });

                return (true, $"Server started successfully. Process: {actualProcess.ProcessName} (PID: {actualProcess.Id})");
            }
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

            // Stop output monitoring before sending exit command (frees console attachment)
            StopOutputMonitoring();

            // Send exit command
            var commandResult = await SendCommandAsync("exit");
            if (!commandResult.Success)
            {
                _logger.LogWarning("Failed to send exit command, falling back to force stop");
                return await StopServerAsync();
            }

            _logger.LogInformation("Exit command sent, waiting for server to shutdown...");

            // Wait for the process to exit gracefully (max 30 seconds)
            var timeout = TimeSpan.FromSeconds(30);
            var startTime = DateTime.Now;
            var checkInterval = TimeSpan.FromMilliseconds(500);

            while (DateTime.Now - startTime < timeout)
            {
                await Task.Delay(checkInterval);

                // Check if process has exited
                var process = GetActualServerProcess();
                if (process == null)
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
                        _actualServerPid = null;

                        // Clear player tracking
                        _playerTracker.Clear();
                    }

                    return (true, $"Server stopped gracefully (was PID: {currentPid})");
                }
            }

            // Timeout - process didn't exit gracefully
            _logger.LogWarning("Server didn't exit gracefully within timeout, forcing shutdown");
            return await StopServerAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop server gracefully, falling back to force stop");
            return await StopServerAsync();
        }
    }

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
            actualProcess.Kill(entireProcessTree: true);
            actualProcess.WaitForExit(10000);

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
                _actualServerPid = null;

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
            _actualServerPid = newPid;
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
            var processId = _actualServerPid ?? GetActualServerProcess()?.Id;
            if (processId == null)
            {
                return (false, "Server is not running");
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
        var process = GetActualServerProcess();
        if (process == null || _serverInputWriter is not IHookMemoryReader reader)
        {
            return null;
        }

        try
        {
            var result = await reader.ReadModuleMemoryAsync(process.Id, rva, size);
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
        var process = GetActualServerProcess();
        if (process == null || _serverInputWriter is not IPlayerSnapshotReader playerSnapshotReader)
        {
            return false;
        }

        try
        {
            var snapshot = await playerSnapshotReader.ReadPlayerSnapshotAsync(process.Id);
            if (!snapshot.Success)
            {
                _logger.LogDebug("Injected hook player snapshot refresh skipped: {Message}", snapshot.Message);
                return false;
            }

            _playerTracker.ProcessHookPlayerSnapshot(snapshot.Players);
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
        var actualProcess = GetActualServerProcess();

        return new ServerStatus
        {
            IsRunning = actualProcess != null,
            ProcessId = actualProcess?.Id,
            Uptime = _startTime.HasValue && actualProcess != null
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

            _actualServerPid = pid;
            _startTime = process.StartTime;
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

        _serverEventTimer = new System.Threading.Timer(
            _ => _ = PollServerEventsAsync(),
            null,
            ServerEventPollInterval,
            ServerEventPollInterval);
    }

    private void StopServerEventPolling()
    {
        _serverEventTimer?.Dispose();
        _serverEventTimer = null;
        _serverEventReader = null;
        _playerTracker.UseServerEvents = false;
        _serverEventsSeeded = false;
    }

    private async Task PollServerEventsAsync()
    {
        var reader = _serverEventReader;
        if (reader == null)
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

            // The first successful poll only adopts the cursor - it deliberately does
            // not replay history - so anyone already connected produced no event. Seed
            // the roster from a snapshot once, then let events maintain it.
            if (!_serverEventsSeeded && reader.HasSynced)
            {
                _serverEventsSeeded = true;
                _playerTracker.UseServerEvents = true;
                await TryRefreshPlayersFromHookAsync();
            }

            foreach (var serverEvent in events)
            {
                _playerTracker.ProcessServerEvent(serverEvent);
            }

            if (overflowed)
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
    private void OnConsoleOutputReceived(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return;

        // Console monitor may send multi-line output, so split by newlines
        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            // Add to output buffer
            _outputBuffer.Enqueue((DateTime.Now, line));
            while (_outputBuffer.Count > MaxBufferSize)
            {
                _outputBuffer.TryDequeue(out _);
            }

            // Notify subscribers
            NotifyConsoleOutput(line);

            // Send to webhook for Laravel
            _consoleLogSender.AddLog(line);

            // Parse for player events, track changes, and server info
            ProcessChatCommandLine(line);
            _playerTracker.ProcessLogLine(line);
            _trackChangeTracker.ProcessLogLine(line);
            _serverInfoTracker.ProcessLogLine(line);
        }
    }

    private void ProcessChatCommandLine(string line)
    {
        var normalizedLine = line.Trim();
        if (Regex.IsMatch(normalizedLine, @"\s+[>*/\\]$"))
        {
            normalizedLine = normalizedLine[..^1].TrimEnd();
        }

        var chatMatch = Regex.Match(normalizedLine, @"^(?:\*\s*)?\d{2}:\d{2}:\d{2}\s+(?:-\s+)?(\*?)([^:]+):\s*(!.*)$");
        if (!chatMatch.Success)
            return;

        var isBot = chatMatch.Groups[1].Value == "*";
        var playerName = chatMatch.Groups[2].Value.Trim();
        var chatMessage = chatMatch.Groups[3].Value.Trim();
        if (ShouldSuppressDuplicateChatCommand(playerName, isBot, chatMessage))
            return;

        EnqueueChatCommand(playerName, isBot, chatMessage);
    }

    /// <summary>
    /// Hands a chat command to the worker. Never blocks the caller - the caller is
    /// the thread draining the hook output pipe.
    /// </summary>
    private void EnqueueChatCommand(string playerName, bool isBot, string chatMessage)
    {
        EnsureChatCommandWorker();
        if (!_chatCommands.Writer.TryWrite((playerName, isBot, chatMessage)))
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
        await foreach (var (player, isBot, message) in _chatCommands.Reader.ReadAllAsync())
        {
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

    private void OnInjectedHookOutputReceived(string output)
    {
        if (ProcessConsoleHookOutput)
        {
            OnConsoleOutputReceived(output);
        }
    }

    private bool ShouldSuppressDuplicateChatCommand(string playerName, bool isBot, string chatMessage)
    {
        var key = $"{isBot}|{playerName}|{chatMessage}";
        var now = DateTime.UtcNow;

        lock (_chatCommandDedupLock)
        {
            if (string.Equals(_lastChatCommandKey, key, StringComparison.Ordinal) &&
                now - _lastChatCommandAtUtc <= DuplicateChatCommandWindow)
            {
                return true;
            }

            _lastChatCommandKey = key;
            _lastChatCommandAtUtc = now;
            return false;
        }
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
                _actualServerPid = processId;
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

            WarnOnUnsupportedBuild(process);
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
    /// derived against one specific build, so surface a mismatch here - before
    /// injecting - rather than leaving it to the hook's own layout guard.
    /// This warns rather than blocks: the offsets may well survive a patch.
    /// </summary>
    private void WarnOnUnsupportedBuild(Process process)
    {
        var build = GetServerBuild(process);
        if (build == null)
        {
            _logger.LogWarning("Could not read Wreckfest build from process {ProcessId} window title", process.Id);
            return;
        }

        var supported = _configuration["WreckfestServer:SupportedBuild"]?.Trim();
        if (string.IsNullOrWhiteSpace(supported))
        {
            _logger.LogInformation("Wreckfest build {Build} (no SupportedBuild configured)", build);
            return;
        }

        if (string.Equals(build, supported, StringComparison.Ordinal))
        {
            _logger.LogInformation("Wreckfest build {Build} matches supported build", build);
            return;
        }

        var message = $"[Controller] Wreckfest build {build} does not match supported build {supported}. " +
                      "Hook offsets were derived for the supported build; injection may fail or misbehave.";
        _logger.LogWarning(
            "Wreckfest build {Build} does not match supported build {Supported}",
            build,
            supported);
        NotifyConsoleOutput(message);
    }

    /// <summary>
    /// Extracts the build number from a Wreckfest console window title.
    /// Returns null when the title is unavailable or does not match.
    /// </summary>
    public static string? GetServerBuild(Process process)
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
