using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace WreckfestController.Tests.Services.Hook;

/// <summary>
/// WreckfestConsoleHookShutdown stops and joins the hook's own threads before releasing
/// anything they use, so the DLL can be unloaded (#24). The real DLL is loaded into the
/// test process. Its offsets cannot validate against the test host, so it installs no
/// hooks, but its output writer and input pipe threads start exactly as in the game.
/// </summary>
public sealed class NativeHookShutdownTests
{
    private const uint ErrorShutdownInProgress = 1115;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint HookExport();

    [Fact]
    public async Task Shutdown_JoinsTheWorkers_AndLeavesTheDllSafeToUnload()
    {
        var dll = CopyHookDll();
        await using var output = CreateOutputPipe();
        var module = NativeLibrary.Load(dll);
        var unloaded = false;
        try
        {
            var initialize = Export(module, "WreckfestConsoleHookInitialize");
            var shutdown = Export(module, "WreckfestConsoleHookShutdown");

            Assert.Equal(3u, Export(module, "WreckfestConsoleHookVersion")());
            Assert.Equal(0u, initialize());

            // Read continuously, as the controller does.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await output.WaitForConnectionAsync(timeout.Token);
            var lines = new List<string>();
            var reader = ReadAllAsync(output, lines, timeout.Token);
            await WaitForLineAsync(lines, "input pipe starting", timeout.Token);

            // The input thread is waiting for a controller; shutdown has to wake it.
            var elapsed = await ShutdownAsync(shutdown);
            Assert.True(elapsed < TimeSpan.FromSeconds(2), $"Shutdown took {elapsed}");

            // Teardown closes the pipe after its last line.
            await reader;
            Assert.Equal("WreckfestConsoleHook shut down.", lines[^1]);

            Assert.Equal(0u, shutdown());
            Assert.Equal(ErrorShutdownInProgress, initialize());

            NativeLibrary.Free(module);
            unloaded = true;

            // A worker left running would now execute unmapped code and take the
            // test host down with it.
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
        finally
        {
            // After a failed shutdown the module stays loaded, since unloading it then
            // is exactly the crash under test, and its file cannot be deleted.
            if (unloaded)
            {
                DeleteTestFiles(dll);
            }
        }
    }

    [Fact]
    public async Task Shutdown_GivesUpOnAControllerThatStoppedReading()
    {
        // The controller connects and never reads. The pipe has no buffer, so the
        // writer's first line stays pending until shutdown abandons it.
        var dll = CopyHookDll();
        await using var output = CreateOutputPipe();
        var module = NativeLibrary.Load(dll);
        var unloaded = false;
        try
        {
            Assert.Equal(0u, Export(module, "WreckfestConsoleHookInitialize")());
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await output.WaitForConnectionAsync(timeout.Token);
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            var elapsed = await ShutdownAsync(Export(module, "WreckfestConsoleHookShutdown"));
            Assert.True(elapsed < TimeSpan.FromSeconds(4), $"Shutdown took {elapsed}");

            NativeLibrary.Free(module);
            unloaded = true;
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
        finally
        {
            if (unloaded)
            {
                DeleteTestFiles(dll);
            }
        }
    }

    [Fact]
    public async Task Shutdown_InterruptsAHookThreadStillLookingForTheController()
    {
        // No output pipe is listening, so the hook thread sits in its connect retries,
        // which take several seconds left alone.
        var dll = CopyHookDll();
        var module = NativeLibrary.Load(dll);
        var unloaded = false;
        try
        {
            Assert.Equal(0u, Export(module, "WreckfestConsoleHookReconnect")());

            var elapsed = await ShutdownAsync(Export(module, "WreckfestConsoleHookShutdown"));
            Assert.True(elapsed < TimeSpan.FromSeconds(2), $"Shutdown took {elapsed}");

            NativeLibrary.Free(module);
            unloaded = true;
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
        finally
        {
            if (unloaded)
            {
                DeleteTestFiles(dll);
            }
        }
    }

    private static HookExport Export(IntPtr module, string name) =>
        Marshal.GetDelegateForFunctionPointer<HookExport>(NativeLibrary.GetExport(module, name));

    // The hook connects to the controller's output pipe by process id. No buffer,
    // like the controller's own, so a write completes only once it has been read.
    private static NamedPipeServerStream CreateOutputPipe() =>
        new($"WreckfestConsoleHook-{Environment.ProcessId}", PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

    // Off the test thread and bounded, so a teardown that hangs fails the test rather
    // than the whole run. The export returns 0 on success.
    private static async Task<TimeSpan> ShutdownAsync(HookExport shutdown)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await Task.Run(() => shutdown()).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(0u, result);
        return stopwatch.Elapsed;
    }

    private static async Task ReadAllAsync(Stream output, List<string> lines, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(output, Encoding.UTF8, leaveOpen: true);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            lock (lines)
            {
                lines.Add(line);
            }
        }
    }

    private static async Task WaitForLineAsync(List<string> lines, string expected, CancellationToken cancellationToken)
    {
        while (true)
        {
            lock (lines)
            {
                if (lines.Any(line => line.Contains(expected, StringComparison.Ordinal)))
                {
                    return;
                }
            }

            await Task.Delay(50, cancellationToken);
        }
    }

    // Loaded from a copy so the build's own DLL is never held open, and so each test
    // gets a fresh module with fresh state.
    private static string CopyHookDll()
    {
        var source = FindHookDll();
        var copy = Path.Combine(Path.GetTempPath(), $"WreckfestConsoleHook-test-{Guid.NewGuid():N}.dll");
        File.Copy(source, copy);
        return copy;
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

    // The copy, and the fallback log the hook writes beside every line it sends.
    private static void DeleteTestFiles(string dll)
    {
        TryDelete(dll);
        TryDelete(Path.Combine(Path.GetTempPath(), $"wreckfest_console_hook_{Environment.ProcessId}.log"));
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
