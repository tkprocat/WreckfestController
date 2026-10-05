using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using WreckfestController.Models;
using WreckfestController.Services.Hook;
using WreckfestController.Services.Publishing;
using WreckfestController.Services.ServerControl;
using WreckfestController.Services.Tracking;

namespace WreckfestController.Tests.Services.ServerControl;

/// <summary>
/// At startup the controller reattaches to its own server, found by its marker, and injects
/// the hook (#201): only when exactly one configured server carries its id, and with one
/// retry, because an inject can report failure although the DLL loaded.
/// </summary>
public class ReattachOwnServerTests
{
    private readonly Mock<ServerManager> _server;

    public ReattachOwnServerTests()
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
            events,
            Mock.Of<IServerInputWriter>(),
            Mock.Of<IInjectedHookOutputReader>(),
            new ControllerInstance(@"C:\Data\controller.db"))
        {
            CallBase = true,
        };
        _server.Setup(s => s.AttachToConfiguredServer(It.IsAny<int>(), It.IsAny<long?>())).Returns((true, "Attached"));
        _server.Setup(s => s.InjectConsoleHookAsync(It.IsAny<int>())).ReturnsAsync((true, "Injected"));
    }

    private void Running(params ServerProcessInfo[] servers) =>
        _server.Setup(s => s.GetRunningWreckfestServers()).Returns(servers.ToList());

    private static ServerProcessInfo Server(int pid, ServerOwner owner, bool configured = true) =>
        new() { ProcessId = pid, IsConfiguredServer = configured, Owner = owner };

    [Fact]
    public async Task OneOwnServer_IsAttachedAndInjected()
    {
        Running(
            Server(10, ServerOwner.OtherController),
            Server(11, ServerOwner.Unmarked),
            Server(12, ServerOwner.ThisController));

        var (success, message) = await _server.Object.ReattachOwnServerAsync();

        Assert.True(success, message);
        // Conditional on the selection read before the scan, so an admin's attach meanwhile wins.
        _server.Verify(s => s.AttachToConfiguredServer(12, _server.Object.CurrentSelectionId), Times.Once);
        _server.Verify(s => s.AttachToConfiguredServer(It.Is<int>(pid => pid != 12), It.IsAny<long?>()), Times.Never);
        _server.Verify(s => s.InjectConsoleHookAsync(12), Times.Once);
        Assert.Contains("Hook injected", message);
    }

    [Fact]
    public async Task NoOwnServer_AttachesNothing()
    {
        Running(Server(10, ServerOwner.OtherController), Server(11, ServerOwner.Unmarked));

        var (success, _) = await _server.Object.ReattachOwnServerAsync();

        Assert.False(success);
        _server.Verify(s => s.AttachToConfiguredServer(It.IsAny<int>(), It.IsAny<long?>()), Times.Never);
    }

    [Fact]
    public async Task TwoOwnServers_AttachesNothing_AndNamesThem()
    {
        Running(Server(12, ServerOwner.ThisController), Server(13, ServerOwner.ThisController));

        var (success, message) = await _server.Object.ReattachOwnServerAsync();

        Assert.False(success);
        Assert.Contains("12, 13", message);
        _server.Verify(s => s.AttachToConfiguredServer(It.IsAny<int>(), It.IsAny<long?>()), Times.Never);
    }

    [Fact]
    public async Task OwnMarkerOnAnotherInstall_DoesNotCount()
    {
        Running(Server(12, ServerOwner.ThisController, configured: false));

        var (success, _) = await _server.Object.ReattachOwnServerAsync();

        Assert.False(success);
        _server.Verify(s => s.AttachToConfiguredServer(It.IsAny<int>(), It.IsAny<long?>()), Times.Never);
    }

    [Fact]
    public async Task FailedInject_IsRetriedOnce()
    {
        Running(Server(12, ServerOwner.ThisController));
        _server.SetupSequence(s => s.InjectConsoleHookAsync(12))
            .ReturnsAsync((false, "The console hook could not be injected."))
            .ReturnsAsync((true, "Console hook reconnected existing for process 12"));

        var (success, message) = await _server.Object.ReattachOwnServerAsync();

        Assert.True(success, message);
        Assert.Contains("Hook injected", message);
        _server.Verify(s => s.InjectConsoleHookAsync(12), Times.Exactly(2));
    }

    [Fact]
    public async Task InjectFailingTwice_StillReattaches_AndSaysTheHookIsMissing()
    {
        Running(Server(12, ServerOwner.ThisController));
        _server.Setup(s => s.InjectConsoleHookAsync(12)).ReturnsAsync((false, "Unsupported build"));

        var (success, message) = await _server.Object.ReattachOwnServerAsync();

        Assert.True(success, message);
        Assert.Contains("use INJECT", message);
        _server.Verify(s => s.InjectConsoleHookAsync(12), Times.Exactly(2));
    }

    [Fact]
    public async Task FailedAttach_DoesNotInject()
    {
        Running(Server(12, ServerOwner.ThisController));
        _server.Setup(s => s.AttachToConfiguredServer(12, It.IsAny<long?>())).Returns((false, "The attachment changed meanwhile; left as it is."));

        var (success, _) = await _server.Object.ReattachOwnServerAsync();

        Assert.False(success);
        _server.Verify(s => s.InjectConsoleHookAsync(It.IsAny<int>()), Times.Never);
    }
}
