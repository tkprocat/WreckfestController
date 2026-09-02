using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using System.Diagnostics;
using WreckfestController.Services;
using Xunit;

namespace WreckfestController.Tests.Services;

public class ServerManagerTests
{
    private readonly Mock<IConfiguration> _mockConfiguration;
    private readonly Mock<ILogger<ServerManager>> _mockLogger;
    private readonly Mock<ILogger<PlayerTracker>> _mockPlayerTrackerLogger;
    private readonly Mock<ILogger<TrackChangeTracker>> _mockTrackChangeTrackerLogger;
    private readonly Mock<ILogger<ServerInfoTracker>> _mockServerInfoTrackerLogger;
    private readonly Mock<WreckfestWebWebhookService> _mockWebhookService;
    private readonly PlayerTracker _playerTracker;
    private readonly TrackChangeTracker _trackChangeTracker;
    private readonly ServerInfoTracker _serverInfoTracker;
    private readonly ServerManager _serverManager;

    public ServerManagerTests()
    {
        _mockConfiguration = new Mock<IConfiguration>();
        _mockLogger = new Mock<ILogger<ServerManager>>();
        _mockPlayerTrackerLogger = new Mock<ILogger<PlayerTracker>>();
        _mockTrackChangeTrackerLogger = new Mock<ILogger<TrackChangeTracker>>();
        _mockServerInfoTrackerLogger = new Mock<ILogger<ServerInfoTracker>>();
        _mockWebhookService = new Mock<WreckfestWebWebhookService>(
            Mock.Of<ILogger<WreckfestWebWebhookService>>(),
            Mock.Of<IConfiguration>(),
            Mock.Of<HttpClient>());

        // Setup mock configuration with test values
        _mockConfiguration.Setup(c => c["WreckfestServer:ServerPath"])
            .Returns("C:\\test\\server.bat");
        _mockConfiguration.Setup(c => c["WreckfestServer:WorkingDirectory"])
            .Returns("C:\\test");

        _playerTracker = new PlayerTracker(_mockPlayerTrackerLogger.Object, _mockWebhookService.Object);
        _trackChangeTracker = new TrackChangeTracker(_mockTrackChangeTrackerLogger.Object, _mockWebhookService.Object);
        _serverInfoTracker = new ServerInfoTracker(_mockServerInfoTrackerLogger.Object);

        var mockConsoleLogSender = new Mock<ConsoleLogWebhookSender>(
            Mock.Of<HttpClient>(),
            Mock.Of<IConfiguration>(),
            Mock.Of<ILogger<ConsoleLogWebhookSender>>());

        _serverManager = new ServerManager(
            _mockConfiguration.Object,
            _mockLogger.Object,
            _playerTracker,
            _trackChangeTracker,
            _serverInfoTracker,
            _mockWebhookService.Object,
            mockConsoleLogSender.Object);
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

        var mockConsoleLogSender = new Mock<ConsoleLogWebhookSender>(
            Mock.Of<HttpClient>(),
            Mock.Of<IConfiguration>(),
            Mock.Of<ILogger<ConsoleLogWebhookSender>>());

        var serverManager = new ServerManager(
            _mockConfiguration.Object,
            _mockLogger.Object,
            _playerTracker,
            _trackChangeTracker,
            _serverInfoTracker,
            _mockWebhookService.Object,
            mockConsoleLogSender.Object);

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

        var mockConsoleLogSender = new Mock<ConsoleLogWebhookSender>(
            Mock.Of<HttpClient>(),
            Mock.Of<IConfiguration>(),
            Mock.Of<ILogger<ConsoleLogWebhookSender>>());

        var serverManager = new ServerManager(
            _mockConfiguration.Object,
            _mockLogger.Object,
            _playerTracker,
            _trackChangeTracker,
            _serverInfoTracker,
            _mockWebhookService.Object,
            mockConsoleLogSender.Object);

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
    public async Task SendCommandAsync_WhenInputWriterInjected_UsesInjectedWriter()
    {
        // Arrange
        var inputWriter = new Mock<IServerInputWriter>();
        inputWriter
            .Setup(w => w.SendCommandAsync("status", Process.GetCurrentProcess().Id))
            .ReturnsAsync((true, "sent by injected input"));

        var outputReader = new Mock<IInjectedHookOutputReader>();
        outputReader.SetupGet(r => r.Mode).Returns(ServerOutputModes.InjectedHook);
        outputReader.Setup(r => r.StartAsync(It.IsAny<int>())).ReturnsAsync(true);
        outputReader.Setup(r => r.StopAsync()).Returns(Task.CompletedTask);

        var mockConsoleLogSender = new Mock<ConsoleLogWebhookSender>(
            Mock.Of<HttpClient>(),
            Mock.Of<IConfiguration>(),
            Mock.Of<ILogger<ConsoleLogWebhookSender>>());

        var serverManager = new ServerManager(
            _mockConfiguration.Object,
            _mockLogger.Object,
            _playerTracker,
            _trackChangeTracker,
            _serverInfoTracker,
            _mockWebhookService.Object,
            mockConsoleLogSender.Object,
            inputWriter.Object,
            outputReader.Object);

        serverManager.AttachToExistingProcess(Process.GetCurrentProcess().Id);

        // Act
        var result = await serverManager.SendCommandAsync("status");

        // Assert
        Assert.True(result.Success);
        Assert.Equal("sent by injected input", result.Message);
        inputWriter.Verify(w => w.SendCommandAsync("status", Process.GetCurrentProcess().Id), Times.Once);
    }

    [Fact]
    public async Task SendCommandAsync_TrimsTrailingLineBreaksBeforeInputWriter()
    {
        // Arrange
        var inputWriter = new Mock<IServerInputWriter>();
        inputWriter
            .Setup(w => w.SendCommandAsync("/message hello", Process.GetCurrentProcess().Id))
            .ReturnsAsync((true, "sent"));

        var outputReader = new Mock<IInjectedHookOutputReader>();
        outputReader.SetupGet(r => r.Mode).Returns(ServerOutputModes.InjectedHook);
        outputReader.Setup(r => r.StartAsync(It.IsAny<int>())).ReturnsAsync(true);
        outputReader.Setup(r => r.StopAsync()).Returns(Task.CompletedTask);

        var mockConsoleLogSender = new Mock<ConsoleLogWebhookSender>(
            Mock.Of<HttpClient>(),
            Mock.Of<IConfiguration>(),
            Mock.Of<ILogger<ConsoleLogWebhookSender>>());

        var serverManager = new ServerManager(
            _mockConfiguration.Object,
            _mockLogger.Object,
            _playerTracker,
            _trackChangeTracker,
            _serverInfoTracker,
            _mockWebhookService.Object,
            mockConsoleLogSender.Object,
            inputWriter.Object,
            outputReader.Object);

        serverManager.AttachToExistingProcess(Process.GetCurrentProcess().Id);

        // Act
        var result = await serverManager.SendCommandAsync("/message hello\r\n");

        // Assert
        Assert.True(result.Success);
        inputWriter.Verify(w => w.SendCommandAsync("/message hello", Process.GetCurrentProcess().Id), Times.Once);
        inputWriter.Verify(w => w.SendCommandAsync(It.Is<string>(command => command.Contains('\r') || command.Contains('\n')), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task SendCommandAsync_WhenOnlyLineBreaks_ReturnsEmptyCommand()
    {
        // Arrange
        var inputWriter = new Mock<IServerInputWriter>();
        var outputReader = new Mock<IInjectedHookOutputReader>();
        outputReader.SetupGet(r => r.Mode).Returns(ServerOutputModes.InjectedHook);
        outputReader.Setup(r => r.StartAsync(It.IsAny<int>())).ReturnsAsync(true);
        outputReader.Setup(r => r.StopAsync()).Returns(Task.CompletedTask);

        var mockConsoleLogSender = new Mock<ConsoleLogWebhookSender>(
            Mock.Of<HttpClient>(),
            Mock.Of<IConfiguration>(),
            Mock.Of<ILogger<ConsoleLogWebhookSender>>());

        var serverManager = new ServerManager(
            _mockConfiguration.Object,
            _mockLogger.Object,
            _playerTracker,
            _trackChangeTracker,
            _serverInfoTracker,
            _mockWebhookService.Object,
            mockConsoleLogSender.Object,
            inputWriter.Object,
            outputReader.Object);

        serverManager.AttachToExistingProcess(Process.GetCurrentProcess().Id);

        // Act
        var result = await serverManager.SendCommandAsync("\r\n");

        // Assert
        Assert.False(result.Success);
        Assert.Equal("Command cannot be empty", result.Message);
        inputWriter.Verify(w => w.SendCommandAsync(It.IsAny<string>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task TryRefreshPlayersFromHookAsync_WhenInjectedHookSnapshotSucceeds_UpdatesPlayerTracker()
    {
        // Arrange

        var inputWriter = new Mock<IServerInputWriter>();
        inputWriter
            .As<IPlayerSnapshotReader>()
            .Setup(w => w.ReadPlayerSnapshotAsync(Process.GetCurrentProcess().Id))
            .ReturnsAsync((true, "ok", new[]
            {
                new Models.Player { Name = "Procat", Slot = 1, IsBot = false, JoinedAt = DateTime.UtcNow },
                new Models.Player { Name = "eRacer", Slot = 2, IsBot = true, JoinedAt = DateTime.UtcNow }
            }));

        var outputReader = new Mock<IInjectedHookOutputReader>();
        outputReader.SetupGet(r => r.Mode).Returns(ServerOutputModes.InjectedHook);
        outputReader.Setup(r => r.StartAsync(It.IsAny<int>())).ReturnsAsync(true);
        outputReader.Setup(r => r.StopAsync()).Returns(Task.CompletedTask);

        var mockConsoleLogSender = new Mock<ConsoleLogWebhookSender>(
            Mock.Of<HttpClient>(),
            Mock.Of<IConfiguration>(),
            Mock.Of<ILogger<ConsoleLogWebhookSender>>());

        var serverManager = new ServerManager(
            _mockConfiguration.Object,
            _mockLogger.Object,
            _playerTracker,
            _trackChangeTracker,
            _serverInfoTracker,
            _mockWebhookService.Object,
            mockConsoleLogSender.Object,
            inputWriter.Object,
            outputReader.Object);

        serverManager.AttachToExistingProcess(Process.GetCurrentProcess().Id);

        // Act
        var refreshed = await serverManager.TryRefreshPlayersFromHookAsync();

        // Assert
        Assert.True(refreshed);
        var (humans, total) = _playerTracker.GetPlayerCount();
        Assert.Equal(1, humans);
        Assert.Equal(2, total);
        Assert.Contains(_playerTracker.GetPlayers(), player => player.Name == "Procat" && player.Slot == 1);
    }

    [Fact]
    public async Task TryRefreshPlayersFromHookAsync_ReadsSnapshotAndDoesNotFallBackToListCommand()
    {
        // Arrange

        var inputWriter = new Mock<IServerInputWriter>();
        inputWriter
            .As<IPlayerSnapshotReader>()
            .Setup(w => w.ReadPlayerSnapshotAsync(It.IsAny<int>()))
            .ReturnsAsync((true, "ok", Array.Empty<Models.Player>()));

        var outputReader = new Mock<IInjectedHookOutputReader>();
        outputReader.SetupGet(r => r.Mode).Returns(ServerOutputModes.InjectedHook);
        outputReader.Setup(r => r.StartAsync(It.IsAny<int>())).ReturnsAsync(true);
        outputReader.Setup(r => r.StopAsync()).Returns(Task.CompletedTask);

        var mockConsoleLogSender = new Mock<ConsoleLogWebhookSender>(
            Mock.Of<HttpClient>(),
            Mock.Of<IConfiguration>(),
            Mock.Of<ILogger<ConsoleLogWebhookSender>>());

        var serverManager = new ServerManager(
            _mockConfiguration.Object,
            _mockLogger.Object,
            _playerTracker,
            _trackChangeTracker,
            _serverInfoTracker,
            _mockWebhookService.Object,
            mockConsoleLogSender.Object,
            inputWriter.Object,
            outputReader.Object);

        serverManager.AttachToExistingProcess(Process.GetCurrentProcess().Id);

        // Act
        var refreshed = await serverManager.TryRefreshPlayersFromHookAsync();

        // Assert
        Assert.True(refreshed);
        inputWriter.As<IPlayerSnapshotReader>().Verify(w => w.ReadPlayerSnapshotAsync(It.IsAny<int>()), Times.Once);
        inputWriter.Verify(w => w.SendCommandAsync(It.IsAny<string>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task SendCommandAsync_WhenCalledConcurrently_SerializesInputWriterCalls()
    {
        // Arrange
        var activeCalls = 0;
        var maxActiveCalls = 0;
        var inputWriter = new Mock<IServerInputWriter>();
        inputWriter
            .Setup(w => w.SendCommandAsync(It.IsAny<string>(), Process.GetCurrentProcess().Id))
            .Returns(async () =>
            {
                var current = Interlocked.Increment(ref activeCalls);
                maxActiveCalls = Math.Max(maxActiveCalls, current);
                await Task.Delay(50);
                Interlocked.Decrement(ref activeCalls);
                return (true, "sent");
            });

        var outputReader = new Mock<IInjectedHookOutputReader>();
        outputReader.SetupGet(r => r.Mode).Returns(ServerOutputModes.InjectedHook);
        outputReader.Setup(r => r.StartAsync(It.IsAny<int>())).ReturnsAsync(true);
        outputReader.Setup(r => r.StopAsync()).Returns(Task.CompletedTask);

        var mockConsoleLogSender = new Mock<ConsoleLogWebhookSender>(
            Mock.Of<HttpClient>(),
            Mock.Of<IConfiguration>(),
            Mock.Of<ILogger<ConsoleLogWebhookSender>>());

        var serverManager = new ServerManager(
            _mockConfiguration.Object,
            _mockLogger.Object,
            _playerTracker,
            _trackChangeTracker,
            _serverInfoTracker,
            _mockWebhookService.Object,
            mockConsoleLogSender.Object,
            inputWriter.Object,
            outputReader.Object);

        serverManager.AttachToExistingProcess(Process.GetCurrentProcess().Id);

        // Act
        await Task.WhenAll(
            serverManager.SendCommandAsync("/message one"),
            serverManager.SendCommandAsync("/message two"),
            serverManager.SendCommandAsync("/message three"));

        // Assert
        Assert.Equal(1, maxActiveCalls);
    }

    [Fact]
    public async Task InjectConsoleHookAsync_WhenProcessIdIsInvalid_ReturnsFailure()
    {
        // Act
        var result = await _serverManager.InjectConsoleHookAsync(-1);

        // Assert
        Assert.False(result.Success);
        Assert.Contains("Failed to validate target process", result.Message);
    }

    [Fact]
    public async Task InjectConsoleHookAsync_WhenProcessIsValid_DelegatesToInjectedHookOutputReader()
    {
        // Arrange
        var inputWriter = new Mock<IServerInputWriter>();
        var outputReader = new Mock<IInjectedHookOutputReader>();
        outputReader.SetupGet(r => r.Mode).Returns(ServerOutputModes.InjectedHook);
        outputReader.Setup(r => r.StartAsync(It.IsAny<int>())).ReturnsAsync(true);
        outputReader.Setup(r => r.StopAsync()).Returns(Task.CompletedTask);

        var injectedHookReader = new Mock<IInjectedHookOutputReader>();
        injectedHookReader.SetupGet(r => r.Mode).Returns(ServerOutputModes.InjectedHook);
        injectedHookReader
            .Setup(r => r.InjectAsync(Process.GetCurrentProcess().Id))
            .ReturnsAsync((true, "injected through reader"));

        var mockConsoleLogSender = new Mock<ConsoleLogWebhookSender>(
            Mock.Of<HttpClient>(),
            Mock.Of<IConfiguration>(),
            Mock.Of<ILogger<ConsoleLogWebhookSender>>());

        var serverManager = new ServerManager(
            _mockConfiguration.Object,
            _mockLogger.Object,
            _playerTracker,
            _trackChangeTracker,
            _serverInfoTracker,
            _mockWebhookService.Object,
            mockConsoleLogSender.Object,
            inputWriter.Object,
            injectedHookReader.Object);

        // Act
        var result = await serverManager.InjectConsoleHookAsync(Process.GetCurrentProcess().Id);

        // Assert
        Assert.True(result.Success);
        Assert.Equal("injected through reader", result.Message);
        injectedHookReader.Verify(r => r.InjectAsync(Process.GetCurrentProcess().Id), Times.Once);
    }

    [Fact]
    public void StartOutputMonitoring_EnablesInjectedHookOutput()
    {
        // Arrange
        _mockConfiguration.Setup(c => c["WreckfestServer:OutputMode"])
            .Returns(ServerOutputModes.InjectedHook);

        // Act
        InvokeStartOutputMonitoring(_serverManager);

        // Assert
        Assert.True(_serverManager.ProcessConsoleHookOutput);
    }

    [Fact]
    public void ProcessConsoleHookOutput_WhenEnabled_IsReported()
    {
        // Act
        _serverManager.ProcessConsoleHookOutput = true;

        // Assert
        Assert.True(_serverManager.ProcessConsoleHookOutput);
    }

    [Theory]
    [InlineData("Wreckfest 1.308438 64bit - Dedicated Server", "1.308438")]
    [InlineData("Wreckfest 1.2 64bit - Dedicated Server", "1.2")]
    [InlineData("Wreckfest 2.0.15.3 64bit - Dedicated Server", "2.0.15.3")]
    public void ParseServerBuild_ExtractsBuildFromWindowTitle(string title, string expected)
    {
        Assert.Equal(expected, ServerManager.ParseServerBuild(title));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Some Other Console Window")]
    [InlineData("Wreckfest 64bit - Dedicated Server")]
    public void ParseServerBuild_ReturnsNull_WhenTitleHasNoBuild(string? title)
    {
        Assert.Null(ServerManager.ParseServerBuild(title));
    }

    [Theory]
    [InlineData("^9* 17:57:22^0 ^8Procat: ^0test", "* 17:57:22 Procat: test")]
    [InlineData("^:Server message^0", "Server message")]
    [InlineData("   ^0   ", "")]
    public void NormalizeConsoleHookLine_StripsColorCodesAndTrims(string input, string expected)
    {
        // Act
        var result = ServerManager.NormalizeConsoleHookLine(input);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void SubscribeToOutput_DoesNotThrowException()
    {
        // Arrange
        Action<string> callback = (message) => { };

        // Act & Assert
        var exception = Record.Exception(() => _serverManager.ConsoleOutput += callback);
        Assert.Null(exception);
    }

    [Fact]
    public void UnsubscribeFromOutput_DoesNotThrowException()
    {
        // Arrange
        Action<string> callback = (message) => { };
        _serverManager.ConsoleOutput += callback;

        // Act & Assert
        var exception = Record.Exception(() => _serverManager.ConsoleOutput -= callback);
        Assert.Null(exception);
    }

    [Fact]
    public void OnConsoleOutputReceived_WreckfestChatCommandLine_RaisesChatCommand()
    {
        // Arrange
        (string PlayerName, bool IsBot, string Message)? received = null;
        _serverManager.ChatCommandReceived += (playerName, isBot, message) =>
            received = (playerName, isBot, message);

        // Act
        InvokeOnConsoleOutputReceived(_serverManager, "* 22:42:03 Procat: !vote mixed_1 6");

        // Assert
        WaitForChat(() => received != null);
        Assert.NotNull(received);
        Assert.Equal("Procat", received.Value.PlayerName);
        Assert.False(received.Value.IsBot);
        Assert.Equal("!vote mixed_1 6", received.Value.Message);
    }

    [Fact]
    public void OnConsoleOutputReceived_IndentedChatCommandLineWithPromptMarker_RaisesCleanChatCommand()
    {
        // Arrange
        (string PlayerName, bool IsBot, string Message)? received = null;
        _serverManager.ChatCommandReceived += (playerName, isBot, message) =>
            received = (playerName, isBot, message);

        // Act
        InvokeOnConsoleOutputReceived(_serverManager, " 19:24:18 Procat: !vote urban06 4                      >");

        // Assert
        WaitForChat(() => received != null);
        Assert.NotNull(received);
        Assert.Equal("Procat", received.Value.PlayerName);
        Assert.False(received.Value.IsBot);
        Assert.Equal("!vote urban06 4", received.Value.Message);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("/")]
    public void OnConsoleOutputReceived_ChatCommandLineWithSpinnerMarker_RaisesCleanChatCommand(string marker)
    {
        // Arrange
        (string PlayerName, bool IsBot, string Message)? received = null;
        _serverManager.ChatCommandReceived += (playerName, isBot, message) =>
            received = (playerName, isBot, message);

        // Act
        InvokeOnConsoleOutputReceived(_serverManager, $" 19:46:56 Procat: !search hill                         {marker}");

        // Assert
        WaitForChat(() => received != null);
        Assert.NotNull(received);
        Assert.Equal("Procat", received.Value.PlayerName);
        Assert.False(received.Value.IsBot);
        Assert.Equal("!search hill", received.Value.Message);
    }

    [Fact]
    public void OnConsoleOutputReceived_ObservedVoteLine_RaisesChatCommand()
    {
        // Arrange
        (string PlayerName, bool IsBot, string Message)? received = null;
        _serverManager.ChatCommandReceived += (playerName, isBot, message) =>
            received = (playerName, isBot, message);

        // Act
        InvokeOnConsoleOutputReceived(_serverManager, "* 12:38:08 Shachor: !vote bonebreaker_valley_main_circuit 6");

        // Assert
        WaitForChat(() => received != null);
        Assert.NotNull(received);
        Assert.Equal("Shachor", received.Value.PlayerName);
        Assert.False(received.Value.IsBot);
        Assert.Equal("!vote bonebreaker_valley_main_circuit 6", received.Value.Message);
    }

    [Fact]
    public void OnConsoleOutputReceived_DuplicateChatCommandLineWithinShortWindow_RaisesOnce()
    {
        // Arrange
        var receivedCount = 0;
        _serverManager.ChatCommandReceived += (_, _, _) => receivedCount++;

        // Act
        InvokeOnConsoleOutputReceived(_serverManager, "* 22:42:03 Procat: !search tvtp misc");
        InvokeOnConsoleOutputReceived(_serverManager, "* 22:42:03 Procat: !search tvtp misc");

        // Assert
        // Wait for the first to arrive, then allow time for a second that must not come.
        WaitForChat(() => receivedCount > 0);
        Thread.Sleep(150);
        Assert.Equal(1, receivedCount);
    }

    [Fact]
    public void OnConsoleOutputReceived_ControllerMessageContainingCommands_DoesNotRaiseChatCommand()
    {
        // Arrange
        var receivedCount = 0;
        _serverManager.ChatCommandReceived += (_, _, _) => receivedCount++;

        // Act
        InvokeOnConsoleOutputReceived(_serverManager,
            "* 18:00:12 Monday Night Wrecking EU - Development Server: Commands: !vote <trackId> <laps> - start vote; !yes - vote yes");

        // Assert
        Assert.Equal(0, receivedCount);
    }

    [Fact]
    public void OnConsoleOutputReceived_ChatMessageContainingCommandLater_DoesNotRaiseChatCommand()
    {
        // Arrange
        var receivedCount = 0;
        _serverManager.ChatCommandReceived += (_, _, _) => receivedCount++;

        // Act
        InvokeOnConsoleOutputReceived(_serverManager, "* 18:00:12 Procat: please type !help");

        // Assert
        Assert.Equal(0, receivedCount);
    }

    /// <summary>
    /// Chat commands are dispatched on ServerManager's own worker so the hook output
    /// reader is never blocked, which means delivery is asynchronous. Poll rather than
    /// sleeping a fixed amount.
    /// </summary>
    private static void WaitForChat(Func<bool> condition, int timeoutMs = 3000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
        {
            Thread.Sleep(5);
        }
    }

    private static void InvokeOnConsoleOutputReceived(ServerManager serverManager, string output)
    {
        var method = typeof(ServerManager).GetMethod(
            "OnConsoleOutputReceived",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        Assert.NotNull(method);
        method.Invoke(serverManager, [output]);
    }

    private static void InvokeStartOutputMonitoring(ServerManager serverManager)
    {
        var method = typeof(ServerManager).GetMethod(
            "StartOutputMonitoring",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        Assert.NotNull(method);
        method.Invoke(serverManager, null);
    }
}
