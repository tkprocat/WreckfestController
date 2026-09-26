using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Moq;
using WreckfestController.Hubs;
using WreckfestController.Models;
using WreckfestController.Services;
using Xunit;

namespace WreckfestController.Tests.Services;

public class HubServerEventPublisherTests : IDisposable
{
    private readonly Mock<IServerHubClient> _public = new();
    private readonly Mock<IServerHubClient> _admin = new();
    private readonly List<IReadOnlyList<string>> _batches = [];
    private readonly HubServerEventPublisher _publisher =
        new(Mock.Of<ILogger<HubServerEventPublisher>>());

    public HubServerEventPublisherTests()
    {
        _admin.Setup(c => c.ConsoleLog(It.IsAny<ConsoleLogMessage>()))
            .Callback<ConsoleLogMessage>(m => _batches.Add(m.Logs))
            .Returns(Task.CompletedTask);
    }

    public void Dispose() => _publisher.Dispose();

    private IHubContext<ServerHub, IServerHubClient> Hub()
    {
        var clients = new Mock<IHubClients<IServerHubClient>>();
        clients.Setup(c => c.Group(ServerHub.PublicGroup)).Returns(_public.Object);
        clients.Setup(c => c.Group(ServerHub.AdminGroup)).Returns(_admin.Object);
        var hub = new Mock<IHubContext<ServerHub, IServerHubClient>>();
        hub.Setup(h => h.Clients).Returns(clients.Object);
        return hub.Object;
    }

    [Fact]
    public async Task WhileDetached_EventsAndConsoleLinesAreDropped()
    {
        await _publisher.TrackChangedAsync("fields14");
        _publisher.AddConsoleLog("before attach");

        _publisher.Attach(Hub());
        await _publisher.FlushConsoleLogAsync();

        _public.VerifyNoOtherCalls();
        Assert.Empty(_batches);
    }

    [Fact]
    public async Task PublicEvents_GoToThePublicGroup()
    {
        _publisher.Attach(Hub());

        await _publisher.TrackChangedAsync("fields14");
        await _publisher.ServerStoppedAsync(new ServerStoppedEvent { ProcessId = 42, StopMethod = "Force" });
        await _publisher.PlayersUpdatedAsync([new Player { Name = "Procat", IsBot = false, Slot = 3 }]);

        _public.Verify(c => c.TrackChanged(new TrackChangedMessage("fields14")));
        _public.Verify(c => c.ServerStopped(It.Is<ServerStoppedMessage>(m => m.ProcessId == 42 && m.StopMethod == "Force")));
        _public.Verify(c => c.PlayersUpdated(It.Is<PlayersUpdatedMessage>(m =>
            m.Players.Single().Name == "Procat" && m.Players.Single().Slot == 3)));
        _admin.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ConsoleLines_GoToTheAdminGroup_AsOneBatch()
    {
        _publisher.Attach(Hub());

        _publisher.AddConsoleLog("one");
        _publisher.AddConsoleLog("two");
        _publisher.AddConsoleLog("three");
        await _publisher.FlushConsoleLogAsync();

        Assert.Equal([["one", "two", "three"]], _batches);
        _public.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ALongBacklog_IsSplitIntoBatches_InOrder()
    {
        // Held so the size-triggered flush cannot send before the backlog is complete.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _admin.Setup(c => c.ConsoleLog(It.IsAny<ConsoleLogMessage>()))
            .Returns<ConsoleLogMessage>(async m =>
            {
                await gate.Task;
                lock (_batches) _batches.Add(m.Logs);
            });
        _publisher.Attach(Hub());

        var total = HubServerEventPublisher.MaxBatchSize * 2 + 5;
        for (var i = 0; i < total; i++)
        {
            _publisher.AddConsoleLog($"line {i}");
        }

        var flush = _publisher.FlushConsoleLogAsync();
        gate.SetResult();
        await flush;

        List<IReadOnlyList<string>> batches;
        lock (_batches) batches = [.. _batches];
        Assert.All(batches, b => Assert.InRange(b.Count, 1, HubServerEventPublisher.MaxBatchSize));
        Assert.Equal(Enumerable.Range(0, total).Select(i => $"line {i}"), batches.SelectMany(b => b));
    }

    [Fact]
    public async Task AFailedSend_IsLoggedNotThrown()
    {
        _public.Setup(c => c.TrackChanged(It.IsAny<TrackChangedMessage>()))
            .ThrowsAsync(new InvalidOperationException("connection gone"));
        _publisher.Attach(Hub());

        await _publisher.TrackChangedAsync("fields14");
    }

    [Fact]
    public async Task DetachingAnOlderHub_LeavesTheNewerOneAttached()
    {
        var older = Hub();
        var newer = Hub();
        _publisher.Attach(older);
        _publisher.Attach(newer);

        _publisher.Detach(older);
        Assert.True(_publisher.IsAttached);

        _publisher.Detach(newer);
        Assert.False(_publisher.IsAttached);
        await _publisher.TrackChangedAsync("fields14");
        _public.Verify(c => c.TrackChanged(It.IsAny<TrackChangedMessage>()), Times.Never);
    }

    [Fact]
    public async Task Detach_DiscardsUnsentConsoleLines()
    {
        var hub = Hub();
        _publisher.Attach(hub);
        _publisher.AddConsoleLog("for clients that are leaving");

        _publisher.Detach(hub);
        _publisher.Attach(hub);
        await _publisher.FlushConsoleLogAsync();

        Assert.Empty(_batches);
    }
}
