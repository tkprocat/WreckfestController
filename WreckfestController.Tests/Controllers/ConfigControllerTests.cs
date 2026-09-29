using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using WreckfestController.Controllers;
using WreckfestController.Models;
using Xunit;
using WreckfestController.Services.Config;
using WreckfestController.Services.Publishing;
using WreckfestController.Services.ServerControl;
using WreckfestController.Services.Tracking;
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

        var mockEvents = new Mock<IServerEventPublisher>();
        var playerTracker = new PlayerTracker(Mock.Of<ILogger<PlayerTracker>>(), mockEvents.Object);
        var trackChangeTracker = new TrackChangeTracker(Mock.Of<ILogger<TrackChangeTracker>>(), mockEvents.Object);
        var serverInfoTracker = new ServerInfoTracker(Mock.Of<ILogger<ServerInfoTracker>>());

        _mockConfigService = new Mock<ConfigService>(TestSettings.Server(), mockConfigLogger.Object) { CallBase = false };
        // Every key has an active line unless a test says otherwise (ConfigControllerFileTests
        // covers the real file).
        _mockConfigService.Setup(s => s.MissingBasicKeys(It.IsAny<IEnumerable<string>>())).Returns([]);
        _mockServerManager = new Mock<ServerManager>(
            Mock.Of<Microsoft.Extensions.Configuration.IConfiguration>(), TestSettings.Server(), TestSettings.SteamCmd(),
            Mock.Of<ILogger<ServerManager>>(),
            playerTracker,
            trackChangeTracker,
            serverInfoTracker,
            mockEvents.Object) { CallBase = false };
        _mockLogger = new Mock<ILogger<ConfigController>>();
        _controller = new ConfigController(_mockConfigService.Object, _mockServerManager.Object, _mockLogger.Object).Hosted();
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
        var config = result.Value!;
        Assert.Equal("Test Server", config.ServerName);
        Assert.Equal(24, config.MaxPlayers);
        _mockConfigService.Verify(s => s.ReadBasicConfig(), Times.Once);
    }

    [Fact]
    public void GetBasicConfig_WhenTheFileCannotBeRead_IsRefused()
    {
        _mockConfigService.Setup(s => s.ReadBasicConfig())
            .Throws(new System.IO.FileNotFoundException("Config file not found"));

        var result = _controller.GetBasicConfig();

        Assert.Contains("Config file not found", ControllerTesting.RefusalOf(result), StringComparison.Ordinal);
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

        // The settings as they now are.
        Assert.Equal(("New name", 20), (result.Value!.ServerName, result.Value.MaxPlayers));
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

        Assert.NotNull(result.Value);
        Assert.Equal("New name", written?.ServerName);
    }

    [Theory]
    [InlineData("""{"serverNmae":"typo"}""", "serverNmae")]
    [InlineData("""{"maxPlayers":"lots"}""", "maxPlayers")]
    // Strict numbers, like the rest of the API.
    [InlineData("""{"maxPlayers":"5"}""", "maxPlayers")]
    [InlineData("""{"maxPlayers":null}""", "maxPlayers")]
    [InlineData("""{"serverName":null}""", "serverName")]
    [InlineData("""{"serverName":"a\nlaps=99"}""", "serverName")]
    [InlineData("""["serverName"]""", "body")]
    // log= names the file the log viewer returns: never written over the API.
    [InlineData("""{"log":"C:\\Users\\someone\\AppData\\Local\\WreckfestController\\user-settings.json"}""", "log")]
    [InlineData("""{"Log":"log.txt"}""", "Log")]
    [InlineData("""{"serverName":"New name","log":"..\\..\\secret.txt"}""", "log")]
    public void UpdateBasicConfig_InvalidBody_NamesTheField_AndWritesNothing(string body, string field)
    {
        var current = SetUpCurrentConfig();

        var result = _controller.UpdateBasicConfig(Json(body));

        ControllerTesting.AssertFieldError(result, field);
        _mockConfigService.Verify(s => s.WriteBasicConfig(It.IsAny<ServerConfig>()), Times.Never);
        Assert.Equal("Old name", current.ServerName);
    }

    [Fact]
    public void UpdateBasicConfig_WhenTheFileCannotBeWritten_IsRefused()
    {
        SetUpCurrentConfig();
        _mockConfigService.Setup(s => s.WriteBasicConfig(It.IsAny<ServerConfig>()))
            .Throws(new System.IO.IOException("Write failed"));

        var result = _controller.UpdateBasicConfig(Json("""{"serverName":"New name"}"""));

        Assert.Contains("Write failed", ControllerTesting.RefusalOf(result), StringComparison.Ordinal);
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
        Assert.Equal(2, result.Value!.Count);
        Assert.Equal(["track1", "track2"], result.Value.Tracks.Select(t => t.Track));
        _mockConfigService.Verify(s => s.ReadEventLoopTracks(), Times.Once);
    }

    [Fact]
    public void GetEventLoopTracks_WhenTheFileCannotBeRead_IsRefused()
    {
        _mockConfigService.Setup(s => s.ReadEventLoopTracks())
            .Throws(new System.Exception("Read failed"));

        var result = _controller.GetEventLoopTracks();

        Assert.Contains("Read failed", ControllerTesting.RefusalOf(result), StringComparison.Ordinal);
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

        _mockConfigService.Setup(s => s.ReadEventLoopTracks()).Returns(tracks);

        // Act
        var result = _controller.UpdateEventLoopTracks(new UpdateEventLoopTracksRequest(collectionName, tracks));

        // Assert
        Assert.Equal(1, result.Value!.Count);
        _mockConfigService.Verify(s => s.WriteEventLoopTracks(collectionName, tracks), Times.Once);
    }

    [Fact]
    public void UpdateEventLoopTracks_WhenTheFileCannotBeWritten_IsRefused()
    {
        var collectionName = "New collection";
        var tracks = new List<EventLoopTrack> { new() { Track = "track1" } };
        _mockConfigService.Setup(s => s.WriteEventLoopTracks(collectionName, It.IsAny<List<EventLoopTrack>>()))
            .Throws(new System.Exception("Write failed"));

        var result = _controller.UpdateEventLoopTracks(new UpdateEventLoopTracksRequest(collectionName, tracks));

        Assert.Contains("Write failed", ControllerTesting.RefusalOf(result), StringComparison.Ordinal);
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
        { "rotation", [new EventLoopTrack { Track = "track 1" }] },
        { "rotation", [new EventLoopTrack { Track = "track1", Laps = -1 }] },
        { "rotation", [new EventLoopTrack { Track = "track1", CarResetDisabled = 2 }] },
        { new string('x', 129), [new EventLoopTrack { Track = "track1" }] },
    };

    [Theory]
    [MemberData(nameof(InvalidTrackRequests))]
    public void UpdateEventLoopTracks_InvalidRequest_ReturnsBadRequestWithoutWriting(
        string collectionName, List<EventLoopTrack>? tracks)
    {
        var result = _controller.UpdateEventLoopTracks(new UpdateEventLoopTracksRequest(collectionName, tracks!));

        var invalid = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        Assert.IsType<ValidationProblemDetails>(invalid.Value);
        _mockConfigService.Verify(
            s => s.WriteEventLoopTracks(It.IsAny<string>(), It.IsAny<List<EventLoopTrack>>()), Times.Never);
    }
}
