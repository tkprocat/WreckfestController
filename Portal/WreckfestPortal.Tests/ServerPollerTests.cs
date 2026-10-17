using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace WreckfestPortal.Tests;

public sealed class ServerPollerTests
{
    private const string Vanilla = "http://127.0.0.1:5100/vanilla";
    private const string Tvtp2 = "http://127.0.0.1:5102/tvtp2/";

    private readonly FakeController _controllers = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 14, 18, 0, 0, TimeSpan.Zero));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ServerPoller Poller() => new(
        Options.Create(new PortalOptions
        {
            TimeoutSeconds = 1,
            Servers =
            [
                new PortalServer { Id = "vanilla", Name = "Vanilla", ApiUrl = Vanilla },
                new PortalServer { Id = "tvtp2", Name = "TVTP2", Description = "Mod tracks", ApiUrl = Tvtp2 },
            ],
        }),
        new FakeHttpClientFactory(_controllers),
        _time,
        NullLogger<ServerPoller>.Instance);

    [Fact]
    public void BeforeTheFirstCheck_EveryServerIsChecking()
    {
        var cards = Poller().Cards;

        Assert.Equal(["vanilla", "tvtp2"], cards.Select(c => c.Id));
        Assert.All(cards, c => Assert.Equal(ServerState.Checking, c.State));
        Assert.All(cards, c => Assert.Null(c.CheckedAt));
    }

    [Fact]
    public async Task ARunningServer_IsOnline_WithWhatItsControllerSaid()
    {
        _controllers.Answer(Vanilla, FakeController.Overview());
        _controllers.Answer(Tvtp2, FakeController.Overview(isRunning: false, humans: 0, bots: 0, track: null));
        var poller = Poller();

        await poller.PollOnceAsync(Ct);

        var vanilla = poller.Cards[0];
        Assert.Equal(ServerState.Online, vanilla.State);
        Assert.Equal("^5Monday Night", vanilla.ServerName);
        Assert.Equal((3, 1, 24), (vanilla.Humans, vanilla.Bots, vanilla.MaxPlayers));
        Assert.Equal("Big Valley - Main Route", vanilla.CurrentTrack);
        Assert.Equal(_time.GetUtcNow(), vanilla.CheckedAt);
        Assert.Equal(_time.GetUtcNow(), vanilla.LastSeenAt);

        var tvtp2 = poller.Cards[1];
        Assert.Equal(ServerState.ServerStopped, tvtp2.State);
        Assert.Null(tvtp2.CurrentTrack);
        Assert.Equal("Mod tracks", tvtp2.Description);
    }

    [Fact]
    public async Task APathInTheAddress_IsKept_AndATrailingSlashIsNotDoubled()
    {
        _controllers.Answer(Vanilla, FakeController.Overview());
        _controllers.Answer(Tvtp2, FakeController.Overview());

        await Poller().PollOnceAsync(Ct);

        Assert.Equal(
            ["http://127.0.0.1:5100/vanilla/api/public/overview", "http://127.0.0.1:5102/tvtp2/api/public/overview"],
            _controllers.Requests.Select(u => u.ToString()).Order());
    }

    public static TheoryData<string> Failures => ["refused", "hang", "500", "not json", "no status", "cut short", "bad charset"];

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task AControllerThatDoesNotAnswerWell_IsUnreachable_KeepingWhatItLastSaid(string failure)
    {
        _controllers.Answer(Vanilla, FakeController.Overview());
        _controllers.Answer(Tvtp2, FakeController.Overview());
        var poller = Poller();
        await poller.PollOnceAsync(Ct);
        var seen = _time.GetUtcNow();

        _time.Advance(TimeSpan.FromSeconds(15));
        switch (failure)
        {
            case "refused": _controllers.Refuse(Vanilla); break;
            case "hang": _controllers.Hang(Vanilla); break;
            case "500": _controllers.Answer(Vanilla, "{}", HttpStatusCode.InternalServerError); break;
            case "not json": _controllers.Answer(Vanilla, "<html>proxy error</html>"); break;
            case "no status": _controllers.Answer(Vanilla, """{ "serverName": "x" }"""); break;
            case "cut short": _controllers.Throw(Vanilla, new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely.")); break;
            case "bad charset": _controllers.Throw(Vanilla, new InvalidOperationException("The character set provided in ContentType is invalid.")); break;
        }

        await poller.PollOnceAsync(Ct);

        var vanilla = poller.Cards[0];
        Assert.Equal(ServerState.Unreachable, vanilla.State);
        Assert.Equal(_time.GetUtcNow(), vanilla.CheckedAt);
        Assert.Equal(seen, vanilla.LastSeenAt);
        Assert.Equal("^5Monday Night", vanilla.ServerName);
        // One server's trouble is not another's.
        Assert.Equal(ServerState.Online, poller.Cards[1].State);
    }

    [Fact]
    public async Task AControllerNeverSeen_IsUnreachable_WithNothingToShow()
    {
        _controllers.Answer(Tvtp2, FakeController.Overview());
        var poller = Poller();

        await poller.PollOnceAsync(Ct);

        var vanilla = poller.Cards[0];
        Assert.Equal(ServerState.Unreachable, vanilla.State);
        Assert.Null(vanilla.LastSeenAt);
        Assert.Null(vanilla.ServerName);
    }

    [Fact]
    public async Task AControllerBack_IsOnlineAgain()
    {
        var poller = Poller();
        await poller.PollOnceAsync(Ct);

        _controllers.Answer(Vanilla, FakeController.Overview());
        await poller.PollOnceAsync(Ct);

        Assert.Equal(ServerState.Online, poller.Cards[0].State);
    }
}
