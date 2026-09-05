using WreckfestController.Services;
using Xunit;

namespace WreckfestController.Tests.Services;

/// <summary>
/// <see cref="HookChatRecordTests"/> parses the record exactly as the hook writes it.
/// Production never sees that string: <c>InjectedHookOutputReader</c> raises
/// OutputReceivedFrom with the line it has already put through
/// <see cref="InjectedHookOutputReader.NormalizeLine"/>, and ServerManager demuxes
/// what arrives there. That gap is how colour-code stripping could break every chat
/// command while every record test stayed green, so these cover the real path.
/// </summary>
public class HookChatRecordPipelineTests
{
    private const char S = HookChatRecord.FieldSeparator;
    private const char E = HookChatRecord.RecordEnd;

    private static string BuildRecord(string ringIndex, string rawMessage, string consoleLine) =>
        $"{HookChatRecord.Marker}{S}{ringIndex}{S}{consoleLine}{S}{rawMessage}{E}";

    /// <summary>Mirrors the game's "^8%s%s^0%s" format, where the second %s is ": ".</summary>
    private static string Formatted(string name, string message) =>
        $"^9* 14:31:49^0 ^8{name}: ^0{message}";

    /// <summary>
    /// The reader's own decision, not a copy of it - a test that reimplements the
    /// transform under test passes whatever the reader does.
    /// </summary>
    private static string Delivered(string line) =>
        InjectedHookOutputReader.PrepareForFanout(line);

    [Theory]
    [InlineData("Procat", "!lucky")]
    [InlineData("Procat", "!help")]
    [InlineData("Procat", "!vote mixed_1 6")]
    // A name carrying the colour codes and colon that sender recovery has to survive.
    [InlineData("Foo:Bar", "!vote mixed_1 6")]
    public void Record_survives_the_normalisation_the_reader_applies(string name, string message)
    {
        var delivered = Delivered(BuildRecord("10", message, Formatted(name, message)));

        var record = HookChatRecord.TryParse(delivered);

        Assert.NotNull(record);
        Assert.Equal(10, record!.RingIndex);
        Assert.Equal(name, record.PlayerName);
        Assert.Equal(message, record.Message);
    }

    [Fact]
    public void Console_text_is_still_normalised()
    {
        // Not a record, so it keeps going through colour stripping as before.
        Assert.Equal(
            "* 14:31:49 Procat: !lucky",
            Delivered("^9* 14:31:49^0 ^8Procat: ^0!lucky"));
    }
}
