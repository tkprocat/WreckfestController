using Microsoft.Extensions.Configuration;
using WreckfestController.Models;

namespace WreckfestController.Services;

public class VotingService
{
    private readonly ServerManager _serverManager;
    private readonly PlayerTracker _playerTracker;
    private readonly ConfigService _configService;
    private readonly ILogger<VotingService> _logger;
    private readonly IConfiguration _configuration;

    private enum VoteState { Idle, Active }
    private VoteState _state = VoteState.Idle;
    private string? _votedTrackId;
    private int _votedLaps;
    private string? _voteInitiator;
    private readonly HashSet<string> _yesVoters = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _noVoters = new(StringComparer.OrdinalIgnoreCase);
    private System.Threading.Timer? _voteTimer;
    private System.Threading.Timer? _twentySecondStatusTimer;
    private System.Threading.Timer? _tenSecondStatusTimer;
    private readonly object _stateLock = new();
    private const int SearchPageSize = 5;
    private readonly Queue<AllowedVoteTrack> _searchResultBuffer = new();
    private readonly object _searchLock = new();

    private bool VotingEnabled => _configuration.GetValue("Vote:Enabled", true);
    private int VoteTimeoutSeconds => _configuration.GetValue<int?>("Vote:VoteTimeoutSeconds") ?? 30;
    private int MaxLapsAllowed => Math.Max(1, _configuration.GetValue<int?>("Vote:MaxLapsAllowed") ?? 10);

    public VotingService(
        ServerManager serverManager,
        PlayerTracker playerTracker,
        ConfigService configService,
        ILogger<VotingService> logger,
        IConfiguration configuration)
    {
        _serverManager = serverManager;
        _playerTracker = playerTracker;
        _configService = configService;
        _logger = logger;
        _configuration = configuration;

        _serverManager.ChatCommandReceived += ProcessChatCommand;
    }

    public void ProcessChatCommand(string playerName, bool isBot, string message)
    {
        if (isBot) return;

        var lower = message.ToLowerInvariant().Trim();

        if (lower == "!help")
        {
            if (VotingEnabled)
            {
                _ = BroadcastMessages([
                    $"Help: max laps is {MaxLapsAllowed}.",
                    "Help: !vote <trackId> <laps> - start a vote. Example: !vote misc_bsv 6",
                    "Help: !yes - vote yes on the active vote.",
                    "Help: !no - vote no on the active vote.",
                    "Help: !search <text> - find track IDs. Example: !search tvtp misc",
                    "Help: !more - show the next search results."
                ]);
            }
            else
            {
                _ = BroadcastMessage("Voting is currently disabled.");
            }
            return;
        }

        if (!VotingEnabled && IsVotingCommand(lower))
        {
            _ = BroadcastMessage("Voting is currently disabled.");
            return;
        }

        if (lower.StartsWith("!vote "))
        {
            var parts = message.Substring(6).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !int.TryParse(parts[^1], out var laps) || laps < 1)
            {
                _ = BroadcastMessage($"Usage: !vote <trackId> <laps> (laps must be between 1 and {MaxLapsAllowed})");
                return;
            }

            if (laps > MaxLapsAllowed)
            {
                _ = BroadcastMessage($"Invalid laps: must be between 1 and {MaxLapsAllowed}.");
                return;
            }

            var trackId = string.Join(" ", parts[..^1]);
            var allowedTrack = FindAllowedTrack(trackId);
            if (allowedTrack == null)
            {
                _ = BroadcastMessage($"Track '{trackId}' is not allowed for voting. Allowed tracks: {FormatAllowedTracks()}.");
                return;
            }

            StartVote(playerName, allowedTrack.Id, laps);
        }
        else if (lower == "!yes")
        {
            RecordVote(playerName, yes: true);
        }
        else if (lower == "!no")
        {
            RecordVote(playerName, yes: false);
        }
        else if (lower == "!search")
        {
            _ = BroadcastMessage("Usage: !search <track name or id>");
        }
        else if (lower.StartsWith("!search "))
        {
            SearchTracks(message.Substring(8).Trim());
        }
        else if (lower == "!more")
        {
            ShowMoreSearchResults();
        }
    }

    private static bool IsVotingCommand(string lower)
    {
        return lower.StartsWith("!vote ") ||
               lower == "!yes" ||
               lower == "!no" ||
               lower == "!search" ||
               lower.StartsWith("!search ") ||
               lower == "!more";
    }

    private AllowedVoteTrack? FindAllowedTrack(string trackId)
    {
        return GetAllowedTracks()
            .FirstOrDefault(track => string.Equals(track.Id, trackId, StringComparison.OrdinalIgnoreCase));
    }

    private List<AllowedVoteTrack> GetAllowedTracks()
    {
        return _configuration
            .GetSection("Vote:AllowedTracks")
            .Get<List<AllowedVoteTrack>>() ?? new List<AllowedVoteTrack>();
    }

    private string FormatAllowedTracks()
    {
        var tracks = GetAllowedTracks();
        if (tracks.Count == 0)
            return "none configured";

        return string.Join(", ", tracks.Select(track =>
            string.IsNullOrWhiteSpace(track.Name) ? track.Id : $"{track.Name} ({track.Id})"));
    }

    private void SearchTracks(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            _ = BroadcastMessage("Usage: !search <track name or id>");
            return;
        }

        var matches = GetAllowedTracks()
            .Where(track =>
                track.Id.Contains(pattern, StringComparison.OrdinalIgnoreCase) ||
                track.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
        {
            ClearSearchResults();
            _ = BroadcastMessage($"No tracks found matching '{pattern}'.");
            return;
        }

        StoreSearchResults(matches.Skip(SearchPageSize));
        _ = BroadcastMessages(FormatSearchResults("Matches", matches.Take(SearchPageSize), GetBufferedSearchResultCount()));
    }

    private void ShowMoreSearchResults()
    {
        List<AllowedVoteTrack> page;
        int remainingCount;

        lock (_searchLock)
        {
            if (_searchResultBuffer.Count == 0)
            {
                _ = BroadcastMessage("No more search results. Use !search <track name or id>.");
                return;
            }

            page = new List<AllowedVoteTrack>();
            while (page.Count < SearchPageSize && _searchResultBuffer.Count > 0)
            {
                page.Add(_searchResultBuffer.Dequeue());
            }

            remainingCount = _searchResultBuffer.Count;
        }

        _ = BroadcastMessages(FormatSearchResults("More matches", page, remainingCount));
    }

    private void StoreSearchResults(IEnumerable<AllowedVoteTrack> remainingMatches)
    {
        lock (_searchLock)
        {
            _searchResultBuffer.Clear();
            foreach (var match in remainingMatches)
            {
                _searchResultBuffer.Enqueue(match);
            }
        }
    }

    private int GetBufferedSearchResultCount()
    {
        lock (_searchLock)
        {
            return _searchResultBuffer.Count;
        }
    }

    private void ClearSearchResults()
    {
        lock (_searchLock)
        {
            _searchResultBuffer.Clear();
        }
    }

    private static List<string> FormatSearchResults(string label, IEnumerable<AllowedVoteTrack> tracks, int remainingCount)
    {
        var messages = tracks
            .Select(track => $"{label}: {FormatTrackSearchResult(track)}")
            .ToList();

        if (remainingCount > 0)
        {
            messages.Add($"{remainingCount} more. Type !more for next results.");
        }

        return messages;
    }

    private static string FormatTrackSearchResult(AllowedVoteTrack track)
    {
        if (string.IsNullOrWhiteSpace(track.Name))
            return track.Id;

        return $"{track.Id} - {track.Name}";
    }

    private void StartVote(string initiator, string trackId, int laps)
    {
        lock (_stateLock)
        {
            if (_state == VoteState.Active)
            {
                _ = BroadcastMessage("A vote is already in progress! Type !yes or !no.");
                return;
            }

            _state = VoteState.Active;
            _votedTrackId = trackId;
            _votedLaps = laps;
            _voteInitiator = initiator;
            _yesVoters.Clear();
            _noVoters.Clear();
            _yesVoters.Add(initiator);

            var timeout = VoteTimeoutSeconds;
            _voteTimer?.Dispose();
            _voteTimer = new System.Threading.Timer(_ => TallyVotes(), null,
                TimeSpan.FromSeconds(timeout), Timeout.InfiniteTimeSpan);
            ScheduleVoteStatusTimers(timeout);

            _logger.LogInformation("Vote started by {Initiator}: {TrackId} for {Laps} laps ({Timeout}s timeout)",
                initiator, trackId, laps, timeout);
        }

        _ = BroadcastMessage(
            $"Vote started by {initiator}: {trackId} for {laps} laps. " +
            $"Type !yes or !no. Voting ends in {VoteTimeoutSeconds}s.");
    }

    private void ScheduleVoteStatusTimers(int timeoutSeconds)
    {
        _twentySecondStatusTimer?.Dispose();
        _tenSecondStatusTimer?.Dispose();
        _twentySecondStatusTimer = null;
        _tenSecondStatusTimer = null;

        if (timeoutSeconds > 20)
        {
            _twentySecondStatusTimer = new System.Threading.Timer(_ => BroadcastVoteStatus(20), null,
                TimeSpan.FromSeconds(timeoutSeconds - 20), Timeout.InfiniteTimeSpan);
        }

        if (timeoutSeconds > 10)
        {
            _tenSecondStatusTimer = new System.Threading.Timer(_ => BroadcastVoteStatus(10), null,
                TimeSpan.FromSeconds(timeoutSeconds - 10), Timeout.InfiniteTimeSpan);
        }
    }

    private void BroadcastVoteStatus(int secondsRemaining)
    {
        int yesCount;
        int noCount;

        lock (_stateLock)
        {
            if (_state != VoteState.Active)
                return;

            yesCount = _yesVoters.Count;
            noCount = _noVoters.Count;
        }

        var status = yesCount > noCount ? "passing" : "failing";
        _ = BroadcastMessage(
            $"{secondsRemaining} seconds left for voting, currently the vote is {status}! " +
            $"({yesCount} yes, {noCount} no)");
    }

    private void RecordVote(string playerName, bool yes)
    {
        string? trackId;
        int laps;
        bool earlyResult;
        bool earlyPassed = false;

        lock (_stateLock)
        {
            if (_state != VoteState.Active)
                return;

            if (_yesVoters.Contains(playerName) || _noVoters.Contains(playerName))
            {
                _ = BroadcastMessage($"{playerName} already voted.");
                return;
            }

            if (yes)
                _yesVoters.Add(playerName);
            else
                _noVoters.Add(playerName);

            trackId = _votedTrackId!;
            laps = _votedLaps;
            var humanCount = _playerTracker.GetPlayerCount().online;
            var yesCount = _yesVoters.Count;
            var noCount = _noVoters.Count;

            _ = BroadcastMessage($"Vote for {trackId}: {yesCount} yes, {noCount} no ({humanCount} players online).");

            earlyResult = HasMajority(yesCount, humanCount) || HasMajority(noCount, humanCount);
            if (earlyResult)
            {
                earlyPassed = HasMajority(yesCount, humanCount);
                ResetVoteState();
            }
        }

        if (earlyResult)
        {
            if (earlyPassed)
                _ = ApplyVotedTrack(trackId!, laps);
            else
                _ = BroadcastMessage($"Vote failed: majority voted no. Next race unchanged.");
        }
    }

    private void TallyVotes()
    {
        string? trackId;
        int laps;
        bool passed;

        lock (_stateLock)
        {
            if (_state != VoteState.Active)
                return;

            trackId = _votedTrackId!;
            laps = _votedLaps;
            passed = _yesVoters.Count > _noVoters.Count;
            ResetVoteState();
        }

        _logger.LogInformation("Vote tally for {TrackId}: {Result}", trackId, passed ? "passed" : "failed");

        if (passed)
            _ = ApplyVotedTrack(trackId!, laps);
        else
            _ = BroadcastMessage("Vote timed out: not enough yes votes. Next race unchanged.");
    }

    private async Task ApplyVotedTrack(string trackId, int laps)
    {
        try
        {
            await _serverManager.SendCommandAsync($"track={trackId}");
            await _serverManager.SendCommandAsync($"laps={laps}");
            _logger.LogInformation("Vote passed: {TrackId} for {Laps} laps sent to server settings", trackId, laps);
            await BroadcastMessage($"Vote passed! Next race: {trackId} for {laps} laps.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply voted track settings {TrackId}", trackId);
            await BroadcastMessage("Vote passed but failed to update track settings.");
        }
    }

    private async Task BroadcastMessage(string message)
    {
        try
        {
            await _serverManager.SendCommandAsync($"/message {message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to broadcast message: {Message}", message);
        }
    }

    private async Task BroadcastMessages(IEnumerable<string> messages)
    {
        foreach (var message in messages)
        {
            await BroadcastMessage(message);
        }
    }

    // Must be called while holding _stateLock
    private void ResetVoteState()
    {
        _state = VoteState.Idle;
        _voteTimer?.Dispose();
        _voteTimer = null;
        _twentySecondStatusTimer?.Dispose();
        _twentySecondStatusTimer = null;
        _tenSecondStatusTimer?.Dispose();
        _tenSecondStatusTimer = null;
        _votedTrackId = null;
        _voteInitiator = null;
        _yesVoters.Clear();
        _noVoters.Clear();
    }

    private static bool HasMajority(int count, int total) => total > 0 && count * 2 > total;
}
