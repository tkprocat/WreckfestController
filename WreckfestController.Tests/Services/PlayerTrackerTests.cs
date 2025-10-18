using Microsoft.Extensions.Logging;
using Moq;
using WreckfestController.Models;
using WreckfestController.Services;
using Xunit;

namespace WreckfestController.Tests.Services;

public class PlayerTrackerTests
{
    private readonly Mock<ILogger<PlayerTracker>> _mockLogger;
    private readonly PlayerTracker _playerTracker;

    public PlayerTrackerTests()
    {
        _mockLogger = new Mock<ILogger<PlayerTracker>>();
        _playerTracker = new PlayerTracker(_mockLogger.Object);
    }

    [Fact]
    public void ProcessLogLine_BotJoinEvent_AddsBot()
    {
        // Arrange
        var logLine = "16:53:14 - *eRacer has joined.";

        // Act
        _playerTracker.ProcessLogLine(logLine);

        // Assert
        var players = _playerTracker.GetOnlinePlayers();
        Assert.Single(players);
        Assert.Equal("eRacer", players[0].Name);
        Assert.True(players[0].IsBot);
        Assert.True(players[0].IsOnline);
    }

    [Fact]
    public void ProcessLogLine_HumanJoinEvent_AddsHuman()
    {
        // Arrange
        var logLine = "16:53:14 - Player123 has joined.";

        // Act
        _playerTracker.ProcessLogLine(logLine);

        // Assert
        var players = _playerTracker.GetOnlinePlayers();
        Assert.Single(players);
        Assert.Equal("Player123", players[0].Name);
        Assert.False(players[0].IsBot);
        Assert.True(players[0].IsOnline);
    }

    [Fact]
    public void ProcessLogLine_BotQuitEvent_MarksOffline()
    {
        // Arrange
        _playerTracker.ProcessLogLine("16:53:14 - *eRacer has joined.");
        var logLine = "16:55:00 - *eRacer has quit (ping timeout).";

        // Act
        _playerTracker.ProcessLogLine(logLine);

        // Assert
        var onlinePlayers = _playerTracker.GetOnlinePlayers();
        Assert.Empty(onlinePlayers);

        var allPlayers = _playerTracker.GetAllPlayers();
        Assert.Single(allPlayers);
        Assert.False(allPlayers[0].IsOnline);
    }

    [Fact]
    public void ProcessLogLine_HumanQuitEvent_MarksOffline()
    {
        // Arrange
        _playerTracker.ProcessLogLine("16:53:14 - Player123 has joined.");
        var logLine = "16:55:00 - Player123 has quit.";

        // Act
        _playerTracker.ProcessLogLine(logLine);

        // Assert
        var onlinePlayers = _playerTracker.GetOnlinePlayers();
        Assert.Empty(onlinePlayers);

        var allPlayers = _playerTracker.GetAllPlayers();
        Assert.Single(allPlayers);
        Assert.False(allPlayers[0].IsOnline);
    }

    [Fact]
    public void ProcessLogLine_PlayerRejoins_UpdatesStatus()
    {
        // Arrange
        _playerTracker.ProcessLogLine("16:53:14 - Player123 has joined.");
        _playerTracker.ProcessLogLine("16:55:00 - Player123 has quit.");

        // Act
        _playerTracker.ProcessLogLine("17:00:00 - Player123 has joined.");

        // Assert
        var onlinePlayers = _playerTracker.GetOnlinePlayers();
        Assert.Single(onlinePlayers);
        Assert.True(onlinePlayers[0].IsOnline);
    }

    [Fact]
    public void ProcessLogLine_EmptyLine_DoesNothing()
    {
        // Act
        _playerTracker.ProcessLogLine("");
        _playerTracker.ProcessLogLine("   ");

        // Assert
        var players = _playerTracker.GetAllPlayers();
        Assert.Empty(players);
    }

    [Fact]
    public void ProcessLogLine_UnrelatedLine_DoesNothing()
    {
        // Act
        _playerTracker.ProcessLogLine("Some random log line");
        _playerTracker.ProcessLogLine("16:53:14 - Server started");

        // Assert
        var players = _playerTracker.GetAllPlayers();
        Assert.Empty(players);
    }

    [Fact]
    public void ProcessListResponse_WithBots_AddsBotsCorrectly()
    {
        // Arrange
        var lines = new[]
        {
            "Players: 2/24",
            "0: *eRacer",
            "1: Player123"
        };

        // Act
        _playerTracker.ProcessListResponse(lines);

        // Assert
        var players = _playerTracker.GetOnlinePlayers();
        Assert.Equal(2, players.Count);

        var bot = players.FirstOrDefault(p => p.Name == "eRacer");
        Assert.NotNull(bot);
        Assert.True(bot.IsBot);
        Assert.Equal(0, bot.Slot);

        var human = players.FirstOrDefault(p => p.Name == "Player123");
        Assert.NotNull(human);
        Assert.False(human.IsBot);
        Assert.Equal(1, human.Slot);
    }

    [Fact]
    public void ProcessListResponse_EmptyList_MarksAllOffline()
    {
        // Arrange
        var lines = new[]
        {
            "Players: 2/24",
            "0: Player1",
            "1: Player2"
        };
        _playerTracker.ProcessListResponse(lines);

        // Act - Process empty list
        var emptyLines = new[] { "Players: 0/24" };
        _playerTracker.ProcessListResponse(emptyLines);

        // Assert
        var onlinePlayers = _playerTracker.GetOnlinePlayers();
        Assert.Empty(onlinePlayers);

        var allPlayers = _playerTracker.GetAllPlayers();
        Assert.Equal(2, allPlayers.Count);
        Assert.All(allPlayers, p => Assert.False(p.IsOnline));
    }

    [Fact]
    public void ProcessListResponse_UpdatesExistingPlayers()
    {
        // Arrange
        _playerTracker.ProcessLogLine("16:53:14 - Player123 has joined.");
        var lines = new[]
        {
            "Players: 1/24",
            "0: Player123"
        };

        // Act
        _playerTracker.ProcessListResponse(lines);

        // Assert
        var players = _playerTracker.GetOnlinePlayers();
        Assert.Single(players);
        Assert.Equal(0, players[0].Slot);
        Assert.True(players[0].IsOnline);
    }

    [Fact]
    public void GetPlayerCount_ReturnsCorrectCounts()
    {
        // Arrange
        _playerTracker.ProcessLogLine("16:53:14 - Player1 has joined.");
        _playerTracker.ProcessLogLine("16:53:15 - Player2 has joined.");
        _playerTracker.ProcessLogLine("16:53:16 - Player3 has joined.");
        _playerTracker.ProcessLogLine("16:55:00 - Player3 has quit.");

        // Act
        var (online, total) = _playerTracker.GetPlayerCount();

        // Assert
        Assert.Equal(2, online);
        Assert.Equal(3, total);
    }

    [Fact]
    public void GetOnlinePlayers_OrdersBySlotThenJoinTime()
    {
        // Arrange
        var lines = new[]
        {
            "Players: 3/24",
            "2: Player3",
            "0: Player1",
            "1: Player2"
        };

        // Act
        _playerTracker.ProcessListResponse(lines);
        var players = _playerTracker.GetOnlinePlayers();

        // Assert
        Assert.Equal(3, players.Count);
        Assert.Equal("Player1", players[0].Name);
        Assert.Equal("Player2", players[1].Name);
        Assert.Equal("Player3", players[2].Name);
    }

    [Fact]
    public void GetAllPlayers_IncludesOfflinePlayers()
    {
        // Arrange
        _playerTracker.ProcessLogLine("16:53:14 - Player1 has joined.");
        _playerTracker.ProcessLogLine("16:53:15 - Player2 has joined.");
        _playerTracker.ProcessLogLine("16:55:00 - Player1 has quit.");

        // Act
        var allPlayers = _playerTracker.GetAllPlayers();

        // Assert
        Assert.Equal(2, allPlayers.Count);
        Assert.Contains(allPlayers, p => p.Name == "Player1" && !p.IsOnline);
        Assert.Contains(allPlayers, p => p.Name == "Player2" && p.IsOnline);
    }

    [Fact]
    public void Clear_RemovesAllPlayers()
    {
        // Arrange
        _playerTracker.ProcessLogLine("16:53:14 - Player1 has joined.");
        _playerTracker.ProcessLogLine("16:53:15 - Player2 has joined.");

        // Act
        _playerTracker.Clear();

        // Assert
        var players = _playerTracker.GetAllPlayers();
        Assert.Empty(players);

        var (online, total) = _playerTracker.GetPlayerCount();
        Assert.Equal(0, online);
        Assert.Equal(0, total);
    }

    [Fact]
    public void TimeSinceLastListUpdate_ReturnsCorrectTime()
    {
        // Arrange
        var lines = new[] { "Players: 0/24" };
        _playerTracker.ProcessListResponse(lines);

        // Act
        System.Threading.Thread.Sleep(100); // Wait a bit
        var timeSince = _playerTracker.TimeSinceLastListUpdate();

        // Assert
        Assert.True(timeSince.TotalMilliseconds >= 100);
    }

    [Fact]
    public void ProcessListResponse_MixedBotsAndHumans_TracksCorrectly()
    {
        // Arrange
        var lines = new[]
        {
            "Players: 5/24",
            "0: *BotRacer1",
            "1: HumanPlayer1",
            "2: *BotRacer2",
            "3: HumanPlayer2",
            "4: *BotRacer3"
        };

        // Act
        _playerTracker.ProcessListResponse(lines);

        // Assert
        var players = _playerTracker.GetOnlinePlayers();
        Assert.Equal(5, players.Count);

        var bots = players.Where(p => p.IsBot).ToList();
        var humans = players.Where(p => !p.IsBot).ToList();

        Assert.Equal(3, bots.Count);
        Assert.Equal(2, humans.Count);

        Assert.All(bots, bot => Assert.True(bot.IsBot));
        Assert.All(humans, human => Assert.False(human.IsBot));
    }

    [Fact]
    public void ProcessLogLine_BotWithSpacesJoinEvent_AddsBot()
    {
        // Arrange
        var logLine = "16:53:14 - *Ultimate Night of Super Se has joined.";

        // Act
        _playerTracker.ProcessLogLine(logLine);

        // Assert
        var players = _playerTracker.GetOnlinePlayers();
        Assert.Single(players);
        Assert.Equal("Ultimate Night of Super Se", players[0].Name);
        Assert.True(players[0].IsBot);
        Assert.True(players[0].IsOnline);
    }

    [Fact]
    public void ProcessLogLine_HumanWithSpacesJoinEvent_AddsHuman()
    {
        // Arrange
        var logLine = "16:53:14 - Pro Gamer 123 has joined.";

        // Act
        _playerTracker.ProcessLogLine(logLine);

        // Assert
        var players = _playerTracker.GetOnlinePlayers();
        Assert.Single(players);
        Assert.Equal("Pro Gamer 123", players[0].Name);
        Assert.False(players[0].IsBot);
        Assert.True(players[0].IsOnline);
    }

    [Fact]
    public void ProcessLogLine_BotWithSpacesQuitEvent_MarksOffline()
    {
        // Arrange
        _playerTracker.ProcessLogLine("16:53:14 - *Ultimate Night of Super Se has joined.");
        var logLine = "16:55:00 - *Ultimate Night of Super Se has quit (ping timeout).";

        // Act
        _playerTracker.ProcessLogLine(logLine);

        // Assert
        var onlinePlayers = _playerTracker.GetOnlinePlayers();
        Assert.Empty(onlinePlayers);

        var allPlayers = _playerTracker.GetAllPlayers();
        Assert.Single(allPlayers);
        Assert.Equal("Ultimate Night of Super Se", allPlayers[0].Name);
        Assert.False(allPlayers[0].IsOnline);
    }

    [Fact]
    public void ProcessListResponse_PlayerNamesWithSpaces_ParsesCorrectly()
    {
        // Arrange
        var lines = new[]
        {
            "Players: 3/24",
            "0: *Ultimate Night of Super Se",
            "1: Pro Gamer 123",
            "2: Another Player Name"
        };

        // Act
        _playerTracker.ProcessListResponse(lines);

        // Assert
        var players = _playerTracker.GetOnlinePlayers();
        Assert.Equal(3, players.Count);

        var bot = players.FirstOrDefault(p => p.Name == "Ultimate Night of Super Se");
        Assert.NotNull(bot);
        Assert.True(bot.IsBot);
        Assert.Equal(0, bot.Slot);

        var human1 = players.FirstOrDefault(p => p.Name == "Pro Gamer 123");
        Assert.NotNull(human1);
        Assert.False(human1.IsBot);
        Assert.Equal(1, human1.Slot);

        var human2 = players.FirstOrDefault(p => p.Name == "Another Player Name");
        Assert.NotNull(human2);
        Assert.False(human2.IsBot);
        Assert.Equal(2, human2.Slot);
    }
}
