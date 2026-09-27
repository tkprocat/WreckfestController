using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using WreckfestController.Models;
using WreckfestController.Services;
using Xunit;

namespace WreckfestController.Tests.Services;

public class RestartCountdownTests : IDisposable
{
    private readonly ManualClock _clock = new();
    private readonly CapturePublisher _published = new();
    private readonly List<string> _messages = [];
    private readonly SmartRestartService _restart;
    private readonly Mock<ServerManager> _server;

    public RestartCountdownTests()
    {
        var settings = new ConfigurationBuilder().Build();
        var events = _published;
        var players = new PlayerTracker(Mock.Of<ILogger<PlayerTracker>>(), events);
        players.ProcessHookPlayerSnapshot([new Player { PlayerId = 1, Name = "Player", IsBot = false }]);
        var tracks = new TrackChangeTracker(Mock.Of<ILogger<TrackChangeTracker>>(), events);
        _server = new Mock<ServerManager>(settings, Mock.Of<ILogger<ServerManager>>(), players, tracks,
            new ServerInfoTracker(Mock.Of<ILogger<ServerInfoTracker>>()), events);
        _server.Setup(s => s.SendCommandAsync(It.IsAny<string>())).Returns((string command) => {
            _messages.Add(command);
            return Task.FromResult((true, "Sent"));
        });
        var config = new Mock<ConfigService>(settings, Mock.Of<ILogger<ConfigService>>());
        config.Setup(c => c.ReadBasicConfig()).Returns(new ServerConfig());
        _restart = new SmartRestartService(_server.Object, players, tracks, config.Object, events,
            Mock.Of<ILogger<SmartRestartService>>(), _clock);
        Assert.True(_restart.InitiateRestart(new Event { Id = 7, Name = "Test event" }, _ => { }));
    }

    [Fact]
    public async Task CountdownWarnsAtMinuteFourAndBecomesPendingAtMinuteFive()
    {
        var deadline = _clock.GetUtcNow().UtcDateTime.AddMinutes(5);
        for (var minute = 0; minute <= 5; minute++)
        {
            _clock.Elapsed = TimeSpan.FromMinutes(minute);
            _clock.Timer!.Fire();
            var payload = await _published.Next();
            Assert.Equal(5 - minute, payload.MinutesRemaining);
            Assert.Equal(7, payload.EventId);
            Assert.Equal(deadline, payload.ScheduledRestartTime);
            Assert.Equal(minute < 5 ? SmartRestartState.Warning : SmartRestartState.Pending, _restart.GetState());
        }
        Assert.Equal(new[] {
            "/message Server will restart in 5 minutes.", "/message Server will restart in 4 minutes.",
            "/message Server will restart in 3 minutes.", "/message Server will restart in 2 minutes.",
            "/message Server will restart in 1 minute.", "/message Server will restart at the next lobby."
        }, _messages);
        _server.Verify(s => s.RestartServerViaCommandAsync(), Times.Never);
    }

    [Fact]
    public async Task DelayedAndRepeatedTicksUseElapsedTime()
    {
        _clock.Elapsed = TimeSpan.FromMinutes(4.5);
        _clock.Timer!.Fire();
        Assert.Equal(1, (await _published.Next()).MinutesRemaining);
        for (var i = 0; i < 3; i++)
        {
            _clock.Timer!.Fire();
            Assert.False(_published.HasPendingPayload());
            Assert.Single(_messages);
            Assert.Equal(SmartRestartState.Warning, _restart.GetState());
        }
        _clock.Elapsed = TimeSpan.FromMinutes(5.5);
        _clock.Timer!.Fire();
        Assert.Equal(0, (await _published.Next()).MinutesRemaining);
        Assert.Equal(SmartRestartState.Pending, _restart.GetState());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task EarlyTickRechecksBoundaryWithoutRepeatingWarningOrFinishingEarly(int minute)
    {
        _clock.Elapsed = TimeSpan.FromMinutes(minute - 1);
        _clock.Timer!.Fire();
        Assert.Equal(6 - minute, (await _published.Next()).MinutesRemaining);

        _clock.Elapsed = TimeSpan.FromMinutes(minute) - TimeSpan.FromMilliseconds(2);
        _clock.Timer.Fire();
        Assert.Equal(SmartRestartState.Warning, _restart.GetState());
        Assert.Single(_messages);
        Assert.False(_published.HasPendingPayload());
        Assert.Equal(TimeSpan.FromMilliseconds(2), _clock.Timer.DueTime);
        Assert.Equal(Timeout.InfiniteTimeSpan, _clock.Timer.Period);

        _clock.Elapsed += _clock.Timer.DueTime;
        _clock.Timer.Fire();
        Assert.Equal(5 - minute, (await _published.Next()).MinutesRemaining);
        Assert.Equal(minute == 5 ? SmartRestartState.Pending : SmartRestartState.Warning, _restart.GetState());
        Assert.Equal(2, _messages.Count);
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(2)]
    public async Task PendingTimeoutUsesTenElapsedMinutesDespiteWallClockChanges(int hours)
    {
        var restarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _server.Setup(s => s.RestartServerViaCommandAsync()).Returns(() => {
            restarted.TrySetResult();
            return Task.FromResult((false, "Test finished"));
        });
        _clock.Elapsed = TimeSpan.FromMinutes(5);
        _clock.Timer!.Fire();
        await _published.Next();

        _clock.WallClockAdjustment = TimeSpan.FromHours(hours);
        _clock.Timer.Fire();
        Assert.Equal(SmartRestartState.Pending, _restart.GetState());
        Assert.Equal(TimeSpan.FromSeconds(30), _clock.Timer.DueTime);
        _server.Verify(s => s.RestartServerViaCommandAsync(), Times.Never);

        _clock.Elapsed = TimeSpan.FromMinutes(15) - TimeSpan.FromMilliseconds(2);
        _clock.Timer.Fire();
        Assert.Equal(SmartRestartState.Pending, _restart.GetState());
        Assert.Equal(TimeSpan.FromMilliseconds(2), _clock.Timer.DueTime);
        _server.Verify(s => s.RestartServerViaCommandAsync(), Times.Never);

        _clock.Elapsed += _clock.Timer.DueTime;
        _clock.Timer.Fire();
        await restarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        _server.Verify(s => s.RestartServerViaCommandAsync(), Times.Once);
        Assert.Contains("/message Server restarting now (timeout).", _messages);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public async Task SynchronouslyBlockedPublisherDoesNotHoldStateLock(int minute)
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _published.BeforePendingSend = () => {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Test publisher was not released");
        };
        _clock.Elapsed = TimeSpan.FromMinutes(minute);
        var tick = Task.Run(() => _clock.Timer!.Fire(), TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var state = await Task.Run(() => _restart.GetState(), TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.Equal(minute == 0 ? SmartRestartState.Warning : SmartRestartState.Pending, state);
        }
        finally
        {
            release.Set();
            await tick.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        Assert.Equal(5 - minute, (await _published.Next()).MinutesRemaining);
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(2)]
    public async Task WallClockChangesDoNotShortenOrExtendCountdown(int hours)
    {
        _clock.WallClockAdjustment = TimeSpan.FromHours(hours);
        _clock.Elapsed = TimeSpan.FromMinutes(4);
        _clock.Timer!.Fire();
        Assert.Equal(1, (await _published.Next()).MinutesRemaining);
        Assert.Equal(SmartRestartState.Warning, _restart.GetState());
        _clock.Elapsed = TimeSpan.FromMinutes(5);
        _clock.Timer!.Fire();
        Assert.Equal(0, (await _published.Next()).MinutesRemaining);
        Assert.Equal(SmartRestartState.Pending, _restart.GetState());
    }

    public void Dispose() => _restart.CancelRestart();

    private sealed class ManualClock : TimeProvider
    {
        public TimeSpan Elapsed { get; set; }
        public TimeSpan WallClockAdjustment { get; set; }
        public ManualTimer? Timer { get; private set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Elapsed.Ticks;
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero) + Elapsed + WallClockAdjustment;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => Timer = new ManualTimer(callback, state, dueTime, period);
    }

    private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) : ITimer
    {
        private bool _disposed;
        public TimeSpan DueTime { get; private set; } = dueTime;
        public TimeSpan Period { get; private set; } = period;
        public void Fire() { if (!_disposed) callback(state); }
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (_disposed) return false;
            DueTime = dueTime;
            Period = period;
            return true;
        }
        public void Dispose() => _disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }

    private sealed class CapturePublisher : IServerEventPublisher
    {
        private readonly Channel<ServerRestartPendingEvent> _payloads = Channel.CreateUnbounded<ServerRestartPendingEvent>();
        public Action? BeforePendingSend { get; set; }
        public bool HasPendingPayload() => _payloads.Reader.TryPeek(out _);
        public Task ServerRestartPendingAsync(ServerRestartPendingEvent serverEvent)
        {
            BeforePendingSend?.Invoke();
            _payloads.Writer.TryWrite(serverEvent);
            return Task.CompletedTask;
        }
        public Task PlayersUpdatedAsync(IReadOnlyList<Player> players) => Task.CompletedTask;
        public Task PlayerJoinedAsync(string playerName, bool isBot) => Task.CompletedTask;
        public Task PlayerLeftAsync(string playerName) => Task.CompletedTask;
        public Task TrackChangedAsync(string trackId) => Task.CompletedTask;
        public Task EventActivatedAsync(int eventId, string eventName) => Task.CompletedTask;
        public Task ServerStartedAsync(ServerStartedEvent serverEvent) => Task.CompletedTask;
        public Task ServerStoppedAsync(ServerStoppedEvent serverEvent) => Task.CompletedTask;
        public Task ServerRestartedAsync(ServerRestartedEvent serverEvent) => Task.CompletedTask;
        public Task ServerAttachedAsync(ServerAttachedEvent serverEvent) => Task.CompletedTask;
        public void AddConsoleLog(string line) { }
        public async Task<ServerRestartPendingEvent> Next()
            => await _payloads.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }
}
