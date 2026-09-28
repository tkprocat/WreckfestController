using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using WreckfestController.Data.Events;
using WreckfestController.Models;
using WreckfestController.Services;

namespace WreckfestController.Tests.Services;

/// <summary>
/// The scheduler and manual activation, end to end through the real SmartRestartService
/// and a real database. Only the game (ServerManager's restart) and the config file are mocked.
/// </summary>
public sealed class EventActivationTests : IDisposable
{
    private readonly EventTestDatabase _db = new();
    private readonly Mock<ServerManager> _server;
    private readonly Mock<ConfigService> _config;
    private readonly Mock<IServerEventPublisher> _publisher = new();
    private readonly PlayerTracker _players;
    private readonly SmartRestartService _restart;
    private readonly EventActivator _activator;
    private readonly EventSchedulerService _scheduler;
    private readonly List<string> _writes = new();

    public EventActivationTests()
    {
        var settings = new ConfigurationBuilder().Build();
        var events = Mock.Of<IServerEventPublisher>();
        _players = new PlayerTracker(Mock.Of<ILogger<PlayerTracker>>(), events);
        var tracks = new TrackChangeTracker(Mock.Of<ILogger<TrackChangeTracker>>(), events);
        _server = new Mock<ServerManager>(settings, Mock.Of<ILogger<ServerManager>>(), _players, tracks,
            new ServerInfoTracker(Mock.Of<ILogger<ServerInfoTracker>>()), events);
        _server.Setup(s => s.RestartServerViaCommandAsync()).ReturnsAsync((true, "Restarted"));

        _config = new Mock<ConfigService>(settings, Mock.Of<ILogger<ConfigService>>());
        _config.Setup(c => c.ReadBasicConfig()).Returns(new ServerConfig { ServerName = "Old name" });
        _config.Setup(c => c.WriteBasicConfig(It.IsAny<ServerConfig>()))
            .Callback<ServerConfig>(c => _writes.Add($"settings:{c.ServerName}"));
        _config.Setup(c => c.WriteEventLoopTracks(It.IsAny<string>(), It.IsAny<List<EventLoopTrack>>()))
            .Callback<string, List<EventLoopTrack>>((name, t) => _writes.Add($"tracks:{name}:{string.Join(",", t.Select(x => x.Track))}"));

        _restart = new SmartRestartService(_server.Object, _players, tracks, _config.Object, events,
            Mock.Of<ILogger<SmartRestartService>>());
        _activator = new EventActivator(_db.Store, _restart, _publisher.Object, _db.Clock, Mock.Of<ILogger<EventActivator>>());
        _scheduler = new EventSchedulerService(_db.Store, _activator, _db.Clock, Mock.Of<ILogger<EventSchedulerService>>());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DateTime Now => _db.Clock.UtcNow;

    private static RepeatSchedule Daily(DateTime at) => new() { Frequency = "daily", Time = at.ToString("HH:mm", CultureInfo.InvariantCulture) };

    private static EventServerConfig NewName => new() { ServerName = "New name" };

    private static IReadOnlyList<EventLoopTrack> OneTrack => [new EventLoopTrack { Track = "urban09_1" }];

    // ---- Scheduled ----

    [Fact]
    public async Task ADueEvent_IsApplied_ThenActivated_AndAOneOffEventFinishes()
    {
        var start = Now.AddMinutes(2);
        var evt = await _db.CreateAsync(EventTestDatabase.Definition("Race night", start, serverConfig: NewName, tracks: OneTrack));
        _server.Setup(s => s.RestartServerViaCommandAsync()).Returns(() =>
        {
            // Settings and tracks are written before the restart, not after.
            Assert.Equal(["settings:New name", "tracks:Event: Race night:urban09_1"], _writes);
            return Task.FromResult((true, "Restarted"));
        });

        await _scheduler.CheckAsync();
        var current = await EventuallyAsync(evt.Id, e => e.NextOccurrence is null);

        Assert.True(current.IsActive);
        Assert.Equal(start, current.LastOccurrence);
        _publisher.Verify(p => p.EventActivatedAsync(evt.Id, "Race night"), Times.Once);
    }

    [Fact]
    public async Task ARecurringEvent_MovesToItsNextOccurrence()
    {
        var start = Now.AddMinutes(2);
        var evt = await _db.CreateAsync(EventTestDatabase.Definition("Daily", start, Daily(start)));

        await _scheduler.CheckAsync();
        var current = await EventuallyAsync(evt.Id, e => e.NextOccurrence != start);

        Assert.Equal(start.AddDays(1), current.NextOccurrence);
        Assert.True((await EventuallyAsync(evt.Id, e => e.IsActive)).IsActive);
    }

    [Fact]
    public async Task AnEventNotYetInTheLeadIn_IsLeftAlone()
    {
        var evt = await _db.CreateAsync(EventTestDatabase.Definition("Race night", Now.AddMinutes(6)));

        await _scheduler.CheckAsync();

        Assert.Equal(SmartRestartState.Idle, _restart.GetState());
        Assert.Empty(_writes);
        Assert.Null((await _db.ReloadAsync(evt.Id)).LastOccurrence);
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

        var first = await _db.CreateAsync(EventTestDatabase.Definition("First", Now.AddMinutes(1)));
        await _scheduler.CheckAsync();
        var failed = await EventuallyAsync(first.Id, e => e.NextOccurrence is null);
        Assert.False(failed.IsActive);
        await EventuallyIdleAsync();

        var second = await _db.CreateAsync(EventTestDatabase.Definition("Second", Now.AddMinutes(2)));
        await EventuallyCheckedAsync(second.Id);

        // One attempt each: the failed occurrence was not tried again.
        _server.Verify(s => s.RestartServerViaCommandAsync(), Times.Exactly(2));
    }

    [Fact]
    public async Task ACancelledOccurrence_IsNotStartedAgain()
    {
        _players.ProcessHookPlayerSnapshot([new Player { PlayerId = 1, Name = "Player", IsBot = false }]);
        var evt = await _db.CreateAsync(EventTestDatabase.Definition("Race night", Now.AddMinutes(1)));

        await _scheduler.CheckAsync();
        Assert.Equal(SmartRestartState.Warning, _restart.GetState());
        Assert.True(_restart.CancelRestart());

        var current = await EventuallyAsync(evt.Id, e => e.NextOccurrence is null);
        await _scheduler.CheckAsync();

        Assert.False(current.IsActive);
        Assert.Equal(SmartRestartState.Idle, _restart.GetState());
        _server.Verify(s => s.RestartServerViaCommandAsync(), Times.Never);
    }

    [Fact]
    public async Task AMissedOccurrence_IsSkipped_WithoutARestart()
    {
        var oneOff = await _db.CreateAsync(EventTestDatabase.Definition("Yesterday", Now.AddDays(-1)));
        var start = Now.AddHours(1);
        var daily = await _db.CreateAsync(EventTestDatabase.Definition("Daily", start, Daily(start)));
        _db.Clock.Now = _db.Clock.Now.AddHours(3); // The app was down through the daily's occurrence.

        await _scheduler.CheckAsync();

        Assert.Equal(SmartRestartState.Idle, _restart.GetState());
        Assert.Empty(_writes);
        var skipped = await _db.ReloadAsync(oneOff.Id);
        Assert.Null(skipped.NextOccurrence);
        Assert.False(skipped.IsActive);
        var moved = await _db.ReloadAsync(daily.Id);
        Assert.Equal(start, moved.LastOccurrence);
        Assert.Equal(start.AddDays(1), moved.NextOccurrence);
    }

    [Fact]
    public async Task ALateOccurrence_WithinTheGrace_StillStarts()
    {
        var evt = await _db.CreateAsync(EventTestDatabase.Definition("Race night", Now.AddMinutes(1)));
        _db.Clock.Now = _db.Clock.Now.AddMinutes(20);

        await _scheduler.CheckAsync();

        Assert.True((await EventuallyAsync(evt.Id, e => e.IsActive)).IsActive);
    }

    [Fact]
    public async Task SettingsThatCannotBeWritten_SkipTheOccurrence_AndReleaseTheScheduler()
    {
        _config.Setup(c => c.WriteBasicConfig(It.IsAny<ServerConfig>())).Throws(new IOException("Write failed"));
        var broken = await _db.CreateAsync(EventTestDatabase.Definition("Broken", Now.AddMinutes(1), serverConfig: NewName));
        var plain = await _db.CreateAsync(EventTestDatabase.Definition("Plain", Now.AddMinutes(2)));

        // One check: the broken event is skipped, and the next due one starts.
        await _scheduler.CheckAsync();

        var skipped = await _db.ReloadAsync(broken.Id);
        Assert.False(skipped.IsActive);
        Assert.Null(skipped.NextOccurrence);
        Assert.True((await EventuallyAsync(plain.Id, e => e.IsActive)).IsActive);
        _server.Verify(s => s.RestartServerViaCommandAsync(), Times.Once);
    }

    [Fact]
    public async Task AnAlreadyActiveEvent_NeedsNoRestartForItsOccurrence()
    {
        var start = Now.AddMinutes(2);
        var evt = await _db.CreateAsync(EventTestDatabase.Definition("Daily", start, Daily(start)));
        await _db.Store.SetActiveAsync(evt.Id, Ct);

        await _scheduler.CheckAsync();

        Assert.Equal(SmartRestartState.Idle, _restart.GetState());
        Assert.Empty(_writes);
        Assert.Equal(start.AddDays(1), (await _db.ReloadAsync(evt.Id)).NextOccurrence);
    }

    [Fact]
    public async Task ADueEvent_WaitsWhileAnotherRestartRuns()
    {
        _players.ProcessHookPlayerSnapshot([new Player { PlayerId = 1, Name = "Player", IsBot = false }]);
        Assert.True(_restart.InitiateRestart(new Event { Id = 99, Name = "Someone else's" }, _ => { }));
        var start = Now.AddMinutes(2);
        var evt = await _db.CreateAsync(EventTestDatabase.Definition("Race night", start));

        await _scheduler.CheckAsync();
        Assert.Equal(start, (await _db.ReloadAsync(evt.Id)).NextOccurrence);

        Assert.True(_restart.CancelRestart());
        _players.ProcessHookPlayerSnapshot([]);
        await _scheduler.CheckAsync();

        Assert.True((await EventuallyAsync(evt.Id, e => e.IsActive)).IsActive);
    }

    // ---- Manual ----

    [Fact]
    public async Task ManualActivation_AppliesTheEvent_AndMarksItActive()
    {
        var evt = await _db.CreateAsync(EventTestDatabase.Definition("Race night", Now.AddDays(1), serverConfig: NewName, tracks: OneTrack));

        Assert.Equal(ActivationResult.Started, await _activator.ActivateAsync(evt.Id));

        var current = await EventuallyAsync(evt.Id, e => e.IsActive);
        Assert.Equal(["settings:New name", "tracks:Event: Race night:urban09_1"], _writes);

        // Well ahead of its start: the scheduled occurrence still happens.
        Assert.Equal(Now.AddDays(1), current.NextOccurrence);
    }

    [Fact]
    public async Task ManualActivation_WithinTheLeadIn_StandsInForTheScheduledOccurrence()
    {
        // 1.x marked the event active without moving it on, so a recurring event
        // activated by hand just before its start never recurred again.
        var start = Now.AddMinutes(3);
        var evt = await _db.CreateAsync(EventTestDatabase.Definition("Daily", start, Daily(start)));

        Assert.Equal(ActivationResult.Started, await _activator.ActivateAsync(evt.Id));
        var current = await EventuallyAsync(evt.Id, e => e.NextOccurrence != start);
        await EventuallyIdleAsync();
        await _scheduler.CheckAsync();

        Assert.Equal(start.AddDays(1), current.NextOccurrence);
        Assert.Equal(start, current.LastOccurrence);
        _server.Verify(s => s.RestartServerViaCommandAsync(), Times.Once);
    }

    [Fact]
    public async Task ManualActivation_DuringAnotherRestart_WritesNothing()
    {
        _players.ProcessHookPlayerSnapshot([new Player { PlayerId = 1, Name = "Player", IsBot = false }]);
        var evt = await _db.CreateAsync(EventTestDatabase.Definition("Race night", Now.AddDays(1), serverConfig: NewName));
        try
        {
            Assert.Equal(ActivationResult.Started, await _activator.ActivateAsync(evt.Id));
            Assert.Equal(ActivationResult.Busy, await _activator.ActivateAsync(evt.Id));
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
        var evt = await _db.CreateAsync(EventTestDatabase.Definition("Race night", Now.AddDays(1), tracks: OneTrack));

        await Assert.ThrowsAsync<IOException>(() => _activator.ActivateAsync(evt.Id));

        Assert.Equal(SmartRestartState.Idle, _restart.GetState());
        Assert.False((await _db.ReloadAsync(evt.Id)).IsActive);
        _server.Verify(s => s.RestartServerViaCommandAsync(), Times.Never);
    }

    [Fact]
    public async Task ManualActivation_OfTheActiveOrAMissingEvent_IsRefused()
    {
        var evt = await _db.CreateAsync(EventTestDatabase.Definition("Race night", Now.AddDays(1)));
        await _db.Store.SetActiveAsync(evt.Id, Ct);

        Assert.Equal(ActivationResult.AlreadyActive, await _activator.ActivateAsync(evt.Id));
        Assert.Equal(ActivationResult.NotFound, await _activator.ActivateAsync(evt.Id + 1));
        Assert.Empty(_writes);
    }

    private async Task<ScheduledEvent> EventuallyAsync(int id, Func<ScheduledEvent, bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (true)
        {
            var evt = await _db.ReloadAsync(id);
            if (condition(evt))
            {
                return evt;
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
