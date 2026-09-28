using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using WreckfestController.Data.Cups;
using WreckfestController.Models;
using WreckfestController.Services.Cups;
using WreckfestController.Services.Config;
using WreckfestController.Services.Publishing;
using WreckfestController.Services.ServerControl;
using WreckfestController.Services.Tracking;

namespace WreckfestController.Tests.Services.Cups;

/// <summary>
/// The scheduler and manual activation, end to end through the real SmartRestartService
/// and a real database. Only the game (ServerManager's restart) and the config file are mocked.
/// </summary>
public sealed class CupActivationTests : IDisposable
{
    private readonly CupTestDatabase _db = new();
    private readonly Mock<ServerManager> _server;
    private readonly Mock<ConfigService> _config;
    private readonly Mock<IServerEventPublisher> _publisher = new();
    private readonly PlayerTracker _players;
    private readonly SmartRestartService _restart;
    private readonly CupActivator _activator;
    private readonly CupSchedulerService _scheduler;
    private readonly List<string> _writes = new();

    public CupActivationTests()
    {
        var settings = new ConfigurationBuilder().Build();
        var cups = Mock.Of<IServerEventPublisher>();
        _players = new PlayerTracker(Mock.Of<ILogger<PlayerTracker>>(), cups);
        var tracks = new TrackChangeTracker(Mock.Of<ILogger<TrackChangeTracker>>(), cups);
        _server = new Mock<ServerManager>(settings, Mock.Of<ILogger<ServerManager>>(), _players, tracks,
            new ServerInfoTracker(Mock.Of<ILogger<ServerInfoTracker>>()), cups);
        _server.Setup(s => s.RestartServerViaCommandAsync()).ReturnsAsync((true, "Restarted"));

        _config = new Mock<ConfigService>(settings, Mock.Of<ILogger<ConfigService>>());
        _config.Setup(c => c.ReadBasicConfig()).Returns(new ServerConfig { ServerName = "Old name" });
        _config.Setup(c => c.WriteBasicConfig(It.IsAny<ServerConfig>()))
            .Callback<ServerConfig>(c => _writes.Add($"settings:{c.ServerName}"));
        _config.Setup(c => c.WriteEventLoopTracks(It.IsAny<string>(), It.IsAny<List<EventLoopTrack>>()))
            .Callback<string, List<EventLoopTrack>>((name, t) => _writes.Add($"tracks:{name}:{string.Join(",", t.Select(x => x.Track))}"));

        _restart = new SmartRestartService(_server.Object, _players, tracks, _config.Object, cups,
            Mock.Of<ILogger<SmartRestartService>>());
        _activator = new CupActivator(_db.Store, _restart, _publisher.Object, _db.Clock, Mock.Of<ILogger<CupActivator>>());
        _scheduler = new CupSchedulerService(_db.Store, _activator, _db.Clock, Mock.Of<ILogger<CupSchedulerService>>());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DateTime Now => _db.Clock.UtcNow;

    private static RepeatSchedule Daily(DateTime at) => new() { Frequency = "daily", Time = at.ToString("HH:mm", CultureInfo.InvariantCulture) };

    private static EventServerConfig NewName => new() { ServerName = "New name" };

    private static IReadOnlyList<EventLoopTrack> OneTrack => [new EventLoopTrack { Track = "urban09_1" }];

    // ---- Scheduled ----

    [Fact]
    public async Task ADueCup_IsApplied_ThenActivated_AndAOneOffCupFinishes()
    {
        var start = Now.AddMinutes(2);
        var cup = await _db.CreateAsync(CupTestDatabase.Definition("Race night", start, serverConfig: NewName, tracks: OneTrack));
        _server.Setup(s => s.RestartServerViaCommandAsync()).Returns(() =>
        {
            // Settings and tracks are written before the restart, not after.
            Assert.Equal(["settings:New name", "tracks:Cup: Race night:urban09_1"], _writes);
            return Task.FromResult((true, "Restarted"));
        });

        await _scheduler.CheckAsync();
        var current = await EventuallyAsync(cup.Id, e => e.NextOccurrence is null);

        Assert.True(current.IsActive);
        Assert.Equal(start, current.LastOccurrence);
        Assert.Equal(OccurrenceOutcome.Activated, current.LastOutcome);
        _publisher.Verify(p => p.CupActivatedAsync(cup.Id, "Race night"), Times.Once);
        _publisher.Verify(p => p.CupOccurrenceEndedAsync(cup.Id, "Race night", start, OccurrenceOutcome.Activated), Times.Once);
    }

    [Fact]
    public async Task ARecurringCup_MovesToItsNextOccurrence()
    {
        var start = Now.AddMinutes(2);
        var cup = await _db.CreateAsync(CupTestDatabase.Definition("Daily", start, Daily(start)));

        await _scheduler.CheckAsync();
        var current = await EventuallyAsync(cup.Id, e => e.NextOccurrence != start);

        Assert.Equal(start.AddDays(1), current.NextOccurrence);
        Assert.True((await EventuallyAsync(cup.Id, e => e.IsActive)).IsActive);
    }

    [Fact]
    public async Task AnCupNotYetInTheLeadIn_IsLeftAlone()
    {
        var cup = await _db.CreateAsync(CupTestDatabase.Definition("Race night", Now.AddMinutes(6)));

        await _scheduler.CheckAsync();

        Assert.Equal(SmartRestartState.Idle, _restart.GetState());
        Assert.Empty(_writes);
        Assert.Null((await _db.ReloadAsync(cup.Id)).LastOccurrence);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFailedRestart_IsNotRetried_AndTheSchedulerMovesOn(bool throws)
    {
        if (throws)
            _server.Setup(s => s.RestartServerViaCommandAsync()).ThrowsAsync(new IOException("Restart failed"));
        else
            _server.Setup(s => s.RestartServerViaCommandAsync()).ReturnsAsync((false, "Restart failed"));

        var first = await _db.CreateAsync(CupTestDatabase.Definition("First", Now.AddMinutes(1)));
        await _scheduler.CheckAsync();
        var failed = await EventuallyAsync(first.Id, e => e.NextOccurrence is null);
        Assert.False(failed.IsActive);
        Assert.Equal(OccurrenceOutcome.Failed, failed.LastOutcome);
        _publisher.Verify(p => p.CupOccurrenceEndedAsync(first.Id, "First", It.IsAny<DateTime>(), OccurrenceOutcome.Failed), Times.Once);
        await EventuallyIdleAsync();

        var second = await _db.CreateAsync(CupTestDatabase.Definition("Second", Now.AddMinutes(2)));
        await EventuallyCheckedAsync(second.Id);

        // One attempt each: the failed occurrence was not tried again.
        _server.Verify(s => s.RestartServerViaCommandAsync(), Times.Exactly(2));
    }

    [Fact]
    public async Task ACancelledOccurrence_IsNotStartedAgain()
    {
        _players.ProcessHookPlayerSnapshot([new Player { PlayerId = 1, Name = "Player", IsBot = false }]);
        var cup = await _db.CreateAsync(CupTestDatabase.Definition("Race night", Now.AddMinutes(1)));

        await _scheduler.CheckAsync();
        Assert.Equal(SmartRestartState.Warning, _restart.GetState());
        Assert.True(_restart.CancelRestart());

        var current = await EventuallyAsync(cup.Id, e => e.NextOccurrence is null);
        await _scheduler.CheckAsync();

        Assert.False(current.IsActive);
        Assert.Equal(OccurrenceOutcome.Cancelled, current.LastOutcome);
        Assert.Equal(SmartRestartState.Idle, _restart.GetState());
        _server.Verify(s => s.RestartServerViaCommandAsync(), Times.Never);
    }

    [Fact]
    public async Task AMissedOccurrence_IsRecordedAndReported_WithoutARestart()
    {
        var oneOff = await _db.CreateAsync(CupTestDatabase.Definition("Yesterday", Now.AddDays(-1)));
        var start = Now.AddHours(1);
        var daily = await _db.CreateAsync(CupTestDatabase.Definition("Daily", start, Daily(start)));
        _db.Clock.Now = _db.Clock.Now.AddHours(3); // The app was down through the daily's occurrence.

        await _scheduler.CheckAsync();

        Assert.Equal(SmartRestartState.Idle, _restart.GetState());
        Assert.Empty(_writes);
        var skipped = await _db.ReloadAsync(oneOff.Id);
        Assert.Null(skipped.NextOccurrence);
        Assert.False(skipped.IsActive);
        Assert.Equal(OccurrenceOutcome.Missed, skipped.LastOutcome);
        var moved = await _db.ReloadAsync(daily.Id);
        Assert.Equal(start, moved.LastOccurrence);
        Assert.Equal(OccurrenceOutcome.Missed, moved.LastOutcome);
        Assert.Equal(start.AddDays(1), moved.NextOccurrence);
        _publisher.Verify(p => p.CupOccurrenceEndedAsync(daily.Id, "Daily", start, OccurrenceOutcome.Missed), Times.Once);

        // Still the admin's to run: a manual activation works as normal.
        Assert.Equal(ActivationResult.Started, await _activator.ActivateAsync(oneOff.Id));
    }

    [Fact]
    public async Task ALateOccurrence_WithinTheGrace_StillStarts()
    {
        var cup = await _db.CreateAsync(CupTestDatabase.Definition("Race night", Now.AddMinutes(1)));
        _db.Clock.Now = _db.Clock.Now.AddMinutes(10);

        await _scheduler.CheckAsync();

        Assert.True((await EventuallyAsync(cup.Id, e => e.IsActive)).IsActive);
    }

    [Fact]
    public async Task SettingsThatCannotBeWritten_SkipTheOccurrence_AndReleaseTheScheduler()
    {
        _config.Setup(c => c.WriteBasicConfig(It.IsAny<ServerConfig>())).Throws(new IOException("Write failed"));
        var broken = await _db.CreateAsync(CupTestDatabase.Definition("Broken", Now.AddMinutes(1), serverConfig: NewName));
        var plain = await _db.CreateAsync(CupTestDatabase.Definition("Plain", Now.AddMinutes(2)));

        // One check: the broken cup is skipped, and the next due one starts.
        await _scheduler.CheckAsync();

        var skipped = await _db.ReloadAsync(broken.Id);
        Assert.False(skipped.IsActive);
        Assert.Null(skipped.NextOccurrence);
        Assert.Equal(OccurrenceOutcome.Failed, skipped.LastOutcome);
        Assert.True((await EventuallyAsync(plain.Id, e => e.IsActive)).IsActive);
        _server.Verify(s => s.RestartServerViaCommandAsync(), Times.Once);
    }

    [Fact]
    public async Task AnAlreadyActiveCup_NeedsNoRestartForItsOccurrence()
    {
        var start = Now.AddMinutes(2);
        var cup = await _db.CreateAsync(CupTestDatabase.Definition("Daily", start, Daily(start)));
        await _db.Store.SetActiveAsync(cup.Id, Ct);

        await _scheduler.CheckAsync();

        Assert.Equal(SmartRestartState.Idle, _restart.GetState());
        Assert.Empty(_writes);
        Assert.Equal(start.AddDays(1), (await _db.ReloadAsync(cup.Id)).NextOccurrence);
    }

    [Fact]
    public async Task ADueCup_WaitsWhileAnotherRestartRuns()
    {
        _players.ProcessHookPlayerSnapshot([new Player { PlayerId = 1, Name = "Player", IsBot = false }]);
        Assert.True(_restart.InitiateRestart(new Event { Id = 99, Name = "Someone else's" }, _ => { }));
        var start = Now.AddMinutes(2);
        var cup = await _db.CreateAsync(CupTestDatabase.Definition("Race night", start));

        await _scheduler.CheckAsync();
        Assert.Equal(start, (await _db.ReloadAsync(cup.Id)).NextOccurrence);

        Assert.True(_restart.CancelRestart());
        _players.ProcessHookPlayerSnapshot([]);
        await _scheduler.CheckAsync();

        Assert.True((await EventuallyAsync(cup.Id, e => e.IsActive)).IsActive);
    }

    // ---- Manual ----

    [Fact]
    public async Task ManualActivation_AppliesTheCup_AndMarksItActive()
    {
        var cup = await _db.CreateAsync(CupTestDatabase.Definition("Race night", Now.AddDays(1), serverConfig: NewName, tracks: OneTrack));

        Assert.Equal(ActivationResult.Started, await _activator.ActivateAsync(cup.Id));

        var current = await EventuallyAsync(cup.Id, e => e.IsActive);
        Assert.Equal(["settings:New name", "tracks:Cup: Race night:urban09_1"], _writes);

        // Well ahead of its start: the scheduled occurrence still happens.
        Assert.Equal(Now.AddDays(1), current.NextOccurrence);
    }

    [Fact]
    public async Task Activation_WritesTheCupsScoring_AndKeepsTheServersOwnWhenUnset()
    {
        var written = new List<IReadOnlyDictionary<string, string>>();
        _config.Setup(c => c.WriteSettings(It.IsAny<IReadOnlyDictionary<string, string>>()))
            .Callback<IReadOnlyDictionary<string, string>>(s => written.Add(new Dictionary<string, string>(s)));
        var scored = await _db.CreateAsync(CupTestDatabase.Definition("Cup night", Now.AddDays(1), sessionMode: "30p-aggr", gridOrder: "cup_reverse"));
        var gridOnly = await _db.CreateAsync(CupTestDatabase.Definition("Grid night", Now.AddDays(2), gridOrder: "random"));

        Assert.Equal(ActivationResult.Started, await _activator.ActivateAsync(scored.Id));
        await EventuallyAsync(scored.Id, e => e.IsActive);
        await EventuallyIdleAsync();
        Assert.Equal(ActivationResult.Started, await _activator.ActivateAsync(gridOnly.Id));
        await EventuallyAsync(gridOnly.Id, e => e.IsActive);

        // Only what the cup sets is written; the rest of the server's settings are untouched.
        Assert.Collection(
            written,
            s => Assert.Equal(new Dictionary<string, string> { ["session_mode"] = "30p-aggr", ["grid_order"] = "cup_reverse" }, s),
            s => Assert.Equal(new Dictionary<string, string> { ["grid_order"] = "random" }, s));
        Assert.Empty(_writes);
    }

    [Fact]
    public async Task ManualActivation_WithinTheLeadIn_StandsInForTheScheduledOccurrence()
    {
        // 1.x marked the cup active without moving it on, so a recurring cup
        // activated by hand just before its start never recurred again.
        var start = Now.AddMinutes(3);
        var cup = await _db.CreateAsync(CupTestDatabase.Definition("Daily", start, Daily(start)));

        Assert.Equal(ActivationResult.Started, await _activator.ActivateAsync(cup.Id));
        var current = await EventuallyAsync(cup.Id, e => e.NextOccurrence != start);
        await EventuallyIdleAsync();
        await _scheduler.CheckAsync();

        Assert.Equal(start.AddDays(1), current.NextOccurrence);
        Assert.Equal(start, current.LastOccurrence);
        _server.Verify(s => s.RestartServerViaCommandAsync(), Times.Once);
    }

    [Fact]
    public async Task ARunningManualActivation_IsNotCountedMissed_NorStartedAgainOnceCancelled()
    {
        // Activated by hand 14 minutes late, with players online, so it counts down.
        _players.ProcessHookPlayerSnapshot([new Player { PlayerId = 1, Name = "Player", IsBot = false }]);
        var start = Now.AddMinutes(-14);
        var cup = await _db.CreateAsync(CupTestDatabase.Definition("Race night", start, serverConfig: NewName));
        Assert.Equal(ActivationResult.Started, await _activator.ActivateAsync(cup.Id));

        // Past the grace while the countdown runs: the scheduler leaves it alone.
        _db.Clock.Now = _db.Clock.Now.AddMinutes(2);
        await _scheduler.CheckAsync();
        Assert.Null((await _db.ReloadAsync(cup.Id)).LastOutcome);

        Assert.True(_restart.CancelRestart());
        var cancelled = await EventuallyAsync(cup.Id, e => e.LastOutcome is not null);
        await _scheduler.CheckAsync();

        Assert.Equal(OccurrenceOutcome.Cancelled, cancelled.LastOutcome);
        Assert.Equal(start, cancelled.LastOccurrence);
        Assert.Equal(SmartRestartState.Idle, _restart.GetState());
        Assert.True(_activator.TryClaim(cup.Id), "the activation's claim was not released");
        _activator.Release(cup.Id);
        // Applied once, by the manual activation; nothing started it a second time.
        Assert.Equal(["settings:New name"], _writes);
        _server.Verify(s => s.RestartServerViaCommandAsync(), Times.Never);
    }

    [Fact]
    public async Task ASecondActivationOfTheSameCup_IsRefused_AndLeavesTheFirstOneProtected()
    {
        _players.ProcessHookPlayerSnapshot([new Player { PlayerId = 1, Name = "Player", IsBot = false }]);
        var cup = await _db.CreateAsync(CupTestDatabase.Definition("Race night", Now.AddMinutes(-14)));
        Assert.Equal(ActivationResult.Started, await _activator.ActivateAsync(cup.Id));

        Assert.Equal(ActivationResult.Busy, await _activator.ActivateAsync(cup.Id));

        // The refused request must not have released the first activation's claim.
        _db.Clock.Now = _db.Clock.Now.AddMinutes(2);
        await _scheduler.CheckAsync();
        Assert.Null((await _db.ReloadAsync(cup.Id)).LastOutcome);
    }

    [Fact]
    public async Task TheScheduler_LeavesAClaimedCupAlone_UntilItIsReleased()
    {
        var cup = await _db.CreateAsync(CupTestDatabase.Definition("Yesterday", Now.AddDays(-1)));
        Assert.True(_activator.TryClaim(cup.Id));

        await _scheduler.CheckAsync();
        Assert.Null((await _db.ReloadAsync(cup.Id)).LastOutcome);

        _activator.Release(cup.Id);
        await _scheduler.CheckAsync();
        Assert.Equal(OccurrenceOutcome.Missed, (await _db.ReloadAsync(cup.Id)).LastOutcome);
    }

    [Fact]
    public async Task ManualActivation_DuringAnotherRestart_WritesNothing()
    {
        _players.ProcessHookPlayerSnapshot([new Player { PlayerId = 1, Name = "Player", IsBot = false }]);
        var cup = await _db.CreateAsync(CupTestDatabase.Definition("Race night", Now.AddDays(1), serverConfig: NewName));
        try
        {
            Assert.Equal(ActivationResult.Started, await _activator.ActivateAsync(cup.Id));
            Assert.Equal(ActivationResult.Busy, await _activator.ActivateAsync(cup.Id));
            Assert.Single(_writes);
        }
        finally
        {
            _restart.CancelRestart();
        }
    }

    [Fact]
    public async Task ManualActivation_WhoseSettingsCannotBeWritten_Throws_AndRestartsNothing()
    {
        _config.Setup(c => c.WriteEventLoopTracks(It.IsAny<string>(), It.IsAny<List<EventLoopTrack>>()))
            .Throws(new IOException("Track write failed"));
        var cup = await _db.CreateAsync(CupTestDatabase.Definition("Race night", Now.AddDays(1), tracks: OneTrack));

        await Assert.ThrowsAsync<IOException>(() => _activator.ActivateAsync(cup.Id));

        Assert.Equal(SmartRestartState.Idle, _restart.GetState());
        Assert.False((await _db.ReloadAsync(cup.Id)).IsActive);
        _server.Verify(s => s.RestartServerViaCommandAsync(), Times.Never);
    }

    [Fact]
    public async Task ManualActivation_OfTheActiveOrAMissingCup_IsRefused()
    {
        var cup = await _db.CreateAsync(CupTestDatabase.Definition("Race night", Now.AddDays(1)));
        await _db.Store.SetActiveAsync(cup.Id, Ct);

        Assert.Equal(ActivationResult.AlreadyActive, await _activator.ActivateAsync(cup.Id));
        Assert.Equal(ActivationResult.NotFound, await _activator.ActivateAsync(cup.Id + 1));
        Assert.Empty(_writes);
    }

    private async Task<Cup> EventuallyAsync(int id, Func<Cup, bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (true)
        {
            var cup = await _db.ReloadAsync(id);
            if (condition(cup))
            {
                return cup;
            }

            await Task.Delay(25, timeout.Token);
        }
    }

    private async Task EventuallyIdleAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (_restart.GetState() != SmartRestartState.Idle)
        {
            await Task.Delay(25, timeout.Token);
        }
    }

    /// <summary>Checks until the scheduler has dealt with <paramref name="id"/>'s occurrence.</summary>
    private async Task EventuallyCheckedAsync(int id)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while ((await _db.ReloadAsync(id)).LastOccurrence is null)
        {
            await _scheduler.CheckAsync();
            await Task.Delay(25, timeout.Token);
        }
    }

    public void Dispose()
    {
        _restart.CancelRestart();
        _scheduler.Dispose();

        // A restart that just succeeded may still be writing its bookkeeping.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (_restart.GetState() != SmartRestartState.Idle && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(25);
        }

        _db.Dispose();
    }
}
