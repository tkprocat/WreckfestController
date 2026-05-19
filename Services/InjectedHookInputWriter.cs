using System.IO.Pipes;
using System.Text;
using System.Text.RegularExpressions;
using WreckfestController.Models;

namespace WreckfestController.Services;

public class InjectedHookInputWriter : IServerInputWriter, IPlayerSnapshotReader
{
    private const int ConnectTimeoutMs = 1000;
    private const string PlayerSnapshotCommand = "__hook_players";
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

        try
        {
            var responseLines = await SendPipeCommandAsync(command, processId);
            var response = responseLines.FirstOrDefault();

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
            var pipeName = GetPipeName(processId);
            _logger.LogWarning(ex, "Timed out sending command through injected hook input pipe {PipeName}", pipeName);
            return (false, $"Injected hook input timed out on {pipeName}");
        }
        catch (Exception ex)
        {
            var pipeName = GetPipeName(processId);
            _logger.LogError(ex, "Failed to send command through injected hook input pipe {PipeName}", pipeName);
            return (false, $"Injected hook input failed: {ex.Message}");
        }
    }

    public virtual async Task<(bool Success, string Message, IReadOnlyList<Player> Players)> ReadPlayerSnapshotAsync(int processId)
    {
        try
        {
            var responseLines = await SendPipeCommandAsync(PlayerSnapshotCommand, processId);
            var players = ParsePlayerSnapshot(responseLines);

            if (responseLines.Any(line => line.StartsWith("OK players", StringComparison.OrdinalIgnoreCase)))
            {
                return (true, $"Read {players.Count} players through injected hook", players);
            }

            var error = responseLines.FirstOrDefault(line => line.StartsWith("ERR", StringComparison.OrdinalIgnoreCase));
            return (false, string.IsNullOrWhiteSpace(error) ? "Injected hook player snapshot returned no OK response" : error, players);
        }
        catch (OperationCanceledException ex)
        {
            var pipeName = GetPipeName(processId);
            _logger.LogWarning(ex, "Timed out reading player snapshot through injected hook input pipe {PipeName}", pipeName);
            return (false, $"Injected hook player snapshot timed out on {pipeName}", Array.Empty<Player>());
        }
        catch (Exception ex)
        {
            var pipeName = GetPipeName(processId);
            _logger.LogError(ex, "Failed to read player snapshot through injected hook input pipe {PipeName}", pipeName);
            return (false, $"Injected hook player snapshot failed: {ex.Message}", Array.Empty<Player>());
        }
    }

    private static List<Player> ParsePlayerSnapshot(IReadOnlyList<string> responseLines)
    {
        var players = new List<Player>();

        foreach (var line in responseLines)
        {
            var match = Regex.Match(line, @"^PLAYER slot=(\d+) status=(\d+) flags=(\d+) ping=(-?\d+) name=(.*)$");
            if (!match.Success)
            {
                continue;
            }

            var name = match.Groups[5].Value.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            players.Add(new Player
            {
                Name = name.TrimStart('*'),
                JoinedAt = DateTime.UtcNow,
                IsBot = name.StartsWith('*'),
                Slot = int.Parse(match.Groups[1].Value),
                IsAdmin = (int.Parse(match.Groups[3].Value) & 1) != 0
            });
        }

        return players;
    }

    private static async Task<IReadOnlyList<string>> SendPipeCommandAsync(string command, int processId)
    {
        var pipeName = GetPipeName(processId);
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

        var responseLines = new List<string>();
        while (true)
        {
            var response = await reader.ReadLineAsync(cancellation.Token);
            if (response == null)
            {
                break;
            }

            responseLines.Add(response);
        }

        return responseLines;
    }

    private static string GetPipeName(int processId)
    {
        return $"WreckfestConsoleHookInput-{processId}";
    }
}
