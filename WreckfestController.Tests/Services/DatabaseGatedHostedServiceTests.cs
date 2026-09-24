using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WreckfestController.Data;
using WreckfestController.Services;

namespace WreckfestController.Tests.Services;

public class DatabaseGatedHostedServiceTests
{
    private readonly Mock<IHostedService> _inner = new();
    private readonly DatabaseState _state = new("test.db");

    [Fact]
    public async Task Start_WhenDatabaseReady_StartsInnerService()
    {
        _state.MarkReady(backupPath: null);
        var gate = CreateGate();

        await gate.StartAsync(TestContext.Current.CancellationToken);

        _inner.Verify(s => s.StartAsync(It.IsAny<CancellationToken>()), Times.Once);
        Assert.True(gate.IsStarted);
    }

    [Fact]
    public async Task Start_InRecoveryMode_HoldsInnerServiceBack()
    {
        _state.MarkFailed("broken", backupPath: null);
        var gate = CreateGate();

        await gate.StartAsync(TestContext.Current.CancellationToken);

        _inner.Verify(s => s.StartAsync(It.IsAny<CancellationToken>()), Times.Never);
        Assert.False(gate.IsStarted);
    }

    [Fact]
    public async Task SuccessfulRetry_StartsInnerServiceOnce()
    {
        _state.MarkFailed("broken", backupPath: null);
        var gate = CreateGate();
        await gate.StartAsync(TestContext.Current.CancellationToken);

        _state.MarkFailed("still broken", backupPath: null);
        _state.MarkReady(backupPath: null);
        _state.MarkReady(backupPath: null);

        _inner.Verify(s => s.StartAsync(It.IsAny<CancellationToken>()), Times.Once);
        Assert.True(gate.IsStarted);
    }

    [Fact]
    public async Task Retry_AfterHostStopped_DoesNotStartInnerService()
    {
        _state.MarkFailed("broken", backupPath: null);
        var gate = CreateGate();
        await gate.StartAsync(TestContext.Current.CancellationToken);
        await gate.StopAsync(TestContext.Current.CancellationToken);

        _state.MarkReady(backupPath: null);

        _inner.Verify(s => s.StartAsync(It.IsAny<CancellationToken>()), Times.Never);
        _inner.Verify(s => s.StopAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Stop_AfterStart_StopsInnerService()
    {
        _state.MarkReady(backupPath: null);
        var gate = CreateGate();
        await gate.StartAsync(TestContext.Current.CancellationToken);

        await gate.StopAsync(TestContext.Current.CancellationToken);

        _inner.Verify(s => s.StopAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    private DatabaseGatedHostedService<IHostedService> CreateGate() =>
        new(_inner.Object, _state, NullLogger<DatabaseGatedHostedService<IHostedService>>.Instance);
}
