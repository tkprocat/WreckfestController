using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using WreckfestController.Data.Cups;
using WreckfestController.Services.Config;
using WreckfestController.Services.Cups;
using WreckfestController.Services.Hook;
using WreckfestController.Services.Publishing;
using WreckfestController.Services.ServerControl;
using WreckfestController.Services.Tracking;

namespace WreckfestController.Tests.Services.Cups;

/// <summary>
/// The active cup's run after its restart (#204): the warning before the start, /cupreset at
/// the first lobby at or after it, and the end. A real database; the game is mocked, and every
/// command it is sent is recorded in order.
/// </summary>
public sealed class CupRunServiceTests : IDisposable
{
    private readonly CupTestDatabase _db = new();
    private readonly Mock<ServerManager> _server;
    private readonly Mock<EventLoopControl> _eventLoop;
    private readonly Mock<ConfigService> _config;
    private readonly Mock<IServerEventPublisher> _publisher = new();
    private readonly List<string> _sent = new();
    private readonly List<IReadOnlyDictionary<string, string>> _written = new();
    private ServerSessionPhase? _session = ServerSessionPhase.Lobby;
    private bool _restarting;
    private readonly CupRunService _runs;

    public CupRunServiceTests()
    {
        var events = Mock.Of<IServerEventPublisher>();
        _server = new Mock<ServerManager>(
            new ConfigurationBuilder().Build(),
            TestSettings.Server(),
            TestSettings.SteamCmd(),
            Mock.Of<ILogger<ServerManager>>(),
            new PlayerTracker(Mock.Of<ILogger<PlayerTracker>>(), events),
            new TrackChangeTracker(Mock.Of<ILogger<TrackChangeTracker>>(), events),
            new ServerInfoTracker(Mock.Of<ILogger<ServerInfoTracker>>()),
            events);
        _server.Setup(s => s.SendCommandAsync(It.IsAny<string>()))
            .ReturnsAsync((string command) =>
            {
                _sent.Add(command);
                return (true, "OK dispatched");
            });
        _server.Setup(s => s.ReadHookSessionAsync())
            .ReturnsAsync(() => _session is { } phase ? new HookSessionState((int)phase, 0, 0, false) : null);

        _eventLoop = new Mock<EventLoopControl>(_server.Object);
        _eventLoop.Setup(l => l.RestartAsync())
            .ReturnsAsync(() =>
            {
                _sent.Add("<rotation back to the beginning>");
                return new EventLoopState(true, 0, 4);
            });

        _config = new Mock<ConfigService>(TestSettings.Server(), Mock.Of<ILogger<ConfigService>>());
        _config.Setup(c => c.WriteSettings(It.IsAny<IReadOnlyDictionary<string, string>>()))
            .Callback<IReadOnlyDictionary<string, string>>(_written.Add);

        _runs = new CupRunService(
            _db.Store,
            _server.Object,
            _eventLoop.Object,
            _config.Object,
            _publisher.Object,
            _db.Clock,
            () => _restarting,
            Mock.Of<ILogger<CupRunService>>());
    }

    private DateTime Now => _db.Clock.UtcNow;

    private void At(DateTime time) => _db.Clock.Now = new DateTimeOffset(time, TimeSpan.Zero);

    /// <summary>An active cup starting an hour from now, warming up from half an hour before, ending two hours after.</summary>
    private async Task<(Cup Cup, DateTime Start, DateTime End)> ActiveAsync(
        CupPhase phase = CupPhase.Warmup,
        string? sessionMode = "30p-aggr",
        bool restartRotation = false,
        bool withEnd = true)
    {
        var start = Now.AddHours(1);
        var end = start.AddHours(2);
        var cup = await _db.CreateAsync(CupTestDatabase.Definition(
            "Friday Derby",
            start,
            sessionMode: sessionMode,
            warmup: TimeOnly.FromDateTime(start.AddMinutes(-30)),
            end: withEnd ? TimeOnly.FromDateTime(end) : null,
            restartRotation: restartRotation));
        Assert.True(await _db.Store.SetActiveAsync(cup.Id, start, phase));
        return (cup, start, end);
    }

    // ---- Warmup ----

    [Fact]
    public async Task BeforeTheWarning_NothingIsSent()
    {
        var (_, start, _) = await ActiveAsync();
        At(start.AddMinutes(-6));

        await _runs.TickAsync();

        Assert.Empty(_sent);
    }

    [Fact]
    public async Task FiveMinutesBefore_TheStartIsAnnouncedOnce()
    {
        var (cup, start, _) = await ActiveAsync();
        At(start.AddMinutes(-5));

        await _runs.TickAsync();
        At(start.AddMinutes(-4));
        await _runs.TickAsync();

        Assert.Equal(["/message Friday Derby starts in 5 minutes."], _sent);
        Assert.Equal(CupPhase.Warmup, (await _db.ReloadAsync(cup.Id)).Phase);
    }

    [Fact]
    public async Task AtTheStart_InTheLobby_CupPointsAreReset_AndTheCupStarts()
    {
        var (cup, start, _) = await ActiveAsync();
        At(start);

        await _runs.TickAsync();

        Assert.Equal(["/cupreset", "/message Friday Derby has started - good luck!"], _sent);
        Assert.Equal(CupPhase.Running, (await _db.ReloadAsync(cup.Id)).Phase);
        Assert.Equal(CupPhase.Running, _db.Store.CachedActiveCup?.Phase);
        _publisher.Verify(p => p.CupStartedAsync(cup.Id, "Friday Derby"), Times.Once);
        _eventLoop.Verify(l => l.RestartAsync(), Times.Never);
    }

    [Fact]
    public async Task AtTheStart_MidRace_PlayersAreToldOnce_AndTheResetWaitsForTheLobby()
    {
        var (cup, start, _) = await ActiveAsync();
        _session = ServerSessionPhase.Racing;
        At(start.AddMinutes(1));

        await _runs.TickAsync();
        await _runs.TickAsync();

        Assert.Equal(["/message Friday Derby starts after this race."], _sent);
        Assert.Equal(CupPhase.Warmup, (await _db.ReloadAsync(cup.Id)).Phase);

        _session = ServerSessionPhase.Lobby;
        await _runs.TickAsync();

        Assert.Equal(
            ["/message Friday Derby starts after this race.", "/cupreset", "/message Friday Derby has started - good luck!"],
            _sent);
        Assert.Equal(CupPhase.Running, (await _db.ReloadAsync(cup.Id)).Phase);
    }

    [Fact]
    public async Task NoLobbyWithinTheWait_TheCupStartsAnyway()
    {
        var (cup, start, _) = await ActiveAsync();
        _session = ServerSessionPhase.Racing;
        At(start + CupRunService.LobbyWait);

        await _runs.TickAsync();

        Assert.Contains("/cupreset", _sent);
        Assert.Equal(CupPhase.Running, (await _db.ReloadAsync(cup.Id)).Phase);
    }

    [Fact]
    public async Task AnUnreadableSession_DoesNotHoldTheStartBack()
    {
        var (cup, start, _) = await ActiveAsync();
        _session = null;
        At(start);

        await _runs.TickAsync();

        Assert.Contains("/cupreset", _sent);
        Assert.Equal(CupPhase.Running, (await _db.ReloadAsync(cup.Id)).Phase);
    }

    [Fact]
    public async Task WithRestartRotation_TheLoopGoesBackToItsBeginning_BeforeTheReset()
    {
        var (_, start, _) = await ActiveAsync(restartRotation: true);
        At(start);

        await _runs.TickAsync();

        Assert.Equal(
            ["<rotation back to the beginning>", "/cupreset", "/message Friday Derby has started - good luck!"],
            _sent);
    }

    [Fact]
    public async Task AFailedReset_IsRetriedOnTheNextCheck()
    {
        var (cup, start, _) = await ActiveAsync();
        _server.SetupSequence(s => s.SendCommandAsync("/cupreset"))
            .ReturnsAsync((false, "No hook"))
            .ReturnsAsync((true, "OK dispatched"));
        At(start);

        await _runs.TickAsync();
        Assert.Equal(CupPhase.Warmup, (await _db.ReloadAsync(cup.Id)).Phase);

        await _runs.TickAsync();
        Assert.Equal(CupPhase.Running, (await _db.ReloadAsync(cup.Id)).Phase);
        Assert.Equal(["/message Friday Derby has started - good luck!"], _sent);
    }

    [Fact]
    public async Task DuringARestart_NothingHappens()
    {
        var (cup, start, _) = await ActiveAsync();
        _restarting = true;
        At(start);

        await _runs.TickAsync();

        Assert.Empty(_sent);
        Assert.Equal(CupPhase.Warmup, (await _db.ReloadAsync(cup.Id)).Phase);
    }

    [Fact]
    public async Task AnActivationForNoOccurrence_HasNoRunToManage()
    {
        var cup = await _db.CreateAsync(CupTestDatabase.Definition("Manual", Now.AddDays(3)));
        Assert.True(await _db.Store.SetActiveAsync(cup.Id, Ct));

        await _runs.TickAsync();

        Assert.Empty(_sent);
        Assert.True((await _db.ReloadAsync(cup.Id)).IsActive);
    }

    // ---- End ----

    [Fact]
    public async Task AtTheEnd_TheCupEnds_AndCupPointsGoOffLiveAndInTheConfig()
    {
        var (cup, _, end) = await ActiveAsync(CupPhase.Running);
        At(end);

        await _runs.TickAsync();

        Assert.Equal(["/message Friday Derby is over - thanks for racing!", "session_mode=normal"], _sent);
        var ended = await _db.ReloadAsync(cup.Id);
        Assert.False(ended.IsActive);
        Assert.Null(ended.Phase);
        Assert.Null(_db.Store.CachedActiveCup);
        _publisher.Verify(p => p.CupEndedAsync(cup.Id, "Friday Derby"), Times.Once);
        var write = Assert.Single(_written);
        Assert.Equal("normal", write["session_mode"]);

        await _runs.TickAsync();
        Assert.Equal(2, _sent.Count);
    }

    [Fact]
    public async Task AnEndMidRace_TurnsCupPointsOffAtTheNextLobby()
    {
        var (cup, _, end) = await ActiveAsync(CupPhase.Running);
        _session = ServerSessionPhase.Racing;
        At(end);

        await _runs.TickAsync();

        Assert.Equal(["/message Friday Derby is over - thanks for racing!"], _sent);
        Assert.False((await _db.ReloadAsync(cup.Id)).IsActive);
        Assert.Empty(_written);

        _session = ServerSessionPhase.Lobby;
        await _runs.TickAsync();

        Assert.Equal("session_mode=normal", _sent[^1]);
        Assert.Single(_written);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("normal")]
    [InlineData("qualify-sprint")]
    public async Task ACupWithoutCupPoints_LeavesTheSessionModeAlone(string? sessionMode)
    {
        var (_, _, end) = await ActiveAsync(CupPhase.Running, sessionMode);
        At(end);

        await _runs.TickAsync();

        Assert.Equal(["/message Friday Derby is over - thanks for racing!"], _sent);
        Assert.Empty(_written);
    }

    [Fact]
    public async Task ACupWithoutAnEnd_KeepsRunning()
    {
        var (cup, start, _) = await ActiveAsync(CupPhase.Running, withEnd: false);
        At(start.AddDays(2));

        await _runs.TickAsync();

        Assert.Empty(_sent);
        Assert.True((await _db.ReloadAsync(cup.Id)).IsActive);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _db.Dispose();
}
