using System.Diagnostics;
using System.Collections.Concurrent;
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
    /// Event raised when a player sends a chat command (message starting with !).
    /// </summary>
    public event Action<string, bool, string>? ChatCommandReceived;

    private readonly object _lock = new();
    private DateTime? _startTime;
    private int? _actualServerPid;
    private readonly ILogger<ServerManager> _logger;
    private readonly System.Collections.Concurrent.ConcurrentQueue<(DateTime Timestamp, string Message)> _outputBuffer = new();
    private const int MaxBufferSize = 500;
    private readonly ConsoleMonitor _consoleMonitor;
    private readonly ConsoleWriter _consoleWriter;
    private readonly PlayerTracker _playerTracker;
    private readonly TrackChangeTracker _trackChangeTracker;
    private readonly ServerInfoTracker _serverInfoTracker;
    private readonly WreckfestWebWebhookService _webhookService;
    private readonly ConsoleLogWebhookSender _consoleLogSender;
    private string _currentTrack = string.Empty;

    // Log file monitoring fields (used when UseConsoleMonitoring is false)
    private FileSystemWatcher? _logFileWatcher;
    private long _lastLogFilePosition = 0;
    private string? _currentLogFilePath;
    private readonly object _logReadLock = new();
    private System.Threading.Timer? _fileWatcherDebounceTimer;
    private System.Threading.Timer? _pollingTimer;

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
        ConsoleMonitor consoleMonitor,
        ConsoleWriter consoleWriter,
        WreckfestWebWebhookService webhookService,
        ConsoleLogWebhookSender consoleLogSender)
    {
        _configuration = configuration;
        _logger = logger;
        _playerTracker = playerTracker;
        _trackChangeTracker = trackChangeTracker;
        _serverInfoTracker = serverInfoTracker;
        _consoleMonitor = consoleMonitor;
        _consoleWriter = consoleWriter;
        _webhookService = webhookService;
        _consoleLogSender = consoleLogSender;

        // Subscribe to console monitor output
        _consoleMonitor.SubscribeToOutput(OnConsoleOutputReceived);

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

            // Store initial log position to detect new messages
            var initialLogPosition = _lastLogFilePosition;

            while (DateTime.Now - startTime < timeout)
            {
                await Task.Delay(logCheckInterval);

                // Check if we've received new log lines indicating restart
                // The ReadNewLogLines method updates _lastLogFilePosition
                if (_lastLogFilePosition > initialLogPosition)
                {
                    // Look for restart indicators in the output buffer
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
        if (string.IsNullOrWhiteSpace(command))
        {
            return (false, "Command cannot be empty");
        }

        if (!IsRunning)
        {
            return (false, "Server is not running");
        }

        try
        {
            var windowHandle = _consoleWriter.FindConsoleWindow();

            if (windowHandle == IntPtr.Zero)
            {
                return (false, "Could not find console window");
            }

            bool success = _consoleWriter.SendCommand(windowHandle, command + Environment.NewLine);

            if (success)
            {
                _logger.LogInformation("Successfully sent command to console: {Command}", command);
                return (true, $"Command sent successfully: {command}");
            }
            else
            {
                return (false, "Failed to send command to console window");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending command: {Command}", command);
            return (false, $"Error sending command: {ex.Message}");
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
    /// Starts output monitoring using either console monitoring or log file monitoring
    /// based on the UseConsoleMonitoring user setting.
    /// </summary>
    private void StartOutputMonitoring()
    {
        // Check user settings first, fallback to config, default to true (console monitoring)
        var useConsoleMonitoring = _configuration.GetValue("WreckfestServer:UseConsoleMonitoring", true);

        if (useConsoleMonitoring)
        {
            StartConsoleMonitoring();
        }
        else
        {
            StartLogFileMonitoring();
        }
    }

    /// <summary>
    /// Stops output monitoring (both console and log file monitoring).
    /// </summary>
    private void StopOutputMonitoring()
    {
        StopConsoleMonitoring();
        StopLogFileMonitoring();
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

        var chatMatch = Regex.Match(normalizedLine, @"^(?:\*\s*)?\d{2}:\d{2}:\d{2}\s+(?:-\s+)?(\*?)(.+?):\s*(!.+)$");
        if (!chatMatch.Success)
            return;

        var isBot = chatMatch.Groups[1].Value == "*";
        var playerName = chatMatch.Groups[2].Value.Trim();
        var chatMessage = chatMatch.Groups[3].Value.Trim();
        ChatCommandReceived?.Invoke(playerName, isBot, chatMessage);
    }

    /// <summary>
    /// Starts monitoring the server process console using Windows Console API.
    /// Includes retry logic for slow-starting processes.
    /// </summary>
    private void StartConsoleMonitoring()
    {
        if (_actualServerPid == null)
        {
            _logger.LogWarning("Cannot start console monitoring: No server process ID");
            return;
        }

        // Start retry loop in background to not block
        _ = Task.Run(async () =>
        {
            const int maxRetries = 5;
            const int baseDelayMs = 1000; // Start with 1 second

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    // Check if process is still running
                    if (_actualServerPid == null)
                    {
                        _logger.LogWarning("Console monitoring aborted: Server process ID cleared");
                        return;
                    }

                    bool started = _consoleMonitor.StartMonitoring(_actualServerPid.Value);

                    if (started)
                    {
                        _logger.LogInformation("Console monitoring started for PID: {ProcessId} (attempt {Attempt}/{MaxRetries})",
                            _actualServerPid, attempt, maxRetries);
                        return; // Success!
                    }
                    else
                    {
                        _logger.LogWarning("Failed to start console monitoring for PID: {ProcessId} (attempt {Attempt}/{MaxRetries})",
                            _actualServerPid, attempt, maxRetries);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error starting console monitoring (attempt {Attempt}/{MaxRetries})", attempt, maxRetries);
                }

                // Wait before retry with exponential backoff (unless this was the last attempt)
                if (attempt < maxRetries)
                {
                    var delayMs = baseDelayMs * (1 << (attempt - 1)); // 1s, 2s, 4s, 8s
                    _logger.LogDebug("Retrying console monitoring in {DelayMs}ms...", delayMs);
                    await Task.Delay(delayMs);
                }
            }

            _logger.LogError("Failed to start console monitoring after {MaxRetries} attempts for PID: {ProcessId}",
                maxRetries, _actualServerPid);
        });
    }

    /// <summary>
    /// Stops console monitoring.
    /// </summary>
    private void StopConsoleMonitoring()
    {
        try
        {
            _consoleMonitor.StopMonitoring();
            _logger.LogInformation("Console monitoring stopped");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping console monitoring");
        }
    }

    private void StartLogFileMonitoring()
    {
        try
        {
            var logFilePath = GetLogFilePathFromConfig();
            if (string.IsNullOrEmpty(logFilePath))
            {
                logFilePath = _configuration["WreckfestServer:LogFilePath"];
            }

            if (string.IsNullOrEmpty(logFilePath))
            {
                _logger.LogWarning("Cannot start log file monitoring: LogFilePath not configured");
                return;
            }

            _currentLogFilePath = logFilePath;

            // If file exists, get current position to only read new lines
            if (File.Exists(logFilePath))
            {
                var fileInfo = new FileInfo(logFilePath);
                _lastLogFilePosition = fileInfo.Length;
                _logger.LogInformation("Starting log file monitoring from position {Position}", _lastLogFilePosition);
            }
            else
            {
                _lastLogFilePosition = 0;
                _logger.LogInformation("Log file doesn't exist yet, will monitor when created: {Path}", logFilePath);
            }

            // Set up FileSystemWatcher
            var directory = Path.GetDirectoryName(logFilePath);
            var fileName = Path.GetFileName(logFilePath);

            if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(fileName))
            {
                _logger.LogWarning("Invalid log file path: {Path}", logFilePath);
                return;
            }

            _logFileWatcher = new FileSystemWatcher(directory, fileName)
            {
                NotifyFilter = NotifyFilters.LastWrite,
                InternalBufferSize = 65536 // Increase buffer to reduce missed events
            };

            _logFileWatcher.Changed += OnLogFileChanged;
            _logFileWatcher.Created += OnLogFileChanged;
            _logFileWatcher.EnableRaisingEvents = true;

            // Start periodic polling as a fallback (every 2 seconds)
            _pollingTimer = new System.Threading.Timer(
                _ => ReadNewLogLines(),
                null,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(2)
            );

            _logger.LogInformation("Log file monitoring started for: {Path}", logFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start log file monitoring");
        }
    }

    private void StopLogFileMonitoring()
    {
        try
        {
            // Stop and dispose timers
            if (_fileWatcherDebounceTimer != null)
            {
                _fileWatcherDebounceTimer.Dispose();
                _fileWatcherDebounceTimer = null;
            }

            if (_pollingTimer != null)
            {
                _pollingTimer.Dispose();
                _pollingTimer = null;
            }

            if (_logFileWatcher != null)
            {
                _logFileWatcher.EnableRaisingEvents = false;
                _logFileWatcher.Changed -= OnLogFileChanged;
                _logFileWatcher.Created -= OnLogFileChanged;
                _logFileWatcher.Dispose();
                _logFileWatcher = null;
                _logger.LogInformation("Log file monitoring stopped");
            }

            _lastLogFilePosition = 0;
            _currentLogFilePath = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping log file monitoring");
        }
    }

    private void OnLogFileChanged(object sender, FileSystemEventArgs e)
    {
        try
        {
            // Debounce the file change events - wait 100ms before reading
            // This prevents multiple rapid reads for a single logical change
            _fileWatcherDebounceTimer?.Dispose();
            _fileWatcherDebounceTimer = new System.Threading.Timer(
                _ => ReadNewLogLines(),
                null,
                TimeSpan.FromMilliseconds(100),
                Timeout.InfiniteTimeSpan
            );
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error setting up debounce timer for log file changes");
        }
    }

    private void ReadNewLogLines()
    {
        if (string.IsNullOrEmpty(_currentLogFilePath) || !File.Exists(_currentLogFilePath))
        {
            return;
        }

        // Use lock to prevent concurrent reads
        if (!Monitor.TryEnter(_logReadLock, TimeSpan.FromMilliseconds(50)))
        {
            // Another read is in progress, skip this one
            return;
        }

        try
        {
            using var fileStream = new FileStream(_currentLogFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            // Check if file was truncated
            if (fileStream.Length < _lastLogFilePosition)
            {
                _logger.LogInformation("Log file was truncated, resetting position");
                _lastLogFilePosition = 0;
            }

            // Seek to last read position
            fileStream.Seek(_lastLogFilePosition, SeekOrigin.Begin);

            using var reader = new StreamReader(fileStream);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    AddToOutputBuffer(line);
                    NotifyConsoleOutput(line);
                    _consoleLogSender.AddLog(line);
                    ProcessChatCommandLine(line);
                    _playerTracker.ProcessLogLine(line);
                    _trackChangeTracker.ProcessLogLine(line);
                    _serverInfoTracker.ProcessLogLine(line);
                    _logger.LogDebug("Server log: {Line}", line);

                    var trackMatch = System.Text.RegularExpressions.Regex.Match(line, @"Current track loaded!\s*\(([^)]+)\)");
                    if (trackMatch.Success)
                    {
                        _currentTrack = trackMatch.Groups[1].Value;
                        _logger.LogInformation("Current track updated to: {Track}", _currentTrack);
                    }

                }

            }

            // Update last position
            _lastLogFilePosition = fileStream.Position;
        }
        catch (IOException ex)
        {
            // File might be locked, will try again on next change or next poll
            _logger.LogDebug(ex, "Temporary error reading log file (file may be locked)");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error reading new log lines from {Path}", _currentLogFilePath);
        }
        finally
        {
            Monitor.Exit(_logReadLock);
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

            // Start monitoring the existing process (with retry logic) only if we're not already monitoring it
            if (!_consoleMonitor.IsMonitoring || _consoleMonitor.TargetProcessId != processId)
            {
                StartConsoleMonitoring();
            }
            else
            {
                _logger.LogInformation("Already monitoring process {ProcessId}, skipping console monitoring start", processId);
            }

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
