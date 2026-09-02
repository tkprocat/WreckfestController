using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using WreckfestController.Models;
using WreckfestController.Services;
using Xunit;

namespace WreckfestController.Tests.Services;

public class VotingServiceTests
{
    private readonly Mock<ILogger<VotingService>> _mockLogger;
    private readonly IConfiguration _config;
    private readonly Mock<ServerManager> _mockServerManager;
    private readonly Mock<ConfigService> _mockConfigService;
    private readonly PlayerTracker _playerTracker;
    private readonly VotingService _votingService;

    private readonly List<string> _broadcastMessages = new();

    public VotingServiceTests()
    {
        _mockLogger = new Mock<ILogger<VotingService>>();
        _config = CreateVoteConfig();

        var mockWebhook = new Mock<WreckfestWebWebhookService>(
            Mock.Of<ILogger<WreckfestWebWebhookService>>(),
            Mock.Of<IConfiguration>(),
            Mock.Of<HttpClient>());

        _playerTracker = new PlayerTracker(
            Mock.Of<ILogger<PlayerTracker>>(),
            mockWebhook.Object);

        _mockServerManager = new Mock<ServerManager>(
            Mock.Of<IConfiguration>(),
            Mock.Of<ILogger<ServerManager>>(),
            _playerTracker,
            new TrackChangeTracker(Mock.Of<ILogger<TrackChangeTracker>>(), mockWebhook.Object),
            new ServerInfoTracker(Mock.Of<ILogger<ServerInfoTracker>>()),
            mockWebhook.Object,
            new Mock<ConsoleLogWebhookSender>(Mock.Of<HttpClient>(), Mock.Of<IConfiguration>(), Mock.Of<ILogger<ConsoleLogWebhookSender>>()).Object);

        _mockServerManager
            .Setup(m => m.SendCommandAsync(It.IsAny<string>()))
            .Callback<string>(cmd => { if (cmd.StartsWith("/message ")) _broadcastMessages.Add(cmd[9..]); })
            .ReturnsAsync((true, "ok"));

        _mockConfigService = new Mock<ConfigService>(
            Mock.Of<IConfiguration>(),
            Mock.Of<ILogger<ConfigService>>());

        _mockConfigService.Setup(c => c.GetCurrentCollectionName()).Returns("TestCollection");
        _mockConfigService.Setup(c => c.ReadEventLoopTracks()).Returns(new List<EventLoopTrack>
        {
            new() { Track = "existing_track", Laps = 5 }
        });

        _votingService = new VotingService(
            _mockServerManager.Object,
            _playerTracker,
            _mockConfigService.Object,
            _mockLogger.Object,
            _config);
    }

    private void SendChat(string playerName, string message, bool isBot = false)
    {
        _votingService.ProcessChatCommand(playerName, isBot, message);
    }

    private void JoinPlayer(string name) =>
        _playerTracker.ProcessLogLine($"16:53:14 - {name} has joined.");

    [Fact]
    public async Task VoteStarted_BroadcastsAnnouncementAndAutoVotesYes()
    {
        // Two humans: with only one online the vote is skipped entirely.
        JoinPlayer("Alice");
        JoinPlayer("Bob");
        SendChat("Alice", "!vote wrecknado_02 10");
        await Task.Delay(50);

        Assert.Contains(_broadcastMessages, m => m == "Vote: Wrecknado - 10 laps");
        Assert.Contains(_broadcastMessages, m => m == "By Alice. Type !yes or !no. Ends in 30s.");
        Assert.DoesNotContain(_broadcastMessages, m => m.StartsWith("Vote started", StringComparison.Ordinal));
    }

    [Fact]
    public async Task VoteStarted_WithLongTrackName_TruncatesFirstLineToChatLimit()
    {
        var (service, tracker, messages, _) = CreateLongTrackNameSetup();
        tracker.ProcessLogLine("16:53:14 - Alice has joined.");
        tracker.ProcessLogLine("16:53:14 - Bob has joined.");

        service.ProcessChatCommand("Alice", isBot: false, "!vote long_track 99");
        await Task.Delay(50);

        var firstLine = Assert.Single(messages, m => m.StartsWith("Vote: ", StringComparison.Ordinal));
        Assert.EndsWith(" - 99 laps", firstLine);
        Assert.True(firstLine.Length <= 127, firstLine);
        Assert.Contains("...", firstLine);
        Assert.DoesNotContain("long_track", firstLine);
    }

    [Fact]
    public async Task SecondVote_WhileActiveVote_TellsRequesterWhatIsPending()
    {
        JoinPlayer("Alice");
        JoinPlayer("Bob");
        SendChat("Alice", "!vote wrecknado_02 10");
        SendChat("Bob", "!vote other_track 5");
        await Task.Delay(50);

        var refusal = Assert.Single(_broadcastMessages, m => m.StartsWith("Bob:", StringComparison.Ordinal));
        Assert.Contains("vote in progress", refusal, StringComparison.Ordinal);
        Assert.Contains("Wrecknado", refusal, StringComparison.Ordinal);
        Assert.Contains("for 10 laps", refusal, StringComparison.Ordinal);
        Assert.Matches(@"\d+s left", refusal);
    }

    [Fact]
    public async Task SecondVote_WhileActiveVote_DoesNotResolveTheRequestedTrack()
    {
        JoinPlayer("Alice");
        JoinPlayer("Bob");
        SendChat("Alice", "!vote wrecknado_02 10");
        _broadcastMessages.Clear();

        // A nonsense query must still get the in-progress reply, proving the guard
        // runs before track resolution rather than after it.
        SendChat("Bob", "!vote not_a_real_track 5");
        await Task.Delay(50);

        Assert.Contains(_broadcastMessages, m => m.Contains("vote in progress", StringComparison.Ordinal));
        Assert.DoesNotContain(_broadcastMessages, m => m.Contains("is not allowed for voting", StringComparison.Ordinal));
    }

    [Fact]
    public async Task VoteCommand_FromBot_Ignored()
    {
        SendChat("BotPlayer", "!vote wrecknado_02 10", isBot: true);
        await Task.Delay(50);

        Assert.DoesNotContain(_broadcastMessages, m => m.Contains("Vote started"));
    }

    [Fact]
    public async Task VoteCommand_WhenVotingDisabled_SendsDisabledMessageAndDoesNotStartVote()
    {
        var (service, _, messages, configMock) = CreateDisabledVotingSetup();

        service.ProcessChatCommand("Alice", isBot: false, "!vote wrecknado_02 5");
        await Task.Delay(50);

        Assert.Contains(messages, m => m.Contains("Voting is currently disabled"));
        Assert.DoesNotContain(messages, m => m.Contains("Vote started"));
        configMock.Verify(c => c.WriteEventLoopTracks(
            It.IsAny<string>(), It.IsAny<List<EventLoopTrack>>()), Times.Never);
    }

    [Fact]
    public async Task SearchCommand_WhenVotingDisabled_SendsDisabledMessage()
    {
        var (service, _, messages, _) = CreateDisabledVotingSetup();

        service.ProcessChatCommand("Alice", isBot: false, "!search wreck");
        await Task.Delay(50);

        Assert.Contains(messages, m => m.Contains("Voting is currently disabled"));
        Assert.DoesNotContain(messages, m => m.Contains("Matches:"));
    }

    [Fact]
    public async Task MoreCommand_WhenVotingDisabled_SendsDisabledMessage()
    {
        var (service, _, messages, _) = CreateDisabledVotingSetup();

        service.ProcessChatCommand("Alice", isBot: false, "!more");
        await Task.Delay(50);

        Assert.Contains(messages, m => m.Contains("Voting is currently disabled"));
        Assert.DoesNotContain(messages, m => m.Contains("No more search results"));
    }

    [Fact]
    public async Task HelpCommand_WhenVotingDisabled_ReportsDisabled()
    {
        var (service, _, messages, _) = CreateDisabledVotingSetup();

        service.ProcessChatCommand("Alice", isBot: false, "!help");
        await Task.Delay(50);

        Assert.Contains(messages, m => m.Contains("Voting is currently disabled"));
    }

    [Fact]
    public async Task YesVote_DuplicateVoter_SendsDuplicateMessage()
    {
        JoinPlayer("Alice");
        JoinPlayer("Bob");
        SendChat("Alice", "!vote wrecknado_02 5");
        SendChat("Alice", "!yes"); // Alice already auto-voted yes
        await Task.Delay(50);

        Assert.Contains(_broadcastMessages, m => m.Contains("Alice") && m.Contains("already voted"));
    }

    [Fact]
    public async Task YesVote_EarlyStrictMajority_PassesVoteAndSendsTrackSettings()
    {
        JoinPlayer("Alice");
        JoinPlayer("Bob");
        JoinPlayer("Charlie");

        SendChat("Alice", "!vote wrecknado_02 10"); // Alice auto-yes (1/3)
        SendChat("Bob", "!yes");   // Bob yes (2/3) → majority
        await Task.Delay(100);

        _mockServerManager.Verify(m => m.SendCommandAsync("track=wrecknado_02"), Times.Once);
        _mockServerManager.Verify(m => m.SendCommandAsync("laps=10"), Times.Once);
        _mockConfigService.Verify(c => c.WriteEventLoopTracks(
            It.IsAny<string>(), It.IsAny<List<EventLoopTrack>>()), Times.Never);

        Assert.Contains(_broadcastMessages, m => m.Contains("Vote passed"));
    }

    [Fact]
    public async Task VoteStarted_WhenOnlyInitiatorOnline_AppliesDirectly()
    {
        JoinPlayer("Alice");

        SendChat("Alice", "!vote wrecknado_02 10");
        await Task.Delay(100);

        _mockServerManager.Verify(m => m.SendCommandAsync("track=wrecknado_02"), Times.Once);
        _mockServerManager.Verify(m => m.SendCommandAsync("laps=10"), Times.Once);
        Assert.Contains(_broadcastMessages, m => m.StartsWith("Next race:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task VoteStarted_WhenOnlyInitiatorOnline_SendsOnlyTheResultLine()
    {
        var (service, tracker, messages, _) = CreateIsolatedSetup(timeoutSeconds: 30, messageDelayMs: 50);
        tracker.ProcessLogLine("16:53:14 - Alice has joined.");

        service.ProcessChatCommand("Alice", isBot: false, "!vote timeout_track 3");
        await Task.Delay(250);

        // A vote with one participant is not a vote: no announcement, no invitation to
        // vote on something already decided - just the result.
        var line = Assert.Single(messages);
        Assert.Equal("Next race: Timeout Track (3 laps)", line);
    }

    [Fact]
    public async Task LuckyCommand_StartsVoteWithRandomAllowedTrackAndLapCount()
    {
        JoinPlayer("Alice");
        JoinPlayer("Bob");

        SendChat("Alice", "!lucky");
        SendChat("Bob", "!yes");
        await Task.Delay(100);

        _mockServerManager.Verify(m => m.SendCommandAsync(It.IsRegex("^track=(wrecknado_02|new_track|other_track)$")), Times.Once);
        _mockServerManager.Verify(m => m.SendCommandAsync(It.IsRegex("^laps=([1-9]|10)$")), Times.Once);
        Assert.Contains(_broadcastMessages, m => m.Contains("Lucky pick:"));
    }

    [Fact]
    public void LuckyLapWeight_BiasesTowardThreeToFiveLaps()
    {
        Assert.True(GetLuckyLapWeight(4) > GetLuckyLapWeight(3));
        Assert.Equal(GetLuckyLapWeight(3), GetLuckyLapWeight(5));
        Assert.True(GetLuckyLapWeight(3) > GetLuckyLapWeight(2));
        Assert.True(GetLuckyLapWeight(5) > GetLuckyLapWeight(6));
        Assert.True(GetLuckyLapWeight(2) > GetLuckyLapWeight(1));
        Assert.True(GetLuckyLapWeight(6) > GetLuckyLapWeight(8));
    }

    [Fact]
    public async Task IFeelLuckyCommand_StartsVoteWithRandomAllowedTrackAndLapCount()
    {
        JoinPlayer("Alice");
        JoinPlayer("Bob");

        SendChat("Alice", "!ifeellucky");
        SendChat("Bob", "!yes");
        await Task.Delay(100);

        _mockServerManager.Verify(m => m.SendCommandAsync(It.IsRegex("^track=(wrecknado_02|new_track|other_track)$")), Times.Once);
        _mockServerManager.Verify(m => m.SendCommandAsync(It.IsRegex("^laps=([1-9]|10)$")), Times.Once);
        Assert.Contains(_broadcastMessages, m => m.Contains("Lucky pick:"));
    }

    [Fact]
    public async Task LuckyCommand_WhenNoAllowedTracksConfigured_SendsWarning()
    {
        var (service, _, messages, _) = CreateNoAllowedTracksSetup();

        service.ProcessChatCommand("Alice", isBot: false, "!ifeellucky");
        await Task.Delay(50);

        Assert.Contains(messages, m => m.Contains("No tracks configured for lucky vote"));
        Assert.DoesNotContain(messages, m => m.Contains("Vote started"));
    }

    private static int GetLuckyLapWeight(int laps)
    {
        var method = typeof(VotingService).GetMethod(
            "GetLuckyLapWeight",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(method);
        return (int)method.Invoke(null, [laps])!;
    }

    [Fact]
    public async Task NoVote_EarlyStrictMajority_FailsVote()
    {
        JoinPlayer("Alice");
        JoinPlayer("Bob");
        JoinPlayer("Charlie");

        SendChat("Alice", "!vote wrecknado_02 10"); // Alice auto-yes (1/3)
        SendChat("Bob", "!no");     // Bob no (1/3)
        SendChat("Charlie", "!no"); // Charlie no (2/3) → majority no
        await Task.Delay(100);

        _mockConfigService.Verify(c => c.WriteEventLoopTracks(
            It.IsAny<string>(), It.IsAny<List<EventLoopTrack>>()), Times.Never);

        Assert.Contains(_broadcastMessages, m => m.Contains("majority voted no"));
    }

    [Fact]
    public async Task VoteTimeout_YesLeads_PassesVote()
    {
        var (service, tracker, messages, configMock) = CreateIsolatedSetup(timeoutSeconds: 1);
        tracker.ProcessLogLine("16:53:14 - Alice has joined.");
        tracker.ProcessLogLine("16:53:14 - Bob has joined.");
        service.ProcessChatCommand("Alice", isBot: false, "!vote timeout_track 3");

        await Task.Delay(1500);

        configMock.Verify(c => c.WriteEventLoopTracks(
            It.IsAny<string>(), It.IsAny<List<EventLoopTrack>>()), Times.Never);
    }

    [Fact]
    public async Task VoteTimeout_OnlyInitiatorVotedWithoutMajority_FailsVote()
    {
        var (service, tracker, messages, configMock) = CreateIsolatedSetup(timeoutSeconds: 1);
        tracker.ProcessLogLine("16:53:14 - Alice has joined.");
        tracker.ProcessLogLine("16:53:14 - Bob has joined.");
        service.ProcessChatCommand("Bob", isBot: false, "!vote only_initiator_track 3");
        // Only Bob auto-votes yes (1 yes, 0 no), which is not a human majority.

        await Task.Delay(1500);

        configMock.Verify(c => c.WriteEventLoopTracks(
            It.IsAny<string>(), It.IsAny<List<EventLoopTrack>>()), Times.Never);
        Assert.Contains(messages, m => m.Contains("not enough yes votes"));
    }

    [Fact]
    public async Task VoteTimeout_Tied_FailsVote()
    {
        var (service, tracker, messages, configMock) = CreateIsolatedSetup(timeoutSeconds: 1);
        tracker.ProcessLogLine("16:53:14 - Alice has joined.");
        tracker.ProcessLogLine("16:53:14 - Bob has joined.");
        service.ProcessChatCommand("Alice", isBot: false, "!vote tie_track 3"); // Alice auto-yes
        service.ProcessChatCommand("Bob", isBot: false, "!no"); // 1 yes, 1 no → tie

        await Task.Delay(1500);

        configMock.Verify(c => c.WriteEventLoopTracks(
            It.IsAny<string>(), It.IsAny<List<EventLoopTrack>>()), Times.Never);

        Assert.Contains(messages, m => m.Contains("not enough yes votes"));
    }

    [Fact]
    public async Task VoteStatus_WhenTwentySecondsRemain_BroadcastsPassingStatus()
    {
        var (service, tracker, messages, _) = CreateIsolatedSetup(timeoutSeconds: 21);
        tracker.ProcessLogLine("16:53:14 - Alice has joined.");
        tracker.ProcessLogLine("16:53:14 - Bob has joined.");

        service.ProcessChatCommand("Alice", isBot: false, "!vote timeout_track 3");
        await Task.Delay(1500);

        Assert.Contains(messages, m =>
            m.Contains("20 seconds left for voting") &&
            m.Contains("currently the vote is passing") &&
            m.Contains("(1 yes, 0 no)"));
    }

    [Fact]
    public async Task VoteStatus_WhenTenSecondsRemain_BroadcastsFailingStatus()
    {
        var (service, tracker, messages, _) = CreateIsolatedSetup(timeoutSeconds: 11);
        tracker.ProcessLogLine("16:53:14 - Alice has joined.");
        tracker.ProcessLogLine("16:53:14 - Bob has joined.");

        service.ProcessChatCommand("Alice", isBot: false, "!vote timeout_track 3");
        service.ProcessChatCommand("Bob", isBot: false, "!no");
        await Task.Delay(1500);

        Assert.Contains(messages, m =>
            m.Contains("10 seconds left for voting") &&
            m.Contains("currently the vote is failing") &&
            m.Contains("(1 yes, 1 no)"));
    }

    [Fact]
    public async Task VoteStatus_WhenVoteEndsEarly_DoesNotBroadcastPendingStatus()
    {
        var (service, tracker, messages, _) = CreateIsolatedSetup(timeoutSeconds: 21);
        tracker.ProcessLogLine("16:53:14 - Alice has joined.");
        tracker.ProcessLogLine("16:53:14 - Bob has joined.");
        tracker.ProcessLogLine("16:53:14 - Charlie has joined.");

        service.ProcessChatCommand("Alice", isBot: false, "!vote timeout_track 3");
        service.ProcessChatCommand("Bob", isBot: false, "!yes");
        await Task.Delay(1500);

        Assert.DoesNotContain(messages, m => m.Contains("20 seconds left for voting"));
    }

    [Fact]
    public async Task VoteApplied_SendsTrackAndLapSettingsWithoutEditingRotation()
    {
        JoinPlayer("Alice");
        JoinPlayer("Bob");

        SendChat("Alice", "!vote new_track 7"); // Alice auto-yes (1/2)
        SendChat("Bob", "!yes"); // Bob yes (2/2) → early majority
        await Task.Delay(100);

        _mockServerManager.Verify(m => m.SendCommandAsync("track=new_track"), Times.Once);
        _mockServerManager.Verify(m => m.SendCommandAsync("laps=7"), Times.Once);
        _mockConfigService.Verify(c => c.WriteEventLoopTracks(
            It.IsAny<string>(), It.IsAny<List<EventLoopTrack>>()), Times.Never);
    }

    [Fact]
    public async Task VoteApplied_WhenTrackCommandFails_DoesNotReportSuccess()
    {
        JoinPlayer("Alice");

        _mockServerManager
            .Setup(m => m.SendCommandAsync(It.IsAny<string>()))
            .Callback<string>(cmd => { if (cmd.StartsWith("/message ")) _broadcastMessages.Add(cmd[9..]); })
            .ReturnsAsync((string cmd) => cmd == "track=wrecknado_02"
                ? (false, "track failed")
                : (true, "ok"));

        SendChat("Alice", "!vote wrecknado_02 3");
        await Task.Delay(100);

        _mockServerManager.Verify(m => m.SendCommandAsync("track=wrecknado_02"), Times.Once);
        _mockServerManager.Verify(m => m.SendCommandAsync("laps=3"), Times.Never);
        Assert.DoesNotContain(_broadcastMessages, m => m.StartsWith("Next race:", StringComparison.Ordinal));
        Assert.Contains(_broadcastMessages, m => m.Contains("Failed to change track"));
    }

    [Fact]
    public async Task VoteCommand_ExactTrackName_StartsVoteForResolvedTrackId()
    {
        JoinPlayer("Alice");
        JoinPlayer("Bob");

        SendChat("Alice", "!vote New Track 7");
        SendChat("Bob", "!yes");
        await Task.Delay(100);

        _mockServerManager.Verify(m => m.SendCommandAsync("track=new_track"), Times.Once);
        _mockServerManager.Verify(m => m.SendCommandAsync("laps=7"), Times.Once);
    }

    [Fact]
    public async Task VoteCommand_AmbiguousTrackName_AsksForNumberedConfirmation()
    {
        var (service, _, messages, _) = CreateBirkelandSetup();

        service.ProcessChatCommand("Alice", isBot: false, "!vote Birkeland 1");
        await Task.Delay(50);

        Assert.Contains(messages, m => m.Contains("Multiple matches for 'Birkeland'"));
        Assert.Contains(messages, m => m.Contains("1. misc_birkeland - TVTP Misc Birkeland"));
        Assert.Contains(messages, m => m.Contains("2. misc_birkeland_reverse - TVTP Misc Birkeland Reverse"));
        Assert.Contains(messages, m => m.Contains("Type !confirm <number>"));
        Assert.DoesNotContain(messages, m => m.Contains("Vote started"));
    }

    [Fact]
    public async Task ConfirmCommand_StartsPendingVoteForSelectedOption()
    {
        var (service, tracker, messages, _) = CreateBirkelandSetup();
        tracker.ProcessLogLine("16:53:14 - Alice has joined.");
        tracker.ProcessLogLine("16:53:14 - Bob has joined.");

        service.ProcessChatCommand("Alice", isBot: false, "!vote Birkeland 1");
        service.ProcessChatCommand("Alice", isBot: false, "!confirm 2");
        service.ProcessChatCommand("Bob", isBot: false, "!yes");
        await Task.Delay(100);

        Assert.Contains(messages, m => m == "Vote: TVTP Misc Birkeland Reverse - 1 laps");
        Assert.Contains(messages, m => m == "By Alice. Type !yes or !no. Ends in 30s.");
    }

    [Fact]
    public async Task VoteCommand_MisspelledTrackName_OffersFuzzyConfirmationOptions()
    {
        var (service, _, messages, _) = CreateBirkelandSetup();

        service.ProcessChatCommand("Alice", isBot: false, "!vote Birkland 1");
        await Task.Delay(50);

        Assert.Contains(messages, m => m.Contains("Possible matches for 'Birkland'"));
        Assert.Contains(messages, m => m.Contains("misc_birkeland"));
        Assert.Contains(messages, m => m.Contains("Type !confirm <number>"));
        Assert.DoesNotContain(messages, m => m.Contains("Vote started"));
    }

    [Fact]
    public async Task NonBangMessage_DoesNotTriggerVote()
    {
        JoinPlayer("Alice");
        SendChat("Alice", "hello world");
        await Task.Delay(50);

        Assert.DoesNotContain(_broadcastMessages, m => m.Contains("Vote started"));
    }

    [Fact]
    public async Task VoteCommand_WithoutLaps_StartsVoteAndLeavesLapsUnchanged()
    {
        JoinPlayer("Alice");
        JoinPlayer("Bob");
        SendChat("Alice", "!vote wrecknado_02");
        await Task.Delay(50);

        // Laps are optional: the vote starts, and the started-vote line carries no
        // lap count because the server keeps whatever it already has.
        Assert.Contains(_broadcastMessages, m => m.Contains("Vote:", StringComparison.Ordinal));
        Assert.DoesNotContain(_broadcastMessages, m => m.Contains("Usage", StringComparison.Ordinal));
        Assert.DoesNotContain(_broadcastMessages, m => m.Contains(" laps", StringComparison.Ordinal));
    }

    [Fact]
    public async Task VoteCommand_WithOnlyANumber_SendsUsageMessage()
    {
        JoinPlayer("Alice");
        SendChat("Alice", "!vote 5");
        await Task.Delay(50);

        // A lone number is not a track query.
        Assert.Contains(_broadcastMessages, m => m.Contains("Usage", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InvalidVoteCommand_TrackNotAllowed_SendsShortSearchHint()
    {
        JoinPlayer("Alice");
        SendChat("Alice", "!vote unknown_track 5");
        await Task.Delay(50);

        Assert.Contains(_broadcastMessages, m =>
            m.Contains("unknown_track") && m.Contains("not allowed") && m.Contains("!search <text>"));
        Assert.DoesNotContain(_broadcastMessages, m => m.Contains("Allowed tracks:"));
        Assert.DoesNotContain(_broadcastMessages, m => m.Contains("Wrecknado"));
        Assert.DoesNotContain(_broadcastMessages, m => m.Contains("Vote started"));
    }

    [Fact]
    public async Task InvalidVoteCommand_LapsAboveMaximum_SendsAllowedLapRangeMessage()
    {
        JoinPlayer("Alice");
        SendChat("Alice", "!vote wrecknado_02 11");
        await Task.Delay(50);

        Assert.Contains(_broadcastMessages, m => m.Contains("between 1 and 10"));
        Assert.DoesNotContain(_broadcastMessages, m => m.Contains("Vote started"));
    }

    [Fact]
    public async Task SearchCommand_WithoutPattern_SendsUsageMessage()
    {
        SendChat("Alice", "!search");
        await Task.Delay(50);

        Assert.Contains(_broadcastMessages, m => m.Contains("Usage: !search <track name or id>"));
    }

    [Fact]
    public async Task SearchCommand_MatchesTrackNameAndIncludesVoteId()
    {
        SendChat("Alice", "!search wreck");
        await Task.Delay(50);

        Assert.Contains(_broadcastMessages, m =>
            m.Contains("Matches:") &&
            m.Contains("wrecknado_02"));
    }

    [Fact]
    public async Task SearchCommand_MatchesTrackIdCaseInsensitive()
    {
        SendChat("Alice", "!search NEW_TRACK");
        await Task.Delay(50);

        Assert.Contains(_broadcastMessages, m => m.Contains("new_track"));
    }

    [Fact]
    public async Task SearchCommand_WithNoMatches_SendsNoMatchesMessage()
    {
        SendChat("Alice", "!search not-a-track");
        await Task.Delay(50);

        Assert.Contains(_broadcastMessages, m => m.Contains("No tracks found matching 'not-a-track'"));
    }

    [Fact]
    public async Task SearchCommand_WithMoreThanFiveMatches_LimitsResultsAndReportsRemainingCount()
    {
        var (service, _, messages, _) = CreateSearchSetup();

        service.ProcessChatCommand("Alice", isBot: false, "!search circuit");
        await Task.Delay(50);

        var combined = string.Join(" ", messages);
        Assert.Contains("Matches", combined);
        Assert.Contains("track_1", combined);
        Assert.Contains("track_5", combined);
        Assert.DoesNotContain("track_6", combined);
        Assert.Contains("1 more", combined);
        Assert.Contains("!more", combined);
    }

    [Fact]
    public async Task SearchCommand_SplitsLongResultsIntoChatSafeMessages()
    {
        var (service, _, messages, _) = CreateSearchSetup(matchCount: 12);

        service.ProcessChatCommand("Alice", isBot: false, "!search circuit");
        await Task.Delay(50);

        Assert.True(messages.Count >= 2);
        Assert.All(messages, message => Assert.True(message.Length <= 110, message));
    }

    [Fact]
    public async Task SearchCommand_ReturnsOneTrackPerLineWithIdAndName()
    {
        var (service, _, messages, _) = CreateSearchSetup();

        service.ProcessChatCommand("Alice", isBot: false, "!search circuit");
        await Task.Delay(50);

        Assert.Contains(messages, m => m == "Matches: track_1 - Track 1 Circuit");
        Assert.Contains(messages, m => m == "Matches: track_5 - Track 5 Circuit");
    }

    [Fact]
    public async Task MoreCommand_AfterSearch_ReturnsNextBufferedPage()
    {
        var (service, _, messages, _) = CreateSearchSetup(matchCount: 12);

        service.ProcessChatCommand("Alice", isBot: false, "!search circuit");
        service.ProcessChatCommand("Alice", isBot: false, "!more");
        await Task.Delay(50);

        var combined = string.Join(" ", messages);
        Assert.Contains("More matches", combined);
        Assert.Contains("track_6", combined);
        Assert.Contains("track_10", combined);
        Assert.DoesNotContain("track_11", combined);
        Assert.Contains("2 more", combined);
        Assert.Contains("!more", combined);
    }

    [Fact]
    public async Task MoreCommand_WhenBufferRunsOut_ReturnsFinalPageWithoutMoreHint()
    {
        var (service, _, messages, _) = CreateSearchSetup(matchCount: 12);

        service.ProcessChatCommand("Alice", isBot: false, "!search circuit");
        service.ProcessChatCommand("Alice", isBot: false, "!more");
        service.ProcessChatCommand("Alice", isBot: false, "!more");
        await Task.Delay(50);

        Assert.Contains(messages, m => m == "More matches: track_11 - Track 11 Circuit");
        Assert.Contains(messages, m => m == "More matches: track_12 - Track 12 Circuit");
        Assert.DoesNotContain(messages.Skip(messages.Count - 2), m => m.Contains("!more"));
    }

    [Fact]
    public async Task MoreCommand_WithoutBufferedResults_SendsNoMoreMessage()
    {
        var (service, _, messages, _) = CreateSearchSetup(matchCount: 12);

        service.ProcessChatCommand("Alice", isBot: false, "!more");
        await Task.Delay(50);

        Assert.Contains(messages, m => m.Contains("No more search results"));
    }

    [Fact]
    public async Task MoreCommand_UsesLatestSharedSearchBuffer()
    {
        var (service, _, messages, _) = CreateSearchSetup(matchCount: 12);

        service.ProcessChatCommand("Alice", isBot: false, "!search circuit");
        service.ProcessChatCommand("Bob", isBot: false, "!more");
        await Task.Delay(50);

        Assert.Contains(messages, m =>
            m.Contains("More matches:") &&
            m.Contains("track_6"));
    }

    [Fact]
    public async Task HelpCommand_ListsCommandsAndMaxLaps()
    {
        SendChat("Alice", "!help");
        await Task.Delay(50);

        Assert.Contains(_broadcastMessages, m => m.Contains("max laps is 10"));
        Assert.Contains(_broadcastMessages, m => m.Contains("!track <trackId> [laps]") && m.Contains("Example: !track misc_bsv 6"));
        Assert.Contains(_broadcastMessages, m => m.Contains("!yes") && m.Contains("vote yes"));
        Assert.Contains(_broadcastMessages, m => m.Contains("!no") && m.Contains("vote no"));
        Assert.Contains(_broadcastMessages, m => m.Contains("!search <text>") && m.Contains("Example: !search tvtp misc"));
        Assert.Contains(_broadcastMessages, m => m.Contains("!more"));
        Assert.DoesNotContain(_broadcastMessages, m => m.Contains("!help"));
        Assert.DoesNotContain(_broadcastMessages, m => m.Contains("!config"));
        Assert.DoesNotContain(_broadcastMessages, m => m.Contains("!debug"));
        Assert.All(_broadcastMessages.Where(m => m.StartsWith("Help:", StringComparison.Ordinal)), m => Assert.True(m.Length <= 100));
    }

    [Fact]
    public async Task ConfigCommand_ShowsHookStatus()
    {
        SendChat("Alice", "!config");
        await Task.Delay(50);

        Assert.Contains(_broadcastMessages, m =>
            m.Contains("Config:", StringComparison.Ordinal) &&
            m.Contains("hookConnected=no", StringComparison.Ordinal) &&
            m.Contains("outputPrimary=no", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DebugCommand_ShowsLivePlayerAndVoteState()
    {
        _playerTracker.ProcessHookPlayerSnapshot([
            new Player { Name = "Alice", Slot = 1, IsBot = false, JoinedAt = DateTime.UtcNow },
            new Player { Name = "eRacer", Slot = 2, IsBot = true, JoinedAt = DateTime.UtcNow },
            new Player { Name = "BangerBot", Slot = 3, IsBot = true, JoinedAt = DateTime.UtcNow }
        ]);

        SendChat("Alice", "!debug");
        await Task.Delay(50);

        Assert.Contains(_broadcastMessages, m =>
            m.Contains("Debug:", StringComparison.Ordinal) &&
            m.Contains("humans=1", StringComparison.Ordinal) &&
            m.Contains("total=3", StringComparison.Ordinal) &&
            m.Contains("bots=2", StringComparison.Ordinal));
        Assert.Contains(_broadcastMessages, m =>
            m.Contains("humanPlayers=1:Alice", StringComparison.Ordinal));
        Assert.DoesNotContain(_broadcastMessages, m => m.Contains("eRacer"));
        Assert.DoesNotContain(_broadcastMessages, m => m.Contains("BangerBot"));
    }

    [Fact]
    public async Task DebugCommand_WhenSenderIsMissingFromTracker_CountsSenderAsHuman()
    {
        SendChat("Procat", "!debug");
        await Task.Delay(50);

        Assert.Contains(_broadcastMessages, m =>
            m.Contains("Debug:", StringComparison.Ordinal) &&
            m.Contains("humans=1", StringComparison.Ordinal) &&
            m.Contains("total=1", StringComparison.Ordinal) &&
            m.Contains("bots=0", StringComparison.Ordinal));
        Assert.Contains(_broadcastMessages, m =>
            m.Contains("humanPlayers=?:Procat", StringComparison.Ordinal));
    }

    [Fact]
    public async Task VoteCommand_WhenSenderIsOnlyKnownHuman_AppliesDirectlyWithoutAVote()
    {
        SendChat("Procat", "!vote wrecknado_02 3");
        await Task.Delay(50);

        _mockServerManager.Verify(m => m.SendCommandAsync("track=wrecknado_02"), Times.Once);
        _mockServerManager.Verify(m => m.SendCommandAsync("laps=3"), Times.Once);
        Assert.Contains(_broadcastMessages, m => m == "Next race: Wrecknado (3 laps)");
        Assert.DoesNotContain(_broadcastMessages, m => m.Contains("!yes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task VoteCommand_RefreshesPlayersFromHookBeforeMajorityCheck()
    {
        _mockServerManager
            .Setup(m => m.TryRefreshPlayersFromHookAsync())
            .Callback(() => _playerTracker.ProcessHookPlayerSnapshot([
                new Player { Name = "Procat", Slot = 1, IsBot = false, JoinedAt = DateTime.UtcNow },
                new Player { Name = "Bob", Slot = 2, IsBot = false, JoinedAt = DateTime.UtcNow },
                new Player { Name = "Charlie", Slot = 3, IsBot = false, JoinedAt = DateTime.UtcNow }
            ]))
            .ReturnsAsync(true);

        SendChat("Procat", "!vote wrecknado_02 3");
        await Task.Delay(50);

        _mockServerManager.Verify(m => m.TryRefreshPlayersFromHookAsync(), Times.Once);
        _mockServerManager.Verify(m => m.SendCommandAsync("track=wrecknado_02"), Times.Never);
        Assert.Contains(_broadcastMessages, m => m == "Vote: Wrecknado - 3 laps");
        Assert.DoesNotContain(_broadcastMessages, m => m.StartsWith("Vote passed!", StringComparison.Ordinal));
    }

    [Fact]
    public async Task VoteCommand_WhenHookRefreshReturnsEmptySnapshot_CancelsVote()
    {
        _mockServerManager
            .Setup(m => m.TryRefreshPlayersFromHookAsync())
            .Callback(() => _playerTracker.ProcessHookPlayerSnapshot([]))
            .ReturnsAsync(true);

        SendChat("Procat", "!vote wrecknado_02 3");
        await Task.Delay(50);

        _mockServerManager.Verify(m => m.TryRefreshPlayersFromHookAsync(), Times.Once);
        _mockServerManager.Verify(m => m.SendCommandAsync("track=wrecknado_02"), Times.Never);
        Assert.Contains(_broadcastMessages, m => m.Contains("no human players found"));
        Assert.DoesNotContain(_broadcastMessages, m => m == "Vote: Wrecknado - 3 laps");
    }

    [Fact]
    public async Task YesCommand_RefreshesPlayersFromHookBeforeEarlyMajorityCheck()
    {
        JoinPlayer("Procat");
        JoinPlayer("Bob");
        JoinPlayer("Charlie");

        _mockServerManager
            .SetupSequence(m => m.TryRefreshPlayersFromHookAsync())
            .ReturnsAsync(true)
            .Returns(() =>
            {
                _playerTracker.ProcessHookPlayerSnapshot([
                    new Player { Name = "Procat", Slot = 1, IsBot = false, JoinedAt = DateTime.UtcNow },
                    new Player { Name = "Bob", Slot = 2, IsBot = false, JoinedAt = DateTime.UtcNow }
                ]);
                return Task.FromResult(true);
            });

        SendChat("Procat", "!vote wrecknado_02 3");
        SendChat("Bob", "!yes");
        await Task.Delay(50);

        _mockServerManager.Verify(m => m.TryRefreshPlayersFromHookAsync(), Times.Exactly(2));
        _mockServerManager.Verify(m => m.SendCommandAsync("track=wrecknado_02"), Times.Once);
        _mockServerManager.Verify(m => m.SendCommandAsync("laps=3"), Times.Once);
    }

    [Fact]
    public async Task DebugCommand_DuringVote_ShowsActiveVoteCounts()
    {
        JoinPlayer("Alice");
        JoinPlayer("Bob");
        JoinPlayer("Charlie");

        SendChat("Alice", "!vote wrecknado_02 10");
        SendChat("Alice", "!debug");
        await Task.Delay(50);

        Assert.Contains(_broadcastMessages, m =>
            m.Contains("vote=active track=wrecknado_02 laps=10", StringComparison.Ordinal) &&
            m.Contains("yes=1", StringComparison.Ordinal) &&
            m.Contains("no=0", StringComparison.Ordinal));
        Assert.Contains(_broadcastMessages, m =>
            m.Contains("humans=3", StringComparison.Ordinal) &&
            m.Contains("total=3", StringComparison.Ordinal));
    }

    private (VotingService service, PlayerTracker tracker, List<string> messages, Mock<ConfigService> configMock)
        CreateIsolatedSetup(int timeoutSeconds, int messageDelayMs = 0)
    {
        var messages = new List<string>();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> {
                ["Vote:VoteTimeoutSeconds"] = timeoutSeconds.ToString(),
                ["Vote:MaxLapsAllowed"] = "10",
                ["Vote:MessageDelayMs"] = messageDelayMs.ToString(),
                ["Vote:AllowedTracks:0:Id"] = "timeout_track",
                ["Vote:AllowedTracks:0:Name"] = "Timeout Track",
                ["Vote:AllowedTracks:1:Id"] = "only_initiator_track",
                ["Vote:AllowedTracks:1:Name"] = "Only Initiator Track",
                ["Vote:AllowedTracks:2:Id"] = "tie_track",
                ["Vote:AllowedTracks:2:Name"] = "Tie Track"
            })
            .Build();

        var mockWebhook = new Mock<WreckfestWebWebhookService>(
            Mock.Of<ILogger<WreckfestWebWebhookService>>(),
            Mock.Of<IConfiguration>(),
            Mock.Of<HttpClient>());

        var tracker = new PlayerTracker(Mock.Of<ILogger<PlayerTracker>>(), mockWebhook.Object);

        var serverMock = new Mock<ServerManager>(
            Mock.Of<IConfiguration>(),
            Mock.Of<ILogger<ServerManager>>(),
            tracker,
            new TrackChangeTracker(Mock.Of<ILogger<TrackChangeTracker>>(), mockWebhook.Object),
            new ServerInfoTracker(Mock.Of<ILogger<ServerInfoTracker>>()),
            mockWebhook.Object,
            new Mock<ConsoleLogWebhookSender>(Mock.Of<HttpClient>(), Mock.Of<IConfiguration>(), Mock.Of<ILogger<ConsoleLogWebhookSender>>()).Object);

        serverMock
            .Setup(m => m.SendCommandAsync(It.IsAny<string>()))
            .Callback<string>(cmd => { if (cmd.StartsWith("/message ")) messages.Add(cmd[9..]); })
            .ReturnsAsync((true, "ok"));

        var configMock = new Mock<ConfigService>(
            Mock.Of<IConfiguration>(),
            Mock.Of<ILogger<ConfigService>>());
        configMock.Setup(c => c.GetCurrentCollectionName()).Returns("TestCollection");
        configMock.Setup(c => c.ReadEventLoopTracks()).Returns(new List<EventLoopTrack>
        {
            new() { Track = "existing_track", Laps = 5 }
        });

        var service = new VotingService(
            serverMock.Object, tracker, configMock.Object,
            Mock.Of<ILogger<VotingService>>(), config);

        return (service, tracker, messages, configMock);
    }

    private static IConfiguration CreateVoteConfig()
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vote:VoteTimeoutSeconds"] = "30",
                ["Vote:MaxLapsAllowed"] = "10",
                ["Vote:MessageDelayMs"] = "0",
                ["WreckfestServer:OutputMode"] = ServerOutputModes.InjectedHook,
                ["Vote:AllowedTracks:0:Id"] = "wrecknado_02",
                ["Vote:AllowedTracks:0:Name"] = "Wrecknado",
                ["Vote:AllowedTracks:1:Id"] = "new_track",
                ["Vote:AllowedTracks:1:Name"] = "New Track",
                ["Vote:AllowedTracks:2:Id"] = "other_track",
                ["Vote:AllowedTracks:2:Name"] = "Other Track"
            })
            .Build();
    }

    private (VotingService service, PlayerTracker tracker, List<string> messages, Mock<ConfigService> configMock)
        CreateSearchSetup(int matchCount = 6)
    {
        var values = new Dictionary<string, string?>
        {
            ["Vote:VoteTimeoutSeconds"] = "30",
            ["Vote:MaxLapsAllowed"] = "10",
            ["Vote:MessageDelayMs"] = "0"
        };

        for (var i = 1; i <= matchCount; i++)
        {
            values[$"Vote:AllowedTracks:{i - 1}:Id"] = $"track_{i}";
            values[$"Vote:AllowedTracks:{i - 1}:Name"] = $"Track {i} Circuit";
        }

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        var messages = new List<string>();
        var mockWebhook = new Mock<WreckfestWebWebhookService>(
            Mock.Of<ILogger<WreckfestWebWebhookService>>(),
            Mock.Of<IConfiguration>(),
            Mock.Of<HttpClient>());

        var tracker = new PlayerTracker(Mock.Of<ILogger<PlayerTracker>>(), mockWebhook.Object);
        var serverMock = new Mock<ServerManager>(
            Mock.Of<IConfiguration>(),
            Mock.Of<ILogger<ServerManager>>(),
            tracker,
            new TrackChangeTracker(Mock.Of<ILogger<TrackChangeTracker>>(), mockWebhook.Object),
            new ServerInfoTracker(Mock.Of<ILogger<ServerInfoTracker>>()),
            mockWebhook.Object,
            new Mock<ConsoleLogWebhookSender>(Mock.Of<HttpClient>(), Mock.Of<IConfiguration>(), Mock.Of<ILogger<ConsoleLogWebhookSender>>()).Object);

        serverMock
            .Setup(m => m.SendCommandAsync(It.IsAny<string>()))
            .Callback<string>(cmd => { if (cmd.StartsWith("/message ")) messages.Add(cmd[9..]); })
            .ReturnsAsync((true, "ok"));

        var configMock = new Mock<ConfigService>(
            Mock.Of<IConfiguration>(),
            Mock.Of<ILogger<ConfigService>>());

        var service = new VotingService(
            serverMock.Object,
            tracker,
            configMock.Object,
            Mock.Of<ILogger<VotingService>>(),
            config);

        return (service, tracker, messages, configMock);
    }

    private (VotingService service, PlayerTracker tracker, List<string> messages, Mock<ConfigService> configMock)
        CreateLongTrackNameSetup()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vote:VoteTimeoutSeconds"] = "30",
                ["Vote:MaxLapsAllowed"] = "99",
                ["Vote:MessageDelayMs"] = "0",
                ["Vote:AllowedTracks:0:Id"] = "long_track",
                ["Vote:AllowedTracks:0:Name"] = new string('A', 150)
            })
            .Build();

        var messages = new List<string>();
        var mockWebhook = new Mock<WreckfestWebWebhookService>(
            Mock.Of<ILogger<WreckfestWebWebhookService>>(),
            Mock.Of<IConfiguration>(),
            Mock.Of<HttpClient>());

        var tracker = new PlayerTracker(Mock.Of<ILogger<PlayerTracker>>(), mockWebhook.Object);
        var serverMock = new Mock<ServerManager>(
            Mock.Of<IConfiguration>(),
            Mock.Of<ILogger<ServerManager>>(),
            tracker,
            new TrackChangeTracker(Mock.Of<ILogger<TrackChangeTracker>>(), mockWebhook.Object),
            new ServerInfoTracker(Mock.Of<ILogger<ServerInfoTracker>>()),
            mockWebhook.Object,
            new Mock<ConsoleLogWebhookSender>(Mock.Of<HttpClient>(), Mock.Of<IConfiguration>(), Mock.Of<ILogger<ConsoleLogWebhookSender>>()).Object);

        serverMock
            .Setup(m => m.SendCommandAsync(It.IsAny<string>()))
            .Callback<string>(cmd => { if (cmd.StartsWith("/message ")) messages.Add(cmd[9..]); })
            .ReturnsAsync((true, "ok"));

        var configMock = new Mock<ConfigService>(
            Mock.Of<IConfiguration>(),
            Mock.Of<ILogger<ConfigService>>());

        var service = new VotingService(
            serverMock.Object,
            tracker,
            configMock.Object,
            Mock.Of<ILogger<VotingService>>(),
            config);

        return (service, tracker, messages, configMock);
    }

    private (VotingService service, PlayerTracker tracker, List<string> messages, Mock<ConfigService> configMock)
        CreateBirkelandSetup()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vote:VoteTimeoutSeconds"] = "30",
                ["Vote:MaxLapsAllowed"] = "10",
                ["Vote:MessageDelayMs"] = "0",
                ["Vote:AllowedTracks:0:Id"] = "misc_birkeland",
                ["Vote:AllowedTracks:0:Name"] = "TVTP Misc Birkeland",
                ["Vote:AllowedTracks:1:Id"] = "misc_birkeland_reverse",
                ["Vote:AllowedTracks:1:Name"] = "TVTP Misc Birkeland Reverse",
                ["Vote:AllowedTracks:2:Id"] = "misc_birkeland_barriers",
                ["Vote:AllowedTracks:2:Name"] = "TVTP Misc No Construction Fences",
                ["Vote:AllowedTracks:3:Id"] = "ovals_birkeland_oval01",
                ["Vote:AllowedTracks:3:Name"] = "TVTP Ovals Birkeland Oval"
            })
            .Build();

        var messages = new List<string>();
        var mockWebhook = new Mock<WreckfestWebWebhookService>(
            Mock.Of<ILogger<WreckfestWebWebhookService>>(),
            Mock.Of<IConfiguration>(),
            Mock.Of<HttpClient>());

        var tracker = new PlayerTracker(Mock.Of<ILogger<PlayerTracker>>(), mockWebhook.Object);
        var serverMock = new Mock<ServerManager>(
            Mock.Of<IConfiguration>(),
            Mock.Of<ILogger<ServerManager>>(),
            tracker,
            new TrackChangeTracker(Mock.Of<ILogger<TrackChangeTracker>>(), mockWebhook.Object),
            new ServerInfoTracker(Mock.Of<ILogger<ServerInfoTracker>>()),
            mockWebhook.Object,
            new Mock<ConsoleLogWebhookSender>(Mock.Of<HttpClient>(), Mock.Of<IConfiguration>(), Mock.Of<ILogger<ConsoleLogWebhookSender>>()).Object);

        serverMock
            .Setup(m => m.SendCommandAsync(It.IsAny<string>()))
            .Callback<string>(cmd => { if (cmd.StartsWith("/message ")) messages.Add(cmd[9..]); })
            .ReturnsAsync((true, "ok"));

        var configMock = new Mock<ConfigService>(
            Mock.Of<IConfiguration>(),
            Mock.Of<ILogger<ConfigService>>());

        var service = new VotingService(
            serverMock.Object,
            tracker,
            configMock.Object,
            Mock.Of<ILogger<VotingService>>(),
            config);

        return (service, tracker, messages, configMock);
    }

    private (VotingService service, PlayerTracker tracker, List<string> messages, Mock<ConfigService> configMock)
        CreateNoAllowedTracksSetup()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vote:VoteTimeoutSeconds"] = "30",
                ["Vote:MaxLapsAllowed"] = "10",
                ["Vote:MessageDelayMs"] = "0"
            })
            .Build();

        var messages = new List<string>();
        var mockWebhook = new Mock<WreckfestWebWebhookService>(
            Mock.Of<ILogger<WreckfestWebWebhookService>>(),
            Mock.Of<IConfiguration>(),
            Mock.Of<HttpClient>());

        var tracker = new PlayerTracker(Mock.Of<ILogger<PlayerTracker>>(), mockWebhook.Object);
        var serverMock = new Mock<ServerManager>(
            Mock.Of<IConfiguration>(),
            Mock.Of<ILogger<ServerManager>>(),
            tracker,
            new TrackChangeTracker(Mock.Of<ILogger<TrackChangeTracker>>(), mockWebhook.Object),
            new ServerInfoTracker(Mock.Of<ILogger<ServerInfoTracker>>()),
            mockWebhook.Object,
            new Mock<ConsoleLogWebhookSender>(Mock.Of<HttpClient>(), Mock.Of<IConfiguration>(), Mock.Of<ILogger<ConsoleLogWebhookSender>>()).Object);

        serverMock
            .Setup(m => m.SendCommandAsync(It.IsAny<string>()))
            .Callback<string>(cmd => { if (cmd.StartsWith("/message ")) messages.Add(cmd[9..]); })
            .ReturnsAsync((true, "ok"));

        var configMock = new Mock<ConfigService>(
            Mock.Of<IConfiguration>(),
            Mock.Of<ILogger<ConfigService>>());

        var service = new VotingService(
            serverMock.Object,
            tracker,
            configMock.Object,
            Mock.Of<ILogger<VotingService>>(),
            config);

        return (service, tracker, messages, configMock);
    }

    private (VotingService service, PlayerTracker tracker, List<string> messages, Mock<ConfigService> configMock)
        CreateDisabledVotingSetup()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vote:Enabled"] = "false",
                ["Vote:VoteTimeoutSeconds"] = "30",
                ["Vote:MaxLapsAllowed"] = "10",
                ["Vote:MessageDelayMs"] = "0",
                ["Vote:AllowedTracks:0:Id"] = "wrecknado_02",
                ["Vote:AllowedTracks:0:Name"] = "Wrecknado"
            })
            .Build();

        var messages = new List<string>();
        var mockWebhook = new Mock<WreckfestWebWebhookService>(
            Mock.Of<ILogger<WreckfestWebWebhookService>>(),
            Mock.Of<IConfiguration>(),
            Mock.Of<HttpClient>());

        var tracker = new PlayerTracker(Mock.Of<ILogger<PlayerTracker>>(), mockWebhook.Object);
        var serverMock = new Mock<ServerManager>(
            Mock.Of<IConfiguration>(),
            Mock.Of<ILogger<ServerManager>>(),
            tracker,
            new TrackChangeTracker(Mock.Of<ILogger<TrackChangeTracker>>(), mockWebhook.Object),
            new ServerInfoTracker(Mock.Of<ILogger<ServerInfoTracker>>()),
            mockWebhook.Object,
            new Mock<ConsoleLogWebhookSender>(Mock.Of<HttpClient>(), Mock.Of<IConfiguration>(), Mock.Of<ILogger<ConsoleLogWebhookSender>>()).Object);

        serverMock
            .Setup(m => m.SendCommandAsync(It.IsAny<string>()))
            .Callback<string>(cmd => { if (cmd.StartsWith("/message ")) messages.Add(cmd[9..]); })
            .ReturnsAsync((true, "ok"));

        var configMock = new Mock<ConfigService>(
            Mock.Of<IConfiguration>(),
            Mock.Of<ILogger<ConfigService>>());

        var service = new VotingService(
            serverMock.Object,
            tracker,
            configMock.Object,
            Mock.Of<ILogger<VotingService>>(),
            config);

        return (service, tracker, messages, configMock);
    }

    private (VotingService service, PlayerTracker tracker, List<string> messages,
             Mock<ServerManager> serverMock, IConfigurationRoot config)
        CreateModeSetup(string mode)
    {
        var values = new Dictionary<string, string?>
        {
            ["Vote:Mode"] = mode,
            ["Vote:VoteTimeoutSeconds"] = "30",
            ["Vote:MaxLapsAllowed"] = "10",
            ["Vote:MessageDelayMs"] = "0",
            ["Vote:DirectCooldownSeconds"] = "30",
            ["Vote:AllowedTracks:0:Id"] = "wrecknado_02",
            ["Vote:AllowedTracks:0:Name"] = "Wrecknado",
            ["Vote:AllowedTracks:1:Id"] = "wrecknado_03",
            ["Vote:AllowedTracks:1:Name"] = "Wrecknado Reverse"
        };

        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var messages = new List<string>();
        var mockWebhook = new Mock<WreckfestWebWebhookService>(
            Mock.Of<ILogger<WreckfestWebWebhookService>>(),
            Mock.Of<IConfiguration>(),
            Mock.Of<HttpClient>());

        var tracker = new PlayerTracker(Mock.Of<ILogger<PlayerTracker>>(), mockWebhook.Object);
        var serverMock = new Mock<ServerManager>(
            Mock.Of<IConfiguration>(),
            Mock.Of<ILogger<ServerManager>>(),
            tracker,
            new TrackChangeTracker(Mock.Of<ILogger<TrackChangeTracker>>(), mockWebhook.Object),
            new ServerInfoTracker(Mock.Of<ILogger<ServerInfoTracker>>()),
            mockWebhook.Object,
            new Mock<ConsoleLogWebhookSender>(Mock.Of<HttpClient>(), Mock.Of<IConfiguration>(), Mock.Of<ILogger<ConsoleLogWebhookSender>>()).Object);

        serverMock
            .Setup(m => m.SendCommandAsync(It.IsAny<string>()))
            .Callback<string>(cmd => { if (cmd.StartsWith("/message ")) messages.Add(cmd[9..]); })
            .ReturnsAsync((true, "ok"));

        var service = new VotingService(
            serverMock.Object,
            tracker,
            new Mock<ConfigService>(Mock.Of<IConfiguration>(), Mock.Of<ILogger<ConfigService>>()).Object,
            Mock.Of<ILogger<VotingService>>(),
            config);

        return (service, tracker, messages, serverMock, config);
    }

    private static void Join(PlayerTracker tracker, string name) =>
        tracker.ProcessLogLine($"16:53:14 - {name} has joined.");

    // --- aliasing -----------------------------------------------------------

    [Fact]
    public async Task TrackCommand_IsAliasOfVote_InVotingMode()
    {
        var (service, tracker, messages, _, _) = CreateModeSetup(VoteModes.Voting);
        Join(tracker, "Alice");
        Join(tracker, "Bob");

        service.ProcessChatCommand("Alice", false, "!track wrecknado_02 10");
        await Task.Delay(50);

        Assert.Contains(messages, m => m == "Vote: Wrecknado - 10 laps");
        Assert.Contains(messages, m => m.StartsWith("By Alice.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TrackCommand_IsGatedInOffMode()
    {
        var (service, tracker, messages, serverMock, _) = CreateModeSetup(VoteModes.Off);
        Join(tracker, "Alice");

        service.ProcessChatCommand("Alice", false, "!track wrecknado_02 10");
        await Task.Delay(50);

        Assert.Contains(messages, m => m.Contains("disabled", StringComparison.OrdinalIgnoreCase));
        serverMock.Verify(m => m.SendCommandAsync("track=wrecknado_02"), Times.Never);
    }

    // --- direct mode --------------------------------------------------------

    [Fact]
    public async Task DirectMode_AppliesImmediately_WithoutStartingAVote()
    {
        var (service, tracker, messages, serverMock, _) = CreateModeSetup(VoteModes.Direct);
        Join(tracker, "Alice");
        Join(tracker, "Bob");

        service.ProcessChatCommand("Alice", false, "!track wrecknado_02 6");
        await Task.Delay(50);

        serverMock.Verify(m => m.SendCommandAsync("track=wrecknado_02"), Times.Once);
        serverMock.Verify(m => m.SendCommandAsync("laps=6"), Times.Once);
        Assert.DoesNotContain(messages, m => m.Contains("!yes", StringComparison.Ordinal));
        // Two humans online, so the change is attributed.
        Assert.Contains(messages, m => m == "Next race: Wrecknado (6 laps) - set by Alice");
    }

    [Fact]
    public async Task DirectMode_WithoutLaps_SendsNoLapsCommand()
    {
        var (service, tracker, _, serverMock, _) = CreateModeSetup(VoteModes.Direct);
        Join(tracker, "Alice");
        Join(tracker, "Bob");

        service.ProcessChatCommand("Alice", false, "!track wrecknado_02");
        await Task.Delay(50);

        serverMock.Verify(m => m.SendCommandAsync("track=wrecknado_02"), Times.Once);
        serverMock.Verify(m => m.SendCommandAsync(It.Is<string>(c => c.StartsWith("laps="))), Times.Never);
    }

    [Fact]
    public async Task DirectMode_RejectsLapsAboveMaximum()
    {
        var (service, tracker, messages, serverMock, _) = CreateModeSetup(VoteModes.Direct);
        Join(tracker, "Alice");

        service.ProcessChatCommand("Alice", false, "!track wrecknado_02 99");
        await Task.Delay(50);

        Assert.Contains(messages, m => m.Contains("Invalid laps", StringComparison.Ordinal));
        serverMock.Verify(m => m.SendCommandAsync("track=wrecknado_02"), Times.Never);
    }

    [Fact]
    public async Task DirectMode_AmbiguousQuery_AppliesOnConfirm()
    {
        var (service, tracker, messages, serverMock, _) = CreateModeSetup(VoteModes.Direct);
        Join(tracker, "Alice");
        Join(tracker, "Bob");

        // "wreckn" is a substring of both allowed tracks but equals neither name.
        service.ProcessChatCommand("Alice", false, "!track wreckn 4");
        await Task.Delay(50);

        Assert.Contains(messages, m => m.Contains("!confirm", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("change track", StringComparison.Ordinal));
        serverMock.Verify(m => m.SendCommandAsync(It.Is<string>(c => c.StartsWith("track="))), Times.Never);

        service.ProcessChatCommand("Alice", false, "!confirm 1");
        await Task.Delay(50);

        serverMock.Verify(m => m.SendCommandAsync(It.Is<string>(c => c.StartsWith("track="))), Times.Once);
        serverMock.Verify(m => m.SendCommandAsync("laps=4"), Times.Once);
    }

    [Fact]
    public async Task DirectMode_LuckyCommand_AppliesImmediately()
    {
        var (service, tracker, messages, serverMock, _) = CreateModeSetup(VoteModes.Direct);
        Join(tracker, "Alice");
        Join(tracker, "Bob");

        service.ProcessChatCommand("Alice", false, "!lucky");
        await Task.Delay(50);

        serverMock.Verify(m => m.SendCommandAsync(It.Is<string>(c => c.StartsWith("track="))), Times.Once);
        Assert.DoesNotContain(messages, m => m.Contains("!yes", StringComparison.Ordinal));
    }

    // --- direct-mode cooldown ----------------------------------------------

    [Fact]
    public async Task DirectMode_SecondChangeWithinCooldown_IsRefusedWithSecondsRemaining()
    {
        var (service, tracker, messages, serverMock, _) = CreateModeSetup(VoteModes.Direct);
        Join(tracker, "Alice");
        Join(tracker, "Bob");

        service.ProcessChatCommand("Alice", false, "!track wrecknado_02 4");
        await Task.Delay(50);
        messages.Clear();

        service.ProcessChatCommand("Bob", false, "!track wrecknado_03 4");
        await Task.Delay(50);

        var refusal = Assert.Single(messages);
        Assert.StartsWith("Bob: track was just changed by Alice.", refusal);
        Assert.Matches(@"Try again in \d+s\.$", refusal);
        Assert.DoesNotContain("in 0s", refusal);
        serverMock.Verify(m => m.SendCommandAsync("track=wrecknado_03"), Times.Never);
    }

    [Fact]
    public async Task DirectMode_RepeatBySamePlayer_SaysYouRatherThanTheirName()
    {
        var (service, tracker, messages, _, _) = CreateModeSetup(VoteModes.Direct);
        Join(tracker, "Alice");
        Join(tracker, "Bob");

        service.ProcessChatCommand("Alice", false, "!track wrecknado_02 4");
        await Task.Delay(50);
        messages.Clear();

        service.ProcessChatCommand("Alice", false, "!track wrecknado_03 4");
        await Task.Delay(50);

        Assert.StartsWith("Alice: you just changed the track.", Assert.Single(messages));
    }

    [Fact]
    public async Task DirectMode_SoloHuman_IsNeverRateLimited()
    {
        var (service, tracker, _, serverMock, _) = CreateModeSetup(VoteModes.Direct);
        Join(tracker, "Alice");

        service.ProcessChatCommand("Alice", false, "!track wrecknado_02 4");
        await Task.Delay(50);
        service.ProcessChatCommand("Alice", false, "!track wrecknado_03 4");
        await Task.Delay(50);

        // Nobody to fight with, so back-to-back changes are fine.
        serverMock.Verify(m => m.SendCommandAsync("track=wrecknado_02"), Times.Once);
        serverMock.Verify(m => m.SendCommandAsync("track=wrecknado_03"), Times.Once);
    }

    [Fact]
    public async Task DirectMode_AdminBypassesCooldownAndOverridesAnotherPlayer()
    {
        var (service, tracker, _, serverMock, _) = CreateModeSetup(VoteModes.Direct);
        Join(tracker, "Alice");
        Join(tracker, "Bob");
        Join(tracker, "Admin");
        tracker.GetPlayers().Single(p => p.Name == "Admin").IsAdmin = true;

        service.ProcessChatCommand("Alice", false, "!track wrecknado_02 4");
        await Task.Delay(50);

        service.ProcessChatCommand("Admin", false, "!track wrecknado_03 4");
        await Task.Delay(50);

        serverMock.Verify(m => m.SendCommandAsync("track=wrecknado_03"), Times.Once);
    }

    [Fact]
    public async Task DirectMode_ZeroCooldown_DisablesTheLimit()
    {
        var (service, tracker, _, serverMock, config) = CreateModeSetup(VoteModes.Direct);
        config["Vote:DirectCooldownSeconds"] = "0";
        Join(tracker, "Alice");
        Join(tracker, "Bob");

        service.ProcessChatCommand("Alice", false, "!track wrecknado_02 4");
        await Task.Delay(50);
        service.ProcessChatCommand("Bob", false, "!track wrecknado_03 4");
        await Task.Delay(50);

        serverMock.Verify(m => m.SendCommandAsync("track=wrecknado_03"), Times.Once);
    }

    [Fact]
    public async Task DirectMode_FailedApply_DoesNotConsumeTheCooldownWindow()
    {
        var (service, tracker, _, serverMock, _) = CreateModeSetup(VoteModes.Direct);
        Join(tracker, "Alice");
        Join(tracker, "Bob");

        serverMock
            .Setup(m => m.SendCommandAsync("track=wrecknado_02"))
            .ReturnsAsync((false, "server said no"));

        service.ProcessChatCommand("Alice", false, "!track wrecknado_02 4");
        await Task.Delay(50);

        // Alice's attempt failed, so Bob must not be locked out by it.
        service.ProcessChatCommand("Bob", false, "!track wrecknado_03 4");
        await Task.Delay(50);

        serverMock.Verify(m => m.SendCommandAsync("track=wrecknado_03"), Times.Once);
    }

    // --- live config-reload interlocks --------------------------------------

    [Fact]
    public async Task ActiveVote_IsCancelled_WhenModeLeavesVoting()
    {
        var (service, tracker, messages, serverMock, config) = CreateModeSetup(VoteModes.Voting);
        Join(tracker, "Alice");
        Join(tracker, "Bob");
        Join(tracker, "Carol");

        service.ProcessChatCommand("Alice", false, "!track wrecknado_02 4");
        await Task.Delay(50);
        messages.Clear();

        // Configuration is re-read per access, so this takes effect immediately.
        config["Vote:Mode"] = VoteModes.Off;

        service.ProcessChatCommand("Bob", false, "!yes");
        await Task.Delay(50);

        Assert.Contains(messages, m => m.Contains("Vote cancelled", StringComparison.Ordinal));
        serverMock.Verify(m => m.SendCommandAsync("track=wrecknado_02"), Times.Never);
    }

    [Fact]
    public async Task SwitchingToDirectMidVote_RetiresTheVoteThenAppliesTheChange()
    {
        var (service, tracker, messages, serverMock, config) = CreateModeSetup(VoteModes.Voting);
        Join(tracker, "Alice");
        Join(tracker, "Bob");
        Join(tracker, "Carol");

        service.ProcessChatCommand("Alice", false, "!track wrecknado_02 4");
        await Task.Delay(50);

        config["Vote:Mode"] = VoteModes.Direct;
        messages.Clear();

        service.ProcessChatCommand("Bob", false, "!track wrecknado_03 4");
        await Task.Delay(50);

        // The orphaned vote is retired first - leaving it running would let its timer
        // apply a track change under a mode that no longer votes - and only then does
        // the direct change go through.
        Assert.Contains(messages, m => m.Contains("Vote cancelled", StringComparison.Ordinal));
        serverMock.Verify(m => m.SendCommandAsync("track=wrecknado_02"), Times.Never);
        serverMock.Verify(m => m.SendCommandAsync("track=wrecknado_03"), Times.Once);
    }

    [Fact]
    public async Task ConfigCommand_ReportsMode_AndCooldownInDirectMode()
    {
        var (service, tracker, messages, _, _) = CreateModeSetup(VoteModes.Direct);
        Join(tracker, "Alice");

        service.ProcessChatCommand("Alice", false, "!config");
        await Task.Delay(50);

        Assert.Contains(messages, m => m.Contains("mode=direct", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("cooldown=30s", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HelpCommand_InDirectMode_AdvertisesImmediateChangeNotVoting()
    {
        var (service, tracker, messages, _, _) = CreateModeSetup(VoteModes.Direct);
        Join(tracker, "Alice");

        service.ProcessChatCommand("Alice", false, "!help");
        await Task.Delay(50);

        Assert.Contains(messages, m => m.Contains("change the track now", StringComparison.Ordinal));
        Assert.DoesNotContain(messages, m => m.Contains("vote yes", StringComparison.Ordinal));
    }

    // --- early termination on majority --------------------------------------

    [Fact]
    public async Task TwoNoVotesOfThreePlayers_EndsVoteEarlyAsFailed()
    {
        var (service, tracker, messages, serverMock, _) = CreateModeSetup(VoteModes.Voting);
        Join(tracker, "Alice");
        Join(tracker, "Bob");
        Join(tracker, "Carol");

        // Alice starts the vote and is auto-counted as a yes, so yes=1.
        service.ProcessChatCommand("Alice", false, "!track wrecknado_02 4");
        await Task.Delay(50);

        service.ProcessChatCommand("Bob", false, "!no");
        await Task.Delay(50);
        Assert.DoesNotContain(messages, m => m.Contains("Vote failed", StringComparison.Ordinal));

        // no=2 of 3 online is a strict majority, so this ends it without waiting
        // for the 30s timeout.
        service.ProcessChatCommand("Carol", false, "!no");
        await Task.Delay(50);

        Assert.Contains(messages, m => m.Contains("Vote failed: majority voted no", StringComparison.Ordinal));
        serverMock.Verify(m => m.SendCommandAsync("track=wrecknado_02"), Times.Never);
    }

    /// <summary>
    /// The initiator is auto-counted as a yes, so passing needs one fewer vote than
    /// blocking does (at 3 players: one ally passes it, but blocking needs everyone
    /// else). That asymmetry is intentional - the initiator has already stated a
    /// preference and it keeps rounds moving - not an off-by-one.
    /// </summary>
    [Fact]
    public async Task InitiatorAutoYesPlusOneVote_IsEnoughToPassWithThreePlayers()
    {
        var (service, tracker, messages, serverMock, _) = CreateModeSetup(VoteModes.Voting);
        Join(tracker, "Alice");
        Join(tracker, "Bob");
        Join(tracker, "Carol");

        service.ProcessChatCommand("Alice", false, "!track wrecknado_02 4");
        await Task.Delay(50);

        // Alice's auto-yes plus Bob's makes yes=2 of 3 - a strict majority.
        service.ProcessChatCommand("Bob", false, "!yes");
        await Task.Delay(50);

        Assert.Contains(messages, m => m.Contains("Vote passed", StringComparison.Ordinal));
        serverMock.Verify(m => m.SendCommandAsync("track=wrecknado_02"), Times.Once);
    }

    [Fact]
    public async Task VoteEndedEarly_IgnoresLateVotes()
    {
        var (service, tracker, messages, _, _) = CreateModeSetup(VoteModes.Voting);
        Join(tracker, "Alice");
        Join(tracker, "Bob");
        Join(tracker, "Carol");

        service.ProcessChatCommand("Alice", false, "!track wrecknado_02 4");
        await Task.Delay(50);
        service.ProcessChatCommand("Bob", false, "!no");
        service.ProcessChatCommand("Carol", false, "!no");
        await Task.Delay(50);
        messages.Clear();

        // The vote is over; a straggler must not restart or re-tally it.
        service.ProcessChatCommand("Bob", false, "!yes");
        await Task.Delay(50);

        Assert.Empty(messages);
    }

    // --- server-state gates -------------------------------------------------

    private const uint RvaEventLoopCount = 0x1857630;
    private const uint RvaEventLoopIndex = 0x122B270;
    private const uint RvaSessionLobby = 0x19146E0;
    private const uint RvaSessionRacing = 0x19146EC;

    /// <summary>
    /// Stubs the hook memory reads. Values match what a live server returns:
    /// index -1 means the event loop is off; lobby/racing are the byte pair
    /// observed while driving.
    /// </summary>
    private static void StubServerState(Mock<ServerManager> server, int count, int index, bool racing)
    {
        server.Setup(m => m.ReadHookMemoryAsync(RvaEventLoopCount, 4)).ReturnsAsync(BitConverter.GetBytes(count));
        server.Setup(m => m.ReadHookMemoryAsync(RvaEventLoopIndex, 4)).ReturnsAsync(BitConverter.GetBytes(index));
        server.Setup(m => m.ReadHookMemoryAsync(RvaSessionLobby, 1)).ReturnsAsync([(byte)(racing ? 0 : 1)]);
        server.Setup(m => m.ReadHookMemoryAsync(RvaSessionRacing, 1)).ReturnsAsync([(byte)(racing ? 1 : 0)]);
    }

    [Fact]
    public async Task ChatCommands_AreSuppressedDuringARace()
    {
        var (service, tracker, messages, serverMock, _) = CreateModeSetup(VoteModes.Direct);
        StubServerState(serverMock, count: 4, index: -1, racing: true);
        Join(tracker, "Alice");

        service.ProcessChatCommand("Alice", false, "!help");
        await Task.Delay(80);

        Assert.DoesNotContain(messages, m => m.StartsWith("Help:", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("disabled during a race", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RaceRefusal_IsRateLimited_SoItDoesNotBecomeTheSpam()
    {
        var (service, tracker, messages, serverMock, _) = CreateModeSetup(VoteModes.Direct);
        StubServerState(serverMock, count: 4, index: -1, racing: true);
        Join(tracker, "Alice");

        for (var i = 0; i < 4; i++)
        {
            service.ProcessChatCommand("Alice", false, "!help");
            await Task.Delay(40);
        }

        Assert.Single(messages, m => m.Contains("disabled during a race", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChatCommands_WorkWhenNotRacing()
    {
        var (service, tracker, messages, serverMock, _) = CreateModeSetup(VoteModes.Direct);
        StubServerState(serverMock, count: 4, index: -1, racing: false);
        Join(tracker, "Alice");

        service.ProcessChatCommand("Alice", false, "!help");
        await Task.Delay(80);

        Assert.Contains(messages, m => m.StartsWith("Help:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TrackChange_IsRefused_WhileEventLoopIsRunning()
    {
        var (service, tracker, messages, serverMock, _) = CreateModeSetup(VoteModes.Direct);
        StubServerState(serverMock, count: 4, index: 0, racing: false);   // index 0 => enabled
        Join(tracker, "Alice");

        service.ProcessChatCommand("Alice", false, "!track wrecknado_02 4");
        await Task.Delay(80);

        Assert.Contains(messages, m => m.Contains("event loop is running", StringComparison.Ordinal));
        serverMock.Verify(m => m.SendCommandAsync("track=wrecknado_02"), Times.Never);
    }

    [Fact]
    public async Task TrackChange_IsAllowed_WhenEventLoopIsOff()
    {
        var (service, tracker, _, serverMock, _) = CreateModeSetup(VoteModes.Direct);
        StubServerState(serverMock, count: 4, index: -1, racing: false);  // index -1 => off
        Join(tracker, "Alice");

        service.ProcessChatCommand("Alice", false, "!track wrecknado_02 4");
        await Task.Delay(80);

        serverMock.Verify(m => m.SendCommandAsync("track=wrecknado_02"), Times.Once);
    }

    // --- !eventloop ---------------------------------------------------------

    [Fact]
    public async Task EventLoopCommand_IsSilentForUnprivilegedPlayers()
    {
        var (service, tracker, messages, serverMock, _) = CreateModeSetup(VoteModes.Direct);
        StubServerState(serverMock, count: 4, index: 0, racing: false);
        Join(tracker, "Alice");

        service.ProcessChatCommand("Alice", false, "!eventloop");
        await Task.Delay(80);

        // Hidden from !help, so answering back would advertise that it exists.
        Assert.Empty(messages);
    }

    [Fact]
    public async Task EventLoopCommand_ShowsStatusForModerators()
    {
        var (service, tracker, messages, serverMock, _) = CreateModeSetup(VoteModes.Direct);
        StubServerState(serverMock, count: 4, index: 2, racing: false);
        Join(tracker, "Mod");
        tracker.GetPlayers().Single(p => p.Name == "Mod").IsModerator = true;

        service.ProcessChatCommand("Mod", false, "!eventloop");
        await Task.Delay(80);

        Assert.Contains(messages, m => m == "Event loop: on (entry 3/4)");
    }

    [Fact]
    public async Task EventLoopCommand_SaysSoWhenAlreadyInRequestedState()
    {
        var (service, tracker, messages, serverMock, _) = CreateModeSetup(VoteModes.Direct);
        StubServerState(serverMock, count: 4, index: 0, racing: false);   // already on
        Join(tracker, "Admin");
        tracker.GetPlayers().Single(p => p.Name == "Admin").IsAdmin = true;

        service.ProcessChatCommand("Admin", false, "!eventloop on");
        await Task.Delay(80);

        Assert.Contains(messages, m => m.Contains("already on", StringComparison.Ordinal));
        serverMock.Verify(m => m.SendCommandAsync("/eventloop"), Times.Never);
    }

    [Fact]
    public async Task EventLoopCommand_ReportsWhenToggleDidNotTakeEffect()
    {
        var (service, tracker, messages, serverMock, _) = CreateModeSetup(VoteModes.Direct);
        // Reads always say "on", so asking for off must detect that nothing changed
        // rather than claiming success.
        StubServerState(serverMock, count: 4, index: 0, racing: false);
        Join(tracker, "Admin");
        tracker.GetPlayers().Single(p => p.Name == "Admin").IsAdmin = true;

        service.ProcessChatCommand("Admin", false, "!eventloop off");
        // The toggle is polled for up to 8 x 250ms before giving up.
        await Task.Delay(2600);

        serverMock.Verify(m => m.SendCommandAsync("/eventloop"), Times.Once);
        Assert.Contains(messages, m => m.Contains("did not change", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PrivilegedCommand_WorksForAnAdminKnownOnlyFromAJoinLine()
    {
        // Regression: a join line carries no role, so an admin who has just connected
        // looks unprivileged until a hook snapshot lands. The privilege check must
        // refresh first, or the command is silently dropped.
        var (service, tracker, messages, serverMock, _) = CreateModeSetup(VoteModes.Direct);
        StubServerState(serverMock, count: 4, index: 2, racing: false);

        // Tracker knows the player only from the join line - no role information.
        Join(tracker, "Admin");
        Assert.False(tracker.GetPlayers().Single(p => p.Name == "Admin").IsPrivileged);

        // The hook snapshot is where the role actually comes from.
        serverMock.Setup(m => m.TryRefreshPlayersFromHookAsync())
            .Callback(() => tracker.ProcessHookPlayerSnapshot([
                new Player { Name = "Admin", Slot = 1, IsBot = false, IsAdmin = true, JoinedAt = DateTime.UtcNow }
            ]))
            .ReturnsAsync(true);

        service.ProcessChatCommand("Admin", false, "!eventloop");
        await Task.Delay(120);

        Assert.Contains(messages, m => m.StartsWith("Event loop:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EventLoopCommand_SucceedsWhenTheToggleLandsAfterAShortDelay()
    {
        var (service, tracker, messages, serverMock, _) = CreateModeSetup(VoteModes.Direct);
        Join(tracker, "Admin");
        tracker.GetPlayers().Single(p => p.Name == "Admin").IsAdmin = true;

        // Starts enabled (index 0); the game applies the toggle a beat after the
        // command returns, which an immediate read-back would miss.
        var index = 0;
        serverMock.Setup(m => m.ReadHookMemoryAsync(RvaEventLoopCount, 4)).ReturnsAsync(BitConverter.GetBytes(4));
        serverMock.Setup(m => m.ReadHookMemoryAsync(RvaEventLoopIndex, 4))
            .ReturnsAsync(() => BitConverter.GetBytes(index));
        serverMock.Setup(m => m.ReadHookMemoryAsync(RvaSessionLobby, 1)).ReturnsAsync([(byte)1]);
        serverMock.Setup(m => m.ReadHookMemoryAsync(RvaSessionRacing, 1)).ReturnsAsync([(byte)0]);

        serverMock.Setup(m => m.SendCommandAsync("/eventloop"))
            .Callback(() => _ = Task.Run(async () => { await Task.Delay(400); index = -1; }))
            .ReturnsAsync((true, "ok"));

        service.ProcessChatCommand("Admin", false, "!eventloop off");
        await Task.Delay(2000);

        Assert.Contains(messages, m => m == "Event loop: off (4 entries)");
        Assert.DoesNotContain(messages, m => m.Contains("did not change", StringComparison.Ordinal));
    }
}
