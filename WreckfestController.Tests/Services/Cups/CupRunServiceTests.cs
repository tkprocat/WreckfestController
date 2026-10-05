using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using WreckfestController.Data.Cups;
using WreckfestController.Models;
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

        _config.Setup(c => c.ReadBasicConfig()).Returns(() => new ServerConfig { SessionMode = _serverSessionMode });

        _runs = NewService();
    }

    private string _serverSessionMode = "normal";

    /// <summary>A fresh service over the same database: what a controller restart gives.</summary>
    private CupRunService NewService() => new(
        _db.Store,
        _server.Object,
        _eventLoop.Object,
        _config.Object,
        _publisher.Object,
        _db.Clock,
        () => _restarting,
        Mock.Of<ILogger<CupRunService>>());

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
        Assert.True(await _db.Store.SetActiveAsync(cup.Id, start, phase, withEnd ? end : null, Ct));
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

    // #206 review: a reset is never sent mid-race, where it would wipe that race's points.
    // With no lobby within the wait, the cup starts without it.
    [Fact]
    public async Task NoLobbyWithinTheWait_TheCupStartsWithoutAReset()
    {
        var (cup, start, _) = await ActiveAsync();
        _session = ServerSessionPhase.Racing;
        At(start + CupRunService.LobbyWait);

        await _runs.TickAsync();

        Assert.DoesNotContain("/cupreset", _sent);
        Assert.Equal("/message Friday Derby has started - good luck!", _sent[^1]);
        Assert.Equal(CupPhase.Running, (await _db.ReloadAsync(cup.Id)).Phase);
    }

    [Fact]
    public async Task AFailedReset_IsNotRetriedMidRace_EvenPastTheWait()
    {
        var (cup, start, _) = await ActiveAsync();
        _server.SetupSequence(s => s.SendCommandAsync("/cupreset"))
            .ReturnsAsync((false, "No hook"))
            .ReturnsAsync((true, "OK dispatched"));
        At(start);
        await _runs.TickAsync();

        _session = ServerSessionPhase.Racing;
        At(start + CupRunService.LobbyWait);
        await _runs.TickAsync();

        _server.Verify(s => s.SendCommandAsync("/cupreset"), Times.Once);
        Assert.Equal(CupPhase.Running, (await _db.ReloadAsync(cup.Id)).Phase);
    }

    // Unreadable (no hook, say), nothing can be sent anyway: it waits, then starts without.
    [Fact]
    public async Task AnUnreadableSession_GetsNoReset_AndTheCupStartsAfterTheWait()
    {
        var (cup, start, _) = await ActiveAsync();
        _session = null;
        At(start);

        await _runs.TickAsync();
        Assert.Equal(CupPhase.Starting, (await _db.ReloadAsync(cup.Id)).Phase);

        At(start + CupRunService.LobbyWait);
        await _runs.TickAsync();

        Assert.DoesNotContain("/cupreset", _sent);
        Assert.Equal(CupPhase.Running, (await _db.ReloadAsync(cup.Id)).Phase);
    }

    [Fact]
    public async Task WithRestartRotation_TheLoopGoesBackToItsBeginning_AfterTheReset()
    {
        var (_, start, _) = await ActiveAsync(restartRotation: true);
        At(start);

        await _runs.TickAsync();

        Assert.Equal(
            ["/cupreset", "<rotation back to the beginning>", "/message Friday Derby has started - good luck!"],
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

        // Claimed as starting; the cup counts only once the reset has gone through.
        Assert.Equal(CupPhase.Starting, (await _db.ReloadAsync(cup.Id)).Phase);
        Assert.Empty(_sent);
        _publisher.Verify(p => p.CupStartedAsync(It.IsAny<int>(), It.IsAny<string>()), Times.Never);

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
        Assert.Empty(await _db.Store.PendingPointsOffAsync(Ct));
    }

    // #206 review: a run activated after its start (a restart that ran long, or the scheduler
    // checking late) still gets its start: the reset, the rotation and the announcement.
    [Fact]
    public async Task ARunActivatedAfterItsStart_StartsAtOnce()
    {
        var (cup, start, _) = await ActiveAsync(restartRotation: true);
        At(start.AddMinutes(2));

        await _runs.TickAsync();

        Assert.Equal(
            ["/cupreset", "<rotation back to the beginning>", "/message Friday Derby has started - good luck!"],
            _sent);
        Assert.Equal(CupPhase.Running, (await _db.ReloadAsync(cup.Id)).Phase);
    }

    [Fact]
    public async Task ARunNoLongerInWarmup_IsNotStartedAgain()
    {
        var (cup, start, _) = await ActiveAsync();
        Assert.True(await _db.Store.SetPhaseAsync(cup.Id, start, CupPhase.Warmup, CupPhase.Running, Ct));
        At(start);

        // Read as running already: a stale check that read it in warmup loses the claim.
        Assert.False(await _db.Store.SetPhaseAsync(cup.Id, start, CupPhase.Warmup, CupPhase.Running, Ct));
        await _runs.TickAsync();

        Assert.Empty(_sent);
    }

    // #206 review: the pending points-off is kept in the database, so it survives a restart.
    [Fact]
    public async Task APendingPointsOff_SurvivesAControllerRestart()
    {
        var (cup, _, end) = await ActiveAsync(CupPhase.Running);
        _session = ServerSessionPhase.Racing;
        At(end);
        await _runs.TickAsync();
        Assert.Single(await _db.Store.PendingPointsOffAsync(Ct));

        _session = ServerSessionPhase.Lobby;
        await NewService().TickAsync();

        Assert.Equal("session_mode=normal", _sent[^1]);
        Assert.Empty(await _db.Store.PendingPointsOffAsync(Ct));
    }

    // #206 review: once another cup runs the server, the ended cup's points-off would turn
    // off that cup's scoring. It is dropped instead.
    [Fact]
    public async Task APendingPointsOff_IsDropped_WhenAnotherCupIsActive()
    {
        var (cup, _, end) = await ActiveAsync(CupPhase.Running);
        _session = ServerSessionPhase.Racing;
        At(end);
        await _runs.TickAsync();

        var next = await _db.CreateAsync(CupTestDatabase.Definition("Saturday Cup", Now.AddDays(1), sessionMode: "25p-aggr"));
        Assert.True(await _db.Store.SetActiveAsync(next.Id, Ct));
        _session = ServerSessionPhase.Lobby;
        await _runs.TickAsync();

        Assert.DoesNotContain("session_mode=normal", _sent);
        Assert.Empty(_written);
        Assert.Empty(await _db.Store.PendingPointsOffAsync(Ct));
    }

    // #206 review: a cup that keeps the server's own session mode ran with whatever the
    // server has; when that awards cup points, they go off at the end too.
    [Fact]
    public async Task ACupKeepingTheServersPointsSystem_TurnsThemOffAtTheEnd()
    {
        _serverSessionMode = "30p-aggr";
        var (_, _, end) = await ActiveAsync(CupPhase.Running, sessionMode: null);
        At(end);

        await _runs.TickAsync();

        Assert.Equal("session_mode=normal", _sent[^1]);
        Assert.Single(_written);
    }

    // #206 review: the run's end is fixed when it begins; editing the schedule while it runs
    // moves later occurrences, not this one.
    [Fact]
    public async Task EditingTheScheduleWhileItRuns_DoesNotMoveThisRunsEnd()
    {
        var (cup, start, end) = await ActiveAsync(CupPhase.Running);
        var edited = CupTestDatabase.Definition(
            "Friday Derby", Now.AddDays(3), timeZone: "Europe/Copenhagen", end: new TimeOnly(21, 0));

        var (status, _) = await _db.Store.UpdateAsync(cup.Id, edited, cup.Version, Ct);

        Assert.Equal(CupWriteStatus.Saved, status);
        var run = await _db.Store.ActiveRunAsync(Ct);
        Assert.Equal(start, run!.StartsAt);
        Assert.Equal(end, run.EndsAt);
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

    // #206 review: a reset retried after a race began would wipe that race's points; the
    // retry waits for a lobby, as the start did.
    [Fact]
    public async Task AResetRetry_WaitsForTheLobby()
    {
        var (cup, start, _) = await ActiveAsync();
        _server.SetupSequence(s => s.SendCommandAsync("/cupreset"))
            .ReturnsAsync((false, "No hook"))
            .ReturnsAsync((true, "OK dispatched"));
        At(start);
        await _runs.TickAsync();

        _session = ServerSessionPhase.Racing;
        At(start.AddMinutes(2));
        await _runs.TickAsync();
        _server.Verify(s => s.SendCommandAsync("/cupreset"), Times.Once);
        Assert.Equal(CupPhase.Starting, (await _db.ReloadAsync(cup.Id)).Phase);

        _session = ServerSessionPhase.Lobby;
        await _runs.TickAsync();
        _server.Verify(s => s.SendCommandAsync("/cupreset"), Times.Exactly(2));
        Assert.Equal(CupPhase.Running, (await _db.ReloadAsync(cup.Id)).Phase);
    }

    // #206 review: a start whose reset failed is kept in the database, so a controller
    // restart retries it instead of leaving the warmup's points on the board.
    [Fact]
    public async Task AStartWhoseResetFailed_SurvivesAControllerRestart()
    {
        var (cup, start, _) = await ActiveAsync();
        _server.SetupSequence(s => s.SendCommandAsync("/cupreset"))
            .ReturnsAsync((false, "No hook"))
            .ReturnsAsync((true, "OK dispatched"));
        At(start);
        await _runs.TickAsync();

        await NewService().TickAsync();

        Assert.Equal(CupPhase.Running, (await _db.ReloadAsync(cup.Id)).Phase);
        Assert.Equal(["/message Friday Derby has started - good luck!"], _sent);
        _publisher.Verify(p => p.CupStartedAsync(cup.Id, "Friday Derby"), Times.Once);
    }

    // #206 review: the pending points-off is not a column of the cup, so deleting the ended
    // cup before the lobby does not leave cup points on.
    [Fact]
    public async Task DeletingAnEndedCup_KeepsItsPointsOff()
    {
        var (cup, _, end) = await ActiveAsync(CupPhase.Running);
        _session = ServerSessionPhase.Racing;
        At(end);
        await _runs.TickAsync();

        var ended = await _db.ReloadAsync(cup.Id);
        Assert.Equal(CupWriteStatus.Saved, (await _db.Store.DeleteAsync(cup.Id, ended.Version, Ct)).Status);
        _session = ServerSessionPhase.Lobby;
        await _runs.TickAsync();

        Assert.Equal("session_mode=normal", _sent[^1]);
        Assert.Empty(await _db.Store.PendingPointsOffAsync(Ct));
    }

    // #206 review: a check never runs while an activation (writing its settings) or a delete
    // holds the gate, so neither can come between what it reads and what it sends.
    [Fact]
    public async Task WhileTheGateIsHeld_ACheckDoesNothing()
    {
        var (_, start, _) = await ActiveAsync();
        At(start);

        await _db.Store.RunGate.WaitAsync(Ct);
        try
        {
            await _runs.TickAsync();
        }
        finally
        {
            _db.Store.RunGate.Release();
        }

        Assert.Empty(_sent);
        await _runs.TickAsync();
        Assert.Contains("/cupreset", _sent);
    }

    // The loop toggle may put the lobby into the game's track vote for a moment, which on prod
    // left /cupreset without effect: the reset goes first, and a retried reset does not toggle
    // the loop again.
    [Fact]
    public async Task AResetThatIsRetried_TogglesTheLoopOnlyOnce_AfterItGoesThrough()
    {
        var (_, start, _) = await ActiveAsync(restartRotation: true);
        _server.SetupSequence(s => s.SendCommandAsync("/cupreset"))
            .ReturnsAsync((false, "No hook"))
            .ReturnsAsync((true, "OK dispatched"));
        At(start);

        await _runs.TickAsync();
        _eventLoop.Verify(l => l.RestartAsync(), Times.Never);

        await _runs.TickAsync();
        _eventLoop.Verify(l => l.RestartAsync(), Times.Once);
        Assert.Equal(["<rotation back to the beginning>", "/message Friday Derby has started - good luck!"], _sent);
    }

    // The phase is Running before the rotation is touched: a run read as Running (a controller
    // that stopped right after the phase change) never toggles the loop again.
    [Fact]
    public async Task ARunAlreadyRunning_DoesNotSendTheRotationBackAgain()
    {
        var (_, start, _) = await ActiveAsync(CupPhase.Running, restartRotation: true);
        At(start.AddMinutes(1));

        await _runs.TickAsync();

        _eventLoop.Verify(l => l.RestartAsync(), Times.Never);
        Assert.Empty(_sent);
    }

    // Given up after the wait: no reset, but the rotation still goes back, once.
    [Fact]
    public async Task AGivenUpReset_StillSendsTheRotationBack_Once()
    {
        var (cup, start, _) = await ActiveAsync(restartRotation: true);
        _session = ServerSessionPhase.Racing;
        At(start + CupRunService.LobbyWait);

        await _runs.TickAsync();
        await _runs.TickAsync();

        _eventLoop.Verify(l => l.RestartAsync(), Times.Once);
        Assert.DoesNotContain("/cupreset", _sent);
        Assert.Equal(CupPhase.Running, (await _db.ReloadAsync(cup.Id)).Phase);
    }

    // #207 review: a run left Starting (a controller that stopped after the reset) and found
    // again long after the start, in a lobby, is not reset: that would wipe the cup's results.
    [Fact]
    public async Task AStartingRunFoundLate_IsNotResetEvenInALobby()
    {
        var (cup, start, _) = await ActiveAsync(CupPhase.Starting, restartRotation: true);
        _session = ServerSessionPhase.Lobby;
        At(start.AddMinutes(45));

        await _runs.TickAsync();

        Assert.DoesNotContain("/cupreset", _sent);
        Assert.Equal(CupPhase.Running, (await _db.ReloadAsync(cup.Id)).Phase);
        _eventLoop.Verify(l => l.RestartAsync(), Times.Once);
    }

    // Shown and recorded as the warmup: the warmup's points are still on the board.
    [Fact]
    public void Starting_IsShownAsTheWarmup() =>
        Assert.Equal(CupPhase.Warmup, CupStore.Shown(CupPhase.Starting));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _db.Dispose();
}
