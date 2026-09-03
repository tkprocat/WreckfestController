using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using WreckfestController.Models;
using WreckfestController.Services;
using Xunit;

namespace WreckfestController.Tests.Services;

public class PlayerTrackerTests
{
    private readonly Mock<ILogger<PlayerTracker>> _mockLogger;
    private readonly Mock<WreckfestWebWebhookService> _mockWebhookService;
    private readonly PlayerTracker _playerTracker;

    public PlayerTrackerTests()
    {
        _mockLogger = new Mock<ILogger<PlayerTracker>>();
        _mockWebhookService = new Mock<WreckfestWebWebhookService>(
            Mock.Of<ILogger<WreckfestWebWebhookService>>(),
            Mock.Of<IConfiguration>(),
            Mock.Of<HttpClient>());
        _playerTracker = new PlayerTracker(_mockLogger.Object, _mockWebhookService.Object);
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
    public void ProcessHookPlayerSnapshot_ReplacesCurrentPlayers()
    {
        _playerTracker.Seed("OldPlayer");

        _playerTracker.ProcessHookPlayerSnapshot(new[]
        {
            new Player { Name = "Procat", Slot = 11, IsAdmin = true, JoinedAt = DateTime.UtcNow },
            new Player { Name = "eRacer", Slot = 1, IsBot = true, JoinedAt = DateTime.UtcNow }
        });

        var players = _playerTracker.GetPlayers();

        Assert.Equal(2, players.Count);
        Assert.DoesNotContain(players, p => p.Name == "OldPlayer");
        Assert.Contains(players, p => p.Name == "Procat" && p.Slot == 11 && p.IsAdmin);
        Assert.Contains(players, p => p.Name == "eRacer" && p.Slot == 1 && p.IsBot);
    }

    [Fact]
    public void MarkPlayerSeen_WhenChatComesFromUnknownHuman_AddsHumanWithoutRequestingList()
    {
        var listRequests = 0;
        _playerTracker.OnListCommandRequested += () => listRequests++;

        _playerTracker.MarkPlayerSeen("Procat", isBot: false);

        var players = _playerTracker.GetPlayers();
        var (online, total) = _playerTracker.GetPlayerCount();

        Assert.Single(players);
        Assert.Equal("Procat", players[0].Name);
        Assert.False(players[0].IsBot);
        Assert.Equal(1, online);
        Assert.Equal(1, total);
        Assert.Equal(0, listRequests);
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
    public void ProcessListResponse_WithBots_AddsBotsCorrectly()
    {
        // Arrange
        var lines = new[]
        {
            "Players: 2/24",
            "  0:    0 [0] *eRacer         | [A] 244 Stellar      | ping: 0    | ready",
            "  1:    0 [0] Player123       | [A] 244 Stellar      | ping: 0    | ready"
        };

        // Act
        _playerTracker.ProcessListResponse(lines);

        // Assert
        var players = _playerTracker.GetPlayers();
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
    public void ProcessListResponse_EmptyList_RemovesAllPlayers()
    {
        // Arrange
        var lines = new[]
        {
            "Players: 2/24",
            "  0:    0 [0] Player1        | [A] 244 Stellar      | ping: 0    | ready",
            "  1:    0 [0] Player2        | [A] 244 Stellar      | ping: 0    | ready"
        };
        _playerTracker.ProcessListResponse(lines);

        // Act - Process empty list
        var emptyLines = new[] { "Players: 0/24" };
        _playerTracker.ProcessListResponse(emptyLines);

        // Assert
        var players = _playerTracker.GetPlayers();
        Assert.Empty(players);
    }

    [Fact]
    public void ProcessListResponse_UpdatesExistingPlayers()
    {
        // Arrange
        _playerTracker.Seed("Player123");
        var lines = new[]
        {
            "Players: 1/24",
            "  0:    0 [0] Player123      | [A] 244 Stellar      | ping: 0    | ready"
        };

        // Act
        _playerTracker.ProcessListResponse(lines);

        // Assert
        var players = _playerTracker.GetPlayers();
        Assert.Single(players);
        Assert.Equal(0, players[0].Slot);
    }

    [Fact]
    public void GetPlayerCount_ReturnsCorrectCounts()
    {
        // Arrange
        _playerTracker.Seed("Player1", "Player2", "Player3");
        _playerTracker.ProcessLogLine("16:55:00 - Player3 has quit.");

        // Act
        var (online, total) = _playerTracker.GetPlayerCount();

        // Assert
        Assert.Equal(2, online);
        Assert.Equal(2, total);  // Player3 is removed, not just marked offline
    }

    [Fact]
    public void GetPlayers_OrdersBySlotThenJoinTime()
    {
        // Arrange
        var lines = new[]
        {
            "Players: 3/24",
            "  2:    0 [0] Player3        | [A] 244 Stellar      | ping: 0    | ready",
            "  0:    0 [0] Player1        | [A] 244 Stellar      | ping: 0    | ready",
            "  1:    0 [0] Player2        | [A] 244 Stellar      | ping: 0    | ready"
        };

        // Act
        _playerTracker.ProcessListResponse(lines);
        var players = _playerTracker.GetPlayers();

        // Assert
        Assert.Equal(3, players.Count);
        Assert.Equal("Player1", players[0].Name);
        Assert.Equal("Player2", players[1].Name);
        Assert.Equal("Player3", players[2].Name);
    }

    [Fact]
    public void Clear_RemovesAllPlayers()
    {
        // Arrange
        _playerTracker.Seed("Player1", "Player2");

        // Act
        _playerTracker.Clear();

        // Assert
        var players = _playerTracker.GetPlayers();
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
            "  0:    0 [0] *BotRacer1     | [A] 244 Stellar      | ping: 0    | ready",
            "  1:    0 [0] HumanPlayer1   | [A] 244 Stellar      | ping: 0    | ready",
            "  2:    0 [0] *BotRacer2     | [A] 244 Stellar      | ping: 0    | ready",
            "  3:    0 [0] HumanPlayer2   | [A] 244 Stellar      | ping: 0    | ready",
            "  4:    0 [0] *BotRacer3     | [A] 244 Stellar      | ping: 0    | ready"
        };

        // Act
        _playerTracker.ProcessListResponse(lines);

        // Assert
        var players = _playerTracker.GetPlayers();
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
    public void ProcessListResponse_PlayerNamesWithSpaces_ParsesCorrectly()
    {
        // Arrange
        var lines = new[]
        {
            "Players: 3/24",
            "  0:    0 [0] *Ultimate Night of Super Se          | [A] 244 Stellar      | ping: 0    | ready",
            "  1:    0 [0] Pro Gamer 123    | [A] 244 Stellar      | ping: 0    | ready",
            "  2:    0 [0] Another Player Name                   | [A] 244 Stellar      | ping: 0    | ready"
        };

        // Act
        _playerTracker.ProcessListResponse(lines);

        // Assert
        var players = _playerTracker.GetPlayers();
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

    [Fact]
    public void ProcessLogLine_ListCommandWithNewFormatAndInterruption_ParsesCorrectly()
    {
        // Arrange - Simulate the new list format with a "has joined" message interrupting the list
        var lines = new[]
        {
            "list",
            "  1:    0 [0] *Lord Bane        | [A] 244 Stellar      | ping: 0    | ready",
            "  2:    0 [C] *Goose4all        | [C] 115 Starbeast    | ping: 0    | ready",
            "  3:    0 [0] *johnsacka        | [A] 244 Stellar      | ping: 0    | ready",
            "16:53:14 - *NewBot has joined.",  // Random join event interrupting the list
            "  4:    0 [B] *Seppo Hallikainen               >",
            "  5:    0 [A] *Roadwarrior      | [A] 273 Gremlin      | ping: 0    | ready",
            "  6:    0 [C] *L3vn             | [C] 135 Buggy        | ping: 0    | ready",
            "  7:    0 [0] *RPE001           | [A] 236 Warwagon     | ping: 0    | ready",
            "  8:    0 [0] *BZ               | [C] 104 Raven        | ping: 0    | ready",
            "  9:    0 [0] *Icheherntion     | [C] 138 Blade        | ping: 0    | ready",
            " 10:    0 [0] *Ryzza5           | [B] 197 Boomer       | ping: 0    | ready"
        };

        // Act - Process each line as if they're coming from the console monitor
        foreach (var line in lines)
        {
            _playerTracker.ProcessLogLine(line);
        }

        // Process a non-list line to trigger end of collection
        _playerTracker.ProcessLogLine("Some other server message");

        // Assert
        var players = _playerTracker.GetPlayers();

        // When the "has joined" message interrupts the list, the collection stops
        // So we should have only the 3 players BEFORE the interruption + the NewBot that joined
        Assert.Equal(4, players.Count);

        // Verify players that were processed before the interruption
        Assert.Contains(players, p => p.Name == "Lord Bane" && p.IsBot);
        Assert.Contains(players, p => p.Name == "Goose4all" && p.IsBot);
        Assert.Contains(players, p => p.Name == "johnsacka" && p.IsBot);

        // Verify the bot that joined during the list (which caused the interruption)
        Assert.Contains(players, p => p.Name == "NewBot" && p.IsBot);

        // Verify slots are assigned correctly for the entries that were collected
        var lordBane = players.First(p => p.Name == "Lord Bane");
        Assert.Equal(1, lordBane.Slot);

        var goose = players.First(p => p.Name == "Goose4all");
        Assert.Equal(2, goose.Slot);

        // This test demonstrates that interruptions ARE handled gracefully:
        // - The partial list is processed correctly
        // - The interrupting "has joined" event is also processed
        // - No errors or crashes occur
        // - The system continues to function normally
    }

    [Fact]
    public void ProcessLogLine_ListCommandWithNewFormat_ParsesAllPlayers()
    {
        // Arrange - Simulate the full new list format without interruptions
        var lines = new[]
        {
            "list",
            "  1:    0 [0] *Lord Bane        | [A] 244 Stellar      | ping: 0    | ready",
            "  2:    0 [C] *Goose4all        | [C] 115 Starbeast    | ping: 0    | ready",
            "  3:    0 [0] *johnsacka        | [A] 244 Stellar      | ping: 0    | ready",
            "  4:    0 [B] *Seppo Hallikainen               >",
            "  5:    0 [A] *Roadwarrior      | [A] 273 Gremlin      | ping: 0    | ready",
            "  6:    0 [C] *L3vn             | [C] 135 Buggy        | ping: 0    | ready",
            "  7:    0 [0] *RPE001           | [A] 236 Warwagon     | ping: 0    | ready",
            "  8:    0 [0] *BZ               | [C] 104 Raven        | ping: 0    | ready",
            "  9:    0 [0] *Icheherntion     | [C] 138 Blade        | ping: 0    | ready",
            " 10:    0 [0] *Ryzza5           | [B] 197 Boomer       | ping: 0    | ready"
        };

        // Act - Process each line
        foreach (var line in lines)
        {
            _playerTracker.ProcessLogLine(line);
        }

        // Process a non-list line to trigger end of collection
        _playerTracker.ProcessLogLine("Some other server message");

        // Assert
        var players = _playerTracker.GetPlayers();

        // Should have all 10 players
        Assert.Equal(10, players.Count);

        // Verify all players are present and are bots
        Assert.All(players, p => Assert.True(p.IsBot));

        // Verify specific players
        Assert.Contains(players, p => p.Name == "Lord Bane");
        Assert.Contains(players, p => p.Name == "Goose4all");
        Assert.Contains(players, p => p.Name == "johnsacka");
        Assert.Contains(players, p => p.Name == "Seppo Hallikainen");
        Assert.Contains(players, p => p.Name == "Roadwarrior");
        Assert.Contains(players, p => p.Name == "L3vn");
        Assert.Contains(players, p => p.Name == "RPE001");
        Assert.Contains(players, p => p.Name == "BZ");
        Assert.Contains(players, p => p.Name == "Icheherntion");
        Assert.Contains(players, p => p.Name == "Ryzza5");

        // Verify slots are assigned
        Assert.All(players, p => Assert.NotNull(p.Slot));
    }

    // --- server-event driven tracking ---------------------------------------

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
        // This is the gap that silently broke !eventloop: a join line carries no role,
        // so without these events a freshly promoted moderator looks unprivileged
        // until something else refreshes the roster.
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

    private static PlayerTracker CreateTracker()
    {
        var webhook = new Mock<WreckfestWebWebhookService>(
            Mock.Of<ILogger<WreckfestWebWebhookService>>(),
            Mock.Of<IConfiguration>(),
            Mock.Of<HttpClient>());
        return new PlayerTracker(Mock.Of<ILogger<PlayerTracker>>(), webhook.Object);
    }
}
