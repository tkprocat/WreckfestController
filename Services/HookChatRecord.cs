using System.Globalization;

namespace WreckfestController.Services;

/// <summary>
/// One chat message exactly as the injected hook saw it, before the game formatted
/// it into a console line.
///
/// The console line is lossy: sender and message are concatenated with ": ", so the
/// only way back is to guess where the name ends. The regex in
/// <c>ServerManager.ProcessChatCommandLine</c> guesses with <c>[^:]+</c>, which means
/// a player whose name contains a colon can never trigger a chat command. The hook
/// has both values separately, so this record carries them separately.
///
/// Wire format, one record per line on the existing output pipe:
/// <code>
/// \x12CHAT\x1f&lt;slot&gt;\x1f&lt;isBot&gt;\x1f&lt;name&gt;\x1f&lt;message&gt;\x13
/// </code>
/// The framing bytes are control characters that cannot occur in a console line, so
/// a record is never mistaken for output and output is never mistaken for a record.
/// </summary>
public sealed record HookChatRecord(int Slot, bool IsBot, string PlayerName, string Message)
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

    public static bool LooksLikeRecord(string? line) =>
        line != null && line.StartsWith(Marker, StringComparison.Ordinal);

    /// <summary>
    /// Returns null for anything that is not a complete, well-formed record. Never
    /// throws: this runs on the thread draining the hook's output pipe, and a
    /// malformed record must cost us one message, not the pipe.
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

        // Limited to four so the message keeps any separator bytes it contains
        // instead of being cut short by them. The hook sanitises control bytes out
        // of what it sends, but the parser must not depend on that.
        var fields = body.Split(FieldSeparator, 4);
        if (fields.Length != 4)
        {
            return null;
        }

        if (!int.TryParse(fields[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var slot))
        {
            return null;
        }

        bool isBot;
        switch (fields[1])
        {
            case "0":
                isBot = false;
                break;
            case "1":
                isBot = true;
                break;
            default:
                return null;
        }

        var playerName = fields[2].Trim();
        if (playerName.Length == 0)
        {
            return null;
        }

        return new HookChatRecord(slot, isBot, playerName, fields[3].Trim());
    }
}
