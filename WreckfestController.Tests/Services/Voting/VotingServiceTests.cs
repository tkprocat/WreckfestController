using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using WreckfestController.Models;
using Xunit;
using WreckfestController.Services.Config;
using WreckfestController.Services.Hook;
using WreckfestController.Services.Publishing;
using WreckfestController.Services.ServerControl;
using WreckfestController.Services.Tracking;
using WreckfestController.Tests.Services.Config;
using WreckfestController.Tests.Services.Tracking;
using WreckfestController.Services.Voting;

namespace WreckfestController.Tests.Services.Voting;

public class VotingServiceTests
{
    // Every chat command arrives under an attachment session. One stands in for a
    // server attached for the whole test; the mocked dispatch accepts any session.
    private static readonly AttachmentSession TestSession = new(1, 4242, CancellationToken.None);

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

        var mockEvents = new Mock<IServerEventPublisher>();

        _playerTracker = new PlayerTracker(
            Mock.Of<ILogger<PlayerTracker>>(),
            mockEvents.Object);

        _mockServerManager = new Mock<ServerManager>(
            Mock.Of<IConfiguration>(), TestSettings.Server(), TestSettings.SteamCmd(),
            Mock.Of<ILogger<ServerManager>>(),
            _playerTracker,
            new TrackChangeTracker(Mock.Of<ILogger<TrackChangeTracker>>(), mockEvents.Object),
            new ServerInfoTracker(Mock.Of<ILogger<ServerInfoTracker>>()),
            mockEvents.Object);

        _mockServerManager
            .Setup(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), It.IsAny<string>()))
            .Callback<AttachmentSession?, string>((_, cmd) => { if (cmd.StartsWith("/message ")) _broadcastMessages.Add(cmd[9..]); })
            .ReturnsAsync((true, "ok"));

        _mockConfigService = new Mock<ConfigService>(
           TestSettings.Server(),
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
            new ConfiguredVoteSettings(_config), new ConfiguredVotableTracks(_config));
    }

    private void SendChat(string playerName, string message, bool isBot = false)
    {
        _votingService.ProcessChatCommand(TestSession, playerName, isBot, message);
    }

    private void JoinPlayer(string name) =>
        _playerTracker.Seed(name);

    [Fact]
    public async Task VoteStarted_BroadcastsAnnouncementAndAutoVotesYes()
    {
        // Two humans: with only one online the vote is skipped entirely.
        JoinPlayer("Alice");
        JoinPlayer("Bob");
        SendChat("Alice", "!vote wrecknado_02 10");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Contains(_broadcastMessages, m => m == "Vote: Wrecknado - 10 laps");
        Assert.Contains(_broadcastMessages, m => m == "By Alice. Type !yes or !no. Ends in 30s.");
        Assert.DoesNotContain(_broadcastMessages, m => m.StartsWith("Vote started", StringComparison.Ordinal));
    }

    [Fact]
    public async Task VoteStarted_WithLongTrackName_TruncatesFirstLineToChatLimit()
    {
        var (service, tracker, messages, _) = CreateLongTrackNameSetup();
        tracker.Seed("Alice", "Bob");

        service.ProcessChatCommand(TestSession, "Alice", isBot: false, "!vote long_track 99");
        await Task.Delay(50, TestContext.Current.CancellationToken);

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
        await Task.Delay(50, TestContext.Current.CancellationToken);

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
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Contains(_broadcastMessages, m => m.Contains("vote in progress", StringComparison.Ordinal));
        Assert.DoesNotContain(_broadcastMessages, m => m.Contains("is not allowed for voting", StringComparison.Ordinal));
    }

    [Fact]
    public async Task VoteCommand_FromBot_Ignored()
    {
        SendChat("BotPlayer", "!vote wrecknado_02 10", isBot: true);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(_broadcastMessages, m => m.Contains("Vote started"));
    }

    [Fact]
    public async Task VoteCommand_WhenVotingDisabled_SendsDisabledMessageAndDoesNotStartVote()
    {
        var (service, _, messages, configMock) = CreateDisabledVotingSetup();

        service.ProcessChatCommand(TestSession, "Alice", isBot: false, "!vote wrecknado_02 5");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Contains(messages, m => m.Contains("Voting is currently disabled"));
        Assert.DoesNotContain(messages, m => m.Contains("Vote started"));
        configMock.Verify(c => c.WriteEventLoopTracks(
            It.IsAny<string>(), It.IsAny<List<EventLoopTrack>>()), Times.Never);
    }

    [Fact]
    public async Task SearchCommand_WhenVotingDisabled_SendsDisabledMessage()
    {
        var (service, _, messages, _) = CreateDisabledVotingSetup();

        service.ProcessChatCommand(TestSession, "Alice", isBot: false, "!search wreck");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Contains(messages, m => m.Contains("Voting is currently disabled"));
        Assert.DoesNotContain(messages, m => m.Contains("Matches:"));
    }

    [Fact]
    public async Task MoreCommand_WhenVotingDisabled_SendsDisabledMessage()
    {
        var (service, _, messages, _) = CreateDisabledVotingSetup();

        service.ProcessChatCommand(TestSession, "Alice", isBot: false, "!more");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Contains(messages, m => m.Contains("Voting is currently disabled"));
        Assert.DoesNotContain(messages, m => m.Contains("No more search results"));
    }

    [Fact]
    public async Task HelpCommand_WhenVotingDisabled_ReportsDisabled()
    {
        var (service, _, messages, _) = CreateDisabledVotingSetup();

        service.ProcessChatCommand(TestSession, "Alice", isBot: false, "!help");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Contains(messages, m => m.Contains("Voting is currently disabled"));
    }

    [Fact]
    public async Task YesVote_DuplicateVoter_SendsDuplicateMessage()
    {
        JoinPlayer("Alice");
        JoinPlayer("Bob");
        SendChat("Alice", "!vote wrecknado_02 5");
        SendChat("Alice", "!yes"); // Alice already auto-voted yes
        await Task.Delay(50, TestContext.Current.CancellationToken);

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
        await Task.Delay(100, TestContext.Current.CancellationToken);

        _mockServerManager.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), "track=wrecknado_02"), Times.Once);
        _mockServerManager.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), "laps=10"), Times.Once);
        _mockConfigService.Verify(c => c.WriteEventLoopTracks(
            It.IsAny<string>(), It.IsAny<List<EventLoopTrack>>()), Times.Never);

        Assert.Contains(_broadcastMessages, m => m.Contains("Vote passed"));
    }

    [Fact]
    public async Task VoteStarted_WhenOnlyInitiatorOnline_AppliesDirectly()
    {
        JoinPlayer("Alice");

        SendChat("Alice", "!vote wrecknado_02 10");
        await Task.Delay(100, TestContext.Current.CancellationToken);

        _mockServerManager.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), "track=wrecknado_02"), Times.Once);
        _mockServerManager.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), "laps=10"), Times.Once);
        Assert.Contains(_broadcastMessages, m => m.StartsWith("Next race:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task VoteStarted_WhenOnlyInitiatorOnline_SendsOnlyTheResultLine()
    {
        var (service, tracker, messages, _) = CreateIsolatedSetup(timeoutSeconds: 30, messageDelayMs: 50);
        tracker.Seed("Alice");

        service.ProcessChatCommand(TestSession, "Alice", isBot: false, "!vote timeout_track 3");
        await Task.Delay(250, TestContext.Current.CancellationToken);

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
        await Task.Delay(100, TestContext.Current.CancellationToken);

        _mockServerManager.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), It.IsRegex("^track=(wrecknado_02|new_track|other_track)$")), Times.Once);
        _mockServerManager.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), It.IsRegex("^laps=([1-9]|10)$")), Times.Once);
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
        await Task.Delay(100, TestContext.Current.CancellationToken);

        _mockServerManager.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), It.IsRegex("^track=(wrecknado_02|new_track|other_track)$")), Times.Once);
        _mockServerManager.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), It.IsRegex("^laps=([1-9]|10)$")), Times.Once);
        Assert.Contains(_broadcastMessages, m => m.Contains("Lucky pick:"));
    }

    [Fact]
    public async Task LuckyCommand_WhenNoAllowedTracksConfigured_SendsWarning()
    {
        var (service, _, messages, _) = CreateNoAllowedTracksSetup();

        service.ProcessChatCommand(TestSession, "Alice", isBot: false, "!ifeellucky");
        await Task.Delay(50, TestContext.Current.CancellationToken);

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
        await Task.Delay(100, TestContext.Current.CancellationToken);

        _mockConfigService.Verify(c => c.WriteEventLoopTracks(
            It.IsAny<string>(), It.IsAny<List<EventLoopTrack>>()), Times.Never);

        Assert.Contains(_broadcastMessages, m => m.Contains("majority voted no"));
    }

    [Fact]
    public async Task VoteTimeout_YesLeads_PassesVote()
    {
        var (service, tracker, messages, configMock) = CreateIsolatedSetup(timeoutSeconds: 1);
        tracker.Seed("Alice", "Bob");
        service.ProcessChatCommand(TestSession, "Alice", isBot: false, "!vote timeout_track 3");

        await Task.Delay(1500, TestContext.Current.CancellationToken);

        configMock.Verify(c => c.WriteEventLoopTracks(
            It.IsAny<string>(), It.IsAny<List<EventLoopTrack>>()), Times.Never);
    }

    [Fact]
    public async Task VoteTimeout_OnlyInitiatorVoted_PassesVote()
    {
        var (service, tracker, messages, configMock) = CreateIsolatedSetup(timeoutSeconds: 1);
        tracker.Seed("Alice", "Bob");
        service.ProcessChatCommand(TestSession, "Bob", isBot: false, "!vote only_initiator_track 3");
        // Bob's auto-yes is the only cast vote; Alice abstains.

        await Task.Delay(1500, TestContext.Current.CancellationToken);

        configMock.Verify(c => c.WriteEventLoopTracks(
            It.IsAny<string>(), It.IsAny<List<EventLoopTrack>>()), Times.Never);
        Assert.Contains(messages, m => m.Contains("Vote passed"));
    }

    [Fact]
    public async Task VoteTimeout_Tied_FailsVote()
    {
        var (service, tracker, messages, configMock) = CreateIsolatedSetup(timeoutSeconds: 1);
        tracker.Seed("Alice", "Bob");
        service.ProcessChatCommand(TestSession, "Alice", isBot: false, "!vote tie_track 3"); // Alice auto-yes
        service.ProcessChatCommand(TestSession, "Bob", isBot: false, "!no"); // 1 yes, 1 no → tie

        await Task.Delay(1500, TestContext.Current.CancellationToken);

        configMock.Verify(c => c.WriteEventLoopTracks(
            It.IsAny<string>(), It.IsAny<List<EventLoopTrack>>()), Times.Never);

        Assert.Contains(messages, m => m.Contains("not enough yes votes"));
    }

    [Fact]
    public async Task VoteStatus_WhenTwentySecondsRemain_BroadcastsPassingStatus()
    {
        var (service, tracker, messages, _) = CreateIsolatedSetup(timeoutSeconds: 21);
        tracker.Seed("Alice", "Bob");

        service.ProcessChatCommand(TestSession, "Alice", isBot: false, "!vote timeout_track 3");
        await Task.Delay(1500, TestContext.Current.CancellationToken);

        Assert.Contains(messages, m =>
            m.Contains("20 seconds left for voting") &&
            m.Contains("currently the vote is passing") &&
            m.Contains("(1 yes, 0 no)"));
    }

    [Fact]
    public async Task VoteStatus_WhenTenSecondsRemain_BroadcastsFailingStatus()
    {
        var (service, tracker, messages, _) = CreateIsolatedSetup(timeoutSeconds: 11);
        tracker.Seed("Alice", "Bob");

        service.ProcessChatCommand(TestSession, "Alice", isBot: false, "!vote timeout_track 3");
        service.ProcessChatCommand(TestSession, "Bob", isBot: false, "!no");
        await Task.Delay(1500, TestContext.Current.CancellationToken);

        Assert.Contains(messages, m =>
            m.Contains("10 seconds left for voting") &&
            m.Contains("currently the vote is failing") &&
            m.Contains("(1 yes, 1 no)"));
    }

    [Fact]
    public async Task VoteStatus_WhenVoteEndsEarly_DoesNotBroadcastPendingStatus()
    {
        var (service, tracker, messages, _) = CreateIsolatedSetup(timeoutSeconds: 21);
        tracker.Seed("Alice", "Bob", "Charlie");

        service.ProcessChatCommand(TestSession, "Alice", isBot: false, "!vote timeout_track 3");
        service.ProcessChatCommand(TestSession, "Bob", isBot: false, "!yes");
        await Task.Delay(1500, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(messages, m => m.Contains("20 seconds left for voting"));
    }

    [Fact]
    public async Task VoteApplied_SendsTrackAndLapSettingsWithoutEditingRotation()
    {
        JoinPlayer("Alice");
        JoinPlayer("Bob");

        SendChat("Alice", "!vote new_track 7"); // Alice auto-yes (1/2)
        SendChat("Bob", "!yes"); // Bob yes (2/2) → early majority
        await Task.Delay(100, TestContext.Current.CancellationToken);

        _mockServerManager.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), "track=new_track"), Times.Once);
        _mockServerManager.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), "laps=7"), Times.Once);
        _mockConfigService.Verify(c => c.WriteEventLoopTracks(
            It.IsAny<string>(), It.IsAny<List<EventLoopTrack>>()), Times.Never);
    }

    [Fact]
    public async Task VoteApplied_WhenTrackCommandFails_DoesNotReportSuccess()
    {
        JoinPlayer("Alice");

        _mockServerManager
            .Setup(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), It.IsAny<string>()))
            .Callback<AttachmentSession?, string>((_, cmd) => { if (cmd.StartsWith("/message ")) _broadcastMessages.Add(cmd[9..]); })
            .ReturnsAsync((AttachmentSession? _, string cmd) => cmd == "track=wrecknado_02"
                ? (false, "track failed")
                : (true, "ok"));

        SendChat("Alice", "!vote wrecknado_02 3");
        await Task.Delay(100, TestContext.Current.CancellationToken);

        _mockServerManager.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), "track=wrecknado_02"), Times.Once);
        _mockServerManager.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), "laps=3"), Times.Never);
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
        await Task.Delay(100, TestContext.Current.CancellationToken);

        _mockServerManager.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), "track=new_track"), Times.Once);
        _mockServerManager.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), "laps=7"), Times.Once);
    }

    [Fact]
    public async Task VoteCommand_AmbiguousTrackName_AsksForNumberedConfirmation()
    {
        var (service, _, messages, _) = CreateBirkelandSetup();

        service.ProcessChatCommand(TestSession, "Alice", isBot: false, "!vote Birkeland 1");
        await Task.Delay(50, TestContext.Current.CancellationToken);

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
        tracker.Seed("Alice", "Bob");

        service.ProcessChatCommand(TestSession, "Alice", isBot: false, "!vote Birkeland 1");
        service.ProcessChatCommand(TestSession, "Alice", isBot: false, "!confirm 2");
        service.ProcessChatCommand(TestSession, "Bob", isBot: false, "!yes");
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Contains(messages, m => m == "Vote: TVTP Misc Birkeland Reverse - 1 laps");
        Assert.Contains(messages, m => m == "By Alice. Type !yes or !no. Ends in 30s.");
    }

    [Fact]
    public async Task VoteCommand_MisspelledTrackName_OffersFuzzyConfirmationOptions()
    {
        var (service, _, messages, _) = CreateBirkelandSetup();

        service.ProcessChatCommand(TestSession, "Alice", isBot: false, "!vote Birkland 1");
        await Task.Delay(50, TestContext.Current.CancellationToken);

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
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(_broadcastMessages, m => m.Contains("Vote started"));
    }

    [Fact]
    public async Task VoteCommand_WithoutLaps_StartsVoteAndLeavesLapsUnchanged()
    {
        JoinPlayer("Alice");
        JoinPlayer("Bob");
        SendChat("Alice", "!vote wrecknado_02");
        await Task.Delay(50, TestContext.Current.CancellationToken);

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
        await Task.Delay(50, TestContext.Current.CancellationToken);

        // A lone number is not a track query.
        Assert.Contains(_broadcastMessages, m => m.Contains("Usage", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InvalidVoteCommand_TrackNotAllowed_SendsShortSearchHint()
    {
        JoinPlayer("Alice");
        SendChat("Alice", "!vote unknown_track 5");
        await Task.Delay(50, TestContext.Current.CancellationToken);

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
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Contains(_broadcastMessages, m => m.Contains("between 1 and 10"));
        Assert.DoesNotContain(_broadcastMessages, m => m.Contains("Vote started"));
    }

    [Fact]
    public async Task SearchCommand_WithoutPattern_SendsUsageMessage()
    {
        SendChat("Alice", "!search");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Contains(_broadcastMessages, m => m.Contains("Usage: !search <track name or id>"));
    }

    [Fact]
    public async Task SearchCommand_MatchesTrackNameAndIncludesVoteId()
    {
        SendChat("Alice", "!search wreck");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Contains(_broadcastMessages, m =>
            m.Contains("Matches:") &&
            m.Contains("wrecknado_02"));
    }

    [Fact]
    public async Task SearchCommand_MatchesTrackIdCaseInsensitive()
    {
        SendChat("Alice", "!search NEW_TRACK");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Contains(_broadcastMessages, m => m.Contains("new_track"));
    }

    [Fact]
    public async Task SearchCommand_WithNoMatches_SendsNoMatchesMessage()
    {
        SendChat("Alice", "!search not-a-track");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Contains(_broadcastMessages, m => m.Contains("No tracks found matching 'not-a-track'"));
    }

    [Fact]
    public async Task SearchCommand_WithMoreThanFiveMatches_LimitsResultsAndReportsRemainingCount()
    {
        var (service, _, messages, _) = CreateSearchSetup();

        service.ProcessChatCommand(TestSession, "Alice", isBot: false, "!search circuit");
        await Task.Delay(50, TestContext.Current.CancellationToken);

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

        service.ProcessChatCommand(TestSession, "Alice", isBot: false, "!search circuit");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.True(messages.Count >= 2);
        Assert.All(messages, message => Assert.True(message.Length <= 110, message));
    }

    [Fact]
    public async Task SearchCommand_ReturnsOneTrackPerLineWithIdAndName()
    {
        var (service, _, messages, _) = CreateSearchSetup();

        service.ProcessChatCommand(TestSession, "Alice", isBot: false, "!search circuit");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Contains(messages, m => m == "Matches: track_1 - Track 1 Circuit");
        Assert.Contains(messages, m => m == "Matches: track_5 - Track 5 Circuit");
    }

    [Fact]
    public async Task MoreCommand_AfterSearch_ReturnsNextBufferedPage()
    {
        var (service, _, messages, _) = CreateSearchSetup(matchCount: 12);

        service.ProcessChatCommand(TestSession, "Alice", isBot: false, "!search circuit");
        service.ProcessChatCommand(TestSession, "Alice", isBot: false, "!more");
        await Task.Delay(50, TestContext.Current.CancellationToken);

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

        service.ProcessChatCommand(TestSession, "Alice", isBot: false, "!search circuit");
        service.ProcessChatCommand(TestSession, "Alice", isBot: false, "!more");
        service.ProcessChatCommand(TestSession, "Alice", isBot: false, "!more");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Contains(messages, m => m == "More matches: track_11 - Track 11 Circuit");
        Assert.Contains(messages, m => m == "More matches: track_12 - Track 12 Circuit");
        Assert.DoesNotContain(messages.Skip(messages.Count - 2), m => m.Contains("!more"));
    }

    [Fact]
    public async Task MoreCommand_WithoutBufferedResults_SendsNoMoreMessage()
    {
        var (service, _, messages, _) = CreateSearchSetup(matchCount: 12);

        service.ProcessChatCommand(TestSession, "Alice", isBot: false, "!more");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Contains(messages, m => m.Contains("No more search results"));
    }

    [Fact]
    public async Task MoreCommand_UsesLatestSharedSearchBuffer()
    {
        var (service, _, messages, _) = CreateSearchSetup(matchCount: 12);

        service.ProcessChatCommand(TestSession, "Alice", isBot: false, "!search circuit");
        service.ProcessChatCommand(TestSession, "Bob", isBot: false, "!more");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Contains(messages, m =>
            m.Contains("More matches:") &&
            m.Contains("track_6"));
    }

    [Fact]
    public async Task HelpCommand_ListsCommandsAndMaxLaps()
    {
        SendChat("Alice", "!help");
        await Task.Delay(50, TestContext.Current.CancellationToken);

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
        await Task.Delay(50, TestContext.Current.CancellationToken);

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
        await Task.Delay(50, TestContext.Current.CancellationToken);

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
        await Task.Delay(50, TestContext.Current.CancellationToken);

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
        await Task.Delay(50, TestContext.Current.CancellationToken);

        _mockServerManager.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), "track=wrecknado_02"), Times.Once);
        _mockServerManager.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), "laps=3"), Times.Once);
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
        await Task.Delay(50, TestContext.Current.CancellationToken);

        _mockServerManager.Verify(m => m.TryRefreshPlayersFromHookAsync(), Times.Once);
        _mockServerManager.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), "track=wrecknado_02"), Times.Never);
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
        await Task.Delay(50, TestContext.Current.CancellationToken);

        _mockServerManager.Verify(m => m.TryRefreshPlayersFromHookAsync(), Times.Once);
        _mockServerManager.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), "track=wrecknado_02"), Times.Never);
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
        await Task.Delay(50, TestContext.Current.CancellationToken);

        _mockServerManager.Verify(m => m.TryRefreshPlayersFromHookAsync(), Times.Exactly(2));
        _mockServerManager.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), "track=wrecknado_02"), Times.Once);
        _mockServerManager.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), "laps=3"), Times.Once);
    }

    [Fact]
    public async Task DebugCommand_DuringVote_ShowsActiveVoteCounts()
    {
        JoinPlayer("Alice");
        JoinPlayer("Bob");
        JoinPlayer("Charlie");

        SendChat("Alice", "!vote wrecknado_02 10");
        SendChat("Alice", "!debug");
        await Task.Delay(50, TestContext.Current.CancellationToken);

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

        var mockEvents = new Mock<IServerEventPublisher>();

        var tracker = new PlayerTracker(Mock.Of<ILogger<PlayerTracker>>(), mockEvents.Object);

        var serverMock = new Mock<ServerManager>(
            Mock.Of<IConfiguration>(), TestSettings.Server(), TestSettings.SteamCmd(),
            Mock.Of<ILogger<ServerManager>>(),
            tracker,
            new TrackChangeTracker(Mock.Of<ILogger<TrackChangeTracker>>(), mockEvents.Object),
            new ServerInfoTracker(Mock.Of<ILogger<ServerInfoTracker>>()),
            mockEvents.Object);

        serverMock
            .Setup(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), It.IsAny<string>()))
            .Callback<AttachmentSession?, string>((_, cmd) => { if (cmd.StartsWith("/message ")) messages.Add(cmd[9..]); })
            .ReturnsAsync((true, "ok"));

        var configMock = new Mock<ConfigService>(
           TestSettings.Server(),
            Mock.Of<ILogger<ConfigService>>());
        configMock.Setup(c => c.GetCurrentCollectionName()).Returns("TestCollection");
        configMock.Setup(c => c.ReadEventLoopTracks()).Returns(new List<EventLoopTrack>
        {
            new() { Track = "existing_track", Laps = 5 }
        });

        var service = new VotingService(
            serverMock.Object, tracker, configMock.Object,
            Mock.Of<ILogger<VotingService>>(), new ConfiguredVoteSettings(config), new ConfiguredVotableTracks(config));

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
        var mockEvents = new Mock<IServerEventPublisher>();

        var tracker = new PlayerTracker(Mock.Of<ILogger<PlayerTracker>>(), mockEvents.Object);
        var serverMock = new Mock<ServerManager>(
            Mock.Of<IConfiguration>(), TestSettings.Server(), TestSettings.SteamCmd(),
            Mock.Of<ILogger<ServerManager>>(),
            tracker,
            new TrackChangeTracker(Mock.Of<ILogger<TrackChangeTracker>>(), mockEvents.Object),
            new ServerInfoTracker(Mock.Of<ILogger<ServerInfoTracker>>()),
            mockEvents.Object);

        serverMock
            .Setup(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), It.IsAny<string>()))
            .Callback<AttachmentSession?, string>((_, cmd) => { if (cmd.StartsWith("/message ")) messages.Add(cmd[9..]); })
            .ReturnsAsync((true, "ok"));

        var configMock = new Mock<ConfigService>(
           TestSettings.Server(),
            Mock.Of<ILogger<ConfigService>>());

        var service = new VotingService(
            serverMock.Object,
            tracker,
            configMock.Object,
            Mock.Of<ILogger<VotingService>>(),
            new ConfiguredVoteSettings(config), new ConfiguredVotableTracks(config));

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
        var mockEvents = new Mock<IServerEventPublisher>();

        var tracker = new PlayerTracker(Mock.Of<ILogger<PlayerTracker>>(), mockEvents.Object);
        var serverMock = new Mock<ServerManager>(
            Mock.Of<IConfiguration>(), TestSettings.Server(), TestSettings.SteamCmd(),
            Mock.Of<ILogger<ServerManager>>(),
            tracker,
            new TrackChangeTracker(Mock.Of<ILogger<TrackChangeTracker>>(), mockEvents.Object),
            new ServerInfoTracker(Mock.Of<ILogger<ServerInfoTracker>>()),
            mockEvents.Object);

        serverMock
            .Setup(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), It.IsAny<string>()))
            .Callback<AttachmentSession?, string>((_, cmd) => { if (cmd.StartsWith("/message ")) messages.Add(cmd[9..]); })
            .ReturnsAsync((true, "ok"));

        var configMock = new Mock<ConfigService>(
           TestSettings.Server(),
            Mock.Of<ILogger<ConfigService>>());

        var service = new VotingService(
            serverMock.Object,
            tracker,
            configMock.Object,
            Mock.Of<ILogger<VotingService>>(),
            new ConfiguredVoteSettings(config), new ConfiguredVotableTracks(config));

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
        var mockEvents = new Mock<IServerEventPublisher>();

        var tracker = new PlayerTracker(Mock.Of<ILogger<PlayerTracker>>(), mockEvents.Object);
        var serverMock = new Mock<ServerManager>(
            Mock.Of<IConfiguration>(), TestSettings.Server(), TestSettings.SteamCmd(),
            Mock.Of<ILogger<ServerManager>>(),
            tracker,
            new TrackChangeTracker(Mock.Of<ILogger<TrackChangeTracker>>(), mockEvents.Object),
            new ServerInfoTracker(Mock.Of<ILogger<ServerInfoTracker>>()),
            mockEvents.Object);

        serverMock
            .Setup(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), It.IsAny<string>()))
            .Callback<AttachmentSession?, string>((_, cmd) => { if (cmd.StartsWith("/message ")) messages.Add(cmd[9..]); })
            .ReturnsAsync((true, "ok"));

        var configMock = new Mock<ConfigService>(
           TestSettings.Server(),
            Mock.Of<ILogger<ConfigService>>());

        var service = new VotingService(
            serverMock.Object,
            tracker,
            configMock.Object,
            Mock.Of<ILogger<VotingService>>(),
            new ConfiguredVoteSettings(config), new ConfiguredVotableTracks(config));

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
        var mockEvents = new Mock<IServerEventPublisher>();

        var tracker = new PlayerTracker(Mock.Of<ILogger<PlayerTracker>>(), mockEvents.Object);
        var serverMock = new Mock<ServerManager>(
            Mock.Of<IConfiguration>(), TestSettings.Server(), TestSettings.SteamCmd(),
            Mock.Of<ILogger<ServerManager>>(),
            tracker,
            new TrackChangeTracker(Mock.Of<ILogger<TrackChangeTracker>>(), mockEvents.Object),
            new ServerInfoTracker(Mock.Of<ILogger<ServerInfoTracker>>()),
            mockEvents.Object);

        serverMock
            .Setup(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), It.IsAny<string>()))
            .Callback<AttachmentSession?, string>((_, cmd) => { if (cmd.StartsWith("/message ")) messages.Add(cmd[9..]); })
            .ReturnsAsync((true, "ok"));

        var configMock = new Mock<ConfigService>(
           TestSettings.Server(),
            Mock.Of<ILogger<ConfigService>>());

        var service = new VotingService(
            serverMock.Object,
            tracker,
            configMock.Object,
            Mock.Of<ILogger<VotingService>>(),
            new ConfiguredVoteSettings(config), new ConfiguredVotableTracks(config));

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
        var mockEvents = new Mock<IServerEventPublisher>();

        var tracker = new PlayerTracker(Mock.Of<ILogger<PlayerTracker>>(), mockEvents.Object);
        var serverMock = new Mock<ServerManager>(
            Mock.Of<IConfiguration>(), TestSettings.Server(), TestSettings.SteamCmd(),
            Mock.Of<ILogger<ServerManager>>(),
            tracker,
            new TrackChangeTracker(Mock.Of<ILogger<TrackChangeTracker>>(), mockEvents.Object),
            new ServerInfoTracker(Mock.Of<ILogger<ServerInfoTracker>>()),
            mockEvents.Object);

        serverMock
            .Setup(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), It.IsAny<string>()))
            .Callback<AttachmentSession?, string>((_, cmd) => { if (cmd.StartsWith("/message ")) messages.Add(cmd[9..]); })
            .ReturnsAsync((true, "ok"));

        var configMock = new Mock<ConfigService>(
           TestSettings.Server(),
            Mock.Of<ILogger<ConfigService>>());

        var service = new VotingService(
            serverMock.Object,
            tracker,
            configMock.Object,
            Mock.Of<ILogger<VotingService>>(),
            new ConfiguredVoteSettings(config), new ConfiguredVotableTracks(config));

        return (service, tracker, messages, configMock);
    }

    private (VotingService service, PlayerTracker tracker, List<string> messages,
             Mock<ServerManager> serverMock, IConfigurationRoot config)
        CreateModeSetup(string mode, IOptionsMonitor<VoteSettings>? vote = null)
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
        var mockEvents = new Mock<IServerEventPublisher>();

        var tracker = new PlayerTracker(Mock.Of<ILogger<PlayerTracker>>(), mockEvents.Object);
        var serverMock = new Mock<ServerManager>(
            Mock.Of<IConfiguration>(), TestSettings.Server(), TestSettings.SteamCmd(),
            Mock.Of<ILogger<ServerManager>>(),
            tracker,
            new TrackChangeTracker(Mock.Of<ILogger<TrackChangeTracker>>(), mockEvents.Object),
            new ServerInfoTracker(Mock.Of<ILogger<ServerInfoTracker>>()),
            mockEvents.Object);

        serverMock
            .Setup(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), It.IsAny<string>()))
            .Callback<AttachmentSession?, string>((_, cmd) => { if (cmd.StartsWith("/message ")) messages.Add(cmd[9..]); })
            .ReturnsAsync((true, "ok"));

        var service = new VotingService(
            serverMock.Object,
            tracker,
            new Mock<ConfigService>(TestSettings.Server(), Mock.Of<ILogger<ConfigService>>()).Object,
            Mock.Of<ILogger<VotingService>>(),
            vote ?? new ConfiguredVoteSettings(config), new ConfiguredVotableTracks(config));

        return (service, tracker, messages, serverMock, config);
    }

    private static long CurrentVoteId(VotingService service) =>
        (long)typeof(VotingService).GetField("_voteId",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(service)!;

    private static void ExpireVote(VotingService service, long voteId) =>
        typeof(VotingService).GetMethod("TallyVotes",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(service, [voteId]);

    [Fact]
    public void StaleTimeout_DoesNotEndNewVote()
    {
        var (service, tracker, messages, server, _) = CreateModeSetup(VoteModes.Voting);
        tracker.Seed("Alice", "Bob", "Carol");
        service.ProcessChatCommand(TestSession, "Alice", false, "!track wrecknado_02");
        var firstVote = CurrentVoteId(service);
        service.ProcessChatCommand(TestSession, "Bob", false, "!yes");
        service.ProcessChatCommand(TestSession, "Alice", false, "!track wrecknado_03");
        var secondVote = CurrentVoteId(service);

        ExpireVote(service, firstVote);
        Assert.DoesNotContain(messages, m => m.StartsWith("Vote timed out:"));
        service.ProcessChatCommand(TestSession, "Bob", false, "!yes");
        server.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), "track=wrecknado_03"), Times.Once);
        Assert.NotEqual(firstVote, secondVote);
    }

    [Theory]
    [InlineData(2, 0, true)]
    [InlineData(2, 1, true)]
    [InlineData(1, 1, false)]
    [InlineData(1, 2, false)]
    [InlineData(0, 0, false)]
    public void Timeout_CountsOnlyCastVotes(int yesVotes, int noVotes, bool passes)
    {
        var (service, tracker, messages, server, _) = CreateModeSetup(VoteModes.Voting);
        tracker.Seed("Alice", "Bob", "Carol", "Dave", "Eve", "Frank");
        service.ProcessChatCommand(TestSession, "Alice", false, "!track wrecknado_02");
        if (yesVotes > 1)
            service.ProcessChatCommand(TestSession, "Bob", false, "!yes");
        if (noVotes > 0)
            service.ProcessChatCommand(TestSession, "Carol", false, "!no");
        if (noVotes > 1)
            service.ProcessChatCommand(TestSession, "Dave", false, "!no");
        if (yesVotes == 0)
        {
            tracker.Clear();
            tracker.Seed("Bob", "Carol", "Dave", "Eve", "Frank");
        }

        // Leave time for abstaining players to vote before deciding the result.
        server.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), "track=wrecknado_02"), Times.Never);
        ExpireVote(service, CurrentVoteId(service));

        server.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), "track=wrecknado_02"),
            passes ? Times.Once() : Times.Never());
        Assert.Contains(messages, m => m.StartsWith(passes ? "Vote passed!" : "Vote timed out:"));
    }

    [Fact]
    public void Timeout_ExcludesDepartedYesVoter()
    {
        var (service, tracker, messages, server, _) = CreateModeSetup(VoteModes.Voting);
        tracker.Seed("Alice", "Bob", "Carol", "Dave");
        service.ProcessChatCommand(TestSession, "Alice", false, "!track wrecknado_02");
        service.ProcessChatCommand(TestSession, "Bob", false, "!yes");
        service.ProcessChatCommand(TestSession, "Carol", false, "!no");
        tracker.Clear();
        tracker.Seed("Alice", "Carol", "Dave");

        ExpireVote(service, CurrentVoteId(service));

        server.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), "track=wrecknado_02"), Times.Never);
        Assert.Contains(messages, m => m.StartsWith("Vote timed out:"));
    }

    [Fact]
    public void EarlyMajority_ExcludesDepartedYesVoter()
    {
        var (service, tracker, _, server, _) = CreateModeSetup(VoteModes.Voting);
        tracker.Seed("Alice", "Bob", "Carol", "Dave", "Eve");
        service.ProcessChatCommand(TestSession, "Alice", false, "!track wrecknado_02");
        service.ProcessChatCommand(TestSession, "Bob", false, "!yes");
        tracker.Clear();
        tracker.Seed("Alice", "Carol", "Dave", "Eve");
        service.ProcessChatCommand(TestSession, "Carol", false, "!yes");

        server.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), "track=wrecknado_02"), Times.Never);
        ExpireVote(service, CurrentVoteId(service));
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 1)]
    [InlineData(int.MaxValue, 3600)]
    public void InvalidTimeout_IsBoundedAndVoteCanFinish(int configured, int expected)
    {
        var (service, tracker, messages, _, config) = CreateModeSetup(VoteModes.Voting);
        config["Vote:VoteTimeoutSeconds"] = configured.ToString();
        tracker.Seed("Alice", "Bob");
        service.ProcessChatCommand(TestSession, "Alice", false, "!track wrecknado_02");
        Assert.Contains(messages, m => m.EndsWith($"Ends in {expected}s."));
        ExpireVote(service, CurrentVoteId(service));
        service.ProcessChatCommand(TestSession, "Alice", false, "!track wrecknado_03");
        Assert.Contains(messages, m => m == "Vote: Wrecknado Reverse");
        ExpireVote(service, CurrentVoteId(service));
    }

    [Fact]
    public async Task FailedOlderDirectChange_PreservesNewerCooldown()
    {
        var (service, tracker, messages, server, config) = CreateModeSetup(VoteModes.Direct);
        tracker.Seed("Alice", "Bob", "Carol");
        var pending = new TaskCompletionSource<(bool Success, string Message)>();
        server.Setup(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), "track=wrecknado_02")).Returns(pending.Task);
        var apply = typeof(VotingService).GetMethod("ApplyDirectTrackChangeAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var first = (Task)apply.Invoke(service, [TestSession, "Alice", "wrecknado_02", null])!;
        config["Vote:DirectCooldownSeconds"] = "0";
        service.ProcessChatCommand(TestSession, "Bob", false, "!track wrecknado_03");
        config["Vote:DirectCooldownSeconds"] = "30";
        pending.SetResult((false, "failed"));
        await first;

        service.ProcessChatCommand(TestSession, "Carol", false, "!track wrecknado_03");

        server.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), "track=wrecknado_03"), Times.Once);
        Assert.Contains(messages, m => m.Contains("Try again in"));
    }

    [Fact]
    public void Broadcast_LongUnknownTrack_RespectsChatLimit()
    {
        var (service, _, messages, _, _) = CreateModeSetup(VoteModes.Voting);
        service.ProcessChatCommand(TestSession, "Alice", false, "!track " + new string('z', 300));
        Assert.NotEmpty(messages);
        Assert.All(messages, m => Assert.True(m.Length <= 127, m));
    }
    private static void Join(PlayerTracker tracker, string name) =>
        tracker.Seed(name);

    // --- aliasing -----------------------------------------------------------

    [Fact]
    public async Task TrackCommand_IsAliasOfVote_InVotingMode()
    {
        var (service, tracker, messages, _, _) = CreateModeSetup(VoteModes.Voting);
        Join(tracker, "Alice");
        Join(tracker, "Bob");

        service.ProcessChatCommand(TestSession, "Alice", false, "!track wrecknado_02 10");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Contains(messages, m => m == "Vote: Wrecknado - 10 laps");
        Assert.Contains(messages, m => m.StartsWith("By Alice.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TrackCommand_IsGatedInOffMode()
    {
        var (service, tracker, messages, serverMock, _) = CreateModeSetup(VoteModes.Off);
        Join(tracker, "Alice");

        service.ProcessChatCommand(TestSession, "Alice", false, "!track wrecknado_02 10");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Contains(messages, m => m.Contains("disabled", StringComparison.OrdinalIgnoreCase));
        serverMock.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), "track=wrecknado_02"), Times.Never);
    }

    // --- direct mode --------------------------------------------------------

    [Fact]
    public async Task DirectMode_AppliesImmediately_WithoutStartingAVote()
    {
        var (service, tracker, messages, serverMock, _) = CreateModeSetup(VoteModes.Direct);
        Join(tracker, "Alice");
        Join(tracker, "Bob");

        service.ProcessChatCommand(TestSession, "Alice", false, "!track wrecknado_02 6");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        serverMock.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), "track=wrecknado_02"), Times.Once);
        serverMock.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), "laps=6"), Times.Once);
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

        service.ProcessChatCommand(TestSession, "Alice", false, "!track wrecknado_02");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        serverMock.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), "track=wrecknado_02"), Times.Once);
        serverMock.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), It.Is<string>(c => c.StartsWith("laps="))), Times.Never);
    }

    [Fact]
    public async Task DirectMode_RejectsLapsAboveMaximum()
    {
        var (service, tracker, messages, serverMock, _) = CreateModeSetup(VoteModes.Direct);
        Join(tracker, "Alice");

        service.ProcessChatCommand(TestSession, "Alice", false, "!track wrecknado_02 99");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Contains(messages, m => m.Contains("Invalid laps", StringComparison.Ordinal));
        serverMock.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), "track=wrecknado_02"), Times.Never);
    }

    [Fact]
    public async Task DirectMode_AmbiguousQuery_AppliesOnConfirm()
    {
        var (service, tracker, messages, serverMock, _) = CreateModeSetup(VoteModes.Direct);
        Join(tracker, "Alice");
        Join(tracker, "Bob");

        // "wreckn" is a substring of both allowed tracks but equals neither name.
        service.ProcessChatCommand(TestSession, "Alice", false, "!track wreckn 4");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Contains(messages, m => m.Contains("!confirm", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("change track", StringComparison.Ordinal));
        serverMock.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), It.Is<string>(c => c.StartsWith("track="))), Times.Never);

        service.ProcessChatCommand(TestSession, "Alice", false, "!confirm 1");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        serverMock.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), It.Is<string>(c => c.StartsWith("track="))), Times.Once);
        serverMock.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), "laps=4"), Times.Once);
    }

    [Fact]
    public async Task Confirm_RefusesATrackDisallowedSinceItWasOffered()
    {
        var (service, tracker, messages, serverMock, config) = CreateModeSetup(VoteModes.Direct);
        Join(tracker, "Alice");
        Join(tracker, "Bob");
        service.ProcessChatCommand(TestSession, "Alice", false, "!track wreckn 4");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        // An admin takes both offered variants out of voting.
        config["Vote:AllowedTracks:0:Id"] = "retired_one";
        config["Vote:AllowedTracks:1:Id"] = "retired_two";
        service.ProcessChatCommand(TestSession, "Alice", false, "!confirm 1");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        serverMock.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), It.Is<string>(c => c.StartsWith("track="))), Times.Never);
        Assert.Contains(messages, m => m.Contains("no longer available", StringComparison.Ordinal));
    }

    [Fact]
    public async Task APassedVote_IsNotAppliedWhenItsTrackWasDisallowedMeanwhile()
    {
        var (service, tracker, messages, serverMock, config) = CreateModeSetup(VoteModes.Voting);
        Join(tracker, "Alice");
        Join(tracker, "Bob");
        service.ProcessChatCommand(TestSession, "Alice", false, "!vote wrecknado_02");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        config["Vote:AllowedTracks:0:Id"] = "retired_one";
        service.ProcessChatCommand(TestSession, "Bob", false, "!yes");
        await Task.Delay(100, TestContext.Current.CancellationToken);

        serverMock.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), It.Is<string>(c => c.StartsWith("track="))), Times.Never);
        Assert.Contains(messages, m => m.Contains("wrecknado_02 is no longer available", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DirectMode_LuckyCommand_AppliesImmediately()
    {
        var (service, tracker, messages, serverMock, _) = CreateModeSetup(VoteModes.Direct);
        Join(tracker, "Alice");
        Join(tracker, "Bob");

        service.ProcessChatCommand(TestSession, "Alice", false, "!lucky");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        serverMock.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), It.Is<string>(c => c.StartsWith("track="))), Times.Once);
        Assert.DoesNotContain(messages, m => m.Contains("!yes", StringComparison.Ordinal));
    }

    // --- direct-mode cooldown ----------------------------------------------

    [Fact]
    public async Task DirectMode_SecondChangeWithinCooldown_IsRefusedWithSecondsRemaining()
    {
        var (service, tracker, messages, serverMock, _) = CreateModeSetup(VoteModes.Direct);
        Join(tracker, "Alice");
        Join(tracker, "Bob");

        service.ProcessChatCommand(TestSession, "Alice", false, "!track wrecknado_02 4");
        await Task.Delay(50, TestContext.Current.CancellationToken);
        messages.Clear();

        service.ProcessChatCommand(TestSession, "Bob", false, "!track wrecknado_03 4");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        var refusal = Assert.Single(messages);
        Assert.StartsWith("Bob: track was just changed by Alice.", refusal);
        Assert.Matches(@"Try again in \d+s\.$", refusal);
        Assert.DoesNotContain("in 0s", refusal);
        serverMock.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), "track=wrecknado_03"), Times.Never);
    }

    [Fact]
    public async Task DirectMode_RepeatBySamePlayer_SaysYouRatherThanTheirName()
    {
        var (service, tracker, messages, _, _) = CreateModeSetup(VoteModes.Direct);
        Join(tracker, "Alice");
        Join(tracker, "Bob");

        service.ProcessChatCommand(TestSession, "Alice", false, "!track wrecknado_02 4");
        await Task.Delay(50, TestContext.Current.CancellationToken);
        messages.Clear();

        service.ProcessChatCommand(TestSession, "Alice", false, "!track wrecknado_03 4");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.StartsWith("Alice: you just changed the track.", Assert.Single(messages));
    }

    [Fact]
    public async Task DirectMode_SoloHuman_IsNeverRateLimited()
    {
        var (service, tracker, _, serverMock, _) = CreateModeSetup(VoteModes.Direct);
        Join(tracker, "Alice");

        service.ProcessChatCommand(TestSession, "Alice", false, "!track wrecknado_02 4");
        await Task.Delay(50, TestContext.Current.CancellationToken);
        service.ProcessChatCommand(TestSession, "Alice", false, "!track wrecknado_03 4");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        // Nobody to fight with, so back-to-back changes are fine.
        serverMock.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), "track=wrecknado_02"), Times.Once);
        serverMock.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), "track=wrecknado_03"), Times.Once);
    }

    [Fact]
    public async Task DirectMode_AdminBypassesCooldownAndOverridesAnotherPlayer()
    {
        var (service, tracker, _, serverMock, _) = CreateModeSetup(VoteModes.Direct);
        Join(tracker, "Alice");
        Join(tracker, "Bob");
        Join(tracker, "Admin");
        tracker.GetPlayers().Single(p => p.Name == "Admin").IsAdmin = true;

        service.ProcessChatCommand(TestSession, "Alice", false, "!track wrecknado_02 4");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        service.ProcessChatCommand(TestSession, "Admin", false, "!track wrecknado_03 4");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        serverMock.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), "track=wrecknado_03"), Times.Once);
    }

    [Fact]
    public async Task DirectMode_ZeroCooldown_DisablesTheLimit()
    {
        var (service, tracker, _, serverMock, config) = CreateModeSetup(VoteModes.Direct);
        config["Vote:DirectCooldownSeconds"] = "0";
        Join(tracker, "Alice");
        Join(tracker, "Bob");

        service.ProcessChatCommand(TestSession, "Alice", false, "!track wrecknado_02 4");
        await Task.Delay(50, TestContext.Current.CancellationToken);
        service.ProcessChatCommand(TestSession, "Bob", false, "!track wrecknado_03 4");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        serverMock.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), "track=wrecknado_03"), Times.Once);
    }

    [Fact]
    public async Task DirectMode_FailedApply_DoesNotConsumeTheCooldownWindow()
    {
        var (service, tracker, _, serverMock, _) = CreateModeSetup(VoteModes.Direct);
        Join(tracker, "Alice");
        Join(tracker, "Bob");

        serverMock
            .Setup(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), "track=wrecknado_02"))
            .ReturnsAsync((false, "server said no"));

        service.ProcessChatCommand(TestSession, "Alice", false, "!track wrecknado_02 4");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        // Alice's attempt failed, so Bob must not be locked out by it.
        service.ProcessChatCommand(TestSession, "Bob", false, "!track wrecknado_03 4");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        serverMock.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), "track=wrecknado_03"), Times.Once);
    }

    // --- live config-reload interlocks --------------------------------------

    [Fact]
    public async Task ActiveVote_IsCancelled_WhenModeLeavesVoting()
    {
        var (service, tracker, messages, serverMock, config) = CreateModeSetup(VoteModes.Voting);
        Join(tracker, "Alice");
        Join(tracker, "Bob");
        Join(tracker, "Carol");

        service.ProcessChatCommand(TestSession, "Alice", false, "!track wrecknado_02 4");
        await Task.Delay(50, TestContext.Current.CancellationToken);
        messages.Clear();

        // Configuration is re-read per access, so this takes effect immediately.
        config["Vote:Mode"] = VoteModes.Off;

        service.ProcessChatCommand(TestSession, "Bob", false, "!yes");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Contains(messages, m => m.Contains("Vote cancelled", StringComparison.Ordinal));
        serverMock.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), "track=wrecknado_02"), Times.Never);
    }

    [Fact]
    public async Task SwitchingToDirectMidVote_RetiresTheVoteThenAppliesTheChange()
    {
        var (service, tracker, messages, serverMock, config) = CreateModeSetup(VoteModes.Voting);
        Join(tracker, "Alice");
        Join(tracker, "Bob");
        Join(tracker, "Carol");

        service.ProcessChatCommand(TestSession, "Alice", false, "!track wrecknado_02 4");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        config["Vote:Mode"] = VoteModes.Direct;
        messages.Clear();

        service.ProcessChatCommand(TestSession, "Bob", false, "!track wrecknado_03 4");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        // The orphaned vote is retired first - leaving it running would let its timer
        // apply a track change under a mode that no longer votes - and only then does
        // the direct change go through.
        Assert.Contains(messages, m => m.Contains("Vote cancelled", StringComparison.Ordinal));
        serverMock.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), "track=wrecknado_02"), Times.Never);
        serverMock.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), "track=wrecknado_03"), Times.Once);
    }

    [Fact]
    public async Task ConfigCommand_ReportsMode_AndCooldownInDirectMode()
    {
        var (service, tracker, messages, _, _) = CreateModeSetup(VoteModes.Direct);
        Join(tracker, "Alice");

        service.ProcessChatCommand(TestSession, "Alice", false, "!config");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Contains(messages, m => m.Contains("mode=direct", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("cooldown=30s", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HelpCommand_InDirectMode_AdvertisesImmediateChangeNotVoting()
    {
        var (service, tracker, messages, _, _) = CreateModeSetup(VoteModes.Direct);
        Join(tracker, "Alice");

        service.ProcessChatCommand(TestSession, "Alice", false, "!help");
        await Task.Delay(50, TestContext.Current.CancellationToken);

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
        service.ProcessChatCommand(TestSession, "Alice", false, "!track wrecknado_02 4");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        service.ProcessChatCommand(TestSession, "Bob", false, "!no");
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(messages, m => m.Contains("Vote failed", StringComparison.Ordinal));

        // no=2 of 3 online is a strict majority, so this ends it without waiting
        // for the 30s timeout.
        service.ProcessChatCommand(TestSession, "Carol", false, "!no");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Contains(messages, m => m.Contains("Vote failed: majority voted no", StringComparison.Ordinal));
        serverMock.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), "track=wrecknado_02"), Times.Never);
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

        service.ProcessChatCommand(TestSession, "Alice", false, "!track wrecknado_02 4");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        // Alice's auto-yes plus Bob's makes yes=2 of 3 - a strict majority.
        service.ProcessChatCommand(TestSession, "Bob", false, "!yes");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Contains(messages, m => m.Contains("Vote passed", StringComparison.Ordinal));
        serverMock.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), "track=wrecknado_02"), Times.Once);
    }

    [Fact]
    public async Task VoteEndedEarly_IgnoresLateVotes()
    {
        var (service, tracker, messages, _, _) = CreateModeSetup(VoteModes.Voting);
        Join(tracker, "Alice");
        Join(tracker, "Bob");
        Join(tracker, "Carol");

        service.ProcessChatCommand(TestSession, "Alice", false, "!track wrecknado_02 4");
        await Task.Delay(50, TestContext.Current.CancellationToken);
        service.ProcessChatCommand(TestSession, "Bob", false, "!no");
        service.ProcessChatCommand(TestSession, "Carol", false, "!no");
        await Task.Delay(50, TestContext.Current.CancellationToken);
        messages.Clear();

        // The vote is over; a straggler must not restart or re-tally it.
        service.ProcessChatCommand(TestSession, "Bob", false, "!yes");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Empty(messages);
    }

    // --- server-state gates -------------------------------------------------

    private const uint RvaEventLoopCount = 0x1857630;
    private const uint RvaEventLoopIndex = 0x122B270;

    /// <summary>
    /// Stubs the hook reads. Values match what a live server returns: index -1 means
    /// the event loop is off; the session state is SERVER+0x4, 2 while racing.
    /// </summary>
    private static void StubServerState(Mock<ServerManager> server, int count, int index, bool racing) =>
        StubServerState(server, count, index, (int)(racing ? ServerSessionPhase.Racing : ServerSessionPhase.Lobby));

    private static void StubServerState(Mock<ServerManager> server, int count, int index, int sessionState)
    {
        server.Setup(m => m.ReadHookMemoryAsync(RvaEventLoopCount, 4)).ReturnsAsync(BitConverter.GetBytes(count));
        server.Setup(m => m.ReadHookMemoryAsync(RvaEventLoopIndex, 4)).ReturnsAsync(BitConverter.GetBytes(index));
        server.Setup(m => m.ReadHookSessionAsync())
            .ReturnsAsync(new HookSessionState(sessionState, -100000, EventCounter: 1, Ended: false));
    }

    // Issue #189: only the game's racing state silences chat. The countdown, the
    // results screen, the handover and any unknown value all fall open.
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(7, false)]
    [InlineData(-1, false)]
    public async Task ChatCommands_AreSuppressedOnlyInTheRacingState(int sessionState, bool suppressed)
    {
        var (service, tracker, messages, serverMock, config) = CreateModeSetup(VoteModes.Direct);
        config["Vote:SuppressCommandsDuringRace"] = "true";
        StubServerState(serverMock, count: 4, index: -1, sessionState);
        Join(tracker, "Alice");

        service.ProcessChatCommand(TestSession, "Alice", false, "!help");
        await Task.Delay(80, TestContext.Current.CancellationToken);

        Assert.Equal(!suppressed, messages.Any(m => m.StartsWith("Help:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ChatCommands_WorkWhenTheSessionStateCannotBeRead()
    {
        var (service, tracker, messages, serverMock, config) = CreateModeSetup(VoteModes.Direct);
        config["Vote:SuppressCommandsDuringRace"] = "true";
        StubServerState(serverMock, count: 4, index: -1, racing: true);
        serverMock.Setup(m => m.ReadHookSessionAsync()).ReturnsAsync((HookSessionState?)null);
        Join(tracker, "Alice");

        service.ProcessChatCommand(TestSession, "Alice", false, "!help");
        await Task.Delay(80, TestContext.Current.CancellationToken);

        Assert.Contains(messages, m => m.StartsWith("Help:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChatCommands_AreSuppressedDuringARace()
    {
        var (service, tracker, messages, serverMock, config) = CreateModeSetup(VoteModes.Direct);
        config["Vote:SuppressCommandsDuringRace"] = "true";
        StubServerState(serverMock, count: 4, index: -1, racing: true);
        Join(tracker, "Alice");

        service.ProcessChatCommand(TestSession, "Alice", false, "!help");
        await Task.Delay(80, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(messages, m => m.StartsWith("Help:", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("disabled during a race", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RaceRefusal_IsRateLimited_SoItDoesNotBecomeTheSpam()
    {
        var (service, tracker, messages, serverMock, config) = CreateModeSetup(VoteModes.Direct);
        config["Vote:SuppressCommandsDuringRace"] = "true";
        StubServerState(serverMock, count: 4, index: -1, racing: true);
        Join(tracker, "Alice");

        for (var i = 0; i < 4; i++)
        {
            service.ProcessChatCommand(TestSession, "Alice", false, "!help");
            await Task.Delay(40, TestContext.Current.CancellationToken);
        }

        Assert.Single(messages, m => m.Contains("disabled during a race", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChatCommands_WorkWhenNotRacing()
    {
        var (service, tracker, messages, serverMock, _) = CreateModeSetup(VoteModes.Direct);
        StubServerState(serverMock, count: 4, index: -1, racing: false);
        Join(tracker, "Alice");

        service.ProcessChatCommand(TestSession, "Alice", false, "!help");
        await Task.Delay(80, TestContext.Current.CancellationToken);

        Assert.Contains(messages, m => m.StartsWith("Help:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TrackChange_IsRefused_WhileEventLoopIsRunning()
    {
        var (service, tracker, messages, serverMock, _) = CreateModeSetup(VoteModes.Direct);
        StubServerState(serverMock, count: 4, index: 0, racing: false);   // index 0 => enabled
        Join(tracker, "Alice");

        service.ProcessChatCommand(TestSession, "Alice", false, "!track wrecknado_02 4");
        await Task.Delay(80, TestContext.Current.CancellationToken);

        Assert.Contains(messages, m => m.Contains("event loop is running", StringComparison.Ordinal));
        serverMock.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), "track=wrecknado_02"), Times.Never);
    }

    [Fact]
    public async Task TrackChange_IsAllowed_WhenEventLoopIsOff()
    {
        var (service, tracker, _, serverMock, _) = CreateModeSetup(VoteModes.Direct);
        StubServerState(serverMock, count: 4, index: -1, racing: false);  // index -1 => off
        Join(tracker, "Alice");

        service.ProcessChatCommand(TestSession, "Alice", false, "!track wrecknado_02 4");
        await Task.Delay(80, TestContext.Current.CancellationToken);

        serverMock.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), "track=wrecknado_02"), Times.Once);
    }

    [Theory]
    [InlineData(VoteModes.Off, "!voting on", VoteModes.Voting, false)]
    [InlineData(VoteModes.Direct, "  !VOTING ON  ", VoteModes.Voting, true)]
    [InlineData(VoteModes.Voting, "!voting off", VoteModes.Direct, true)]
    [InlineData(VoteModes.Direct, "!voting off", VoteModes.Direct, false)]
    public void VotingCommand_ChangesModeForPrivilegedPlayers(
        string initialMode, string command, string expectedMode, bool moderator)
    {
        var (service, tracker, messages, _, config) = CreateModeSetup(initialMode);
        Join(tracker, "Admin");
        var player = tracker.GetPlayers().Single(p => p.Name == "Admin");
        player.IsAdmin = !moderator;
        player.IsModerator = moderator;

        service.ProcessChatCommand(TestSession, "Admin", false, command);

        Assert.Contains(expectedMode == VoteModes.Direct ? "Voting disabled." : "Voting enabled.", messages);
        AssertReportedMode(service, messages, expectedMode);
        // The override lives in the service; the settings sources are left alone.
        Assert.Equal(initialMode, config["Vote:Mode"]);
    }

    [Fact]
    public void VotingCommand_ASavedVoteChangeRestoresSavedMode_ButNothingElseDoes()
    {
        var (service, tracker, messages, _, config) = CreateModeSetup(VoteModes.Voting);
        Join(tracker, "Admin");
        tracker.GetPlayers().Single(p => p.Name == "Admin").IsAdmin = true;

        service.ProcessChatCommand(TestSession, "Admin", false, "!voting off");
        AssertReportedMode(service, messages, VoteModes.Direct);

        // A reload that changes nothing is not a saved change.
        config.Reload();
        AssertReportedMode(service, messages, VoteModes.Direct);

        config["Vote:MaxLapsAllowed"] = "12";
        config.Reload();
        AssertReportedMode(service, messages, VoteModes.Voting);
    }

    [Fact]
    public async Task VotingCommand_OverTheRealStore_OnlyAChangedVoteSaveRestoresSavedMode()
    {
        using var database = new SettingsTestDatabase();
        var store = database.Store;

        // No pause between reply lines, as the other voting tests configure.
        var initial = store.GetEntry<VoteSettings>();
        initial.Value.MessageDelayMs = 0;
        await store.SaveAsync(initial.Value, initial.Version, TestContext.Current.CancellationToken);

        using var monitor = new SettingsStoreOptionsMonitor<VoteSettings>(store);
        var (service, tracker, messages, _, _) = CreateModeSetup(VoteModes.Voting, monitor);
        Join(tracker, "Admin");
        tracker.GetPlayers().Single(p => p.Name == "Admin").IsAdmin = true;

        service.ProcessChatCommand(TestSession, "Admin", false, "!voting off");
        AssertReportedMode(service, messages, VoteModes.Direct);

        // Saving Vote unchanged, or another section, leaves the chat override in place.
        var vote = store.GetEntry<VoteSettings>();
        await store.SaveAsync(vote.Value, vote.Version, TestContext.Current.CancellationToken);
        var steam = store.GetEntry<SteamCmdSettings>();
        steam.Value.SteamCmdPath = @"C:\steamcmd\steamcmd.exe";
        await store.SaveAsync(steam.Value, steam.Version, TestContext.Current.CancellationToken);
        AssertReportedMode(service, messages, VoteModes.Direct);

        // A saved Vote change ends it.
        vote = store.GetEntry<VoteSettings>();
        vote.Value.MaxLapsAllowed = 12;
        await store.SaveAsync(vote.Value, vote.Version, TestContext.Current.CancellationToken);
        AssertReportedMode(service, messages, VoteModes.Voting);
    }

    private static void AssertReportedMode(VotingService service, List<string> messages, string mode)
    {
        messages.Clear();
        service.ProcessChatCommand(TestSession, "Observer", false, "!config");
        Assert.Contains(messages, m => m.Contains($"mode={mode.ToLowerInvariant()},", StringComparison.Ordinal));
    }

    [Fact]
    public async Task VotingCommand_OffAllowsTrackChangesWithoutAVote()
    {
        var (service, tracker, messages, server, _) = CreateModeSetup(VoteModes.Voting);
        Join(tracker, "Admin");
        Join(tracker, "Alice");
        Join(tracker, "Bob");
        tracker.GetPlayers().Single(p => p.Name == "Admin").IsAdmin = true;

        service.ProcessChatCommand(TestSession, "Admin", false, "!voting off");
        service.ProcessChatCommand(TestSession, "Alice", false, "!track wrecknado_02 4");
        await Task.Delay(80, TestContext.Current.CancellationToken);

        server.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), "track=wrecknado_02"), Times.Once);
        server.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), "laps=4"), Times.Once);
        Assert.DoesNotContain(messages, m => m.Contains("Vote started", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VotingCommand_IgnoresOrdinaryPlayersAndBots(bool isBot)
    {
        var (service, tracker, messages, _, _) = CreateModeSetup(VoteModes.Voting);
        Join(tracker, "Alice");
        tracker.GetPlayers().Single(p => p.Name == "Alice").IsAdmin = isBot;

        service.ProcessChatCommand(TestSession, "Alice", isBot, "!voting off");

        Assert.Empty(messages);
        AssertReportedMode(service, messages, VoteModes.Voting);
    }

    [Theory]
    [InlineData("!voting")]
    [InlineData("!voting maybe")]
    [InlineData("!voting on off")]
    public void VotingCommand_InvalidArgumentsLeaveModeUnchanged(string command)
    {
        var (service, tracker, messages, _, _) = CreateModeSetup(VoteModes.Off);
        Join(tracker, "Admin");
        tracker.GetPlayers().Single(p => p.Name == "Admin").IsAdmin = true;

        service.ProcessChatCommand(TestSession, "Admin", false, command);

        Assert.Contains("Usage: !voting on|off", messages);
        AssertReportedMode(service, messages, VoteModes.Off);
    }

    [Fact]
    public void VotingCommand_OffCancelsVoteAndPreventsExpiredVoteChangingTrack()
    {
        var (service, tracker, messages, server, _) = CreateModeSetup(VoteModes.Voting);
        Join(tracker, "Admin");
        Join(tracker, "Alice");
        Join(tracker, "Bob");
        tracker.GetPlayers().Single(p => p.Name == "Admin").IsAdmin = true;
        service.ProcessChatCommand(TestSession, "Alice", false, "!track wrecknado_02 4");
        var voteId = CurrentVoteId(service);

        service.ProcessChatCommand(TestSession, "Admin", false, "!voting off");
        service.ProcessChatCommand(TestSession, "Bob", false, "!yes");
        service.ProcessChatCommand(TestSession, "Admin", false, "!voting on");
        ExpireVote(service, voteId);

        Assert.Contains(messages, m => m.Contains("Vote cancelled", StringComparison.Ordinal));
        Assert.Contains("Voting disabled.", messages);
        server.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), "track=wrecknado_02"), Times.Never);
    }

    // --- !eventloop ---------------------------------------------------------

    [Fact]
    public async Task EventLoopCommand_IsSilentForUnprivilegedPlayers()
    {
        var (service, tracker, messages, serverMock, _) = CreateModeSetup(VoteModes.Direct);
        StubServerState(serverMock, count: 4, index: 0, racing: false);
        Join(tracker, "Alice");

        service.ProcessChatCommand(TestSession, "Alice", false, "!eventloop");
        await Task.Delay(80, TestContext.Current.CancellationToken);

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

        service.ProcessChatCommand(TestSession, "Mod", false, "!eventloop");
        await Task.Delay(80, TestContext.Current.CancellationToken);

        Assert.Contains(messages, m => m == "Event loop: on (entry 3/4)");
    }

    [Fact]
    public async Task EventLoopCommand_SaysSoWhenAlreadyInRequestedState()
    {
        var (service, tracker, messages, serverMock, _) = CreateModeSetup(VoteModes.Direct);
        StubServerState(serverMock, count: 4, index: 0, racing: false);   // already on
        Join(tracker, "Admin");
        tracker.GetPlayers().Single(p => p.Name == "Admin").IsAdmin = true;

        service.ProcessChatCommand(TestSession, "Admin", false, "!eventloop on");
        await Task.Delay(80, TestContext.Current.CancellationToken);

        Assert.Contains(messages, m => m.Contains("already on", StringComparison.Ordinal));
        serverMock.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), "/eventloop"), Times.Never);
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

        service.ProcessChatCommand(TestSession, "Admin", false, "!eventloop off");
        // The toggle is polled for up to 8 x 250ms before giving up.
        await Task.Delay(2600, TestContext.Current.CancellationToken);

        serverMock.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), "/eventloop"), Times.Once);
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

        service.ProcessChatCommand(TestSession, "Admin", false, "!eventloop");
        await Task.Delay(120, TestContext.Current.CancellationToken);

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
        serverMock.Setup(m => m.ReadHookSessionAsync())
            .ReturnsAsync(new HookSessionState((int)ServerSessionPhase.Lobby, -100000, EventCounter: 0, Ended: false));

        serverMock.Setup(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), "/eventloop"))
            .Callback(() => _ = Task.Run(async () => { await Task.Delay(400); index = -1; }))
            .ReturnsAsync((true, "ok"));

        service.ProcessChatCommand(TestSession, "Admin", false, "!eventloop off");
        await Task.Delay(2000, TestContext.Current.CancellationToken);

        Assert.Contains(messages, m => m == "Event loop: off (4 entries)");
        Assert.DoesNotContain(messages, m => m.Contains("did not change", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AmbiguousTrackQuery_IsRefusedUpFront_WhileEventLoopIsRunning()
    {
        // Regression: the interlock used to sit at the point of applying, which is
        // only reached on an exact match. An ambiguous query printed a five-option
        // list and would only have failed later at !confirm.
        var (service, tracker, messages, serverMock, _) = CreateModeSetup(VoteModes.Direct);
        StubServerState(serverMock, count: 4, index: 0, racing: false);   // loop on
        Join(tracker, "Alice");

        service.ProcessChatCommand(TestSession, "Alice", false, "!track wreckn");
        await Task.Delay(120, TestContext.Current.CancellationToken);

        Assert.Contains(messages, m => m.Contains("event loop is running", StringComparison.Ordinal));
        Assert.DoesNotContain(messages, m => m.Contains("!confirm", StringComparison.Ordinal));
        Assert.DoesNotContain(messages, m => m.Contains("Multiple matches", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LuckyCommand_IsRefused_WhileEventLoopIsRunning()
    {
        var (service, tracker, messages, serverMock, _) = CreateModeSetup(VoteModes.Direct);
        StubServerState(serverMock, count: 4, index: 0, racing: false);
        Join(tracker, "Alice");

        service.ProcessChatCommand(TestSession, "Alice", false, "!lucky");
        await Task.Delay(120, TestContext.Current.CancellationToken);

        Assert.Contains(messages, m => m.Contains("event loop is running", StringComparison.Ordinal));
        Assert.DoesNotContain(messages, m => m.Contains("Lucky pick", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RaceSuppression_IsOffByDefault()
    {
        // The session-state model is under-determined: a countdown or results phase
        // shares its byte pattern with racing, so the gate blocked chat when the
        // player was not driving. Off until the states are properly mapped.
        var (service, tracker, messages, serverMock, _) = CreateModeSetup(VoteModes.Direct);
        StubServerState(serverMock, count: 4, index: -1, racing: true);
        Join(tracker, "Alice");

        service.ProcessChatCommand(TestSession, "Alice", false, "!help");
        await Task.Delay(80, TestContext.Current.CancellationToken);

        Assert.Contains(messages, m => m.StartsWith("Help:", StringComparison.Ordinal));
        Assert.DoesNotContain(messages, m => m.Contains("disabled during a race", StringComparison.Ordinal));
    }

    // --- !laps ----------------------------------------------------------------

    private static void VerifyNoTrackSent(Mock<ServerManager> serverMock) =>
        serverMock.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), It.Is<string>(c => c.StartsWith("track="))), Times.Never);

    [Fact]
    public async Task LapsCommand_InDirectMode_SendsOnlyLaps()
    {
        var (service, tracker, messages, serverMock, _) = CreateModeSetup(VoteModes.Direct);
        Join(tracker, "Alice");
        Join(tracker, "Bob");

        service.ProcessChatCommand(TestSession, "Alice", false, "!laps 6");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        serverMock.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), "laps=6"), Times.Once);
        VerifyNoTrackSent(serverMock);
        Assert.Contains(messages, m => m == "Next race: 6 laps, same track - set by Alice");
        Assert.DoesNotContain(messages, m => m.Contains("!yes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LapsCommand_InDirectMode_ConsumesTheSharedCooldown()
    {
        var (service, tracker, messages, serverMock, _) = CreateModeSetup(VoteModes.Direct);
        Join(tracker, "Alice");
        Join(tracker, "Bob");

        service.ProcessChatCommand(TestSession, "Alice", false, "!track wrecknado_02 4");
        await Task.Delay(50, TestContext.Current.CancellationToken);
        messages.Clear();

        service.ProcessChatCommand(TestSession, "Bob", false, "!laps 6");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.StartsWith("Bob: track was just changed by Alice.", Assert.Single(messages));
        serverMock.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), "laps=6"), Times.Never);
    }

    [Fact]
    public async Task LapsCommand_InDirectMode_WhenServerRejects_ReportsFailureAndKeepsCooldownFree()
    {
        var (service, tracker, messages, serverMock, _) = CreateModeSetup(VoteModes.Direct);
        Join(tracker, "Alice");
        Join(tracker, "Bob");
        serverMock
            .Setup(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), It.IsAny<string>()))
            .Callback<AttachmentSession?, string>((_, cmd) => { if (cmd.StartsWith("/message ")) messages.Add(cmd[9..]); })
            .ReturnsAsync((AttachmentSession? _, string cmd) => cmd.StartsWith("laps=") ? (false, "rejected") : (true, "ok"));

        service.ProcessChatCommand(TestSession, "Alice", false, "!laps 6");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Contains(messages, m => m == "Failed to change laps.");
        Assert.DoesNotContain(messages, m => m.StartsWith("Next race:", StringComparison.Ordinal));

        // The rejected change did not use the window, so Bob is not told to wait.
        messages.Clear();
        service.ProcessChatCommand(TestSession, "Bob", false, "!laps 5");
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(messages, m => m.Contains("Try again", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LapsCommand_InVotingMode_StartsALapsOnlyVote_AndAPassedVoteSendsOnlyLaps()
    {
        var (service, tracker, messages, serverMock, _) = CreateModeSetup(VoteModes.Voting);
        Join(tracker, "Alice");
        Join(tracker, "Bob");
        Join(tracker, "Carol");

        service.ProcessChatCommand(TestSession, "Alice", false, "!laps 7");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Contains(messages, m => m == "Vote: 7 laps, same track");
        Assert.Contains(messages, m => m == "By Alice. Type !yes or !no. Ends in 30s.");
        serverMock.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), It.Is<string>(c => c.StartsWith("laps="))), Times.Never);

        service.ProcessChatCommand(TestSession, "Bob", false, "!yes");
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Contains(messages, m => m == "Vote for 7 laps: 2 yes, 0 no (3 players online).");
        Assert.Contains(messages, m => m == "Vote passed! Next race: 7 laps, same track.");
        serverMock.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), "laps=7"), Times.Once);
        VerifyNoTrackSent(serverMock);
    }

    [Fact]
    public async Task LapsCommand_InVotingMode_WhenAlone_AppliesWithoutAVote()
    {
        var (service, tracker, messages, serverMock, _) = CreateModeSetup(VoteModes.Voting);
        Join(tracker, "Alice");

        service.ProcessChatCommand(TestSession, "Alice", false, "!laps 3");
        await Task.Delay(100, TestContext.Current.CancellationToken);

        serverMock.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), "laps=3"), Times.Once);
        VerifyNoTrackSent(serverMock);
        Assert.Equal("Next race: 3 laps, same track", Assert.Single(messages));
    }

    [Fact]
    public async Task LapsCommand_WhileAVoteIsRunning_IsRefused()
    {
        var (service, tracker, messages, serverMock, _) = CreateModeSetup(VoteModes.Voting);
        Join(tracker, "Alice");
        Join(tracker, "Bob");

        service.ProcessChatCommand(TestSession, "Alice", false, "!track wrecknado_02 4");
        await Task.Delay(50, TestContext.Current.CancellationToken);
        messages.Clear();

        // Even with an invalid count: the vote guard answers before parsing.
        service.ProcessChatCommand(TestSession, "Bob", false, "!laps 99");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        var refusal = Assert.Single(messages);
        Assert.StartsWith("Bob: vote in progress - Wrecknado for 4 laps", refusal);
        serverMock.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), It.Is<string>(c => c.StartsWith("laps="))), Times.Never);
    }

    [Fact]
    public async Task TrackVote_WhileALapsVoteIsRunning_IsRefusedWithTheLapsVote()
    {
        var (service, tracker, messages, _, _) = CreateModeSetup(VoteModes.Voting);
        Join(tracker, "Alice");
        Join(tracker, "Bob");

        service.ProcessChatCommand(TestSession, "Alice", false, "!laps 5");
        await Task.Delay(50, TestContext.Current.CancellationToken);
        messages.Clear();

        service.ProcessChatCommand(TestSession, "Bob", false, "!track wrecknado_02");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        var refusal = Assert.Single(messages);
        Assert.Matches(@"^Bob: vote in progress - 5 laps, \d+s left\. Type !yes or !no\.$", refusal);
    }

    [Fact]
    public async Task LapsCommand_InOffMode_IsRefused()
    {
        var (service, tracker, messages, serverMock, _) = CreateModeSetup(VoteModes.Off);
        Join(tracker, "Alice");

        service.ProcessChatCommand(TestSession, "Alice", false, "!laps 5");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal("Voting is currently disabled.", Assert.Single(messages));
        serverMock.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), It.Is<string>(c => c.StartsWith("laps="))), Times.Never);
    }

    [Fact]
    public async Task LapsCommand_IsRefused_WhileEventLoopIsRunning()
    {
        var (service, tracker, messages, serverMock, _) = CreateModeSetup(VoteModes.Direct);
        StubServerState(serverMock, count: 4, index: 0, racing: false);
        Join(tracker, "Alice");

        service.ProcessChatCommand(TestSession, "Alice", false, "!laps 5");
        await Task.Delay(80, TestContext.Current.CancellationToken);

        Assert.Contains(messages, m => m.Contains("event loop is running", StringComparison.Ordinal));
        serverMock.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), It.Is<string>(c => c.StartsWith("laps="))), Times.Never);
    }

    [Theory]
    [InlineData("!laps 0")]
    [InlineData("!laps 11")]
    [InlineData("!laps -2")]
    public async Task LapsCommand_OutOfRange_SendsInvalidLapsMessage(string command)
    {
        var (service, tracker, messages, serverMock, _) = CreateModeSetup(VoteModes.Direct);
        Join(tracker, "Alice");

        service.ProcessChatCommand(TestSession, "Alice", false, command);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal("Invalid laps: must be between 1 and 10.", Assert.Single(messages));
        serverMock.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), It.Is<string>(c => c.StartsWith("laps="))), Times.Never);
    }

    [Theory]
    [InlineData("!laps")]
    [InlineData("!laps five")]
    [InlineData("!laps 5 6")]
    public async Task LapsCommand_MissingOrMalformed_SendsUsage(string command)
    {
        var (service, tracker, messages, serverMock, _) = CreateModeSetup(VoteModes.Direct);
        Join(tracker, "Alice");

        service.ProcessChatCommand(TestSession, "Alice", false, command);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal("Usage: !laps <laps> (laps must be between 1 and 10)", Assert.Single(messages));
        serverMock.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), It.Is<string>(c => c.StartsWith("laps="))), Times.Never);
    }

    [Theory]
    [InlineData(VoteModes.Direct, "Help: !laps <laps> - change only the laps now. Example: !laps 5")]
    [InlineData(VoteModes.Voting, "Help: !laps <laps> - vote on the laps only. Example: !laps 5")]
    public async Task HelpCommand_ListsLaps_WordedForTheMode(string mode, string expected)
    {
        var (service, tracker, messages, _, _) = CreateModeSetup(mode);
        Join(tracker, "Alice");

        service.ProcessChatCommand(TestSession, "Alice", false, "!help");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Contains(expected, messages);
        Assert.All(messages, m => Assert.True(m.Length <= 127, m));
    }

    // 127 is the longest message the game accepts (docs/finding-rvas.md), and the limit
    // ChatMessageCharacterLimit enforces for every reply.
    [Theory]
    [InlineData(VoteModes.Voting)]
    [InlineData(VoteModes.Direct)]
    public async Task LapsReplies_FitTheChatLimit_WithAVeryLongName(string mode)
    {
        var (service, tracker, messages, _, _) = CreateModeSetup(mode);
        var longName = new string('N', 140);
        Join(tracker, longName);
        Join(tracker, "Bob");

        // The long name starts the change, then is refused while it is pending.
        service.ProcessChatCommand(TestSession, longName, false, "!laps 5");
        await Task.Delay(50, TestContext.Current.CancellationToken);
        service.ProcessChatCommand(TestSession, longName, false, "!laps 6");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Contains(messages, m => m.Contains(mode == VoteModes.Voting ? "vote in progress" : "Try again", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith(mode == VoteModes.Voting ? "By NNN" : "Next race: 5 laps", StringComparison.Ordinal));
        Assert.All(messages, m => Assert.True(m.Length <= 127, m));
    }

    // ---- Attachment sessions (#40) ----------------------------------------------

    [Fact]
    public void Vote_IsDroppedWhenItsAttachmentEnds_AndNeverApplied()
    {
        var (service, tracker, messages, server, _) = CreateModeSetup(VoteModes.Voting);
        tracker.Seed("Alice", "Bob", "Carol");
        using var attachment = new CancellationTokenSource();
        var session = new AttachmentSession(7, 4242, attachment.Token);

        service.ProcessChatCommand(session, "Alice", false, "!track wrecknado_02");
        var vote = CurrentVoteId(service);

        attachment.Cancel();
        ExpireVote(service, vote);

        server.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), "track=wrecknado_02"), Times.Never);
        Assert.DoesNotContain(messages, m => m.StartsWith("Vote passed", StringComparison.Ordinal));
    }

    [Fact]
    public void VoteResult_GoesToTheAttachmentTheVoteStartedUnder()
    {
        var (service, tracker, _, server, _) = CreateModeSetup(VoteModes.Voting);
        tracker.Seed("Alice", "Bob", "Carol");
        var session = new AttachmentSession(7, 4242, CancellationToken.None);

        service.ProcessChatCommand(session, "Alice", false, "!track wrecknado_02");
        service.ProcessChatCommand(session, "Bob", false, "!yes");

        server.Verify(m => m.SendCommandAsync(It.Is<AttachmentSession?>(s => s != null && s.Id == 7), "track=wrecknado_02"), Times.Once);
    }

    [Fact]
    public void ANewerAttachment_ClearsTheVoteAndThePendingConfirmation()
    {
        var (service, tracker, messages, server, _) = CreateModeSetup(VoteModes.Voting);
        tracker.Seed("Alice", "Bob", "Carol");
        var first = new AttachmentSession(1, 4242, CancellationToken.None);
        var second = new AttachmentSession(2, 4343, CancellationToken.None);

        service.ProcessChatCommand(first, "Alice", false, "!track wrecknado_02");
        var vote = CurrentVoteId(service);

        // The vote described the first server; a yes on the second is not a vote on it.
        service.ProcessChatCommand(second, "Bob", false, "!yes");
        ExpireVote(service, vote);

        server.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), "track=wrecknado_02"), Times.Never);
        Assert.DoesNotContain(messages, m => m.StartsWith("Vote for", StringComparison.Ordinal));
    }

    [Fact]
    public void AConfirmationOfferedOnAnEarlierAttachment_CannotBeConfirmedOnTheNext()
    {
        var (service, tracker, messages, server, _) = CreateModeSetup(VoteModes.Direct);
        tracker.Seed("Alice");
        var first = new AttachmentSession(1, 4242, CancellationToken.None);
        var second = new AttachmentSession(2, 4343, CancellationToken.None);

        // Matches both Wrecknado tracks, so it offers a choice.
        service.ProcessChatCommand(first, "Alice", false, "!track wreck");
        Assert.Contains(messages, m => m.Contains("!confirm", StringComparison.Ordinal));

        service.ProcessChatCommand(second, "Alice", false, "!confirm 1");

        Assert.Contains("No vote confirmation is pending.", messages);
        server.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), It.Is<string>(c => c.StartsWith("track="))), Times.Never);
    }

    [Fact]
    public void AChatCommandFromAReplacedAttachment_IsIgnored()
    {
        var (service, tracker, messages, server, _) = CreateModeSetup(VoteModes.Direct);
        tracker.Seed("Alice");
        var first = new AttachmentSession(1, 4242, CancellationToken.None);
        var second = new AttachmentSession(2, 4343, CancellationToken.None);
        service.ProcessChatCommand(second, "Alice", false, "!help");
        messages.Clear();

        service.ProcessChatCommand(first, "Alice", false, "!track wrecknado_02");

        Assert.Empty(messages);
        server.Verify(m => m.SendCommandAsync(It.IsAny<AttachmentSession?>(), "track=wrecknado_02"), Times.Never);
    }
}
