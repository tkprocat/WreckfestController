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
            new Mock<ConsoleMonitor>(Mock.Of<ILogger<ConsoleMonitor>>()).Object,
            new Mock<ConsoleWriter>(Mock.Of<ILogger<ConsoleWriter>>()).Object,
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
        JoinPlayer("Alice");
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
    public async Task SecondVote_WhileActiveVote_SendsAlreadyInProgressMessage()
    {
        JoinPlayer("Alice");
        JoinPlayer("Bob");
        SendChat("Alice", "!vote wrecknado_02 10");
        SendChat("Bob", "!vote other_track 5");
        await Task.Delay(50);

        Assert.Contains(_broadcastMessages, m => m.Contains("already in progress"));
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
    public async Task VoteStarted_WhenOnlyInitiatorOnline_PassesImmediately()
    {
        JoinPlayer("Alice");

        SendChat("Alice", "!vote wrecknado_02 10");
        await Task.Delay(100);

        _mockServerManager.Verify(m => m.SendCommandAsync("track=wrecknado_02"), Times.Once);
        _mockServerManager.Verify(m => m.SendCommandAsync("laps=10"), Times.Once);
        Assert.Contains(_broadcastMessages, m => m.Contains("Vote passed"));
    }

    [Fact]
    public async Task VoteStarted_WhenOnlyInitiatorOnline_SendsBothStartLinesBeforePassedMessage()
    {
        var (service, tracker, messages, _) = CreateIsolatedSetup(timeoutSeconds: 30, messageDelayMs: 50);
        tracker.ProcessLogLine("16:53:14 - Alice has joined.");

        service.ProcessChatCommand("Alice", isBot: false, "!vote timeout_track 3");
        await Task.Delay(250);

        Assert.True(messages.Count >= 3);
        Assert.Equal("Vote: Timeout Track - 3 laps", messages[0]);
        Assert.Equal("By Alice. Type !yes or !no. Ends in 30s.", messages[1]);
        Assert.Equal("Vote passed! Next race: timeout_track for 3 laps.", messages[2]);
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
    public async Task VoteTimeout_OnlyInitiatorVoted_PassesVote()
    {
        var (service, tracker, messages, configMock) = CreateIsolatedSetup(timeoutSeconds: 1);
        tracker.ProcessLogLine("16:53:14 - Alice has joined.");
        tracker.ProcessLogLine("16:53:14 - Bob has joined.");
        service.ProcessChatCommand("Bob", isBot: false, "!vote only_initiator_track 3");
        // Only Bob auto-votes yes (1 yes, 0 no) → yes > no at timeout

        await Task.Delay(1500);

        configMock.Verify(c => c.WriteEventLoopTracks(
            It.IsAny<string>(), It.IsAny<List<EventLoopTrack>>()), Times.Never);
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
    public async Task InvalidVoteCommand_MissingLaps_SendsUsageMessage()
    {
        JoinPlayer("Alice");
        SendChat("Alice", "!vote wrecknado_02");
        await Task.Delay(50);

        Assert.Contains(_broadcastMessages, m => m.Contains("Usage"));
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
        Assert.Contains(_broadcastMessages, m => m.Contains("!vote <trackId> <laps>") && m.Contains("Example: !vote misc_bsv 6"));
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
    public async Task ConfigCommand_ShowsConfiguredModesAndHookStatus()
    {
        SendChat("Alice", "!config");
        await Task.Delay(50);

        Assert.Contains(_broadcastMessages, m =>
            m.Contains("Config:", StringComparison.Ordinal) &&
            m.Contains("input=InjectedHook", StringComparison.Ordinal) &&
            m.Contains("output=InjectedHook", StringComparison.Ordinal));
        Assert.Contains(_broadcastMessages, m =>
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
    public async Task VoteCommand_WhenSenderIsOnlyKnownHuman_PassesImmediately()
    {
        SendChat("Procat", "!vote wrecknado_02 3");
        await Task.Delay(50);

        _mockServerManager.Verify(m => m.SendCommandAsync("track=wrecknado_02"), Times.Once);
        _mockServerManager.Verify(m => m.SendCommandAsync("laps=3"), Times.Once);
        Assert.Contains(_broadcastMessages, m => m == "Vote passed! Next race: wrecknado_02 for 3 laps.");
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
            new Mock<ConsoleMonitor>(Mock.Of<ILogger<ConsoleMonitor>>()).Object,
            new Mock<ConsoleWriter>(Mock.Of<ILogger<ConsoleWriter>>()).Object,
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
                ["WreckfestServer:InputMode"] = ServerInputModes.InjectedHook,
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
            new Mock<ConsoleMonitor>(Mock.Of<ILogger<ConsoleMonitor>>()).Object,
            new Mock<ConsoleWriter>(Mock.Of<ILogger<ConsoleWriter>>()).Object,
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
            new Mock<ConsoleMonitor>(Mock.Of<ILogger<ConsoleMonitor>>()).Object,
            new Mock<ConsoleWriter>(Mock.Of<ILogger<ConsoleWriter>>()).Object,
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
            new Mock<ConsoleMonitor>(Mock.Of<ILogger<ConsoleMonitor>>()).Object,
            new Mock<ConsoleWriter>(Mock.Of<ILogger<ConsoleWriter>>()).Object,
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
            new Mock<ConsoleMonitor>(Mock.Of<ILogger<ConsoleMonitor>>()).Object,
            new Mock<ConsoleWriter>(Mock.Of<ILogger<ConsoleWriter>>()).Object,
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
            new Mock<ConsoleMonitor>(Mock.Of<ILogger<ConsoleMonitor>>()).Object,
            new Mock<ConsoleWriter>(Mock.Of<ILogger<ConsoleWriter>>()).Object,
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
}
