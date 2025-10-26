using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using WreckfestController.Controllers;
using WreckfestController.Models;
using WreckfestController.Services;
using Xunit;
using static WreckfestController.Controllers.ConfigController;

namespace WreckfestController.Tests.Controllers;

public class ConfigControllerTests
{
    private readonly Mock<ConfigService> _mockConfigService;
    private readonly Mock<ILogger<ConfigController>> _mockLogger;
    private readonly ConfigController _controller;

    public ConfigControllerTests()
    {
        var mockConfiguration = new Mock<Microsoft.Extensions.Configuration.IConfiguration>();
        var mockConfigLogger = new Mock<ILogger<ConfigService>>();

        _mockConfigService = new Mock<ConfigService>(mockConfiguration.Object, mockConfigLogger.Object) { CallBase = false };
        _mockLogger = new Mock<ILogger<ConfigController>>();
        _controller = new ConfigController(_mockConfigService.Object, _mockLogger.Object);
    }

    [Fact]
    public void GetBasicConfig_WhenSuccessful_ReturnsOkWithConfig()
    {
        // Arrange
        var expectedConfig = new ServerConfig
        {
            ServerName = "Test Server",
            MaxPlayers = 24,
            Password = "test123"
        };

        _mockConfigService.Setup(s => s.ReadBasicConfig())
            .Returns(expectedConfig);

        // Act
        var result = _controller.GetBasicConfig();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var config = Assert.IsType<ServerConfig>(okResult.Value);
        Assert.Equal("Test Server", config.ServerName);
        Assert.Equal(24, config.MaxPlayers);
        _mockConfigService.Verify(s => s.ReadBasicConfig(), Times.Once);
    }

    [Fact]
    public void GetBasicConfig_WhenException_ReturnsBadRequest()
    {
        // Arrange
        _mockConfigService.Setup(s => s.ReadBasicConfig())
            .Throws(new System.IO.FileNotFoundException("Config file not found"));

        // Act
        var result = _controller.GetBasicConfig();

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
        Assert.NotNull(badRequestResult.Value);
    }

    [Fact]
    public void UpdateBasicConfig_WhenSuccessful_ReturnsOk()
    {
        // Arrange
        var config = new ServerConfig
        {
            ServerName = "Updated Server",
            MaxPlayers = 16
        };

        // Act
        var result = _controller.UpdateBasicConfig(config);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);
        _mockConfigService.Verify(s => s.WriteBasicConfig(config), Times.Once);
    }

    [Fact]
    public void UpdateBasicConfig_WhenException_ReturnsBadRequest()
    {
        // Arrange
        var config = new ServerConfig();
        _mockConfigService.Setup(s => s.WriteBasicConfig(It.IsAny<ServerConfig>()))
            .Throws(new System.IO.IOException("Write failed"));

        // Act
        var result = _controller.UpdateBasicConfig(config);

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
        Assert.NotNull(badRequestResult.Value);
    }

    [Fact]
    public void GetEventLoopTracks_WhenSuccessful_ReturnsOkWithTracks()
    {
        // Arrange
        var expectedTracks = new List<EventLoopTrack>
        {
            new EventLoopTrack { Track = "track1", Gamemode = "race", Laps = 5 },
            new EventLoopTrack { Track = "track2", Gamemode = "derby", Laps = 3 }
        };

        _mockConfigService.Setup(s => s.ReadEventLoopTracks())
            .Returns(expectedTracks);

        // Act
        var result = _controller.GetEventLoopTracks();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);
        _mockConfigService.Verify(s => s.ReadEventLoopTracks(), Times.Once);
    }

    [Fact]
    public void GetEventLoopTracks_WhenException_ReturnsBadRequest()
    {
        // Arrange
        _mockConfigService.Setup(s => s.ReadEventLoopTracks())
            .Throws(new System.Exception("Read failed"));

        // Act
        var result = _controller.GetEventLoopTracks();

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
        Assert.NotNull(badRequestResult.Value);
    }

    [Fact]
    public void UpdateEventLoopTracks_WhenSuccessful_ReturnsOk()
    {
        var collectionName = "New collection";
        // Arrange
        var tracks = new List<EventLoopTrack>
        {
            new EventLoopTrack { Track = "track1", Gamemode = "race" }
        };

        // Act
        var result = _controller.UpdateEventLoopTracks(new UpdateEventLoopTracksRequest(collectionName, tracks));

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);
        _mockConfigService.Verify(s => s.WriteEventLoopTracks(collectionName, tracks), Times.Once);
    }

    [Fact]
    public void UpdateEventLoopTracks_WhenException_ReturnsBadRequest()
    {
        // Arrange
        var collectionName = "New collection"; 
        var tracks = new List<EventLoopTrack>();
        _mockConfigService.Setup(s => s.WriteEventLoopTracks(collectionName, It.IsAny<List<EventLoopTrack>>()))
            .Throws(new System.Exception("Write failed"));

        // Act
        var result = _controller.UpdateEventLoopTracks(new UpdateEventLoopTracksRequest(collectionName, tracks));

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
        Assert.NotNull(badRequestResult.Value);
    }
}
