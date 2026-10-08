using System.Runtime.InteropServices;
using WreckfestController.Services.Hook;
using Xunit;

namespace WreckfestController.Tests.Services.Hook;

/// <summary>
/// Exports are called at addresses read from the loaded image's own export table, so the
/// call lands in the build that is loaded, whatever the DLL file holds now. Checked here
/// against this process, where GetProcAddress gives the right answer.
/// </summary>
public class NativeConsoleHookInjectorTests
{
    private const uint DontResolveDllReferences = 0x00000001;
    private static readonly IntPtr CurrentProcess = new(-1);

    [Theory]
    [InlineData("WreckfestConsoleHookShutdown")]
    [InlineData("WreckfestConsoleHookInitialize")]
    public void ResolvesAnExportFromTheLoadedImage(string exportName)
    {
        var module = LoadHookImage();
        try
        {
            Assert.True(
                NativeConsoleHookInjector.TryResolveRemoteExport(CurrentProcess, module, exportName, out var address, out var error),
                error);
            Assert.Equal(GetProcAddress(module, exportName), address);
        }
        finally
        {
            FreeLibrary(module);
        }
    }

    [Theory]
    [InlineData("WreckfestConsoleHookShut")]
    [InlineData("NoSuchExport")]
    public void RefusesANameThatIsNotExported(string exportName)
    {
        var module = LoadHookImage();
        try
        {
            Assert.False(NativeConsoleHookInjector.TryResolveRemoteExport(CurrentProcess, module, exportName, out var address, out _));
            Assert.Equal(IntPtr.Zero, address);
        }
        finally
        {
            FreeLibrary(module);
        }
    }

    // Mapped as an image without running DllMain, so nothing of the hook starts here.
    private static IntPtr LoadHookImage()
    {
        var module = LoadLibraryEx(FindHookDll(), IntPtr.Zero, DontResolveDllReferences);
        Assert.NotEqual(IntPtr.Zero, module);
        return module;
    }

    private static string FindHookDll()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            foreach (var configuration in new[] { "Debug", "Release" })
            {
                var candidate = Path.Combine(directory.FullName, "NativeHooks", "WreckfestConsoleHook", "bin", configuration, "WreckfestConsoleHook.dll");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new FileNotFoundException("WreckfestConsoleHook.dll was not found above the test output; building the app builds it.");
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hFile, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr hModule);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);
}
