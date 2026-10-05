using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using WreckfestController.Services.Hook;

namespace WreckfestController.Tests.Services.Hook;

/// <summary>
/// WreckfestConsoleHookShutdown stops and joins the hook's own threads before releasing
/// anything they use, so the DLL can be unloaded (#24).
///
/// The real DLL is injected, by the controller's own injector, into a throwaway child
/// process - not the test host, where other tests attach a ServerManager to the host's
/// own process id and its output listener competes for the same pipe names. The hook's
/// offsets cannot validate against that process, so it installs no hooks - detour
/// removal is covered by live testing against the game - but its output writer, input
/// pipe and hook threads start exactly as in the game.
/// </summary>
public sealed class NativeHookShutdownTests
{
    private const uint ErrorShutdownInProgress = 1115;
    private const string RefusedDispatch = "refused dispatch";

    [Fact]
    public async Task Shutdown_JoinsTheWorkers_AndLeavesTheDllSafeToUnload()
    {
        await using var host = HookHost.Start();
        await using var output = host.CreateOutputPipe();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        host.Inject();
        Assert.Equal(3u, host.Call("WreckfestConsoleHookVersion"));

        // Read continuously, as the controller does.
        await output.WaitForConnectionAsync(timeout.Token);
        var lines = new List<string>();
        var reader = ReadAllAsync(output, lines, timeout.Token);
        // Queued only once the pipe is up, so it always arrives through it.
        await WaitForLineAsync(lines, "WreckfestConsoleHook connected.", timeout.Token);

        // A command round trip proves the overlapped input pipe still answers.
        Assert.StartsWith("ERR dispatch failed", await host.SendInputCommandAsync("list", timeout.Token));

        // The input thread is waiting for a controller; shutdown has to wake it.
        var elapsed = host.Shutdown();
        Assert.True(elapsed < TimeSpan.FromSeconds(2), $"Shutdown took {elapsed}");

        // Teardown closes the pipe after its last line.
        await reader;
        Assert.Equal("WreckfestConsoleHook shut down.", lines[^1]);

        // Already done: answers 0 again, and the start exports refuse.
        host.Shutdown();
        Assert.Equal(ErrorShutdownInProgress, host.Call("WreckfestConsoleHookInitialize"));

        // Shutdown gave back the reference its first worker took, so the injector's
        // own is the last one. A worker left running would take the process down.
        host.FreeLibrary();
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.False(host.IsHookLoaded);
        Assert.False(host.Process.HasExited);
    }

    [Fact]
    public async Task Shutdown_GivesUpOnAControllerThatStoppedReading()
    {
        await using var host = HookHost.Start();
        await using var output = host.CreateOutputPipe();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        host.Inject();
        await output.WaitForConnectionAsync(timeout.Token);

        // The controller connects and never reads. Every refused command queues a
        // line, and the hook writes each line to its fallback log only after the
        // pipe has taken it. Once the log stops moving with lines still owed, the
        // writer is stuck on a full pipe, however large Windows made the buffer.
        var sent = 0;
        var logged = 0;
        var stalled = false;
        while (!stalled && sent < 1500)
        {
            for (var i = 0; i < 25; i++, sent++)
            {
                await host.SendInputCommandAsync("list", timeout.Token);
            }

            var before = host.CountFallbackLines(RefusedDispatch);
            await Task.Delay(500, timeout.Token);
            logged = host.CountFallbackLines(RefusedDispatch);
            stalled = logged < sent && logged == before;
        }

        Assert.True(stalled, $"The pipe never filled: {sent} sent, {logged} written");

        var elapsed = host.Shutdown();
        Assert.True(elapsed < TimeSpan.FromSeconds(4), $"Shutdown took {elapsed}");

        // The hook says when it cancels a pending write, so this proves the case was
        // reached rather than inferring it from timing.
        Assert.Equal(1, host.CountFallbackLines("gave up on a controller that stopped reading"));

        // Abandoning the controller loses nothing the fallback log would keep.
        Assert.Equal(sent, host.CountFallbackLines(RefusedDispatch));
        Assert.Equal(1, host.CountFallbackLines("WreckfestConsoleHook shut down."));

        host.FreeLibrary();
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.False(host.IsHookLoaded);
        Assert.False(host.Process.HasExited);
    }

    [Fact]
    public async Task Shutdown_InterruptsAHookThreadStillLookingForTheController()
    {
        await using var host = HookHost.Start();

        // No output pipe is listening, so the hook thread retries its connect for
        // about five seconds before giving up and saying so in its log.
        host.Inject();
        await Task.Delay(TimeSpan.FromSeconds(1));

        var elapsed = host.Shutdown();
        Assert.True(elapsed < TimeSpan.FromSeconds(2), $"Shutdown took {elapsed}");
        Assert.Equal(0, host.CountFallbackLines("could not connect to controller pipe"));

        host.FreeLibrary();
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.False(host.IsHookLoaded);
        Assert.False(host.Process.HasExited);
    }

    [Fact]
    public async Task AFreeLibraryWithoutShutdown_LeavesTheRunningHookMapped()
    {
        await using var host = HookHost.Start();
        await using var output = host.CreateOutputPipe();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        host.Inject();
        await output.WaitForConnectionAsync(timeout.Token);
        var lines = new List<string>();
        var reader = ReadAllAsync(output, lines, timeout.Token);
        // Queued only once the pipe is up, so it always arrives through it.
        await WaitForLineAsync(lines, "WreckfestConsoleHook connected.", timeout.Token);

        // The injector's reference goes, but the workers' pin holds the module.
        host.FreeLibrary();
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.True(host.IsHookLoaded);
        Assert.StartsWith("ERR dispatch failed", await host.SendInputCommandAsync("list", timeout.Token));

        // Shutdown then drops the last reference as its thread leaves the module.
        host.Shutdown();
        await reader;
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.False(host.IsHookLoaded);
        Assert.False(host.Process.HasExited);
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

    /// <summary>A disposable child process to inject the hook into.</summary>
    private sealed class HookHost : IAsyncDisposable
    {
        private const string HookFileName = "WreckfestConsoleHook.dll";
        private readonly string _dll;

        private HookHost(Process process, string dll)
        {
            Process = process;
            _dll = dll;
        }

        public Process Process { get; }

        private string FallbackLogPath =>
            Path.Combine(Path.GetTempPath(), $"wreckfest_console_hook_{Process.Id}.log");

        public bool IsHookLoaded => FindModule() != null;

        // ping.exe: a native x64 process that sits idle for as long as asked.
        public static HookHost Start()
        {
            var directory = Directory.CreateTempSubdirectory("wfc-hook-tests-");
            var dll = Path.Combine(directory.FullName, HookFileName);
            File.Copy(FindHookDll(), dll);

            var process = Process.Start(new ProcessStartInfo(
                Path.Combine(Environment.SystemDirectory, "ping.exe"), "-n 600 127.0.0.1")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            })!;
            process.BeginOutputReadLine();
            return new HookHost(process, dll);
        }

        // The controller's output pipe for this process. Asked for a small buffer so
        // it fills quickly; Windows may round that up.
        public NamedPipeServerStream CreateOutputPipe() =>
            new($"WreckfestConsoleHook-{Process.Id}", PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 1024, 1024);

        // Loads the hook and calls WreckfestConsoleHookInitialize, as INJECT does.
        public void Inject()
        {
            Assert.True(
                NativeConsoleHookInjector.InjectDll(Process.Id, _dll, TimeSpan.FromSeconds(10), out var error, out _),
                error);
        }

        // The way the controller calls an export: as the start routine of a remote
        // thread. A successful shutdown ends that thread with FreeLibraryAndExitThread.
        public uint Call(string export)
        {
            var module = FindModule() ?? throw new InvalidOperationException("The hook is not loaded.");
            var local = LoadLibraryEx(_dll, IntPtr.Zero, DontResolveDllReferences);
            Assert.NotEqual(IntPtr.Zero, local);
            try
            {
                var offset = NativeLibrary.GetExport(local, export) - local;
                return RunRemoteThread(module.BaseAddress + offset, IntPtr.Zero);
            }
            finally
            {
                FreeLibraryLocal(local);
            }
        }

        public TimeSpan Shutdown()
        {
            var stopwatch = Stopwatch.StartNew();
            Assert.Equal(0u, Call("WreckfestConsoleHookShutdown"));
            return stopwatch.Elapsed;
        }

        // kernel32 is mapped at the same address in every process for this boot.
        public void FreeLibrary()
        {
            var module = FindModule() ?? throw new InvalidOperationException("The hook is not loaded.");
            var freeLibrary = NativeLibrary.GetExport(NativeLibrary.Load("kernel32.dll"), "FreeLibrary");
            Assert.NotEqual(0u, RunRemoteThread(freeLibrary, module.BaseAddress));
        }

        // One command per connection, read to the end, as InjectedHookInputWriter does.
        public async Task<string> SendInputCommandAsync(string command, CancellationToken cancellationToken)
        {
            await using var pipe = new NamedPipeClientStream(".", $"WreckfestConsoleHookInput-{Process.Id}", PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(cancellationToken);
            await pipe.WriteAsync(Encoding.UTF8.GetBytes(command + "\n"), cancellationToken);

            using var response = new MemoryStream();
            await pipe.CopyToAsync(response, cancellationToken);
            return Encoding.UTF8.GetString(response.ToArray());
        }

        // The hook writes every line here too, after the pipe has taken it.
        public int CountFallbackLines(string text)
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

        public async ValueTask DisposeAsync()
        {
            try
            {
                Process.Kill();
                await Process.WaitForExitAsync();
            }
            catch (InvalidOperationException)
            {
            }

            TryDelete(FallbackLogPath);
            Process.Dispose();
            TryDelete(_dll);
            try
            {
                Directory.Delete(Path.GetDirectoryName(_dll)!);
            }
            catch (IOException)
            {
            }
        }

        private ProcessModule? FindModule()
        {
            Process.Refresh();
            return Process.Modules.Cast<ProcessModule>().FirstOrDefault(module =>
                string.Equals(module.FileName, _dll, StringComparison.OrdinalIgnoreCase));
        }

        private uint RunRemoteThread(IntPtr start, IntPtr parameter)
        {
            var thread = CreateRemoteThread(Process.Handle, IntPtr.Zero, UIntPtr.Zero, start, parameter, 0, IntPtr.Zero);
            if (thread == IntPtr.Zero)
            {
                throw new Win32Exception();
            }

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
        }

        private static string FindHookDll()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            {
                foreach (var configuration in new[] { "Debug", "Release" })
                {
                    var candidate = Path.Combine(directory.FullName, "NativeHooks", "WreckfestConsoleHook", "bin", configuration, HookFileName);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }

            throw new FileNotFoundException($"{HookFileName} was not found above the test output; building the app builds it.");
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

        private const uint DontResolveDllReferences = 0x00000001;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateRemoteThread(IntPtr process, IntPtr attributes, UIntPtr stackSize, IntPtr startAddress, IntPtr parameter, uint flags, IntPtr threadId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeThread(IntPtr thread, out uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryEx(string fileName, IntPtr file, uint flags);

        [DllImport("kernel32.dll", EntryPoint = "FreeLibrary", SetLastError = true)]
        private static extern bool FreeLibraryLocal(IntPtr module);
    }
}
