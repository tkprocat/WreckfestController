using Microsoft.Extensions.Logging;
using Moq;
using WreckfestController.Services;
using Xunit;

namespace WreckfestController.Tests.Services;

public class ServerEventReaderTests
{
    // Captured verbatim from a live server: five bot joins.
    private const string LiveRingHex =
        "12275e322a5e30655261636572130a12275e322a5e30446a6b6576696e6f130a" +
        "12275e322a5e30536d69646765793837130a";

    private static byte[] Hex(string s) => Convert.FromHexString(s);

    [Fact]
    public void Parse_DecodesLiveJoinEvents()
    {
        var events = ServerEventReader.Parse(Hex(LiveRingHex), argCounts: null);

        Assert.Equal(3, events.Count);
        Assert.All(events, e => Assert.Equal(ServerEvent.PlayerHasJoined, e.Id));
        // Colour codes are stripped; the bot marker survives for the tracker.
        Assert.Equal("*eRacer", events[0].Name);
        Assert.Equal("*Djkevino", events[1].Name);
        Assert.Equal("*Smidgey87", events[2].Name);
    }

    [Fact]
    public void Parse_ReadsQuitReason()
    {
        // 0x21 = 0x20 + 0x01 -> QUIT_TIMEOUT
        var span = Hex("1221" + Convert.ToHexString("Procat"u8.ToArray()) + "130a");
        var e = Assert.Single(ServerEventReader.Parse(span, null));

        Assert.Equal(ServerEvent.QuitTimeout, e.Id);
        Assert.True(e.IsQuit);
        Assert.Equal("timeout", e.QuitReason);
        Assert.Equal("Procat", e.Name);
    }

    [Fact]
    public void Parse_ReadsPrivilegeEvents()
    {
        var span = Hex("1235" + Convert.ToHexString("Mod"u8.ToArray()) + "130a"    // 0x15 moderator
                     + "1236" + Convert.ToHexString("Adm"u8.ToArray()) + "130a");  // 0x16 admin
        var events = ServerEventReader.Parse(span, null);

        Assert.Equal(2, events.Count);
        Assert.Equal(ServerEvent.NewModerator, events[0].Id);
        Assert.Equal(ServerEvent.NewAdmin, events[1].Id);
    }

    [Fact]
    public void Parse_SkipsArgumentBytesSoTheyDoNotBecomePartOfTheName()
    {
        // 0x3A = 0x20 + 0x1A (SHUTDOWN_WARN), which the table says carries one arg.
        // The arg byte is stored as value + 0x20, so 0x25 means 5.
        var counts = new short[0x21];
        counts[0x1A] = 1;
        var span = Hex("123A25" + Convert.ToHexString("Server"u8.ToArray()) + "130a");

        var e = Assert.Single(ServerEventReader.Parse(span, counts));
        Assert.Equal(0x1A, e.Id);
        Assert.Equal("Server", e.Name);          // not "%Server"
        Assert.Equal(5, Assert.Single(e.Args));
    }

    [Fact]
    public void Parse_IgnoresGarbageRatherThanInventingEvents()
    {
        // No marker, and a marker with an out-of-range id.
        Assert.Empty(ServerEventReader.Parse(Hex("deadbeef"), null));
        Assert.Empty(ServerEventReader.Parse(Hex("12ff41424313"), null));
    }

    [Fact]
    public void Parse_StopsAtATruncatedTrailingEntry()
    {
        // Second entry has no terminator: it arrives on the next poll.
        var span = Hex("1227" + Convert.ToHexString("Bob"u8.ToArray()) + "130a"
                     + "1227" + Convert.ToHexString("Half"u8.ToArray()));

        var e = Assert.Single(ServerEventReader.Parse(span, null));
        Assert.Equal("Bob", e.Name);
    }

    [Fact]
    public async Task PollAsync_FirstPollAdoptsCursorWithoutReplayingHistory()
    {
        var reader = MakeReader(cursor: 500, ring: Hex(LiveRingHex));
        var (events, overflowed) = await reader.PollAsync();

        Assert.Empty(events);
        Assert.False(overflowed);
    }

    [Fact]
    public async Task PollAsync_FlagsOverflowWhenMoreThanARingWasWritten()
    {
        long cursor = 100;
        var reader = MakeReader(() => cursor, _ => new byte[1024]);

        await reader.PollAsync();      // adopt
        cursor += 0x2000;              // two rings' worth
        var (_, overflowed) = await reader.PollAsync();

        Assert.True(overflowed);
    }

    [Fact]
    public async Task PollAsync_TreatsACursorGoingBackwardsAsARestart()
    {
        long cursor = 5000;
        var reader = MakeReader(() => cursor, _ => new byte[1024]);

        await reader.PollAsync();
        cursor = 10;                   // server restarted
        var (events, overflowed) = await reader.PollAsync();

        Assert.Empty(events);
        Assert.True(overflowed);
    }

    private static ServerEventReader MakeReader(long cursor, byte[] ring) =>
        MakeReader(() => cursor, _ => ring);

    private static ServerEventReader MakeReader(Func<long> cursor, Func<int, byte[]> ring)
    {
        return new ServerEventReader(
            (rva, size) =>
            {
                if (size == 8) return Task.FromResult<byte[]?>(BitConverter.GetBytes(cursor()));
                var data = ring(size);
                var slice = new byte[size];
                Array.Copy(data, slice, Math.Min(size, data.Length));
                return Task.FromResult<byte[]?>(slice);
            },
            Mock.Of<ILogger<ServerEventReader>>());
    }
}
