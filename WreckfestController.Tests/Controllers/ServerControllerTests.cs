using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using WreckfestController.Controllers;
using WreckfestController.Services;
using Xunit;

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
        var mockLoggerFactory = new Mock<ILoggerFactory>();
        var mockPlayerTrackerLogger = new Mock<ILogger<PlayerTracker>>();
        var mockTrackChangeTrackerLogger = new Mock<ILogger<TrackChangeTracker>>();
        var mockWebhookService = new Mock<LaravelWebhookService>(
            Mock.Of<ILogger<LaravelWebhookService>>(),
            Mock.Of<IConfiguration>(),
            Mock.Of<HttpClient>());

        var playerTracker = new PlayerTracker(mockPlayerTrackerLogger.Object, mockWebhookService.Object);
        var trackChangeTracker = new TrackChangeTracker(mockTrackChangeTrackerLogger.Object, mockWebhookService.Object);

        _mockServerManager = new Mock<ServerManager>(
            mockConfiguration.Object,
            mockServerManagerLogger.Object,
            mockLoggerFactory.Object,
            playerTracker,
            trackChangeTracker);
        _mockLogger = new Mock<ILogger<ServerController>>();
        _controller = new ServerController(_mockServerManager.Object, _mockLogger.Object);
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
        var okResult = Assert.IsType<OkObjectResult>(result);
        var status = Assert.IsType<ServerStatus>(okResult.Value);
        Assert.Equal(expectedStatus.IsRunning, status.IsRunning);
        Assert.Equal(expectedStatus.ProcessId, status.ProcessId);
        Assert.Equal(expectedStatus.Uptime, status.Uptime);
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
        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);
        _mockServerManager.Verify(m => m.StartServerAsync(), Times.Once);
    }

    [Fact]
    public async Task StartServer_WhenFailed_ReturnsBadRequest()
    {
        // Arrange
        _mockServerManager.Setup(m => m.StartServerAsync())
            .ReturnsAsync((false, "Server is already running"));

        // Act
        var result = await _controller.StartServer();

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
        Assert.NotNull(badRequestResult.Value);
        _mockServerManager.Verify(m => m.StartServerAsync(), Times.Once);
    }

    [Fact]
    public async Task StopServer_WhenSuccessful_ReturnsOkResult()
    {
        // Arrange
        _mockServerManager.Setup(m => m.StopServerAsync())
            .ReturnsAsync((true, "Server stopped successfully"));

        // Act
        var result = await _controller.StopServer();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);
        _mockServerManager.Verify(m => m.StopServerAsync(), Times.Once);
    }

    [Fact]
    public async Task StopServer_WhenFailed_ReturnsBadRequest()
    {
        // Arrange
        _mockServerManager.Setup(m => m.StopServerAsync())
            .ReturnsAsync((false, "Server is not running"));

        // Act
        var result = await _controller.StopServer();

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
        Assert.NotNull(badRequestResult.Value);
        _mockServerManager.Verify(m => m.StopServerAsync(), Times.Once);
    }

    [Fact]
    public async Task RestartServer_WhenSuccessful_ReturnsOkResult()
    {
        // Arrange
        _mockServerManager.Setup(m => m.RestartServerAsync())
            .ReturnsAsync((true, "Server restarted successfully"));

        // Act
        var result = await _controller.RestartServer();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);
        _mockServerManager.Verify(m => m.RestartServerAsync(), Times.Once);
    }

    [Fact]
    public async Task RestartServer_WhenFailed_ReturnsBadRequest()
    {
        // Arrange
        _mockServerManager.Setup(m => m.RestartServerAsync())
            .ReturnsAsync((false, "Failed to restart"));

        // Act
        var result = await _controller.RestartServer();

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
        Assert.NotNull(badRequestResult.Value);
        _mockServerManager.Verify(m => m.RestartServerAsync(), Times.Once);
    }

    [Fact]
    public void GetPlayers_ReturnsPlayerList()
    {
        // Arrange
        var expectedResponse = new Models.PlayerListResponse
        {
            TotalPlayers = 3,
            MaxPlayers = 24,
            Players = new List<Models.Player>
            {
                new Models.Player { Name = "Player1", IsOnline = true, IsBot = false, Slot = 0 },
                new Models.Player { Name = "eRacer", IsOnline = true, IsBot = true, Slot = 1 },
                new Models.Player { Name = "Player2", IsOnline = true, IsBot = false, Slot = 2 }
            },
            LastUpdated = DateTime.Now
        };

        _mockServerManager.Setup(m => m.GetPlayerList())
            .Returns(expectedResponse);

        // Act
        var result = _controller.GetPlayers();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var playerList = Assert.IsType<Models.PlayerListResponse>(okResult.Value);
        Assert.Equal(3, playerList.TotalPlayers);
        Assert.Equal(24, playerList.MaxPlayers);
        Assert.Equal(3, playerList.Players.Count);
        Assert.Contains(playerList.Players, p => p.IsBot);
        Assert.Contains(playerList.Players, p => !p.IsBot);
        _mockServerManager.Verify(m => m.GetPlayerList(), Times.Once);
    }
}
