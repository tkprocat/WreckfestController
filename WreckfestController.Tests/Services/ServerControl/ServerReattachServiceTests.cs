using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using WreckfestController.Services.Publishing;
using WreckfestController.Services.ServerControl;
using WreckfestController.Services.Tracking;

namespace WreckfestController.Tests.Services.ServerControl;

/// <summary>
/// The startup reattach settles <see cref="StartupReattach"/> only once it is done -
/// the roster read included, since the cup scheduler decides from it whether to warn
/// the players on the server (#232).
/// </summary>
public sealed class ServerReattachServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly Mock<ServerManager> _server;
    private readonly StartupReattach _startup = new();
    private readonly ServerReattachService _service;

    public ServerReattachServiceTests()
    {
        var events = Mock.Of<IServerEventPublisher>();
        var players = new PlayerTracker(Mock.Of<ILogger<PlayerTracker>>(), events);
        var tracks = new TrackChangeTracker(Mock.Of<ILogger<TrackChangeTracker>>(), events);
        _server = new Mock<ServerManager>(new ConfigurationBuilder().Build(), TestSettings.Server(), TestSettings.SteamCmd(),
            Mock.Of<ILogger<ServerManager>>(), players, tracks, new ServerInfoTracker(Mock.Of<ILogger<ServerInfoTracker>>()), events);
        _service = new ServerReattachService(_server.Object, _startup, Mock.Of<ILogger<ServerReattachService>>());
    }

    private async Task SettledAsync()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!_startup.IsSettled)
        {
            Assert.True(DateTime.UtcNow < deadline, "The startup reattach never settled.");
            await Task.Delay(20, Ct);
        }
    }

    [Fact]
    public async Task Settles_only_after_the_roster_is_read()
    {
        var roster = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _server.Setup(s => s.ReattachOwnServerAsync()).ReturnsAsync((true, "Reattached"));
        _server.Setup(s => s.TryRefreshPlayersFromHookAsync()).Returns(roster.Task);

        await _service.StartAsync(Ct);
        await Task.Delay(200, Ct);
        Assert.False(_startup.IsSettled);

        roster.SetResult(true);
        await SettledAsync();
    }

    [Fact]
    public async Task Settles_without_reading_a_roster_when_there_is_nothing_to_reattach_to()
    {
        _server.Setup(s => s.ReattachOwnServerAsync()).ReturnsAsync((false, "No running server carries this controller's id."));

        await _service.StartAsync(Ct);
        await SettledAsync();

        _server.Verify(s => s.TryRefreshPlayersFromHookAsync(), Times.Never);
    }

    [Fact]
    public async Task Settles_when_the_reattach_throws()
    {
        _server.Setup(s => s.ReattachOwnServerAsync()).ThrowsAsync(new InvalidOperationException("Broken"));

        await _service.StartAsync(Ct);

        await SettledAsync();
    }

    // Stopping ends the roster retries, and still settles: nothing is left waiting on it.
    [Fact]
    public async Task Stopping_ends_the_roster_retries()
    {
        _server.Setup(s => s.ReattachOwnServerAsync()).ReturnsAsync((true, "Reattached"));
        _server.Setup(s => s.TryRefreshPlayersFromHookAsync()).ReturnsAsync(false);

        await _service.StartAsync(Ct);
        await Task.Delay(100, Ct);
        await _service.StopAsync(Ct);
        await SettledAsync();

        _server.Verify(s => s.TryRefreshPlayersFromHookAsync(), Times.AtMost(2));
    }
}
