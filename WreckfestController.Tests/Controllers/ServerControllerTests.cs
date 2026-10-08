using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using WreckfestController.Controllers;
using Xunit;
using WreckfestController.Services.Publishing;
using WreckfestController.Services.ServerControl;
using WreckfestController.Services.Tracking;

namespace WreckfestController.Tests.Controllers;

public class ServerControllerTests
{
    private readonly Mock<ServerManager> _mockServerManager;
    private readonly Mock<ILogger<ServerController>> _mockLogger;
    private readonly ServerController _controller;

    public ServerControllerTests()
    {
        // Create a mock IConfiguration for ServerManager constructor
        var mockConfiguration = new Mock<Microsoft.Extensions.Configuration.IConfiguration>();
        mockConfiguration.Setup(c => c["WreckfestServer:ServerPath"])
            .Returns("C:\\test\\server.bat");
        mockConfiguration.Setup(c => c["WreckfestServer:WorkingDirectory"])
            .Returns("C:\\test");

        var mockServerManagerLogger = new Mock<ILogger<ServerManager>>();
        var mockPlayerTrackerLogger = new Mock<ILogger<PlayerTracker>>();
        var mockTrackChangeTrackerLogger = new Mock<ILogger<TrackChangeTracker>>();
        var mockServerInfoTrackerLogger = new Mock<ILogger<ServerInfoTracker>>();
        var mockEvents = new Mock<IServerEventPublisher>();

        var playerTracker = new PlayerTracker(mockPlayerTrackerLogger.Object, mockEvents.Object);
        var trackChangeTracker = new TrackChangeTracker(mockTrackChangeTrackerLogger.Object, mockEvents.Object);
        var serverInfoTracker = new ServerInfoTracker(mockServerInfoTrackerLogger.Object);

        _mockServerManager = new Mock<ServerManager>(
            mockConfiguration.Object, TestSettings.Server(), TestSettings.SteamCmd(),
            mockServerManagerLogger.Object,
            playerTracker,
            trackChangeTracker,
            serverInfoTracker,
            mockEvents.Object);
        _mockLogger = new Mock<ILogger<ServerController>>();
        _controller = new ServerController(_mockServerManager.Object, _mockLogger.Object).Hosted();
    }

    [Fact]
    public void GetStatus_ReturnsOkResultWithStatus()
    {
        // Arrange
        var expectedStatus = new ServerStatus
        {
            IsRunning = true,
            ProcessId = 1234,
            Uptime = TimeSpan.FromMinutes(30)
        };

        _mockServerManager.Setup(m => m.GetStatus())
            .Returns(expectedStatus);

        // Act
        var result = _controller.GetStatus();

        // Assert
        Assert.Equal(new ServerStatusResponse(true, 1234, 1800, null), result);
    }

    [Fact]
    public async Task StartServer_WhenSuccessful_ReturnsOkResult()
    {
        // Arrange
        _mockServerManager.Setup(m => m.StartServerAsync())
            .ReturnsAsync((true, "Server started successfully"));

        // Act
        var result = await _controller.StartServer();

        // Assert
        Assert.Equal("Server started successfully", result.Value!.Message);
        _mockServerManager.Verify(m => m.StartServerAsync(), Times.Once);
    }

    [Fact]
    public async Task StartServer_WhenFailed_IsRefused()
    {
        // Arrange
        _mockServerManager.Setup(m => m.StartServerAsync())
            .ReturnsAsync((false, "Server is already running"));

        // Act
        var result = await _controller.StartServer();

        // Assert
        Assert.Equal("Server is already running", ControllerTesting.RefusalOf(result));
        _mockServerManager.Verify(m => m.StartServerAsync(), Times.Once);
    }

    [Fact]
    public async Task StopServer_WhenSuccessful_ReturnsOkResult()
    {
        // Arrange
        _mockServerManager.Setup(m => m.StopServerViaCommandAsync())
            .ReturnsAsync((true, "Server stopped successfully"));

        // Act
        var result = await _controller.StopServer();

        // Assert
        Assert.Equal("Server stopped successfully", result.Value!.Message);
        _mockServerManager.Verify(m => m.StopServerViaCommandAsync(), Times.Once);
    }

    [Fact]
    public async Task StopServer_WhenFailed_IsRefused()
    {
        // Arrange
        _mockServerManager.Setup(m => m.StopServerViaCommandAsync())
            .ReturnsAsync((false, "Server is not running"));

        // Act
        var result = await _controller.StopServer();

        // Assert
        Assert.Equal("Server is not running", ControllerTesting.RefusalOf(result));
        _mockServerManager.Verify(m => m.StopServerViaCommandAsync(), Times.Once);
    }

    [Fact]
    public async Task RestartServer_WhenSuccessful_ReturnsOkResult()
    {
        // Arrange
        _mockServerManager.Setup(m => m.RestartServerViaCommandAsync(It.IsAny<AttachmentSession?>()))
            .ReturnsAsync((true, "Server restarted successfully"));

        // Act
        var result = await _controller.RestartServer();

        // Assert
        Assert.Equal("Server restarted successfully", result.Value!.Message);
        _mockServerManager.Verify(m => m.RestartServerViaCommandAsync(It.IsAny<AttachmentSession?>()), Times.Once);
    }

    [Fact]
    public async Task RestartServer_WhenFailed_IsRefused()
    {
        // Arrange
        _mockServerManager.Setup(m => m.RestartServerViaCommandAsync(It.IsAny<AttachmentSession?>()))
            .ReturnsAsync((false, "Failed to restart"));

        // Act
        var result = await _controller.RestartServer();

        // Assert
        Assert.Equal("Failed to restart", ControllerTesting.RefusalOf(result));
        _mockServerManager.Verify(m => m.RestartServerViaCommandAsync(It.IsAny<AttachmentSession?>()), Times.Once);
    }

    [Fact]
    public async Task GetPlayers_ReturnsPlayerList()
    {
        // Arrange
        var expectedResponse = new Models.PlayerListResponse
        {
            TotalPlayers = 3,
            MaxPlayers = 24,
            Players = new List<Models.Player>
            {
                new Models.Player { Name = "Player1", IsBot = false, Slot = 0 },
                new Models.Player { Name = "eRacer", IsBot = true, Slot = 1 },
                new Models.Player { Name = "Player2", IsBot = false, Slot = 2 }
            },
            LastUpdated = DateTime.Now
        };

        _mockServerManager.Setup(m => m.GetPlayerList())
            .Returns(expectedResponse);
        _mockServerManager.Setup(m => m.TryRefreshPlayersFromHookAsync())
            .ReturnsAsync(true);

        // Act
        var result = await _controller.GetPlayers();

        // Assert
        var playerList = result;
        Assert.Equal(3, playerList.TotalPlayers);
        Assert.Equal(24, playerList.MaxPlayers);
        Assert.Equal(3, playerList.Players.Count);
        Assert.Contains(playerList.Players, p => p.IsBot);
        Assert.Contains(playerList.Players, p => !p.IsBot);
        _mockServerManager.Verify(m => m.GetPlayerList(), Times.Once);
        // The endpoint must refresh from the hook snapshot first: hook output only
        // carries lines printed after injection, so players who joined earlier are
        // otherwise missing from the tracker.
        _mockServerManager.Verify(m => m.TryRefreshPlayersFromHookAsync(), Times.Once);
    }
}
