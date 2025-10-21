using System.Diagnostics;
using System.Collections.Concurrent;

namespace WreckfestController.Services;

public class ServerManager
{
    private Process? _serverProcess;
    private readonly IConfiguration _configuration;
    private readonly ConcurrentBag<Action<string>> _outputSubscribers = new();
    private readonly object _lock = new();
    private DateTime? _startTime;
    private int? _actualServerPid;
    private readonly ILogger<ServerManager> _logger;
    private readonly System.Collections.Concurrent.ConcurrentQueue<(DateTime Timestamp, string Message)> _outputBuffer = new();
    private const int MaxBufferSize = 500;
    private FileSystemWatcher? _logFileWatcher;
    private long _lastLogFilePosition = 0;
    private string? _currentLogFilePath;
    private readonly PlayerTracker _playerTracker;
    private readonly object _logReadLock = new();
    private System.Threading.Timer? _fileWatcherDebounceTimer;
    private System.Threading.Timer? _pollingTimer;
    private string _currentTrack = string.Empty;

    public bool IsRunning => GetActualServerProcess() != null;

    public ServerManager(IConfiguration configuration, ILogger<ServerManager> logger, PlayerTracker playerTracker)
    {
        _configuration = configuration;
        _logger = logger;
        _playerTracker = playerTracker;
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
                _logger.LogWarning("Tracked server process (PID: {PID}) no longer exists", _actualServerPid.Value);
                _actualServerPid = null;
                _startTime = null;
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error accessing tracked server process (PID: {PID})", _actualServerPid.Value);
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
                _startTime = DateTime.Now;

                // Start monitoring the log file for real-time output
                StartLogFileMonitoring();

                // Check if process exits immediately
                if (process.WaitForExit(500))
                {
                    var exitCode = process.ExitCode;
                    _logger.LogError("Server process exited immediately with code: {ExitCode}", exitCode);
                    _serverProcess = null;
                    _startTime = null;
                    return (false, $"Server process exited immediately with code: {exitCode}. Check server arguments and config file.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start server");
                return (false, $"Failed to start server: {ex.Message}");
            }
        }

        // Wait outside the lock, check multiple times
        for (int i = 0; i < 5; i++)
        {
            await Task.Delay(1000);

            var actualProcess = GetActualServerProcess();
            if (actualProcess != null)
            {
                _logger.LogInformation("Server started successfully. Process: {ProcessName} (PID: {ProcessId})", actualProcess.ProcessName, actualProcess.Id);
                return (true, $"Server started successfully. Process: {actualProcess.ProcessName} (PID: {actualProcess.Id})");
            }
        }

        // Process is running but not detected by GetActualServerProcess (shouldn't happen with PID tracking)
        _logger.LogWarning("Server process started (PID: {PID}) but not confirmed after 5 seconds", _actualServerPid);
        return (true, $"Server process started (PID: {_actualServerPid}) but not confirmed. Check logs.");
    }

    public virtual async Task<(bool Success, string Message)> StopServerAsync()
    {
        lock (_lock)
        {
            var actualProcess = GetActualServerProcess();
            if (actualProcess == null)
            {
                return (false, "Server is not running");
            }

            try
            {
                _logger.LogInformation("Stopping server process {ProcessId}", actualProcess.Id);

                // Try to kill the actual server process
                actualProcess.Kill(entireProcessTree: true);
                actualProcess.WaitForExit(10000);

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

                _serverProcess = null;
                _startTime = null;
                _actualServerPid = null;

                // Stop monitoring log file
                StopLogFileMonitoring();

                // Clear player tracking
                _playerTracker.Clear();

                return (true, "Server stopped successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to stop server");
                return (false, $"Failed to stop server: {ex.Message}");
            }
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

    public virtual async Task<(bool Success, string Message)> SendCommandAsync(string command)
    {
        if (!IsRunning)
        {
            return (false, "Server is not running");
        }

        // Note: Commands cannot be sent directly to the Wreckfest server process
        // The server doesn't support stdin properly
        // This endpoint is kept for potential future use or alternative command methods
        _logger.LogWarning("SendCommand called but Wreckfest server doesn't support command input via API");
        return (false, "Command sending not supported by Wreckfest dedicated server");
    }

    public void SubscribeToOutput(Action<string> callback)
    {
        _outputSubscribers.Add(callback);
    }

    public void UnsubscribeFromOutput(Action<string> callback)
    {
        // ConcurrentBag doesn't support removal, but we can handle it by checking if callback is null
        // For simplicity, we'll keep the subscriber list as is
    }

    private void NotifySubscribers(string message)
    {
        foreach (var subscriber in _outputSubscribers)
        {
            try
            {
                subscriber(message);
            }
            catch
            {
                // Ignore subscriber errors
            }
        }
    }

    public virtual ServerStatus GetStatus()
    {
        var actualProcess = GetActualServerProcess();

        return new ServerStatus
        {
            IsRunning = actualProcess != null,
            ProcessId = actualProcess?.Id,
            Uptime = _startTime.HasValue && actualProcess != null
                ? DateTime.Now - _startTime.Value
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
            _logger.LogInformation("Attached to existing server process (PID: {PID}, Name: {Name})", pid, process.ProcessName);

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
                    NotifySubscribers(line);
                    _playerTracker.ProcessLogLine(line);
                    _logger.LogInformation("Server log: {Line}", line);
                    var trackMatch = System.Text.RegularExpressions.Regex.Match(line, @"Current track loaded! Track: ('(.+?)')");
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
        var onlinePlayers = _playerTracker.GetOnlinePlayers();
        var (onlineCount, totalCount) = _playerTracker.GetPlayerCount();

        return new Models.PlayerListResponse
        {
            TotalPlayers = onlineCount,
            MaxPlayers = 24, // TODO: Get from config or server query
            Players = onlinePlayers,
            LastUpdated = DateTime.Now
        };
    }
}

public class ServerStatus
{
    public bool IsRunning { get; set; }
    public int? ProcessId { get; set; }
    public TimeSpan? Uptime { get; set; }
    public string CurrentTrack { get; set; } = string.Empty;
}
