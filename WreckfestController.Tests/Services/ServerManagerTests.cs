using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using WreckfestController.Services;
using Xunit;

namespace WreckfestController.Tests.Services;

public class ServerManagerTests
{
    private readonly Mock<IConfiguration> _mockConfiguration;
    private readonly Mock<ILogger<ServerManager>> _mockLogger;
    private readonly Mock<ILoggerFactory> _mockLoggerFactory;
    private readonly Mock<ILogger<PlayerTracker>> _mockPlayerTrackerLogger;
    private readonly Mock<ILogger<TrackChangeTracker>> _mockTrackChangeTrackerLogger;
    private readonly Mock<LaravelWebhookService> _mockWebhookService;
    private readonly PlayerTracker _playerTracker;
    private readonly TrackChangeTracker _trackChangeTracker;
    private readonly Mock<OcrPlayerTracker> _mockOcrPlayerTracker;
    private readonly ServerManager _serverManager;

    public ServerManagerTests()
    {
        _mockConfiguration = new Mock<IConfiguration>();
        _mockLogger = new Mock<ILogger<ServerManager>>();
        _mockLoggerFactory = new Mock<ILoggerFactory>();
        _mockPlayerTrackerLogger = new Mock<ILogger<PlayerTracker>>();
        _mockTrackChangeTrackerLogger = new Mock<ILogger<TrackChangeTracker>>();
        _mockWebhookService = new Mock<LaravelWebhookService>(
            Mock.Of<ILogger<LaravelWebhookService>>(),
            Mock.Of<IConfiguration>(),
            Mock.Of<HttpClient>());

        // Setup mock configuration with test values
        _mockConfiguration.Setup(c => c["WreckfestServer:ServerPath"])
            .Returns("C:\\test\\server.bat");
        _mockConfiguration.Setup(c => c["WreckfestServer:WorkingDirectory"])
            .Returns("C:\\test");
        _mockConfiguration.Setup(c => c["WreckfestServer:EnableOcrPlayerTracking"])
            .Returns("false");

        // Setup configuration section for GetValue<bool>
        var mockOcrSection = new Mock<IConfigurationSection>();
        mockOcrSection.Setup(s => s.Value).Returns("false");
        _mockConfiguration.Setup(c => c.GetSection("WreckfestServer:EnableOcrPlayerTracking"))
            .Returns(mockOcrSection.Object);

        _playerTracker = new PlayerTracker(_mockPlayerTrackerLogger.Object, _mockWebhookService.Object);
        _trackChangeTracker = new TrackChangeTracker(_mockTrackChangeTrackerLogger.Object, _mockWebhookService.Object);

        // Setup mock configuration for OcrPlayerTracker
        var mockOcrConfigSection = new Mock<IConfigurationSection>();
        mockOcrConfigSection.Setup(c => c.Value).Returns("false");

        var mockOcrConfig = new Mock<IConfiguration>();
        mockOcrConfig.Setup(c => c["WreckfestServer:EnableOcrPlayerTracking"]).Returns("false");
        mockOcrConfig.Setup(c => c.GetSection("WreckfestServer:EnableOcrPlayerTracking"))
            .Returns(mockOcrConfigSection.Object);

        _mockOcrPlayerTracker = new Mock<OcrPlayerTracker>(
            Mock.Of<ILogger<OcrPlayerTracker>>(),
            mockOcrConfig.Object,
            _playerTracker,
            Mock.Of<ILogger<ConsoleWriter>>(),
            Mock.Of<ILogger<ConsoleOcr>>());

        _serverManager = new ServerManager(
            _mockConfiguration.Object,
            _mockLogger.Object,
            _mockLoggerFactory.Object,
            _playerTracker,
            _trackChangeTracker,
            _mockOcrPlayerTracker.Object);
    }

    [Fact]
    public void GetStatus_WhenServerNotStarted_ReturnsNotRunning()
    {
        // Act
        var status = _serverManager.GetStatus();

        // Assert
        Assert.False(status.IsRunning);
        Assert.Null(status.ProcessId);
        Assert.Null(status.Uptime);
    }

    [Fact]
    public void IsRunning_WhenServerNotStarted_ReturnsFalse()
    {
        // Assert
        Assert.False(_serverManager.IsRunning);
    }

    [Fact]
    public async Task StartServerAsync_WhenServerPathDoesNotExist_ReturnsFailure()
    {
        // Arrange
        _mockConfiguration.Setup(c => c["WreckfestServer:ServerPath"])
            .Returns("C:\\nonexistent\\server.bat");

        var serverManager = new ServerManager(
            _mockConfiguration.Object,
            _mockLogger.Object,
            _mockLoggerFactory.Object,
            _playerTracker,
            _trackChangeTracker,
            _mockOcrPlayerTracker.Object);

        // Act
        var result = await serverManager.StartServerAsync();

        // Assert
        Assert.False(result.Success);
        Assert.Contains("not found", result.Message);
    }

    [Fact]
    public async Task StartServerAsync_WhenServerPathIsEmpty_ReturnsFailure()
    {
        // Arrange
        _mockConfiguration.Setup(c => c["WreckfestServer:ServerPath"])
            .Returns(string.Empty);

        var serverManager = new ServerManager(
            _mockConfiguration.Object,
            _mockLogger.Object,
            _mockLoggerFactory.Object,
            _playerTracker,
            _trackChangeTracker,
            _mockOcrPlayerTracker.Object);

        // Act
        var result = await serverManager.StartServerAsync();

        // Assert
        Assert.False(result.Success);
        Assert.Contains("not found", result.Message);
    }

    [Fact]
    public async Task StopServerAsync_WhenServerNotRunning_ReturnsFailure()
    {
        // Act
        var result = await _serverManager.StopServerAsync();

        // Assert
        Assert.False(result.Success);
        Assert.Contains("not running", result.Message);
    }

    [Fact]
    public async Task SendCommandAsync_WhenServerNotRunning_ReturnsFailure()
    {
        // Act
        var result = await _serverManager.SendCommandAsync("test command");

        // Assert
        Assert.False(result.Success);
        Assert.Contains("not running", result.Message);
    }

    [Fact]
    public void SubscribeToOutput_DoesNotThrowException()
    {
        // Arrange
        Action<string> callback = (message) => { };

        // Act & Assert
        var exception = Record.Exception(() => _serverManager.SubscribeToConsoleOutput(callback));
        Assert.Null(exception);
    }

    [Fact]
    public void UnsubscribeFromOutput_DoesNotThrowException()
    {
        // Arrange
        Action<string> callback = (message) => { };
        _serverManager.SubscribeToConsoleOutput(callback);

        // Act & Assert
        var exception = Record.Exception(() => _serverManager.UnsubscribeFromConsoleOutput(callback));
        Assert.Null(exception);
    }
}
