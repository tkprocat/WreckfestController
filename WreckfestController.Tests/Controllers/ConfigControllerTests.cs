using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
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
    private readonly Mock<ServerManager> _mockServerManager;
    private readonly Mock<ILogger<ConfigController>> _mockLogger;
    private readonly ConfigController _controller;

    public ConfigControllerTests()
    {
        var mockConfiguration = new Mock<Microsoft.Extensions.Configuration.IConfiguration>();
        var mockConfigLogger = new Mock<ILogger<ConfigService>>();

        var mockWebhookService = new Mock<WreckfestWebWebhookService>(
            Mock.Of<ILogger<WreckfestWebWebhookService>>(),
            Mock.Of<IConfiguration>(),
            Mock.Of<HttpClient>());
        var playerTracker = new PlayerTracker(Mock.Of<ILogger<PlayerTracker>>(), mockWebhookService.Object);
        var trackChangeTracker = new TrackChangeTracker(Mock.Of<ILogger<TrackChangeTracker>>(), mockWebhookService.Object);
        var serverInfoTracker = new ServerInfoTracker(Mock.Of<ILogger<ServerInfoTracker>>());
        var consoleLogSender = new Mock<ConsoleLogWebhookSender>(
            Mock.Of<HttpClient>(),
            Mock.Of<IConfiguration>(),
            Mock.Of<ILogger<ConsoleLogWebhookSender>>());

        _mockConfigService = new Mock<ConfigService>(mockConfiguration.Object, mockConfigLogger.Object) { CallBase = false };
        _mockServerManager = new Mock<ServerManager>(
            Mock.Of<Microsoft.Extensions.Configuration.IConfiguration>(),
            Mock.Of<ILogger<ServerManager>>(),
            playerTracker,
            trackChangeTracker,
            serverInfoTracker,
            mockWebhookService.Object,
            consoleLogSender.Object) { CallBase = false };
        _mockLogger = new Mock<ILogger<ConfigController>>();
        _controller = new ConfigController(_mockConfigService.Object, _mockServerManager.Object, _mockLogger.Object);
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

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private ServerConfig SetUpCurrentConfig()
    {
        var current = new ServerConfig
        {
            ServerName = "Old name",
            Password = "secret",
            MaxPlayers = 16,
            GamePort = 40000,
            Track = "loop"
        };
        _mockConfigService.Setup(s => s.ReadBasicConfig()).Returns(current);
        return current;
    }

    [Fact]
    public void UpdateBasicConfig_PartialBody_ChangesOnlySuppliedFields()
    {
        SetUpCurrentConfig();
        ServerConfig? written = null;
        _mockConfigService.Setup(s => s.WriteBasicConfig(It.IsAny<ServerConfig>()))
            .Callback<ServerConfig>(c => written = c);

        var result = _controller.UpdateBasicConfig(Json("""{"serverName":"New name","maxPlayers":20}"""));

        Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(written);
        Assert.Equal("New name", written.ServerName);
        Assert.Equal(20, written.MaxPlayers);
        Assert.Equal("secret", written.Password);
        Assert.Equal(40000, written.GamePort);
        Assert.Equal("loop", written.Track);
    }

    [Fact]
    public void UpdateBasicConfig_FieldNamesAreCaseInsensitive()
    {
        SetUpCurrentConfig();
        ServerConfig? written = null;
        _mockConfigService.Setup(s => s.WriteBasicConfig(It.IsAny<ServerConfig>()))
            .Callback<ServerConfig>(c => written = c);

        var result = _controller.UpdateBasicConfig(Json("""{"ServerName":"New name"}"""));

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("New name", written?.ServerName);
    }

    [Theory]
    [InlineData("""{"serverNmae":"typo"}""")]
    [InlineData("""{"maxPlayers":"lots"}""")]
    [InlineData("""{"maxPlayers":null}""")]
    [InlineData("""{"serverName":null}""")]
    [InlineData("""{"serverName":"a\nlaps=99"}""")]
    [InlineData("""["serverName"]""")]
    public void UpdateBasicConfig_InvalidBody_ReturnsBadRequestWithoutWriting(string body)
    {
        var current = SetUpCurrentConfig();

        var result = _controller.UpdateBasicConfig(Json(body));

        Assert.IsType<BadRequestObjectResult>(result);
        _mockConfigService.Verify(s => s.WriteBasicConfig(It.IsAny<ServerConfig>()), Times.Never);
        Assert.Equal("Old name", current.ServerName);
    }

    [Fact]
    public void UpdateBasicConfig_WhenException_ReturnsBadRequest()
    {
        SetUpCurrentConfig();
        _mockConfigService.Setup(s => s.WriteBasicConfig(It.IsAny<ServerConfig>()))
            .Throws(new System.IO.IOException("Write failed"));

        var result = _controller.UpdateBasicConfig(Json("""{"serverName":"New name"}"""));

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

    public static TheoryData<string, List<EventLoopTrack>?> InvalidTrackRequests => new()
    {
        { "", [new EventLoopTrack { Track = "track1" }] },
        { "   ", [new EventLoopTrack { Track = "track1" }] },
        { "a\nb", [new EventLoopTrack { Track = "track1" }] },
        { "rotation", null },
        { "rotation", [new EventLoopTrack()] },
        { "rotation", [new EventLoopTrack { Track = "track1" }, new EventLoopTrack { Track = " " }] },
        { "rotation", [new EventLoopTrack { Track = "track1\nel_laps=99" }] },
        { "rotation", [new EventLoopTrack { Track = "track1", Weather = "rain\r\n" }] },
    };

    [Theory]
    [MemberData(nameof(InvalidTrackRequests))]
    public void UpdateEventLoopTracks_InvalidRequest_ReturnsBadRequestWithoutWriting(
        string collectionName, List<EventLoopTrack>? tracks)
    {
        var result = _controller.UpdateEventLoopTracks(new UpdateEventLoopTracksRequest(collectionName, tracks!));

        Assert.IsType<BadRequestObjectResult>(result);
        _mockConfigService.Verify(
            s => s.WriteEventLoopTracks(It.IsAny<string>(), It.IsAny<List<EventLoopTrack>>()), Times.Never);
    }
}
