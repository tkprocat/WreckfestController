using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.RegularExpressions;

namespace WreckfestController.Services;

public class InjectedHookOutputReader : IInjectedHookOutputReader
{
    private static readonly Regex WreckfestColorCodeRegex = new(@"\^[0-9A-Fa-f:]", RegexOptions.Compiled);

    private readonly ILogger<InjectedHookOutputReader> _logger;
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _listeners = new();
    private readonly ConcurrentDictionary<int, bool> _connectedProcesses = new();

    public InjectedHookOutputReader(ILogger<InjectedHookOutputReader> logger)
    {
        _logger = logger;
    }

    public event Action<string>? OutputReceived;
    public event Action<string>? HookOutputReceived;

    public string Mode => ServerOutputModes.InjectedHook;
    public bool IsMonitoring => !_listeners.IsEmpty;
    public bool IsHookConnected => _connectedProcesses.Values.Any(connected => connected);
    public int TargetProcessId { get; private set; }

    public Task<bool> StartAsync(int processId)
    {
        var pipeName = GetPipeName(processId);
        StartPipeListener(processId, pipeName);
        return Task.FromResult(true);
    }

    public Task StopAsync()
    {
        foreach (var processId in _listeners.Keys)
        {
            StopPipeListener(processId);
        }

        TargetProcessId = 0;
        return Task.CompletedTask;
    }

    public Task<(bool Success, string Message)> InjectAsync(int processId)
    {
        var hookDllPath = ResolveHookDllPath();
        if (hookDllPath == null)
        {
            return Task.FromResult((
                false,
                "Console hook DLL not found. Build NativeHooks\\WreckfestConsoleHook and copy WreckfestConsoleHook.dll next to the controller executable."));
        }

        var pipeName = GetPipeName(processId);
        StartPipeListener(processId, pipeName);

        if (!NativeConsoleHookInjector.InjectDll(processId, hookDllPath, TimeSpan.FromSeconds(10), out var error, out var wasAlreadyLoaded))
        {
            StopPipeListener(processId);
            return Task.FromResult((false, $"Console hook injection failed: {error}"));
        }

        var action = wasAlreadyLoaded ? "Reconnected existing" : "Injected";
        PublishHookOutput(processId, $"{action} {Path.GetFileName(hookDllPath)}. Waiting for hook output on pipe {pipeName}.");
        return Task.FromResult((true, $"Console hook {action.ToLowerInvariant()} for process {processId}"));
    }

    public static string NormalizeLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return string.Empty;
        }

        return WreckfestColorCodeRegex.Replace(line, string.Empty).Trim();
    }

    private static string? ResolveHookDllPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "WreckfestConsoleHook.dll"),
            Path.Combine(AppContext.BaseDirectory, "NativeHooks", "WreckfestConsoleHook.dll"),
            Path.Combine(Environment.CurrentDirectory, "WreckfestConsoleHook.dll"),
            Path.Combine(Environment.CurrentDirectory, "NativeHooks", "WreckfestConsoleHook", "x64", "Debug", "WreckfestConsoleHook.dll"),
            Path.Combine(Environment.CurrentDirectory, "NativeHooks", "WreckfestConsoleHook", "x64", "Release", "WreckfestConsoleHook.dll"),
            Path.Combine(Environment.CurrentDirectory, "NativeHooks", "WreckfestConsoleHook", "build", "Debug", "WreckfestConsoleHook.dll"),
            Path.Combine(Environment.CurrentDirectory, "NativeHooks", "WreckfestConsoleHook", "build", "Release", "WreckfestConsoleHook.dll")
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string GetPipeName(int processId)
    {
        return $"WreckfestConsoleHook-{processId}";
    }

    private void StartPipeListener(int processId, string pipeName)
    {
        StopPipeListener(processId);

        var cancellation = new CancellationTokenSource();
        _listeners[processId] = cancellation;
        TargetProcessId = processId;

        _ = Task.Run(() => ListenAsync(processId, pipeName, cancellation.Token));
    }

    private void StopPipeListener(int processId)
    {
        if (_listeners.TryRemove(processId, out var existing))
        {
            existing.Cancel();
        }

        _connectedProcesses.TryRemove(processId, out _);
    }

    private async Task ListenAsync(int processId, string pipeName, CancellationToken cancellationToken)
    {
        PublishHookOutput(processId, $"Listening for injected hook output on {pipeName}.");

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(cancellationToken);
                _connectedProcesses[processId] = true;
                PublishHookOutput(processId, "Injected hook connected.");

                using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                while (!cancellationToken.IsCancellationRequested && pipe.IsConnected)
                {
                    var line = await reader.ReadLineAsync(cancellationToken);
                    if (line == null)
                    {
                        break;
                    }

                    PublishHookOutput(processId, line);

                    var normalizedLine = NormalizeLine(line);
                    if (!string.IsNullOrWhiteSpace(normalizedLine))
                    {
                        OutputReceived?.Invoke(normalizedLine);
                    }
                }

                _connectedProcesses[processId] = false;
                PublishHookOutput(processId, "Injected hook disconnected.");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading console hook pipe {PipeName}", pipeName);
                PublishHookOutput(processId, $"Hook pipe error: {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
    }

    private void PublishHookOutput(int processId, string message)
    {
        HookOutputReceived?.Invoke($"[{DateTime.Now:HH:mm:ss}] PID {processId}: {message}");
    }
}
