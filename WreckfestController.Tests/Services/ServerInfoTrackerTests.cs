using Microsoft.Extensions.Logging;
using Moq;
using WreckfestController.Services;
using Xunit;

namespace WreckfestController.Tests.Services;

public class ServerInfoTrackerTests
{
    // Two clients calling GET /api/config/serverinfo at once used to share one
    // TaskCompletionSource: the second overwrote the first, so the first request's
    // timeout faulted the second's task and the first was never resolved at all -
    // its HTTP request hung until the client gave up. Each request must now own its
    // own completion source, so the loser times out rather than hanging.
    [Fact]
    public async Task RequestServerInfoAsync_WhenASecondRequestArrives_FirstStillCompletes()
    {
        var tracker = new ServerInfoTracker(Mock.Of<ILogger<ServerInfoTracker>>());

        var first = tracker.RequestServerInfoAsync(TimeSpan.FromMilliseconds(500));
        var second = tracker.RequestServerInfoAsync(TimeSpan.FromSeconds(10));

        // Only the newest request owns the collection state, so this resolves `second`.
        tracker.ProcessLogLine("server_name=Test Server");
        tracker.ProcessLogLine("max_players=24");
        // A non-config line ends the response. Not a blank one: ProcessLogLine
        // discards whitespace before the collector ever sees it.
        tracker.ProcessLogLine("Server ready");

        var config = await second;
        Assert.Equal("Test Server", config.ServerName);

        // The point of the test: `first` must reach a terminal state on its own
        // timeout instead of waiting forever for a result that will never come.
        var settled = await Task.WhenAny(first, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(first, settled);
        await Assert.ThrowsAsync<TimeoutException>(() => first);
    }
}
