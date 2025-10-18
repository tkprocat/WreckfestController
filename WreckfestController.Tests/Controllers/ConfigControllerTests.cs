using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using WreckfestController.Controllers;
using WreckfestController.Models;
using WreckfestController.Services;
using Xunit;

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
        // Arrange
        var tracks = new List<EventLoopTrack>
        {
            new EventLoopTrack { Track = "track1", Gamemode = "race" }
        };

        // Act
        var result = _controller.UpdateEventLoopTracks(tracks);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);
        _mockConfigService.Verify(s => s.WriteEventLoopTracks(tracks), Times.Once);
    }

    [Fact]
    public void UpdateEventLoopTracks_WhenException_ReturnsBadRequest()
    {
        // Arrange
        var tracks = new List<EventLoopTrack>();
        _mockConfigService.Setup(s => s.WriteEventLoopTracks(It.IsAny<List<EventLoopTrack>>()))
            .Throws(new System.Exception("Write failed"));

        // Act
        var result = _controller.UpdateEventLoopTracks(tracks);

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
        Assert.NotNull(badRequestResult.Value);
    }

    [Fact]
    public void AddEventLoopTrack_WhenSuccessful_ReturnsOk()
    {
        // Arrange
        var track = new EventLoopTrack { Track = "newtrack", Gamemode = "race" };
        var existingTracks = new List<EventLoopTrack>
        {
            new EventLoopTrack { Track = "track1" }
        };

        _mockConfigService.Setup(s => s.ReadEventLoopTracks())
            .Returns(existingTracks);

        // Act
        var result = _controller.AddEventLoopTrack(track);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);
        _mockConfigService.Verify(s => s.ReadEventLoopTracks(), Times.Once);
        _mockConfigService.Verify(s => s.WriteEventLoopTracks(It.Is<List<EventLoopTrack>>(t => t.Count == 2)), Times.Once);
    }

    [Fact]
    public void AddEventLoopTrack_WhenException_ReturnsBadRequest()
    {
        // Arrange
        var track = new EventLoopTrack();
        _mockConfigService.Setup(s => s.ReadEventLoopTracks())
            .Throws(new System.Exception("Read failed"));

        // Act
        var result = _controller.AddEventLoopTrack(track);

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
        Assert.NotNull(badRequestResult.Value);
    }

    [Fact]
    public void UpdateEventLoopTrack_WithValidIndex_ReturnsOk()
    {
        // Arrange
        var track = new EventLoopTrack { Track = "updated", Gamemode = "derby" };
        var existingTracks = new List<EventLoopTrack>
        {
            new EventLoopTrack { Track = "track1" },
            new EventLoopTrack { Track = "track2" }
        };

        _mockConfigService.Setup(s => s.ReadEventLoopTracks())
            .Returns(existingTracks);

        // Act
        var result = _controller.UpdateEventLoopTrack(1, track);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);
        _mockConfigService.Verify(s => s.WriteEventLoopTracks(It.Is<List<EventLoopTrack>>(
            t => t[1].Track == "updated")), Times.Once);
    }

    [Fact]
    public void UpdateEventLoopTrack_WithInvalidIndex_ReturnsBadRequest()
    {
        // Arrange
        var track = new EventLoopTrack();
        var existingTracks = new List<EventLoopTrack>
        {
            new EventLoopTrack { Track = "track1" }
        };

        _mockConfigService.Setup(s => s.ReadEventLoopTracks())
            .Returns(existingTracks);

        // Act
        var result = _controller.UpdateEventLoopTrack(5, track);

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
        Assert.NotNull(badRequestResult.Value);
    }

    [Fact]
    public void DeleteEventLoopTrack_WithValidIndex_ReturnsOk()
    {
        // Arrange
        var existingTracks = new List<EventLoopTrack>
        {
            new EventLoopTrack { Track = "track1" },
            new EventLoopTrack { Track = "track2" },
            new EventLoopTrack { Track = "track3" }
        };

        _mockConfigService.Setup(s => s.ReadEventLoopTracks())
            .Returns(existingTracks);

        // Act
        var result = _controller.DeleteEventLoopTrack(1);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);
        _mockConfigService.Verify(s => s.WriteEventLoopTracks(It.Is<List<EventLoopTrack>>(
            t => t.Count == 2 && !t.Any(track => track.Track == "track2"))), Times.Once);
    }

    [Fact]
    public void DeleteEventLoopTrack_WithInvalidIndex_ReturnsBadRequest()
    {
        // Arrange
        var existingTracks = new List<EventLoopTrack>
        {
            new EventLoopTrack { Track = "track1" }
        };

        _mockConfigService.Setup(s => s.ReadEventLoopTracks())
            .Returns(existingTracks);

        // Act
        var result = _controller.DeleteEventLoopTrack(10);

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
        Assert.NotNull(badRequestResult.Value);
    }

    [Fact]
    public void DeleteEventLoopTrack_WithNegativeIndex_ReturnsBadRequest()
    {
        // Arrange
        var existingTracks = new List<EventLoopTrack>
        {
            new EventLoopTrack { Track = "track1" }
        };

        _mockConfigService.Setup(s => s.ReadEventLoopTracks())
            .Returns(existingTracks);

        // Act
        var result = _controller.DeleteEventLoopTrack(-1);

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
        Assert.NotNull(badRequestResult.Value);
    }
}
