using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using WreckfestController.Models;
using WreckfestController.Services;
using Xunit;

namespace WreckfestController.Tests.Services;

public class PlayerTrackerTests
{
    private readonly PlayerTracker _playerTracker;

    public PlayerTrackerTests()
    {
        var webhook = new Mock<WreckfestWebWebhookService>(
            Mock.Of<ILogger<WreckfestWebWebhookService>>(),
            Mock.Of<IConfiguration>(),
            Mock.Of<HttpClient>());
        _playerTracker = new PlayerTracker(Mock.Of<ILogger<PlayerTracker>>(), webhook.Object);
    }

    [Fact]
    public void ProcessHookPlayerSnapshot_ReplacesCurrentPlayers()
    {
        _playerTracker.Seed("OldPlayer");

        _playerTracker.ProcessHookPlayerSnapshot([
            new Player { Name = "Procat", Slot = 11, IsAdmin = true, JoinedAt = DateTime.UtcNow },
            new Player { Name = "eRacer", Slot = 1, IsBot = true, JoinedAt = DateTime.UtcNow }
        ]);

        var players = _playerTracker.GetPlayers();

        Assert.Equal(2, players.Count);
        Assert.DoesNotContain(players, p => p.Name == "OldPlayer");
        Assert.Contains(players, p => p.Name == "Procat" && p.Slot == 11 && p.IsAdmin);
        Assert.Contains(players, p => p.Name == "eRacer" && p.Slot == 1 && p.IsBot);
    }

    [Fact]
    public void MarkPlayerSeen_WhenChatComesFromUnknownHuman_AddsHuman()
    {
        _playerTracker.MarkPlayerSeen("Procat", isBot: false);

        var players = _playerTracker.GetPlayers();
        var (online, total) = _playerTracker.GetPlayerCount();

        Assert.Single(players);
        Assert.Equal("Procat", players[0].Name);
        Assert.False(players[0].IsBot);
        Assert.Equal(1, online);
        Assert.Equal(1, total);
    }

    [Fact]
    public void GetPlayerCount_ReturnsCorrectCounts()
    {
        _playerTracker.Seed("Player1", "Player2");

        var (online, total) = _playerTracker.GetPlayerCount();

        Assert.Equal(2, online);
        Assert.Equal(2, total);
    }

    [Fact]
    public void GetPlayers_OrdersBySlotThenJoinTime()
    {
        _playerTracker.ProcessHookPlayerSnapshot([
            new Player { Name = "Player3", Slot = 2, JoinedAt = DateTime.UtcNow },
            new Player { Name = "Player1", Slot = 0, JoinedAt = DateTime.UtcNow },
            new Player { Name = "Player2", Slot = 1, JoinedAt = DateTime.UtcNow }
        ]);

        var players = _playerTracker.GetPlayers();

        Assert.Equal(3, players.Count);
        Assert.Equal("Player1", players[0].Name);
        Assert.Equal("Player2", players[1].Name);
        Assert.Equal("Player3", players[2].Name);
    }

    [Fact]
    public void Clear_RemovesAllPlayers()
    {
        _playerTracker.Seed("Player1", "Player2");

        _playerTracker.Clear();

        Assert.Empty(_playerTracker.GetPlayers());
        var (online, total) = _playerTracker.GetPlayerCount();
        Assert.Equal(0, online);
        Assert.Equal(0, total);
    }

    [Fact]
    public void ProcessServerEvent_JoinAddsPlayerAndDetectsBotFromMarker()
    {
        var tracker = CreateTracker();

        tracker.ProcessServerEvent(new ServerEvent(ServerEvent.PlayerHasJoined, "*eRacer", []));
        tracker.ProcessServerEvent(new ServerEvent(ServerEvent.PlayerHasJoined, "Procat", []));

        var players = tracker.GetPlayers();
        Assert.Equal(2, players.Count);
        Assert.True(players.Single(p => p.Name == "eRacer").IsBot);
        Assert.False(players.Single(p => p.Name == "Procat").IsBot);
    }

    [Fact]
    public void ProcessServerEvent_QuitRemovesPlayerRegardlessOfReason()
    {
        foreach (var quitId in new[]
                 {
                     ServerEvent.QuitNormal, ServerEvent.QuitTimeout, ServerEvent.QuitBanned,
                     ServerEvent.QuitInvalid, ServerEvent.QuitBot
                 })
        {
            var tracker = CreateTracker();
            tracker.ProcessServerEvent(new ServerEvent(ServerEvent.PlayerHasJoined, "Procat", []));
            tracker.ProcessServerEvent(new ServerEvent(quitId, "Procat", []));

            Assert.DoesNotContain(tracker.GetPlayers(), p => p.Name == "Procat");
        }
    }

    [Fact]
    public void ProcessServerEvent_PrivilegeEventsUpdateRolesImmediately()
    {
        var tracker = CreateTracker();
        tracker.ProcessServerEvent(new ServerEvent(ServerEvent.PlayerHasJoined, "Procat", []));

        tracker.ProcessServerEvent(new ServerEvent(ServerEvent.NewModerator, "Procat", []));
        var player = tracker.GetPlayers().Single(p => p.Name == "Procat");
        Assert.True(player.IsModerator);
        Assert.False(player.IsAdmin);
        Assert.True(player.IsPrivileged);

        tracker.ProcessServerEvent(new ServerEvent(ServerEvent.NewAdmin, "Procat", []));
        player = tracker.GetPlayers().Single(p => p.Name == "Procat");
        Assert.True(player.IsAdmin);
        Assert.True(player.IsPrivileged);

        tracker.ProcessServerEvent(new ServerEvent(ServerEvent.Demoted, "Procat", []));
        player = tracker.GetPlayers().Single(p => p.Name == "Procat");
        Assert.False(player.IsPrivileged);
    }

    private static PlayerTracker CreateTracker()
    {
        var webhook = new Mock<WreckfestWebWebhookService>(
            Mock.Of<ILogger<WreckfestWebWebhookService>>(),
            Mock.Of<IConfiguration>(),
            Mock.Of<HttpClient>());
        return new PlayerTracker(Mock.Of<ILogger<PlayerTracker>>(), webhook.Object);
    }

    [Fact]
    public void ProcessLogLine_BotJoinEvent_AddsBot()
    {
        // Arrange
        var logLine = "16:53:14 - *eRacer has joined.";

        // Act
        _playerTracker.ProcessLogLine(logLine);

        // Assert
        var players = _playerTracker.GetPlayers();
        Assert.Single(players);
        Assert.Equal("eRacer", players[0].Name);
        Assert.True(players[0].IsBot);
    }

    [Fact]
    public void ProcessLogLine_HumanJoinEvent_AddsHuman()
    {
        // Arrange
        var logLine = "16:53:14 - Player123 has joined.";

        // Act
        _playerTracker.ProcessLogLine(logLine);

        // Assert
        var players = _playerTracker.GetPlayers();
        Assert.Single(players);
        Assert.Equal("Player123", players[0].Name);
        Assert.False(players[0].IsBot);
    }

    [Fact]
    public void ProcessLogLine_BotQuitEvent_RemovesPlayer()
    {
        // Arrange
        _playerTracker.ProcessLogLine("16:53:14 - *eRacer has joined.");
        var logLine = "16:55:00 - *eRacer has quit (ping timeout).";

        // Act
        _playerTracker.ProcessLogLine(logLine);

        // Assert
        var players = _playerTracker.GetPlayers();
        Assert.Empty(players);
    }

    [Fact]
    public void ProcessLogLine_HumanQuitEvent_RemovesPlayer()
    {
        // Arrange
        _playerTracker.ProcessLogLine("16:53:14 - Player123 has joined.");
        var logLine = "16:55:00 - Player123 has quit.";

        // Act
        _playerTracker.ProcessLogLine(logLine);

        // Assert
        var players = _playerTracker.GetPlayers();
        Assert.Empty(players);
    }

    [Fact]
    public void ProcessLogLine_PlayerRejoins_TracksPlayer()
    {
        // Arrange
        _playerTracker.ProcessLogLine("16:53:14 - Player123 has joined.");
        _playerTracker.ProcessLogLine("16:55:00 - Player123 has quit.");

        // Act
        _playerTracker.ProcessLogLine("17:00:00 - Player123 has joined.");

        // Assert
        var players = _playerTracker.GetPlayers();
        Assert.Single(players);
    }

    [Fact]
    public void ProcessLogLine_EmptyLine_DoesNothing()
    {
        // Act
        _playerTracker.ProcessLogLine("");
        _playerTracker.ProcessLogLine("   ");

        // Assert
        var players = _playerTracker.GetPlayers();
        Assert.Empty(players);
    }

    [Fact]
    public void ProcessLogLine_UnrelatedLine_DoesNothing()
    {
        // Act
        _playerTracker.ProcessLogLine("Some random log line");
        _playerTracker.ProcessLogLine("16:53:14 - Server started");

        // Assert
        var players = _playerTracker.GetPlayers();
        Assert.Empty(players);
    }

    [Fact]
    public void ProcessLogLine_BotWithSpacesJoinEvent_AddsBot()
    {
        // Arrange
        var logLine = "16:53:14 - *Ultimate Night of Super Se has joined.";

        // Act
        _playerTracker.ProcessLogLine(logLine);

        // Assert
        var players = _playerTracker.GetPlayers();
        Assert.Single(players);
        Assert.Equal("Ultimate Night of Super Se", players[0].Name);
        Assert.True(players[0].IsBot);
    }

    [Fact]
    public void ProcessLogLine_HumanWithSpacesJoinEvent_AddsHuman()
    {
        // Arrange
        var logLine = "16:53:14 - Pro Gamer 123 has joined.";

        // Act
        _playerTracker.ProcessLogLine(logLine);

        // Assert
        var players = _playerTracker.GetPlayers();
        Assert.Single(players);
        Assert.Equal("Pro Gamer 123", players[0].Name);
        Assert.False(players[0].IsBot);
    }

    [Fact]
    public void ProcessLogLine_BotWithSpacesQuitEvent_RemovesPlayer()
    {
        // Arrange
        _playerTracker.ProcessLogLine("16:53:14 - *Ultimate Night of Super Se has joined.");
        var logLine = "16:55:00 - *Ultimate Night of Super Se has quit (ping timeout).";

        // Act
        _playerTracker.ProcessLogLine(logLine);

        // Assert
        var players = _playerTracker.GetPlayers();
        Assert.Empty(players);
    }

    [Fact]
    public void ProcessLogLine_SkipsJoinParsing_WhenServerEventsAreAuthoritative()
    {
        var tracker = CreateTracker();
        tracker.UseServerEvents = true;

        tracker.ProcessLogLine("16:53:14 - Procat has joined.");

        // Would otherwise be double-counted alongside the event.
        Assert.Empty(tracker.GetPlayers());
    }

    [Fact]
    public void ProcessLogLine_StillParsesJoins_WhenEventsAreUnavailable()
    {
        var tracker = CreateTracker();
        tracker.UseServerEvents = false;

        tracker.ProcessLogLine("16:53:14 - Procat has joined.");

        Assert.Contains(tracker.GetPlayers(), p => p.Name == "Procat");
    }
}
