using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using WreckfestController.Models;
using Xunit;
using WreckfestController.Services.Config;
using WreckfestController.Services.Publishing;
using WreckfestController.Services.ServerControl;
using WreckfestController.Services.Tracking;

namespace WreckfestController.Tests.Services.ServerControl;

public class RestartCompletionTests : IDisposable
{
    private readonly Mock<ServerManager> _server;
    private readonly PlayerTracker _players;
    private readonly SmartRestartService _restart;

    public RestartCompletionTests()
    {
        var settings = new ConfigurationBuilder().Build();
        var events = Mock.Of<IServerEventPublisher>();
        _players = new PlayerTracker(Mock.Of<ILogger<PlayerTracker>>(), events);
        var tracks = new TrackChangeTracker(Mock.Of<ILogger<TrackChangeTracker>>(), events);
        _server = new Mock<ServerManager>(settings, TestSettings.Server(), TestSettings.SteamCmd(), Mock.Of<ILogger<ServerManager>>(), _players, tracks,
            new ServerInfoTracker(Mock.Of<ILogger<ServerInfoTracker>>()), events);
        var config = new Mock<ConfigService>(TestSettings.Server(), Mock.Of<ILogger<ConfigService>>());
        config.Setup(c => c.ReadBasicConfig()).Returns(new ServerConfig());
        _restart = new SmartRestartService(_server.Object, _players, tracks, config.Object, events,
            Mock.Of<ILogger<SmartRestartService>>());
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
    }
}
