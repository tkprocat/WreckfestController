using WreckfestController.Services;
using Xunit;

namespace WreckfestController.Tests.Services;

public class HookChatRecordTests
{
    private const char S = HookChatRecord.FieldSeparator;
    private const char E = HookChatRecord.RecordEnd;

    /// <summary>
    /// Builds a record the way the hook does: the ring index, the message exactly as
    /// the handler received it, and the line the game formatted from it.
    /// </summary>
    private static string BuildRecord(string ringIndex, string rawMessage, string consoleLine) =>
        $"{HookChatRecord.Marker}{S}{ringIndex}{S}{consoleLine}{S}{rawMessage}{E}";

    /// <summary>Mirrors the game's "^8%s%s^0%s" format, where the second %s is ": ".</summary>
    private static string Formatted(string name, string message) =>
        $"^9* 21:37:50^0 ^8{name}: ^0{message}";

    [Fact]
    public void Parses_a_well_formed_record()
    {
        var record = HookChatRecord.TryParse(
            BuildRecord("3", "!vote mixed_1 6", Formatted("Procat", "!vote mixed_1 6")));

        Assert.NotNull(record);
        Assert.Equal(3, record!.RingIndex);
        Assert.False(record.IsBot);
        Assert.Equal("Procat", record.PlayerName);
        Assert.Equal("!vote mixed_1 6", record.Message);
    }

    // Live testing found the sender arriving as "Procat:" - the ": " the game inserts
    // sits inside what looks like the name, and every command was dropped because no
    // such player exists.
    [Fact]
    public void Strips_the_separator_the_game_inserts_after_the_name()
    {
        var record = HookChatRecord.TryParse(
            BuildRecord("10", "!help", Formatted("Procat", "!help")));

        Assert.NotNull(record);
        Assert.Equal("Procat", record!.PlayerName);
    }

    // The input ring is newline delimited, so the handler receives "!help\n". A
    // command compared whole never matched while that was attached - found live,
    // where !track worked and !help did not.
    [Theory]
    [InlineData("!help\n")]
    [InlineData("!help\r\n")]
    [InlineData("!help ")]
    public void Strips_the_ring_terminator_from_the_message(string rawMessage)
    {
        var record = HookChatRecord.TryParse(
            BuildRecord("10", rawMessage, Formatted("Procat", "!help")));

        Assert.NotNull(record);
        Assert.Equal("!help", record!.Message);
    }

    // The entire point of reading chat structurally: the old regex used [^:]+ for the
    // name, so these players could never trigger a command.
    [Theory]
    [InlineData("Pro:cat")]
    [InlineData(":leading")]
    [InlineData("trailing:")]
    [InlineData("a:b:c")]
    public void Keeps_colons_that_belong_to_the_name(string name)
    {
        var record = HookChatRecord.TryParse(
            BuildRecord("4", "!yes", Formatted(name, "!yes")));

        Assert.NotNull(record);
        Assert.Equal(name, record!.PlayerName);
        Assert.Equal("!yes", record.Message);
    }

    [Fact]
    public void Detects_a_bot_sender()
    {
        var record = HookChatRecord.TryParse(
            BuildRecord("7", "!yes", Formatted("*eRacer", "!yes")));

        Assert.NotNull(record);
        Assert.True(record!.IsBot);
        Assert.Equal("eRacer", record.PlayerName);
    }

    // The hook sanitises control bytes out of both text fields, so a raw separator
    // never reaches the parser. What does reach it is anything a player can type,
    // which must survive untouched.
    [Theory]
    [InlineData("!vote mixed_1 6")]
    [InlineData("!say hello \"world\"")]
    [InlineData("!say café naïve")]
    [InlineData("!say 100% – done")]
    [InlineData("!say a:b:c")]
    public void Keeps_awkward_but_typable_messages_intact(string message)
    {
        var record = HookChatRecord.TryParse(
            BuildRecord("2", message, Formatted("Procat", message)));

        Assert.NotNull(record);
        Assert.Equal(message, record!.Message);
        Assert.Equal("Procat", record.PlayerName);
    }

    [Fact]
    public void Handles_a_message_at_the_games_127_character_limit()
    {
        var message = "!" + new string('a', 126);
        var record = HookChatRecord.TryParse(
            BuildRecord("1", message, Formatted("Procat", message)));

        Assert.NotNull(record);
        Assert.Equal(127, record!.Message.Length);
        Assert.Equal("Procat", record.PlayerName);
    }

    [Fact]
    public void Rejects_a_line_whose_console_text_does_not_carry_the_message()
    {
        var record = HookChatRecord.TryParse(
            BuildRecord("1", "!help", "^9* 21:37:50^0 ^8Procat: ^0something else"));

        Assert.Null(record);
    }

    [Fact]
    public void Rejects_a_console_line_with_no_name_marker()
    {
        Assert.Null(HookChatRecord.TryParse(BuildRecord("1", "!help", "* 21:37:50 Procat: !help")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("   ")]
    public void Rejects_an_empty_message(string rawMessage)
    {
        Assert.Null(HookChatRecord.TryParse(
            BuildRecord("1", rawMessage, Formatted("Procat", "x"))));
    }

    [Fact]
    public void Rejects_a_non_numeric_ring_index()
    {
        Assert.Null(HookChatRecord.TryParse(
            BuildRecord("not-a-number", "!help", Formatted("Procat", "!help"))));
    }

    [Fact]
    public void Rejects_an_empty_sender()
    {
        Assert.Null(HookChatRecord.TryParse(BuildRecord("1", "!help", "^8: ^0!help")));
    }

    [Fact]
    public void Rejects_a_record_with_too_few_fields()
    {
        Assert.Null(HookChatRecord.TryParse($"{HookChatRecord.Marker}{S}1{S}^8Procat: ^0!help{E}"));
    }

    [Fact]
    public void Rejects_a_record_with_no_terminator()
    {
        var full = BuildRecord("1", "!help", Formatted("Procat", "!help"));
        Assert.Null(HookChatRecord.TryParse(full[..^1]));
    }

    [Fact]
    public void Every_truncation_is_rejected_without_throwing()
    {
        var full = BuildRecord("1", "!help", Formatted("Procat", "!help"));
        for (var length = 0; length < full.Length; length++)
        {
            Assert.Null(HookChatRecord.TryParse(full[..length]));
        }
    }

    [Fact]
    public void Console_text_is_not_claimed_as_a_record()
    {
        Assert.False(HookChatRecord.LooksLikeRecord("* 21:37:50 Procat: !help"));
        Assert.Null(HookChatRecord.TryParse("* 21:37:50 Procat: !help"));
    }

    [Fact]
    public void A_truncated_record_is_still_claimed_so_it_never_leaks_to_the_text_path()
    {
        Assert.True(HookChatRecord.LooksLikeRecord(HookChatRecord.Marker));
        Assert.True(HookChatRecord.LooksLikeRecord($"{HookChatRecord.Marker}{S}1{S}!he"));
    }
}
