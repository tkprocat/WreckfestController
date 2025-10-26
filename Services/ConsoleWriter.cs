using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace WreckfestController.Services;

/// <summary>
/// Sends commands to console windows using Windows messages
/// </summary>
public class ConsoleWriter
{
    [DllImport("user32.dll")]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, string lParam);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private const uint WM_CHAR = 0x0102;
    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;
    private const int VK_RETURN = 0x0D;

    private readonly ILogger<ConsoleWriter> _logger;

    public ConsoleWriter(ILogger<ConsoleWriter> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Find console window by class name
    /// </summary>
    public IntPtr FindConsoleWindow(string? windowTitle = null)
    {
        return FindWindow("ConsoleWindowClass", windowTitle);
    }

    /// <summary>
    /// Send a command to the console window (types text and presses Enter)
    /// </summary>
    public bool SendCommand(IntPtr windowHandle, string command)
    {
        if (windowHandle == IntPtr.Zero)
        {
            _logger.LogError("Invalid window handle");
            return false;
        }

        try
        {
            // Optional: bring window to foreground
            // SetForegroundWindow(windowHandle);

            // Send each character
            foreach (char c in command)
            {
                SendMessage(windowHandle, WM_CHAR, (IntPtr)c, IntPtr.Zero);
                Thread.Sleep(10); // Small delay between characters
            }

            // Send Enter key
            SendMessage(windowHandle, WM_KEYDOWN, (IntPtr)VK_RETURN, IntPtr.Zero);
            Thread.Sleep(50);
            SendMessage(windowHandle, WM_KEYUP, (IntPtr)VK_RETURN, IntPtr.Zero);

            _logger.LogDebug("Successfully sent command: {Command}", command);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send command to console");
            return false;
        }
    }

    /// <summary>
    /// Find and send command to console in one call
    /// </summary>
    public bool SendCommandToConsole(string command, string? windowTitle = null)
    {
        IntPtr windowHandle = FindConsoleWindow(windowTitle);

        if (windowHandle == IntPtr.Zero)
        {
            _logger.LogWarning("Could not find console window");
            return false;
        }

        return SendCommand(windowHandle, command);
    }
}
