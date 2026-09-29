using System.Runtime.InteropServices;

namespace WreckfestController.Services.ServerControl;

/// <summary>
/// A process's command line split into arguments the way Windows programs see them
/// (<c>CommandLineToArgvW</c>): quoted paths stay one argument, so a folder named
/// "-s copy" is not mistaken for the server's <c>-s</c> flag.
/// </summary>
public static class WindowsCommandLine
{
    /// <summary>The arguments, the program itself first. Empty for an empty command line.</summary>
    public static IReadOnlyList<string> Split(string? commandLine)
    {
        // CommandLineToArgvW answers an empty string with this process's own path.
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return [];
        }

        var argv = CommandLineToArgvW(commandLine, out var count);
        if (argv == IntPtr.Zero)
        {
            return [];
        }

        try
        {
            var arguments = new string[count];
            for (var i = 0; i < count; i++)
            {
                arguments[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size)) ?? string.Empty;
            }

            return arguments;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    /// <summary>
    /// Whether the command line starts a dedicated server: a standalone <c>-s</c> argument
    /// after the program, as the server is launched (<c>Wreckfest_x64.exe -s server_config=...</c>).
    /// </summary>
    public static bool HasServerFlag(string? commandLine) =>
        Split(commandLine).Skip(1).Any(argument => string.Equals(argument, "-s", StringComparison.OrdinalIgnoreCase));

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW([MarshalAs(UnmanagedType.LPWStr)] string lpCmdLine, out int pNumArgs);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
