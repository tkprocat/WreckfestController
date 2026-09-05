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
    public async Task StopServerViaCommandAsync_WhenExitHasNoHookResponse_WaitsForProcessExit()
    {
        using var serverProcess = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -Command Start-Sleep -Seconds 30",
            UseShellExecute = false,
            CreateNoWindow = true
        });
        Assert.NotNull(serverProcess);

        var inputWriter = new Mock<IServerInputWriter>();
        inputWriter
            .Setup(w => w.SendCommandAsync("exit", serverProcess.Id))
            .ReturnsAsync(() =>
            {
                // Model exit being dispatched, then the game closing before the hook
                // can write its post-dispatch OK response.
                serverProcess.Kill();
                return (false, InjectedHookInputWriter.NoResponseMessage);
            });

        var outputReader = new Mock<IInjectedHookOutputReader>();
        outputReader.SetupGet(r => r.Mode).Returns(ServerOutputModes.InjectedHook);
        outputReader.Setup(r => r.StopAsync()).Returns(Task.CompletedTask);

        var consoleLogSender = new Mock<ConsoleLogWebhookSender>(
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
            consoleLogSender.Object,
            inputWriter.Object,
            outputReader.Object);
        serverManager.AttachToExistingProcess(serverProcess.Id);

        try
        {
            var result = await serverManager.StopServerViaCommandAsync();

            Assert.True(result.Success);
            Assert.Contains("gracefully", result.Message);
            inputWriter.Verify(w => w.SendCommandAsync("exit", serverProcess.Id), Times.Once);
        }
        finally
        {
            if (!serverProcess.HasExited)
            {
                serverProcess.Kill();
                await serverProcess.WaitForExitAsync();
            }
        }
    }

    // The hook's response timeout is shorter than a slow shutdown, so "exit" can be
    // delivered and still time out waiting for the acknowledgement while the server is
    // genuinely on its way down. That must not short-circuit to a force stop.
    [Fact]
    public async Task StopServerViaCommandAsync_WhenExitAcknowledgementTimesOut_WaitsForSlowShutdown()
    {
        using var serverProcess = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -Command Start-Sleep -Seconds 30",
            UseShellExecute = false,
            CreateNoWindow = true
        });
        Assert.NotNull(serverProcess);

        var shutdownDelay = TimeSpan.FromSeconds(2);
        var inputWriter = new Mock<IServerInputWriter>();
        inputWriter
            .Setup(w => w.SendCommandAsync("exit", serverProcess.Id))
            .ReturnsAsync(() =>
            {
                // Delivered, but the game takes its time going down and the hook's
                // response timeout expires first.
                _ = Task.Run(async () =>
                {
                    await Task.Delay(shutdownDelay);
                    if (!serverProcess.HasExited)
                        serverProcess.Kill();
                });
                return (false, InjectedHookInputWriter.DispatchedWithoutResponseMessage);
            });

        var outputReader = new Mock<IInjectedHookOutputReader>();
        outputReader.SetupGet(r => r.Mode).Returns(ServerOutputModes.InjectedHook);
        outputReader.Setup(r => r.StopAsync()).Returns(Task.CompletedTask);

        var consoleLogSender = new Mock<ConsoleLogWebhookSender>(
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
            consoleLogSender.Object,
            inputWriter.Object,
            outputReader.Object);
        serverManager.AttachToExistingProcess(serverProcess.Id);

        try
        {
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            var result = await serverManager.StopServerViaCommandAsync();
            elapsed.Stop();

            // "gracefully" is the proof that force stop was never reached: the fallback
            // returns StopServerAsync's own message instead.
            Assert.True(result.Success);
            Assert.Contains("gracefully", result.Message);
            Assert.True(
                elapsed.Elapsed >= shutdownDelay,
                $"Returned after {elapsed.Elapsed} without waiting out the {shutdownDelay} shutdown.");
        }
        finally
        {
            if (!serverProcess.HasExited)
            {
                serverProcess.Kill();
                await serverProcess.WaitForExitAsync();
            }
        }
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
    public async Task InjectConsoleHookAsync_WhenBuildMatches_DelegatesToInjectedHookOutputReader()
    {
        // Arrange
        _mockConfiguration.Setup(c => c["WreckfestServer:SupportedBuild"])
            .Returns("1.308438");
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

        var serverManager = new TestServerManager(
            _mockConfiguration.Object,
            _mockLogger.Object,
            _playerTracker,
            _trackChangeTracker,
            _serverInfoTracker,
            _mockWebhookService.Object,
            mockConsoleLogSender.Object,
            inputWriter.Object,
            injectedHookReader.Object,
            "1.308438");
        serverManager.AttachToExistingProcess(Process.GetCurrentProcess().Id);

        // Act
        var result = await serverManager.InjectConsoleHookAsync(Process.GetCurrentProcess().Id);

        // Assert
        Assert.True(result.Success);
        Assert.Equal("injected through reader", result.Message);
        injectedHookReader.Verify(r => r.InjectAsync(Process.GetCurrentProcess().Id), Times.Once);
    }

    [Fact]
    public async Task InjectConsoleHookAsync_WhenBuildMismatches_RefusesWithoutInjecting()
    {
        _mockConfiguration.Setup(c => c["WreckfestServer:SupportedBuild"])
            .Returns("1.308438");
        var injectedHookReader = new Mock<IInjectedHookOutputReader>();
        var serverManager = CreateTestServerManager(injectedHookReader.Object, "1.999999");
        serverManager.AttachToExistingProcess(Process.GetCurrentProcess().Id);

        var result = await serverManager.InjectConsoleHookAsync(Process.GetCurrentProcess().Id);

        Assert.False(result.Success);
        Assert.Contains("1.999999", result.Message);
        Assert.Contains("1.308438", result.Message);
        injectedHookReader.Verify(r => r.InjectAsync(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task InjectConsoleHookAsync_WhenBuildIsUnreadable_RefusesWithoutInjecting()
    {
        _mockConfiguration.Setup(c => c["WreckfestServer:SupportedBuild"])
            .Returns("1.308438");
        var injectedHookReader = new Mock<IInjectedHookOutputReader>();
        var serverManager = CreateTestServerManager(injectedHookReader.Object, null);
        serverManager.AttachToExistingProcess(Process.GetCurrentProcess().Id);

        var result = await serverManager.InjectConsoleHookAsync(Process.GetCurrentProcess().Id);

        Assert.False(result.Success);
        Assert.Contains("<unreadable>", result.Message);
        Assert.Contains("1.308438", result.Message);
        injectedHookReader.Verify(r => r.InjectAsync(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task InjectConsoleHookAsync_WhenSupportedBuildIsNotConfigured_RefusesWithoutInjecting()
    {
        var injectedHookReader = new Mock<IInjectedHookOutputReader>();
        var serverManager = CreateTestServerManager(injectedHookReader.Object, "1.308438");
        serverManager.AttachToExistingProcess(Process.GetCurrentProcess().Id);

        var result = await serverManager.InjectConsoleHookAsync(Process.GetCurrentProcess().Id);

        Assert.False(result.Success);
        Assert.Contains("1.308438", result.Message);
        Assert.Contains("<not configured>", result.Message);
        injectedHookReader.Verify(r => r.InjectAsync(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task InjectConsoleHookAsync_WhenNoProcessIsAttached_RefusesWithoutInjecting()
    {
        var injectedHookReader = new Mock<IInjectedHookOutputReader>();
        var serverManager = CreateTestServerManager(injectedHookReader.Object, "1.308438");

        var result = await serverManager.InjectConsoleHookAsync(Process.GetCurrentProcess().Id);

        Assert.False(result.Success);
        Assert.Contains("Injection refused", result.Message);
        Assert.Contains("no process is attached", result.Message);
        injectedHookReader.Verify(r => r.InjectAsync(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task InjectConsoleHookAsync_WhenRequestedProcessIsNotAttached_RefusesWithoutInjecting()
    {
        var injectedHookReader = new Mock<IInjectedHookOutputReader>();
        var serverManager = CreateTestServerManager(injectedHookReader.Object, "1.308438");
        var attachedProcessId = Process.GetCurrentProcess().Id;
        Assert.True(serverManager.AttachToExistingProcess(attachedProcessId).Success);

        using var requestedProcess = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c ping 127.0.0.1 -n 10 > nul",
            CreateNoWindow = true,
            UseShellExecute = false
        })!;

        try
        {
            var result = await serverManager.InjectConsoleHookAsync(requestedProcess.Id);

            Assert.False(result.Success);
            Assert.Contains($"attached process is {attachedProcessId}", result.Message);
            Assert.Contains($"requested process is {requestedProcess.Id}", result.Message);
            injectedHookReader.Verify(r => r.InjectAsync(It.IsAny<int>()), Times.Never);
        }
        finally
        {
            if (!requestedProcess.HasExited)
            {
                requestedProcess.Kill(true);
            }
        }
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
    public void OnInjectedHookOutputReceived_StructuredChatRecord_RaisesChatCommand()
    {
        // Arrange
        (string PlayerName, bool IsBot, string Message)? received = null;
        _serverManager.ChatCommandReceived += (playerName, isBot, message) =>
            received = (playerName, isBot, message);

        // Act
        InvokeOnInjectedHookOutputReceived(
            _serverManager,
            BuildChatRecord("3", "0", "Procat", "!vote mixed_1 6"));

        // Assert
        WaitForChat(() => received != null);
        Assert.NotNull(received);
        Assert.Equal("Procat", received!.Value.PlayerName);
        Assert.False(received.Value.IsBot);
        Assert.Equal("!vote mixed_1 6", received.Value.Message);
    }

    [Fact]
    public void OnInjectedHookOutputReceived_BotChatRecord_RaisesChatCommandWithBotFlag()
    {
        // Arrange
        (string PlayerName, bool IsBot, string Message)? received = null;
        _serverManager.ChatCommandReceived += (playerName, isBot, message) =>
            received = (playerName, isBot, message);

        // Act
        InvokeOnInjectedHookOutputReceived(
            _serverManager,
            BuildChatRecord("8", "1", "eRacer", "!yes"));

        // Assert
        WaitForChat(() => received != null);
        Assert.NotNull(received);
        Assert.True(received!.Value.IsBot);
        Assert.Equal("eRacer", received.Value.PlayerName);
    }

    /// <summary>
    /// The reason issue #7 exists: the console regex matches the sender with [^:]+,
    /// so this player's votes are silently dropped. The record keeps the name whole.
    /// </summary>
    [Fact]
    public void OnInjectedHookOutputReceived_SenderNameContainingColon_RaisesChatCommandTheTextPathDrops()
    {
        // Arrange
        var receivedCount = 0;
        (string PlayerName, bool IsBot, string Message)? received = null;
        _serverManager.ChatCommandReceived += (playerName, isBot, message) =>
        {
            receivedCount++;
            received = (playerName, isBot, message);
        };

        // Act - the console line for this player matches nothing at all.
        InvokeOnConsoleOutputReceived(_serverManager, "* 22:42:03 Foo:Bar: !vote mixed_1 6");
        Thread.Sleep(150);
        Assert.Equal(0, receivedCount);

        InvokeOnInjectedHookOutputReceived(
            _serverManager,
            BuildChatRecord("10", "0", "Foo:Bar", "!vote mixed_1 6"));

        // Assert
        WaitForChat(() => received != null);
        Assert.NotNull(received);
        Assert.Equal("Foo:Bar", received!.Value.PlayerName);
        Assert.Equal("!vote mixed_1 6", received.Value.Message);
    }

    [Fact]
    public void OnInjectedHookOutputReceived_MaximumLengthMessage_RaisesChatCommandIntact()
    {
        // Arrange - 127 characters is the game's cap; see docs/finding-rvas.md.
        var message = "!say " + new string('x', 122);
        Assert.Equal(127, message.Length);

        (string PlayerName, bool IsBot, string Message)? received = null;
        _serverManager.ChatCommandReceived += (playerName, isBot, msg) =>
            received = (playerName, isBot, msg);

        // Act
        InvokeOnInjectedHookOutputReceived(
            _serverManager,
            BuildChatRecord("1", "0", "Procat", message));

        // Assert
        WaitForChat(() => received != null);
        Assert.NotNull(received);
        Assert.Equal(message, received!.Value.Message);
    }

    // Console text is never parsed for chat, with or without a record having been
    // seen. Both travel the same hook pipe, so a text path could not cover the hook
    // being down; it only ever covered record extraction failing, which is exactly
    // when a silently dropped command costs most. The line is reported instead.
    [Fact]
    public void OnInjectedHookOutputReceived_ChatLikeConsoleText_RaisesNoChatCommand()
    {
        // Arrange
        (string PlayerName, bool IsBot, string Message)? received = null;
        _serverManager.ChatCommandReceived += (playerName, isBot, message) =>
            received = (playerName, isBot, message);
        _serverManager.ProcessConsoleHookOutput = true;

        // Act
        InvokeOnInjectedHookOutputReceived(_serverManager, "* 22:42:03 Procat: !vote mixed_1 6");

        // Assert
        Thread.Sleep(150);
        Assert.Null(received);
    }

    [Fact]
    public void OnInjectedHookOutputReceived_ConsoleTextAfterARecord_RaisesNoSecondCommand()
    {
        // Arrange
        var receivedCount = 0;
        _serverManager.ChatCommandReceived += (_, _, _) => receivedCount++;

        // Act
        InvokeOnInjectedHookOutputReceived(
            _serverManager,
            BuildChatRecord("1", "0", "Procat", "!vote mixed_1 6"));
        WaitForChat(() => receivedCount > 0);

        // The console line the hook prints for that same message arrives next, and
        // must not be handled a second time. Nor must any later one.
        InvokeOnConsoleOutputReceived(_serverManager, "* 22:42:03 Procat: !vote mixed_1 6");
        InvokeOnConsoleOutputReceived(_serverManager, "* 22:43:11 Procat: !yes");
        Thread.Sleep(150);

        // Assert
        Assert.Equal(1, receivedCount);
    }

    [Theory]
    [InlineData("markerOnly")]
    [InlineData("noTerminator")]
    [InlineData("truncatedMidField")]
    [InlineData("missingMessageField")]
    [InlineData("unparsableSlot")]
    [InlineData("consoleLineWithoutNameMarker")]
    [InlineData("consoleLineMissingTheMessage")]
    [InlineData("emptyName")]
    public void OnInjectedHookOutputReceived_MalformedRecord_IsDroppedWithoutRaisingAnything(string scenario)
    {
        // Arrange
        var receivedCount = 0;
        var consoleLines = 0;
        _serverManager.ChatCommandReceived += (_, _, _) => receivedCount++;
        _serverManager.ConsoleOutput += _ => consoleLines++;
        _serverManager.ProcessConsoleHookOutput = true;

        var separator = HookChatRecord.FieldSeparator;
        var wellFormed = BuildChatRecord("1", "0", "Procat", "!vote mixed_1 6");
        var malformed = scenario switch
        {
            "markerOnly" => HookChatRecord.Marker,
            "noTerminator" => wellFormed[..^1],
            "truncatedMidField" => wellFormed[..(wellFormed.Length / 2)],
            "missingMessageField" =>
                $"{HookChatRecord.Marker}{separator}1{separator}0{separator}Procat{HookChatRecord.RecordEnd}",
            "unparsableSlot" => BuildChatRecord("notanumber", "0", "Procat", "!yes"),
            "consoleLineWithoutNameMarker" =>
                $"{HookChatRecord.Marker}{separator}1{separator}* 21:37:50 Procat: !yes{separator}!yes{HookChatRecord.RecordEnd}",
            "consoleLineMissingTheMessage" =>
                $"{HookChatRecord.Marker}{separator}1{separator}^8Procat: ^0something else{separator}!yes{HookChatRecord.RecordEnd}",
            "emptyName" => BuildChatRecord("1", "0", "   ", "!yes"),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };

        // Act
        var exception = Xunit.Record.Exception(
            () => InvokeOnInjectedHookOutputReceived(_serverManager, malformed));

        // Assert
        Assert.Null(exception);
        Thread.Sleep(100);
        Assert.Equal(0, receivedCount);

        // A record, even a broken one, is ours: it must not leak into console output.
        Assert.Equal(0, consoleLines);

        // State is intact - a well-formed record afterwards still raises its command.
        InvokeOnInjectedHookOutputReceived(
            _serverManager, BuildChatRecord("1", "0", "Procat", "!vote mixed_1 6"));
        WaitForChat(() => receivedCount > 0);
        Assert.Equal(1, receivedCount);
    }

    [Fact]
    public void OnInjectedHookOutputReceived_OrdinaryChatRecord_RaisesNothing()
    {
        // Arrange
        var receivedCount = 0;
        _serverManager.ChatCommandReceived += (_, _, _) => receivedCount++;

        // Act - not a ! command, but it still proves the hook is emitting records.
        InvokeOnInjectedHookOutputReceived(
            _serverManager,
            BuildChatRecord("1", "0", "Procat", "hello world"));

        // Assert
        Thread.Sleep(100);
        Assert.Equal(0, receivedCount);
    }

    [Fact]
    public void OnInjectedHookOutputReceived_StructuredRecord_IsNotFannedOutAsConsoleText()
    {
        // Arrange
        var consoleLines = new List<string>();
        _serverManager.ConsoleOutput += line => consoleLines.Add(line);
        _serverManager.ProcessConsoleHookOutput = true;

        // Act
        InvokeOnInjectedHookOutputReceived(
            _serverManager,
            BuildChatRecord("1", "0", "Procat", "!yes"));

        // Assert
        Assert.Empty(consoleLines);
    }

    /// <summary>
    /// Builds a record the way the hook does. The hook reports only what it observed
    /// - the ring entry, the line the game formatted as "^8&lt;name&gt;: ^0&lt;message&gt;",
    /// and the raw message - so the sender and bot flag are synthesised into the
    /// console line here and derived back out by HookChatRecord.
    /// </summary>
    private static string BuildChatRecord(string ringIndex, string isBot, string name, string message)
    {
        var separator = HookChatRecord.FieldSeparator;
        var displayName = isBot == "1" ? "*" + name : name;
        var consoleLine = $"^9* 21:37:50^0 ^8{displayName}: ^0{message}";
        return $"{HookChatRecord.Marker}{separator}{ringIndex}{separator}{consoleLine}{separator}{message}{HookChatRecord.RecordEnd}";
    }

    private static void InvokeOnInjectedHookOutputReceived(ServerManager serverManager, string output)
    {
        var method = typeof(ServerManager).GetMethod(
            "OnInjectedHookOutputReceived",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        Assert.NotNull(method);
        method.Invoke(serverManager, [output]);
    }

    private TestServerManager CreateTestServerManager(IInjectedHookOutputReader injectedHookReader, string? build)
    {
        var consoleLogSender = new Mock<ConsoleLogWebhookSender>(
            Mock.Of<HttpClient>(),
            Mock.Of<IConfiguration>(),
            Mock.Of<ILogger<ConsoleLogWebhookSender>>());

        return new TestServerManager(
            _mockConfiguration.Object,
            _mockLogger.Object,
            _playerTracker,
            _trackChangeTracker,
            _serverInfoTracker,
            _mockWebhookService.Object,
            consoleLogSender.Object,
            Mock.Of<IServerInputWriter>(),
            injectedHookReader,
            build);
    }

    private sealed class TestServerManager : ServerManager
    {
        private readonly string? _build;

        public TestServerManager(
            IConfiguration configuration,
            ILogger<ServerManager> logger,
            PlayerTracker playerTracker,
            TrackChangeTracker trackChangeTracker,
            ServerInfoTracker serverInfoTracker,
            WreckfestWebWebhookService webhookService,
            ConsoleLogWebhookSender consoleLogSender,
            IServerInputWriter serverInputWriter,
            IInjectedHookOutputReader injectedHookOutputReader,
            string? build)
            : base(
                configuration,
                logger,
                playerTracker,
                trackChangeTracker,
                serverInfoTracker,
                webhookService,
                consoleLogSender,
                serverInputWriter,
                injectedHookOutputReader)
        {
            _build = build;
        }

        protected override string? GetServerBuild(Process process) => _build;
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

    // Nothing selected and nothing attached must not qualify. Comparing the two
    // nullable ints directly would make that case read as a match, which enabled
    // the INJECT button in the application's initial state.
    [Fact]
    public void CanInjectInto_WhenNothingSelectedAndNothingAttached_ReturnsFalse()
    {
        Assert.Null(_serverManager.AttachedProcessId);

        Assert.False(_serverManager.CanInjectInto(null));
    }

    [Fact]
    public void CanInjectInto_WhenNothingAttached_ReturnsFalseForAnyProcess()
    {
        Assert.False(_serverManager.CanInjectInto(Process.GetCurrentProcess().Id));
    }

    [Fact]
    public void CanInjectInto_WhenNothingSelectedButProcessAttached_ReturnsFalse()
    {
        _serverManager.AttachToExistingProcess(Process.GetCurrentProcess().Id);

        Assert.False(_serverManager.CanInjectInto(null));
    }

    [Fact]
    public void CanInjectInto_WhenSelectionMatchesAttachedProcess_ReturnsTrue()
    {
        var pid = Process.GetCurrentProcess().Id;
        _serverManager.AttachToExistingProcess(pid);

        Assert.True(_serverManager.CanInjectInto(pid));
    }

    [Fact]
    public void CanInjectInto_WhenSelectionDiffersFromAttachedProcess_ReturnsFalse()
    {
        var pid = Process.GetCurrentProcess().Id;
        _serverManager.AttachToExistingProcess(pid);

        Assert.False(_serverManager.CanInjectInto(pid + 1));
    }
}
