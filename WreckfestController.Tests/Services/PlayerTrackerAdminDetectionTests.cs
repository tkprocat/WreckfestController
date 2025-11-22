using Microsoft.Extensions.Logging;
using Moq;
using WreckfestController.Models;
using WreckfestController.Services;

namespace WreckfestController.Tests.Services;

public class PlayerTrackerAdminDetectionTests
{
    private readonly PlayerTracker _playerTracker;
    private readonly Mock<ILogger<PlayerTracker>> _mockLogger;
    private readonly Mock<WreckfestWebWebhookService> _mockWebhookService;

    public PlayerTrackerAdminDetectionTests()
    {
        _mockLogger = new Mock<ILogger<PlayerTracker>>();
        _mockWebhookService = new Mock<WreckfestWebWebhookService>(
            Mock.Of<ILogger<WreckfestWebWebhookService>>(),
            Mock.Of<Microsoft.Extensions.Configuration.IConfiguration>(),
            new HttpClient());

        _playerTracker = new PlayerTracker(_mockLogger.Object, _mockWebhookService.Object);
    }

    [Fact]
    public void ProcessListResponse_ShouldDetectAdmin_WhenPlayerHasOrangeASuffix()
    {
        // Arrange
        var listOutput = new[]
        {
            "Players: 1/24",
            " 11:   28 [2] Procat A                         | [A] 237 Lawn Mower           | ping: 4     | not ready"
        };

        // Act
        _playerTracker.ProcessListResponse(listOutput);
        var players = _playerTracker.GetOnlinePlayers();

        // Assert
        Assert.Single(players);
        Assert.Equal("Procat", players[0].Name);
        Assert.True(players[0].IsAdmin);
        Assert.False(players[0].IsBot);
    }

    [Fact]
    public void ProcessListResponse_ShouldNotDetectAdmin_WhenPlayerHasNoSuffix()
    {
        // Arrange
        var listOutput = new[]
        {
            "Players: 1/24",
            "  1:   38 [2] *OysterBarron                    | [A] 237 Lawn Mower           | ping: 0     | ready"
        };

        // Act
        _playerTracker.ProcessListResponse(listOutput);
        var players = _playerTracker.GetOnlinePlayers();

        // Assert
        Assert.Single(players);
        Assert.Equal("OysterBarron", players[0].Name);
        Assert.False(players[0].IsAdmin);
        Assert.True(players[0].IsBot);  // Has * prefix
    }

    [Fact]
    public void ProcessListResponse_ShouldHandleMultiplePlayers_WithMixedAdminStatus()
    {
        // Arrange
        var listOutput = new[]
        {
            "Players: 3/24",
            "  1:   38 [2] *OysterBarron                    | [A] 237 Lawn Mower           | ping: 0     | ready",
            "  2:   45 [2] *evilt0m                         | [B] 175 Lawn Mower           | ping: 0     | ready",
            " 11:   28 [2] Procat A                         | [A] 237 Lawn Mower           | ping: 4     | not ready"
        };

        // Act
        _playerTracker.ProcessListResponse(listOutput);
        var players = _playerTracker.GetOnlinePlayers();

        // Assert
        Assert.Equal(3, players.Count);

        // First player - bot, no admin
        Assert.Equal("OysterBarron", players[0].Name);
        Assert.True(players[0].IsBot);
        Assert.False(players[0].IsAdmin);

        // Second player - bot, no admin
        Assert.Equal("evilt0m", players[1].Name);
        Assert.True(players[1].IsBot);
        Assert.False(players[1].IsAdmin);

        // Third player - admin, not bot
        Assert.Equal("Procat", players[2].Name);
        Assert.False(players[2].IsBot);
        Assert.True(players[2].IsAdmin);
    }

    [Fact]
    public void ProcessListResponse_ShouldStripANSIColorCodes_FromPlayerNames()
    {
        // Arrange - simulating orange A with ANSI codes
        var listOutput = new[]
        {
            "Players: 1/24",
            " 11:   28 [2] Procat \x1b[38;2;255;165;0mA\x1b[0m                         | [A] 237 Lawn Mower           | ping: 4     | not ready"
        };

        // Act
        _playerTracker.ProcessListResponse(listOutput);
        var players = _playerTracker.GetOnlinePlayers();

        // Assert
        Assert.Single(players);
        Assert.Equal("Procat", players[0].Name);
        Assert.True(players[0].IsAdmin);
    }

    [Fact]
    public void ProcessListResponse_ShouldUpdateExistingPlayer_AdminStatus()
    {
        // Arrange
        var initialList = new[]
        {
            "Players: 1/24",
            "  1:   28 [2] RegularPlayer                    | [A] 237 Lawn Mower           | ping: 4     | ready"
        };

        var updatedList = new[]
        {
            "Players: 1/24",
            "  1:   28 [2] RegularPlayer A                  | [A] 237 Lawn Mower           | ping: 4     | ready"
        };

        // Act - First without admin
        _playerTracker.ProcessListResponse(initialList);
        var playersBeforeAdmin = _playerTracker.GetOnlinePlayers();

        // Act - Then with admin
        _playerTracker.ProcessListResponse(updatedList);
        var playersAfterAdmin = _playerTracker.GetOnlinePlayers();

        // Assert
        Assert.Single(playersBeforeAdmin);
        Assert.Equal("RegularPlayer", playersBeforeAdmin[0].Name);
        Assert.False(playersBeforeAdmin[0].IsAdmin);

        Assert.Single(playersAfterAdmin);
        Assert.Equal("RegularPlayer", playersAfterAdmin[0].Name);
        Assert.True(playersAfterAdmin[0].IsAdmin);
    }

    [Fact]
    public void ProcessListResponse_ShouldHandleWrappedLines()
    {
        // Arrange - simulating wrapped output
        var listOutput = new[]
        {
            "Players: 1/24",
            "  4:   44 [2] *Jacklost Yedmore                >| [C] 102 Lawn Mower           | ping: 0     | ready"
        };

        // Act
        _playerTracker.ProcessListResponse(listOutput);
        var players = _playerTracker.GetOnlinePlayers();

        // Assert
        Assert.Single(players);
        Assert.Equal("Jacklost Yedmore", players[0].Name);
        Assert.True(players[0].IsBot);
        Assert.False(players[0].IsAdmin);
    }

    [Fact]
    public void HasPlayersOnline_ShouldReturnTrue_WhenRealPlayersOnline()
    {
        // Arrange
        var listOutput = new[]
        {
            "Players: 2/24",
            "  1:   38 [2] *OysterBarron                    | [A] 237 Lawn Mower           | ping: 0     | ready",
            " 11:   28 [2] Procat A                         | [A] 237 Lawn Mower           | ping: 4     | not ready"
        };

        // Act
        _playerTracker.ProcessListResponse(listOutput);
        var hasPlayers = _playerTracker.HasPlayersOnline();

        // Assert
        Assert.True(hasPlayers);  // Procat is a real player
    }

    [Fact]
    public void HasPlayersOnline_ShouldReturnFalse_WhenOnlyBotsOnline()
    {
        // Arrange
        var listOutput = new[]
        {
            "Players: 2/24",
            "  1:   38 [2] *OysterBarron                    | [A] 237 Lawn Mower           | ping: 0     | ready",
            "  2:   45 [2] *evilt0m                         | [B] 175 Lawn Mower           | ping: 0     | ready"
        };

        // Act
        _playerTracker.ProcessListResponse(listOutput);
        var hasPlayers = _playerTracker.HasPlayersOnline();

        // Assert
        Assert.False(hasPlayers);  // Only bots
    }

    [Fact]
    public void GetPlayerCount_ShouldCountOnlyRealPlayers()
    {
        // Arrange
        var listOutput = new[]
        {
            "Players: 11/24",
            "  1:   38 [2] *OysterBarron                    | [A] 237 Lawn Mower           | ping: 0     | ready",
            "  2:   45 [2] *evilt0m                         | [B] 175 Lawn Mower           | ping: 0     | ready",
            "  3:   41 [2] *459                             | [A] 237 Lawn Mower           | ping: 0     | ready",
            "  4:   44 [2] *Jacklost Yedmore                | [C] 102 Lawn Mower           | ping: 0     | ready",
            "  5:   41 [2] *WalkingTreeFrog                 | [B] 175 Lawn Mower           | ping: 0     | ready",
            "  6:   44 [2] *helldriver75                    | [B] 175 Lawn Mower           | ping: 0     | ready",
            "  7:   41 [2] *JadedRabbit6911                 | [A] 237 Lawn Mower           | ping: 0     | ready",
            "  8:   40 [2] *Tyalmath                        | [C] 102 Lawn Mower           | ping: 0     | ready",
            "  9:   45 [2] *nikelf1                         | [C] 102 Lawn Mower           | ping: 0     | ready",
            " 10:   41 [2] *Jellivaara                      | [B] 175 Lawn Mower           | ping: 0     | ready",
            " 11:   28 [2] Procat A                         | [A] 237 Lawn Mower           | ping: 4     | not ready"
        };

        // Act
        _playerTracker.ProcessListResponse(listOutput);
        var (onlinePlayers, totalPlayers) = _playerTracker.GetPlayerCount();

        // Assert
        Assert.Equal(1, onlinePlayers);  // Only Procat is a real player
        Assert.Equal(1, totalPlayers);   // Total real players ever seen
    }
}
