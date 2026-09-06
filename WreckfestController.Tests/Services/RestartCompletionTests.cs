using System.Reflection;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using WreckfestController.Models;
using WreckfestController.Services;
using Xunit;

namespace WreckfestController.Tests.Services;

public class RestartCompletionTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"restart-{Guid.NewGuid()}.json");
    private readonly Mock<ServerManager> _server;
    private readonly PlayerTracker _players;
    private readonly EventStorageService _storage;
    private readonly SmartRestartService _restart;
    private readonly EventSchedulerService _scheduler;
    private readonly SignalLogger<EventSchedulerService> _log = new();

    public RestartCompletionTests()
    {
        var settings = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["EventSchedulePath"] = _path }).Build();
        var webhook = new WreckfestWebWebhookService(Mock.Of<ILogger<WreckfestWebWebhookService>>(), settings, new HttpClient());
        _players = new PlayerTracker(Mock.Of<ILogger<PlayerTracker>>(), webhook);
        var tracks = new TrackChangeTracker(Mock.Of<ILogger<TrackChangeTracker>>(), webhook);
        _server = new Mock<ServerManager>(settings, Mock.Of<ILogger<ServerManager>>(), _players, tracks,
            new ServerInfoTracker(Mock.Of<ILogger<ServerInfoTracker>>()), webhook,
            new ConsoleLogWebhookSender(new HttpClient(), settings, Mock.Of<ILogger<ConsoleLogWebhookSender>>()));
        var config = new Mock<ConfigService>(settings, Mock.Of<ILogger<ConfigService>>());
        config.Setup(c => c.ReadBasicConfig()).Returns(new ServerConfig());
        _restart = new SmartRestartService(_server.Object, _players, tracks, config.Object, webhook,
            Mock.Of<ILogger<SmartRestartService>>());
        _storage = new EventStorageService(settings, Mock.Of<ILogger<EventStorageService>>());
        _scheduler = new EventSchedulerService(_storage, _restart,
            new RecurringEventService(Mock.Of<ILogger<RecurringEventService>>()), config.Object, webhook, _log);
    }

    private void MakeDueAndCheck(int id)
    {
        Assert.True(_storage.SaveSchedule(new EventSchedule { Events = [
            new Event { Id = id, Name = $"Event {id}", StartTime = DateTime.UtcNow.AddMinutes(-1) }] }));
        typeof(EventSchedulerService).GetMethod("CheckForDueEvents", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_scheduler, [null]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrThrowingRestartReleasesSchedulerAndAllowsLaterEvent(bool throws)
    {
        if (throws)
            _server.Setup(s => s.RestartServerViaCommandAsync()).ThrowsAsync(new IOException("Restart failed"));
        else
            _server.Setup(s => s.RestartServerViaCommandAsync()).ReturnsAsync((false, "Restart failed"));

        for (var id = 1; id <= 2; id++)
        {
            MakeDueAndCheck(id);
            await _log.WaitFor($"Restart for event {id} finished with Failed; scheduler released");
            Assert.Equal(SmartRestartState.Idle, _restart.GetState());
            Assert.False(_storage.LoadSchedule().GetEventById(id)!.IsActive);
        }
        _server.Verify(s => s.RestartServerViaCommandAsync(), Times.Exactly(2));
    }

    [Fact]
    public async Task CancellationReleasesSchedulerAndAllowsLaterEvent()
    {
        _players.ProcessHookPlayerSnapshot([new Player { PlayerId = 1, Name = "Player", IsBot = false }]);
        for (var id = 1; id <= 2; id++)
        {
            MakeDueAndCheck(id);
            await _log.WaitFor($"Smart restart initiated for event Event {id}");
            Assert.True(_restart.CancelRestart());
            await _log.WaitFor($"Restart for event {id} finished with Cancelled; scheduler released");
            Assert.False(_storage.LoadSchedule().GetEventById(id)!.IsActive);
        }
        _server.Verify(s => s.RestartServerViaCommandAsync(), Times.Never);
    }

    [Fact]
    public async Task SuccessReportsExactlyOneTerminalOutcomeEvenWhenActivationCallbackThrows()
    {
        _server.Setup(s => s.RestartServerViaCommandAsync()).ReturnsAsync((true, "Restarted"));
        var finished = new TaskCompletionSource<RestartOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        Assert.True(_restart.InitiateRestart(new Event { Id = 1 }, _ => throw new InvalidOperationException("Consumer failure"),
            (_, outcome) => { Interlocked.Increment(ref calls); finished.SetResult(outcome); }));
        Assert.Equal(RestartOutcome.Succeeded,
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Equal(1, calls);
        Assert.Equal(SmartRestartState.Idle, _restart.GetState());
        Assert.False(_restart.CancelRestart());
    }

    [Fact]
    public async Task CancelledRestartWorkCannotExecuteNewerRestart()
    {
        _players.ProcessHookPlayerSnapshot([new Player { PlayerId = 1, Name = "Player", IsBot = false }]);
        var outcomes = new List<RestartOutcome>();
        Assert.True(_restart.InitiateRestart(new Event { Id = 1 }, _ => Assert.Fail("Cancelled activation"),
            (_, outcome) => outcomes.Add(outcome)));
        var oldId = (long)typeof(SmartRestartService).GetField("_restartId", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(_restart)!;
        Assert.True(_restart.CancelRestart());
        Assert.True(_restart.InitiateRestart(new Event { Id = 2 }, _ => { }));
        try
        {
            var task = (Task)typeof(SmartRestartService).GetMethod("ExecuteRestartAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(_restart, [oldId])!;
            await task;
            Assert.Equal(SmartRestartState.Warning, _restart.GetState());
            Assert.Equal(2, _restart.GetPendingEvent()!.Id);
            Assert.Equal(RestartOutcome.Cancelled, Assert.Single(outcomes));
            _server.Verify(s => s.RestartServerViaCommandAsync(), Times.Never);
        }
        finally { _restart.CancelRestart(); }
    }

    public void Dispose()
    {
        _restart.CancelRestart();
        _scheduler.Dispose();
        File.Delete(_path);
    }

    private sealed class SignalLogger<T> : ILogger<T>
    {
        private readonly Channel<string> _messages = Channel.CreateUnbounded<string>();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
            => _messages.Writer.TryWrite(formatter(state, error));
        public async Task WaitFor(string prefix)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            while (!(await _messages.Reader.ReadAsync(timeout.Token)).StartsWith(prefix)) { }
        }
    }
}
