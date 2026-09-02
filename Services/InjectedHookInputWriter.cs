using System.IO.Pipes;
using System.Text;
using System.Text.RegularExpressions;
using WreckfestController.Models;

namespace WreckfestController.Services;

public class InjectedHookInputWriter : IServerInputWriter, IPlayerSnapshotReader
{
    // Player flag bits, confirmed against a live server (Wreckfest 1.308438) by
    // toggling privileges with /op and /demote and cross-checking the A/M marker in
    // the "list" output:
    //   normal player = 2, moderator = 18, admin = 50, bot = 10.
    // Bit 4 is set for moderators AND admins; bit 5 distinguishes admin from moderator.
    private const int PlayerFlagPrivileged = 1 << 4;   // 16
    private const int PlayerFlagAdmin = 1 << 5;        // 32

    private const int ConnectTimeoutMs = 1000;
    private const int ResponseTimeoutMs = 10000;
    private const string PlayerSnapshotCommand = "__hook_players";
    private readonly ILogger<InjectedHookInputWriter> _logger;

    public InjectedHookInputWriter(ILogger<InjectedHookInputWriter> logger)
    {
        _logger = logger;
    }

    public virtual async Task<(bool Success, string Message)> SendCommandAsync(string command, int processId)
    {
        command = command.TrimEnd('\r', '\n');
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
                // The hook echoes how it tokenized the command, e.g.
                // "OK dispatched command=track argument=sandpit_derby_2". Surfacing it
                // makes setting-style splitting visible instead of silently guessed at.
                _logger.LogDebug("Injected hook dispatched {Command} as {Response}", command, response);
                return (true, $"Command sent through injected hook: {response.Trim()}");
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

    /// <summary>
    /// Parses the hook's PLAYER lines into player records. Public so the
    /// bot/admin/colour-code handling can be covered directly by tests.
    /// </summary>
    public static List<Player> ParsePlayerSnapshot(IReadOnlyList<string> responseLines)
    {
        var players = new List<Player>();

        foreach (var line in responseLines)
        {
            var match = Regex.Match(line, @"^PLAYER slot=(\d+) status=(\d+) flags=(\d+) ping=(-?\d+) name=(.*)$");
            if (!match.Success)
            {
                continue;
            }

            // The hook returns the raw name, which carries Wreckfest colour codes
            // around the bot marker (e.g. "^2*^0eRacer"). Strip those before testing
            // for the leading '*', otherwise every bot is counted as a human - which
            // would skew vote majorities.
            var name = InjectedHookOutputReader.NormalizeLine(match.Groups[5].Value);
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var isBot = name.StartsWith('*');
            var flags = int.Parse(match.Groups[3].Value);
            var isAdmin = (flags & PlayerFlagAdmin) != 0;

            players.Add(new Player
            {
                Name = name.TrimStart('*').Trim(),
                JoinedAt = DateTime.UtcNow,
                IsBot = isBot,
                Slot = int.Parse(match.Groups[1].Value),
                IsAdmin = isAdmin,
                // Privileged but not admin means moderator.
                IsModerator = (flags & PlayerFlagPrivileged) != 0 && !isAdmin
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

        using (var connectCancellation = new CancellationTokenSource(ConnectTimeoutMs))
        {
            await pipe.ConnectAsync(connectCancellation.Token);
        }

        // Raw byte I/O rather than StreamWriter/StreamReader. The hook serves one
        // command per connection and then closes its end, so a StreamWriter would
        // throw "Pipe is broken" when its disposal flushed the already-closed pipe
        // - turning a completed round-trip into a reported failure.
        using var responseCancellation = new CancellationTokenSource(ResponseTimeoutMs);

        var payload = Encoding.UTF8.GetBytes(command + "\n");
        await pipe.WriteAsync(payload, responseCancellation.Token);
        await pipe.FlushAsync(responseCancellation.Token);

        var buffer = new byte[8192];
        var received = new MemoryStream();
        while (true)
        {
            int read;
            try
            {
                read = await pipe.ReadAsync(buffer, responseCancellation.Token);
            }
            catch (IOException)
            {
                // Server closed its end; whatever we already have is the response.
                break;
            }

            if (read <= 0)
            {
                break;
            }

            received.Write(buffer, 0, read);
        }

        var text = Encoding.UTF8.GetString(received.ToArray());
        return text
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();
    }

    private static string GetPipeName(int processId)
    {
        return $"WreckfestConsoleHookInput-{processId}";
    }
}
