using System.Globalization;

namespace WreckfestController.Services.Hook;

/// <summary>
/// The SERVER object's session state, an int at <c>+0x4</c>. Values from the game's
/// own state machine, confirmed live; see docs/finding-rvas.md (issue #189).
/// </summary>
public enum ServerSessionPhase
{
    Lobby = 0,
    Countdown = 1,
    Racing = 2,
    Results = 3,

    /// <summary>After the results, with the event loop on, while the next event is set up.</summary>
    NextEvent = 4,
}

/// <summary>
/// One answer to the hook's <c>__hook_session</c> command:
/// <c>OK session state=2 timer=-100000 counter=3 ended=0</c>.
/// <see cref="Phase"/> is null for a state value the game is not known to use, so a
/// caller can fail open on it instead of guessing.
/// </summary>
public sealed record HookSessionState(int State, int TimerMs, int EventCounter, bool Ended)
{
    public const string Command = "__hook_session";

    public ServerSessionPhase? Phase =>
        Enum.IsDefined(typeof(ServerSessionPhase), State) ? (ServerSessionPhase)State : null;

    public static bool TryParse(string? line, out HookSessionState? session)
    {
        session = null;
        const string prefix = "OK session ";
        if (line is null || !line.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var fields = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var pair in line[prefix.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            if (equals <= 0 ||
                !int.TryParse(pair[(equals + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
            {
                return false;
            }

            // A repeated key is malformed, not "the last one wins": failing here is what
            // keeps a garbled answer from reading as racing.
            if (!fields.TryAdd(pair[..equals], value))
            {
                return false;
            }
        }

        if (!fields.TryGetValue("state", out var state) ||
            !fields.TryGetValue("timer", out var timer) ||
            !fields.TryGetValue("counter", out var counter) ||
            !fields.TryGetValue("ended", out var ended))
        {
            return false;
        }

        session = new HookSessionState(state, timer, counter, ended != 0);
        return true;
    }
}
