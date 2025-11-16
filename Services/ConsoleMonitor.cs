using System.Runtime.InteropServices;
using System.Text;

namespace WreckfestController.Services;

/// <summary>
/// Monitors a Windows console process output using the Windows Console API.
/// This provides real-time output monitoring without relying on log files.
/// </summary>
public class ConsoleMonitor : IDisposable
{
    private readonly ILogger<ConsoleMonitor> _logger;
    private IntPtr _consoleHandle = IntPtr.Zero;
    private COORD _lastPosition;
    private Timer? _monitorTimer;
    private readonly List<Action<string>> _outputSubscribers = new();
    private int _targetProcessId;
    private bool _isMonitoring;
    private readonly object _lockObject = new();

    // Windows API Constants
    private const int STD_OUTPUT_HANDLE = -11;
    private const int ATTACH_PARENT_PROCESS = -1;

    #region Windows API P/Invoke

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleScreenBufferInfo(
        IntPtr hConsoleOutput,
        out CONSOLE_SCREEN_BUFFER_INFO lpConsoleScreenBufferInfo);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ReadConsoleOutputCharacter(
        IntPtr hConsoleOutput,
        [Out] StringBuilder lpCharacter,
        uint nLength,
        COORD dwReadCoord,
        out uint lpNumberOfCharsRead);

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD
    {
        public short X;
        public short Y;

        public COORD(short x, short y)
        {
            X = x;
            Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SMALL_RECT
    {
        public short Left;
        public short Top;
        public short Right;
        public short Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CONSOLE_SCREEN_BUFFER_INFO
    {
        public COORD dwSize;
        public COORD dwCursorPosition;
        public short wAttributes;
        public SMALL_RECT srWindow;
        public COORD dwMaximumWindowSize;
    }

    #endregion

    public ConsoleMonitor(ILogger<ConsoleMonitor> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Starts monitoring the console output of the specified process.
    /// </summary>
    /// <param name="processId">The process ID to monitor</param>
    /// <param name="pollingIntervalMs">How often to check for new output (default: 100ms)</param>
    /// <returns>True if successfully attached and started monitoring</returns>
    public bool StartMonitoring(int processId, int pollingIntervalMs = 100)
    {
        lock (_lockObject)
        {
            if (_isMonitoring)
            {
                _logger.LogWarning("Already monitoring process {ProcessId}", _targetProcessId);
                return false;
            }

            try
            {
                // WORKAROUND: Since we're a console app, we need to free our own console first
                // This is not ideal but may work for ASP.NET Core applications
                // Note: This will disable console output from our own application!
                try
                {
                    FreeConsole();
                    _logger.LogInformation("Freed own console to attach to process {ProcessId}", processId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not free own console (may not have one)");
                }

                // Attach to the target process's console
                if (!AttachConsole(processId))
                {
                    int error = Marshal.GetLastWin32Error();
                    _logger.LogError("Failed to attach to console for process {ProcessId}. Error code: {ErrorCode}. " +
                        "Note: Console monitoring may not work reliably from console applications. " +
                        "Consider using log file monitoring or running as a Windows Service.",
                        processId, error);
                    return false;
                }

                // Get handle to the console output
                _consoleHandle = GetStdHandle(STD_OUTPUT_HANDLE);
                if (_consoleHandle == IntPtr.Zero || _consoleHandle == new IntPtr(-1))
                {
                    _logger.LogError("Failed to get console handle for process {ProcessId}", processId);
                    FreeConsole();
                    return false;
                }

                // Get initial cursor position
                if (GetConsoleScreenBufferInfo(_consoleHandle, out var bufferInfo))
                {
                    _lastPosition = bufferInfo.dwCursorPosition;
                    _logger.LogInformation("Console attached. Initial cursor position: ({X}, {Y})",
                        _lastPosition.X, _lastPosition.Y);
                }
                else
                {
                    _logger.LogWarning("Could not get initial console buffer info");
                    _lastPosition = new COORD(0, 0);
                }

                _targetProcessId = processId;
                _isMonitoring = true;

                // Start monitoring timer
                _monitorTimer = new Timer(
                    callback: _ => ReadNewOutput(),
                    state: null,
                    dueTime: TimeSpan.FromMilliseconds(pollingIntervalMs),
                    period: TimeSpan.FromMilliseconds(pollingIntervalMs)
                );

                _logger.LogInformation("Started console monitoring for process {ProcessId} (polling every {Interval}ms)",
                    processId, pollingIntervalMs);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception while starting console monitoring for process {ProcessId}", processId);
                Cleanup();
                return false;
            }
        }
    }

    /// <summary>
    /// Stops monitoring the console.
    /// </summary>
    public void StopMonitoring()
    {
        lock (_lockObject)
        {
            if (!_isMonitoring)
                return;

            _logger.LogInformation("Stopping console monitoring for process {ProcessId}", _targetProcessId);
            Cleanup();
        }
    }

    /// <summary>
    /// Subscribe to console output events.
    /// </summary>
    /// <param name="callback">Callback function that receives each line of output</param>
    public void SubscribeToOutput(Action<string> callback)
    {
        lock (_lockObject)
        {
            _outputSubscribers.Add(callback);
        }
    }

    /// <summary>
    /// Unsubscribe from console output events.
    /// </summary>
    public void UnsubscribeFromOutput(Action<string> callback)
    {
        lock (_lockObject)
        {
            _outputSubscribers.Remove(callback);
        }
    }

    private void ReadNewOutput()
    {
        if (_consoleHandle == IntPtr.Zero)
            return;

        try
        {
            // Get current console state
            if (!GetConsoleScreenBufferInfo(_consoleHandle, out var bufferInfo))
            {
                _logger.LogDebug("Failed to get console buffer info");
                return;
            }

            // Check if cursor has moved (indicating new output)
            if (bufferInfo.dwCursorPosition.Y > _lastPosition.Y ||
                (bufferInfo.dwCursorPosition.Y == _lastPosition.Y &&
                 bufferInfo.dwCursorPosition.X > _lastPosition.X))
            {
                // Calculate number of characters to read
                int charsToRead = CalculateCharsToRead(_lastPosition, bufferInfo.dwCursorPosition, bufferInfo.dwSize.X);

                if (charsToRead > 0 && charsToRead < 100000) // Sanity check
                {
                    // Read the characters from the buffer
                    StringBuilder buffer = new StringBuilder(charsToRead + 1);
                    if (ReadConsoleOutputCharacter(_consoleHandle, buffer,
                        (uint)charsToRead, _lastPosition, out uint charsRead))
                    {
                        string output = buffer.ToString(0, (int)charsRead);
                        ProcessOutput(output, bufferInfo.dwSize.X);
                    }
                }

                // Update position
                _lastPosition = bufferInfo.dwCursorPosition;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading console output");
        }
    }

    private int CalculateCharsToRead(COORD fromPos, COORD toPos, short consoleWidth)
    {
        if (toPos.Y == fromPos.Y)
        {
            // Same line - read from last X to current X
            return toPos.X - fromPos.X;
        }
        else
        {
            // Multiple lines - calculate total characters
            // Characters from start position to end of first line
            int firstLineChars = consoleWidth - fromPos.X;

            // Full lines in between
            int fullLines = toPos.Y - fromPos.Y - 1;
            int middleChars = fullLines * consoleWidth;

            // Characters from start of last line to cursor position
            int lastLineChars = toPos.X;

            return firstLineChars + middleChars + lastLineChars;
        }
    }

    private void ProcessOutput(string rawOutput, short consoleWidth)
    {
        if (string.IsNullOrEmpty(rawOutput))
            return;

        StringBuilder processedOutput = new StringBuilder();

        // Split by console width and trim each line
        for (int i = 0; i < rawOutput.Length; i += consoleWidth)
        {
            int lineLength = Math.Min(consoleWidth, rawOutput.Length - i);
            string line = rawOutput.Substring(i, lineLength);

            // Trim trailing spaces but keep content
            line = line.TrimEnd();

            if (!string.IsNullOrEmpty(line))
            {
                processedOutput.AppendLine(line);
            }
        }

        string output = processedOutput.ToString();
        if (!string.IsNullOrEmpty(output))
        {
            // Notify subscribers
            NotifySubscribers(output);
        }
    }

    private void NotifySubscribers(string output)
    {
        lock (_lockObject)
        {
            foreach (var subscriber in _outputSubscribers)
            {
                try
                {
                    subscriber(output);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error notifying subscriber");
                }
            }
        }
    }

    private void Cleanup()
    {
        _monitorTimer?.Dispose();
        _monitorTimer = null;

        if (_consoleHandle != IntPtr.Zero)
        {
            try
            {
                FreeConsole();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error freeing console");
            }
            _consoleHandle = IntPtr.Zero;
        }

        _isMonitoring = false;
        _targetProcessId = 0;
    }

    public void Dispose()
    {
        StopMonitoring();
    }

    /// <summary>
    /// Gets whether the monitor is currently active.
    /// </summary>
    public bool IsMonitoring => _isMonitoring;

    /// <summary>
    /// Gets the process ID being monitored.
    /// </summary>
    public int TargetProcessId => _targetProcessId;
}
