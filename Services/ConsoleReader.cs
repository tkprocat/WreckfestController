using System.Drawing;
using System.Runtime.InteropServices;

namespace WreckfestController.Services;

/// <summary>
/// Reads text from console windows using screen capture and OCR
/// </summary>
public class ConsoleReader
{
    [DllImport("user32.dll")]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, int nFlags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private const int SW_RESTORE = 9;
    private const int SW_MAXIMIZE = 3;
    private const int PW_RENDERFULLCONTENT = 0x00000002;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    /// <summary>
    /// Capture screenshot of a console window by its handle
    /// </summary>
    public Bitmap? CaptureConsoleWindow(IntPtr windowHandle, int? targetWidth = null, int? targetHeight = null)
    {
        if (windowHandle == IntPtr.Zero)
            return null;

        // Restore window if minimized or maximized (for resizing)
        if (IsIconic(windowHandle) || (targetWidth.HasValue && targetHeight.HasValue && IsZoomed(windowHandle)))
        {
            ShowWindow(windowHandle, SW_RESTORE);
            Thread.Sleep(100); // Give window time to restore
        }

        // Resize window to specific dimensions if provided, otherwise maximize
        if (targetWidth.HasValue && targetHeight.HasValue)
        {
            SetWindowPos(windowHandle, IntPtr.Zero, 0, 0, targetWidth.Value, targetHeight.Value,
                SWP_NOMOVE | SWP_NOZORDER);
            Thread.Sleep(200); // Give window time to resize
        }
        else if (!IsZoomed(windowHandle))
        {
            ShowWindow(windowHandle, SW_MAXIMIZE);
            Thread.Sleep(200); // Give window time to maximize
        }

        // Bring window to foreground for better capture
        SetForegroundWindow(windowHandle);
        Thread.Sleep(100); // Give window time to come to front

        // Get window dimensions
        if (!GetWindowRect(windowHandle, out RECT rect))
            return null;

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;

        // Create bitmap to hold screenshot
        Bitmap bitmap = new Bitmap(width, height);

        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            IntPtr hdc = graphics.GetHdc();
            // Use PW_RENDERFULLCONTENT flag for better capture
            PrintWindow(windowHandle, hdc, PW_RENDERFULLCONTENT);
            graphics.ReleaseHdc(hdc);
        }

        return bitmap;
    }

    /// <summary>
    /// Find console window by class name (e.g., "ConsoleWindowClass")
    /// </summary>
    public IntPtr FindConsoleWindow(string? windowTitle = null)
    {
        return FindWindow("ConsoleWindowClass", windowTitle);
    }

    /// <summary>
    /// Find any window by partial title
    /// </summary>
    public IntPtr FindWindowByTitle(string partialTitle)
    {
        // This is a simplified version - you'd need EnumWindows for full search
        return FindWindow(null, partialTitle);
    }
}
