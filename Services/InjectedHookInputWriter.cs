using System.IO.Pipes;
using System.Text;

namespace WreckfestController.Services;

public class InjectedHookInputWriter : IServerInputWriter
{
    private const int ConnectTimeoutMs = 1000;
    private readonly ILogger<InjectedHookInputWriter> _logger;

    public InjectedHookInputWriter(ILogger<InjectedHookInputWriter> logger)
    {
        _logger = logger;
    }

    public virtual async Task<(bool Success, string Message)> SendCommandAsync(string command, int processId)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return (false, "Command cannot be empty");
        }

        var pipeName = GetPipeName(processId);
        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            using var cancellation = new CancellationTokenSource(ConnectTimeoutMs);
            await pipe.ConnectAsync(cancellation.Token);

            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true
            };
            using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

            await writer.WriteLineAsync(command);
            var response = await reader.ReadLineAsync(cancellation.Token);

            if (response?.StartsWith("OK", StringComparison.OrdinalIgnoreCase) == true)
            {
                return (true, $"Command sent through injected hook: {command}");
            }

            return (false, string.IsNullOrWhiteSpace(response)
                ? "Injected hook input returned no response"
                : $"Injected hook input failed: {response}");
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(ex, "Timed out sending command through injected hook input pipe {PipeName}", pipeName);
            return (false, $"Injected hook input timed out on {pipeName}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send command through injected hook input pipe {PipeName}", pipeName);
            return (false, $"Injected hook input failed: {ex.Message}");
        }
    }

    private static string GetPipeName(int processId)
    {
        return $"WreckfestConsoleHookInput-{processId}";
    }
}
