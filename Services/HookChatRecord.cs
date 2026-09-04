using System.Globalization;

namespace WreckfestController.Services;

/// <summary>
/// One chat message as the injected hook observed it.
///
/// The console line is lossy: sender and message are concatenated with ": ", so the
/// only way back is to work out where the name ends. The regex in
/// <c>ServerManager.ProcessChatCommandLine</c> guesses with <c>[^:]+</c>, which means
/// a player whose name contains a colon can never trigger a chat command. The hook
/// sees the message before the game formats it, so the two can be separated properly.
///
/// Wire format, one record per line on the existing output pipe:
/// <code>
/// \x12CHAT\x1f&lt;ringIndex&gt;\x1f&lt;rawMessage&gt;\x1f&lt;consoleLine&gt;\x13
/// </code>
/// The framing bytes are control characters that cannot occur in a console line, so
/// a record is never mistaken for output and output is never mistaken for a record.
///
/// Every field is something the hook directly observed. Deriving the sender means
/// reasoning about "^8", "^0" and the ": " separator, and that reasoning lives here
/// rather than in the DLL: here it is unit tested, and a mistake costs a dropped
/// command instead of a crashed game process.
/// </summary>
public sealed record HookChatRecord(int RingIndex, bool IsBot, string PlayerName, string Message)
{
    public const char RecordStart = '\u0012';
    public const char RecordEnd = '\u0013';
    public const char FieldSeparator = '\u001F';

    /// <summary>
    /// Enough to claim a line as a chat record. Deliberately shorter than a
    /// well-formed record, so a truncated one is still recognised as ours and
    /// discarded rather than leaking into the console text fanout.
    /// </summary>
    public const string Marker = "\u0012CHAT";

    private const string Prefix = Marker + "\u001F";

    /// <summary>Marks the sender in the formatted line; the message follows "^0".</summary>
    private const string NameColorCode = "^8";
    private const string MessageColorCode = "^0";

    public static bool LooksLikeRecord(string? line) =>
        line != null && line.StartsWith(Marker, StringComparison.Ordinal);

    /// <summary>
    /// Returns null for anything that is not a complete, well-formed record, or
    /// whose sender cannot be recovered. Never throws: this runs on the thread
    /// draining the hook's output pipe, and a malformed record must cost us one
    /// message, not the pipe.
    /// </summary>
    public static HookChatRecord? TryParse(string? line)
    {
        if (line == null ||
            !line.StartsWith(Prefix, StringComparison.Ordinal) ||
            line.Length <= Prefix.Length ||
            line[^1] != RecordEnd)
        {
            return null;
        }

        var body = line[Prefix.Length..^1];

        // Limited to three so the message keeps any separator bytes it contains
        // instead of being cut short by them. The message is player-controlled, which
        // is why it is last; the console line the game produced is not.
        var fields = body.Split(FieldSeparator, 3);
        if (fields.Length != 3)
        {
            return null;
        }

        if (!int.TryParse(fields[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var ringIndex))
        {
            return null;
        }

        // The input ring is newline delimited, so the message the handler received
        // still carries its terminator while the formatted line does not. A command
        // compared whole, such as "!help", never matches while that is attached.
        var message = fields[2].TrimEnd('\n', '\r', ' ', '\t');
        if (message.Length == 0)
        {
            return null;
        }

        var sender = TryExtractSender(fields[1], message);
        if (sender == null)
        {
            return null;
        }

        var (playerName, isBot) = sender.Value;
        return new HookChatRecord(ringIndex, isBot, playerName, message);
    }

    /// <summary>
    /// Recovers the sender from a line the game formatted as "^8&lt;name&gt;: ^0&lt;message&gt;".
    /// The message is known exactly, so it is removed as a suffix rather than located
    /// with a delimiter - which is precisely what lets a name containing colons survive.
    /// </summary>
    private static (string Name, bool IsBot)? TryExtractSender(string consoleLine, string message)
    {
        var nameStart = consoleLine.LastIndexOf(NameColorCode, StringComparison.Ordinal);
        if (nameStart < 0)
        {
            return null;
        }

        var tail = consoleLine[(nameStart + NameColorCode.Length)..];

        var messageStart = tail.LastIndexOf(message, StringComparison.Ordinal);
        if (messageStart < 0)
        {
            return null;
        }

        var head = tail[..messageStart].TrimEnd();

        if (head.EndsWith(MessageColorCode, StringComparison.Ordinal))
        {
            head = head[..^MessageColorCode.Length].TrimEnd();
        }

        // Exactly one trailing colon: that is the separator the game inserted. Any
        // colons inside the name are the player's own and must survive.
        if (head.EndsWith(':'))
        {
            head = head[..^1].TrimEnd();
        }

        var isBot = head.StartsWith('*');
        if (isBot)
        {
            head = head[1..];
        }

        return head.Length == 0 ? null : (head, isBot);
    }
}
