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
/// hooks - detour removal is covered by live testing against the game - but its output
/// writer, input pipe and hook threads start exactly as in the game.
/// </summary>
public sealed class NativeHookShutdownTests
{
    private const uint ErrorShutdownInProgress = 1115;
    private const string RefusedDispatch = "refused dispatch";

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

            Assert.Equal(3u, Export(module, "WreckfestConsoleHookVersion")());
            Assert.Equal(0u, initialize());

            // Read continuously, as the controller does.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await output.WaitForConnectionAsync(timeout.Token);
            var lines = new List<string>();
            var reader = ReadAllAsync(output, lines, timeout.Token);
            await WaitForLineAsync(lines, "input pipe starting", timeout.Token);

            // A command round trip proves the overlapped input pipe still answers.
            Assert.StartsWith("ERR dispatch failed", await SendInputCommandAsync("list", timeout.Token));

            // The input thread is waiting for a controller; shutdown has to wake it.
            var elapsed = await ShutdownAsync(module);
            Assert.True(elapsed < TimeSpan.FromSeconds(2), $"Shutdown took {elapsed}");

            // Teardown closes the pipe after its last line.
            await reader;
            Assert.Equal("WreckfestConsoleHook shut down.", lines[^1]);

            // Already done: answers 0 again, and does not give back a second reference.
            await ShutdownAsync(module);
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
        var dll = CopyHookDll();
        TryDelete(FallbackLogPath);
        await using var output = CreateOutputPipe();
        var module = NativeLibrary.Load(dll);
        var unloaded = false;
        try
        {
            Assert.Equal(0u, Export(module, "WreckfestConsoleHookInitialize")());
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await output.WaitForConnectionAsync(timeout.Token);

            // The controller connects and never reads. Every refused command queues a
            // line, and the hook writes each line to its fallback log only after the
            // pipe has taken it - so once the log falls behind, the pipe is full and a
            // write is pending, however large Windows made the buffer.
            var sent = 0;
            var logged = 0;
            while (sent - logged <= 20 && sent < 1500)
            {
                for (var i = 0; i < 25; i++, sent++)
                {
                    await SendInputCommandAsync("list", timeout.Token);
                }

                await Task.Delay(200, timeout.Token);
                logged = CountFallbackLines(RefusedDispatch);
            }

            Assert.True(sent - logged > 20, $"The pipe never filled: {sent} sent, {logged} written");

            // The pending write gets a one second grace once shutdown begins.
            var elapsed = await ShutdownAsync(module);
            Assert.InRange(elapsed, TimeSpan.FromSeconds(0.9), TimeSpan.FromSeconds(4));

            NativeLibrary.Free(module);
            unloaded = true;
            await Task.Delay(TimeSpan.FromSeconds(1));

            // Abandoning the controller loses nothing the fallback log would keep.
            Assert.Equal(sent, CountFallbackLines(RefusedDispatch));
            Assert.Equal(1, CountFallbackLines("WreckfestConsoleHook shut down."));
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
        var dll = CopyHookDll();
        TryDelete(FallbackLogPath);
        var module = NativeLibrary.Load(dll);
        var unloaded = false;
        try
        {
            // No output pipe is listening, so the hook thread retries its connect for
            // about five seconds before giving up and saying so in its log.
            Assert.Equal(0u, Export(module, "WreckfestConsoleHookReconnect")());
            await Task.Delay(TimeSpan.FromSeconds(1));

            var elapsed = await ShutdownAsync(module);
            Assert.True(elapsed < TimeSpan.FromSeconds(2), $"Shutdown took {elapsed}");
            Assert.Equal(0, CountFallbackLines("could not connect to controller pipe"));

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

    private static string FallbackLogPath =>
        Path.Combine(Path.GetTempPath(), $"wreckfest_console_hook_{Environment.ProcessId}.log");

    private static HookExport Export(IntPtr module, string name) =>
        Marshal.GetDelegateForFunctionPointer<HookExport>(NativeLibrary.GetExport(module, name));

    // The hook connects to the controller's output pipe by process id. Asked for a
    // small buffer so it fills quickly; Windows may round that up.
    private static NamedPipeServerStream CreateOutputPipe() =>
        new($"WreckfestConsoleHook-{Environment.ProcessId}", PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 1024, 1024);

    // The way the controller calls it: as the start routine of its own thread, since a
    // successful shutdown ends that thread with FreeLibraryAndExitThread. Bounded, so a
    // teardown that hangs fails the test rather than the whole run.
    private static async Task<TimeSpan> ShutdownAsync(IntPtr module)
    {
        var export = NativeLibrary.GetExport(module, "WreckfestConsoleHookShutdown");
        var stopwatch = Stopwatch.StartNew();
        var exitCode = await Task.Run(() =>
        {
            var thread = CreateThread(IntPtr.Zero, UIntPtr.Zero, export, IntPtr.Zero, 0, IntPtr.Zero);
            Assert.NotEqual(IntPtr.Zero, thread);
            try
            {
                Assert.Equal(0u, WaitForSingleObject(thread, 15000));
                Assert.True(GetExitCodeThread(thread, out var code));
                return code;
            }
            finally
            {
                CloseHandle(thread);
            }
        });

        Assert.Equal(0u, exitCode);
        return stopwatch.Elapsed;
    }

    // One command per connection, read to the end, as InjectedHookInputWriter does.
    private static async Task<string> SendInputCommandAsync(string command, CancellationToken cancellationToken)
    {
        await using var pipe = new NamedPipeClientStream(".", $"WreckfestConsoleHookInput-{Environment.ProcessId}", PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(cancellationToken);
        await pipe.WriteAsync(Encoding.UTF8.GetBytes(command + "\n"), cancellationToken);

        using var response = new MemoryStream();
        await pipe.CopyToAsync(response, cancellationToken);
        return Encoding.UTF8.GetString(response.ToArray());
    }

    private static int CountFallbackLines(string text)
    {
        if (!File.Exists(FallbackLogPath))
        {
            return 0;
        }

        // The hook keeps appending; share with it rather than lock it out.
        using var stream = new FileStream(FallbackLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var count = 0;
        while (reader.ReadLine() is { } line)
        {
            if (line.Contains(text, StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
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
        TryDelete(FallbackLogPath);
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateThread(IntPtr attributes, UIntPtr stackSize, IntPtr startAddress, IntPtr parameter, uint flags, IntPtr threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeThread(IntPtr thread, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
