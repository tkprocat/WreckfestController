using WreckfestController.Services;
using Xunit;

namespace WreckfestController.Tests.Services;

/// <summary>
/// The structured chat record exists because the console line cannot express a
/// sender and a message separately. These tests pin the cases that broke the text
/// parsing, above all a name containing a colon.
/// </summary>
public class HookChatRecordTests
{
    private const char Separator = HookChatRecord.FieldSeparator;
    private const char End = HookChatRecord.RecordEnd;

    private static string BuildRecord(string slot, string isBot, string name, string message) =>
        $"{HookChatRecord.Marker}{Separator}{slot}{Separator}{isBot}{Separator}{name}{Separator}{message}{End}";

    [Fact]
    public void TryParse_WellFormedRecord_ReturnsSenderMessageSlotAndBotFlag()
    {
        var record = HookChatRecord.TryParse(BuildRecord("3", "0", "Procat", "!vote mixed_1 6"));

        Assert.NotNull(record);
        Assert.Equal(3, record!.Slot);
        Assert.False(record.IsBot);
        Assert.Equal("Procat", record.PlayerName);
        Assert.Equal("!vote mixed_1 6", record.Message);
    }

    [Fact]
    public void TryParse_BotRecord_SetsBotFlag()
    {
        var record = HookChatRecord.TryParse(BuildRecord("7", "1", "eRacer", "!yes"));

        Assert.NotNull(record);
        Assert.True(record!.IsBot);
        Assert.Equal("eRacer", record.PlayerName);
    }

    /// <summary>
    /// The bug this whole path exists to fix. The console regex matches the name with
    /// [^:]+, so a colon in the name means the line never matches and the player's
    /// vote is silently dropped. The record keeps the name whole.
    /// </summary>
    [Theory]
    [InlineData("Foo:Bar")]
    [InlineData(":LeadingColon")]
    [InlineData("TrailingColon:")]
    [InlineData("a:b:c:d")]
    public void TryParse_NameContainingColon_KeepsTheWholeName(string name)
    {
        var record = HookChatRecord.TryParse(BuildRecord("1", "0", name, "!yes"));

        Assert.NotNull(record);
        Assert.Equal(name, record!.PlayerName);
        Assert.Equal("!yes", record.Message);
    }

    [Fact]
    public void TryParse_MessageContainingFieldSeparator_KeepsItInTheMessage()
    {
        // The hook sanitises control bytes out of what it sends, but the parser must
        // not depend on that: a stray separator belongs to the message, and must not
        // shear the record into an extra field.
        var message = $"!say a{Separator}b{Separator}c";

        var record = HookChatRecord.TryParse(BuildRecord("2", "0", "Procat", message));

        Assert.NotNull(record);
        Assert.Equal("Procat", record!.PlayerName);
        Assert.Equal(message, record.Message);
    }

    [Theory]
    [InlineData("!vote track_with_\"quotes\" 4")]
    [InlineData("!search a|b\\c/d")]
    [InlineData("!say <tag> & åäö 中文")]
    [InlineData("!say {}[]()%s%d")]
    public void TryParse_AwkwardMessageBytes_SurviveUnchanged(string message)
    {
        var record = HookChatRecord.TryParse(BuildRecord("4", "0", "Procat", message));

        Assert.NotNull(record);
        Assert.Equal(message, record!.Message);
    }

    [Fact]
    public void TryParse_MaximumLengthMessage_RoundTripsExactly()
    {
        // 127 characters is the game's cap; see docs/finding-rvas.md.
        var message = "!" + new string('x', 126);
        Assert.Equal(127, message.Length);

        var record = HookChatRecord.TryParse(BuildRecord("5", "0", "Procat", message));

        Assert.NotNull(record);
        Assert.Equal(message, record!.Message);
        Assert.Equal(127, record.Message.Length);
    }

    [Fact]
    public void TryParse_TruncatedRecord_ReturnsNullWithoutThrowing()
    {
        var full = BuildRecord("1", "0", "Procat", "!vote mixed_1 6");

        for (var length = 1; length < full.Length; length++)
        {
            var truncated = full[..length];
            var exception = Xunit.Record.Exception(() => HookChatRecord.TryParse(truncated));
            Assert.Null(exception);
            Assert.Null(HookChatRecord.TryParse(truncated));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("* 22:42:03 Procat: !vote mixed_1 6")]
    [InlineData("WreckfestConsoleHook connected.")]
    public void TryParse_ConsoleText_ReturnsNull(string line)
    {
        Assert.Null(HookChatRecord.TryParse(line));
        Assert.False(HookChatRecord.LooksLikeRecord(line));
    }

    [Fact]
    public void TryParse_Null_ReturnsNull()
    {
        Assert.Null(HookChatRecord.TryParse(null));
        Assert.False(HookChatRecord.LooksLikeRecord(null));
    }

    [Fact]
    public void TryParse_TooFewFields_ReturnsNull()
    {
        var line = $"{HookChatRecord.Marker}{Separator}1{Separator}0{Separator}Procat{End}";

        Assert.Null(HookChatRecord.TryParse(line));
        Assert.True(HookChatRecord.LooksLikeRecord(line));
    }

    [Theory]
    [InlineData("notanumber")]
    [InlineData("")]
    [InlineData("1.5")]
    public void TryParse_UnparsableSlot_ReturnsNull(string slot)
    {
        Assert.Null(HookChatRecord.TryParse(BuildRecord(slot, "0", "Procat", "!yes")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("2")]
    [InlineData("true")]
    public void TryParse_UnparsableBotFlag_ReturnsNull(string isBot)
    {
        Assert.Null(HookChatRecord.TryParse(BuildRecord("1", isBot, "Procat", "!yes")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParse_EmptyName_ReturnsNull(string name)
    {
        Assert.Null(HookChatRecord.TryParse(BuildRecord("1", "0", name, "!yes")));
    }

    [Fact]
    public void TryParse_MissingTerminator_ReturnsNull()
    {
        var line = BuildRecord("1", "0", "Procat", "!yes")[..^1];

        Assert.Null(HookChatRecord.TryParse(line));
        Assert.True(HookChatRecord.LooksLikeRecord(line));
    }

    [Fact]
    public void LooksLikeRecord_MarkerWithoutAValidBody_IsStillClaimed()
    {
        // A malformed record must be recognised as ours so it is dropped, rather than
        // falling through into the console text fanout.
        Assert.True(HookChatRecord.LooksLikeRecord(HookChatRecord.Marker));
        Assert.Null(HookChatRecord.TryParse(HookChatRecord.Marker));
    }
}
