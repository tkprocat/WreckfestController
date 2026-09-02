using WreckfestController.Services;

namespace WreckfestController.Tests.Services;

public class InjectedHookPlayerSnapshotTests
{
    // Captured verbatim from a live server (build 1.308438). The bot marker '*'
    // is wrapped in Wreckfest colour codes, which previously defeated bot
    // detection and left every bot counted as a human.
    private static readonly string[] LiveSnapshot =
    [
        "PLAYER slot=1 status=4 flags=10 ping=0 name=^2*^0eRacer",
        "PLAYER slot=2 status=4 flags=10 ping=0 name=^2*^0Djkevino",
        "PLAYER slot=5 status=2 flags=50 ping=32 name=Procat",
        "OK players count=3"
    ];

    [Fact]
    public void ParsePlayerSnapshot_DetectsBots_ThroughColorCodes()
    {
        var players = InjectedHookInputWriter.ParsePlayerSnapshot(LiveSnapshot);

        Assert.Equal(3, players.Count);
        Assert.True(players[0].IsBot);
        Assert.True(players[1].IsBot);
        Assert.False(players[2].IsBot);
    }

    [Fact]
    public void ParsePlayerSnapshot_StripsColorCodesAndBotMarkerFromName()
    {
        var players = InjectedHookInputWriter.ParsePlayerSnapshot(LiveSnapshot);

        Assert.Equal("eRacer", players[0].Name);
        Assert.Equal("Djkevino", players[1].Name);
        Assert.Equal("Procat", players[2].Name);
    }

    [Fact]
    public void ParsePlayerSnapshot_ReadsSlotAndAdminFlag()
    {
        var players = InjectedHookInputWriter.ParsePlayerSnapshot(LiveSnapshot);

        Assert.Equal(1, players[0].Slot);
        Assert.Equal(5, players[2].Slot);
        Assert.False(players[0].IsAdmin);   // flags=10 -> bot, no privilege bits
        Assert.True(players[2].IsAdmin);    // flags=50 -> bits 4 and 5 set
    }

    [Fact]
    public void ParsePlayerSnapshot_IgnoresNonPlayerLines()
    {
        var players = InjectedHookInputWriter.ParsePlayerSnapshot(
            ["OK players count=0", "ERR something went wrong", ""]);

        Assert.Empty(players);
    }

    // Flag values captured from a live server (Wreckfest 1.308438) by toggling
    // privileges with /op and /demote and cross-checking the A/M marker in "list".
    [Theory]
    [InlineData(2,  false, false)]  // normal player
    [InlineData(18, false, true)]   // moderator - "M" in list
    [InlineData(50, true,  false)]  // admin     - "A" in list
    public void ParsePlayerSnapshot_DecodesPrivilegeFlags(int flags, bool expectAdmin, bool expectModerator)
    {
        var players = InjectedHookInputWriter.ParsePlayerSnapshot(
            [$"PLAYER slot=1 status=2 flags={flags} ping=1 name=Procat"]);

        var player = Assert.Single(players);
        Assert.Equal(expectAdmin, player.IsAdmin);
        Assert.Equal(expectModerator, player.IsModerator);
        Assert.Equal(expectAdmin || expectModerator, player.IsPrivileged);
    }

    [Fact]
    public void ParsePlayerSnapshot_DoesNotTreatBit0AsAdmin()
    {
        // The original decode used (flags & 1), which is never set on a real player -
        // admins were silently treated as ordinary players.
        var players = InjectedHookInputWriter.ParsePlayerSnapshot(
            ["PLAYER slot=1 status=2 flags=50 ping=1 name=Procat"]);

        Assert.True(Assert.Single(players).IsAdmin);
    }

    [Fact]
    public void ParsePlayerSnapshot_BotsAreNeitherAdminNorModerator()
    {
        var players = InjectedHookInputWriter.ParsePlayerSnapshot(
            ["PLAYER slot=1 status=4 flags=10 ping=0 name=^2*^0eRacer"]);

        var bot = Assert.Single(players);
        Assert.True(bot.IsBot);
        Assert.False(bot.IsPrivileged);
    }
}
