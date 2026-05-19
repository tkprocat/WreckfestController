using Microsoft.Extensions.Configuration;
using WreckfestController.Models;

namespace WreckfestController.Services;

public class VotingService
{
    private enum VoteTrackResolutionKind { None, Exact, Ambiguous, Fuzzy }
    private sealed record VoteTrackResolution(VoteTrackResolutionKind Kind, AllowedVoteTrack? Track, List<AllowedVoteTrack> Options);

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
    private const int VoteConfirmationOptionLimit = 5;
    private const int ChatMessageCharacterLimit = 127;
    private readonly Queue<AllowedVoteTrack> _searchResultBuffer = new();
    private readonly object _searchLock = new();
    private readonly object _pendingVoteLock = new();
    private List<AllowedVoteTrack> _pendingVoteOptions = new();
    private string? _pendingVoteRequester;
    private int _pendingVoteLaps;

    private bool VotingEnabled => _configuration.GetValue("Vote:Enabled", true);
    private int VoteTimeoutSeconds => _configuration.GetValue<int?>("Vote:VoteTimeoutSeconds") ?? 30;
    private int MaxLapsAllowed => Math.Max(1, _configuration.GetValue<int?>("Vote:MaxLapsAllowed") ?? 10);
    private int MessageDelayMs => Math.Clamp(_configuration.GetValue<int?>("Vote:MessageDelayMs") ?? 250, 0, 5000);

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
                    "Help: !more - show the next search results.",
                    "Help: !lucky - vote on a random track/laps. Alias: !ifeellucky."
                ]);
            }
            else
            {
                _ = BroadcastMessage("Voting is currently disabled.");
            }
            return;
        }

        if (lower == "!config")
        {
            _ = BroadcastMessages(GetConfigMessages());
            return;
        }

        if (lower == "!debug")
        {
            _ = BroadcastMessages(GetDebugMessages());
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

            var requestedTrack = string.Join(" ", parts[..^1]);
            var resolvedTrack = ResolveVoteTrack(requestedTrack);
            if (resolvedTrack.Kind == VoteTrackResolutionKind.Exact && resolvedTrack.Track != null)
            {
                ClearPendingVote();
                StartVote(playerName, resolvedTrack.Track.Id, laps);
                return;
            }

            if (resolvedTrack.Options.Count > 0)
            {
                StorePendingVote(playerName, laps, resolvedTrack.Options);
                var label = resolvedTrack.Kind == VoteTrackResolutionKind.Fuzzy
                    ? "Possible matches"
                    : "Multiple matches";
                _ = BroadcastMessages(FormatVoteConfirmationOptions(label, requestedTrack, resolvedTrack.Options));
                return;
            }

            ClearPendingVote();
            _ = BroadcastMessage($"Track '{requestedTrack}' is not allowed for voting. Use !search <text> to find valid track IDs.");
        }
        else if (IsLuckyCommand(lower))
        {
            StartLuckyVote(playerName);
        }
        else if (lower == "!confirm" || lower.StartsWith("!confirm "))
        {
            ConfirmPendingVote(playerName, message);
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
               lower == "!confirm" ||
               lower.StartsWith("!confirm ") ||
               IsLuckyCommand(lower) ||
               lower == "!search" ||
               lower.StartsWith("!search ") ||
               lower == "!more";
    }

    private static bool IsLuckyCommand(string lower)
    {
        return lower is "!lucky" or "!ifeellucky" or "!ifeeelucky";
    }

    private VoteTrackResolution ResolveVoteTrack(string query)
    {
        var tracks = GetAllowedTracks();
        var normalizedQuery = NormalizeTrackText(query);
        if (string.IsNullOrWhiteSpace(normalizedQuery))
        {
            return new VoteTrackResolution(VoteTrackResolutionKind.None, null, []);
        }

        var exactTrack = tracks.FirstOrDefault(track =>
            string.Equals(track.Id, query, StringComparison.OrdinalIgnoreCase) ||
            NormalizeTrackText(track.Id) == normalizedQuery ||
            NormalizeTrackText(track.Name) == normalizedQuery);
        if (exactTrack != null)
        {
            return new VoteTrackResolution(VoteTrackResolutionKind.Exact, exactTrack, []);
        }

        var substringMatches = tracks
            .Where(track => TrackContainsNormalizedQuery(track, normalizedQuery))
            .OrderBy(track => GetSubstringMatchSortScore(track, normalizedQuery))
            .ThenBy(track => track.Id, StringComparer.OrdinalIgnoreCase)
            .Take(VoteConfirmationOptionLimit)
            .ToList();

        if (substringMatches.Count == 1)
        {
            return new VoteTrackResolution(VoteTrackResolutionKind.Exact, substringMatches[0], []);
        }

        if (substringMatches.Count > 1)
        {
            return new VoteTrackResolution(VoteTrackResolutionKind.Ambiguous, null, substringMatches);
        }

        var fuzzyMatches = tracks
            .Select(track => new { Track = track, Score = GetFuzzyMatchScore(track, normalizedQuery) })
            .Where(match => match.Score < int.MaxValue)
            .OrderBy(match => match.Score)
            .ThenBy(match => match.Track.Id.Length)
            .ThenBy(match => match.Track.Id, StringComparer.OrdinalIgnoreCase)
            .Take(VoteConfirmationOptionLimit)
            .Select(match => match.Track)
            .ToList();

        return fuzzyMatches.Count > 0
            ? new VoteTrackResolution(VoteTrackResolutionKind.Fuzzy, null, fuzzyMatches)
            : new VoteTrackResolution(VoteTrackResolutionKind.None, null, []);
    }

    private List<AllowedVoteTrack> GetAllowedTracks()
    {
        return _configuration
            .GetSection("Vote:AllowedTracks")
            .Get<List<AllowedVoteTrack>>() ?? new List<AllowedVoteTrack>();
    }

    private void StartLuckyVote(string playerName)
    {
        var tracks = GetAllowedTracks();
        if (tracks.Count == 0)
        {
            _ = BroadcastMessage("No tracks configured for lucky vote.");
            return;
        }

        var track = tracks[Random.Shared.Next(tracks.Count)];
        var laps = GetRandomLuckyLapCount();

        _ = BroadcastMessage($"Lucky pick: {FormatTrackSearchResult(track)}, {laps} laps.");
        StartVote(playerName, track.Id, laps);
    }

    private int GetRandomLuckyLapCount()
    {
        var maxLaps = MaxLapsAllowed;
        var totalWeight = Enumerable.Range(1, maxLaps).Sum(GetLuckyLapWeight);
        var roll = Random.Shared.Next(totalWeight);

        for (var laps = 1; laps <= maxLaps; laps++)
        {
            roll -= GetLuckyLapWeight(laps);
            if (roll < 0)
            {
                return laps;
            }
        }

        return maxLaps;
    }

    private static int GetLuckyLapWeight(int laps)
    {
        return Math.Max(1, 10 - Math.Abs(laps - 4) * 3);
    }

    private void StorePendingVote(string playerName, int laps, List<AllowedVoteTrack> options)
    {
        lock (_pendingVoteLock)
        {
            _pendingVoteRequester = playerName;
            _pendingVoteLaps = laps;
            _pendingVoteOptions = options.ToList();
        }
    }

    private void ClearPendingVote()
    {
        lock (_pendingVoteLock)
        {
            _pendingVoteRequester = null;
            _pendingVoteLaps = 0;
            _pendingVoteOptions.Clear();
        }
    }

    private void ConfirmPendingVote(string playerName, string message)
    {
        var parts = message.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !int.TryParse(parts[1], out var optionNumber))
        {
            _ = BroadcastMessage("Usage: !confirm <number>");
            return;
        }

        AllowedVoteTrack selectedTrack;
        int laps;
        lock (_pendingVoteLock)
        {
            if (_pendingVoteOptions.Count == 0 || _pendingVoteRequester == null)
            {
                _ = BroadcastMessage("No vote confirmation is pending.");
                return;
            }

            if (!string.Equals(_pendingVoteRequester, playerName, StringComparison.OrdinalIgnoreCase))
            {
                _ = BroadcastMessage($"Only {_pendingVoteRequester} can confirm this vote.");
                return;
            }

            if (optionNumber < 1 || optionNumber > _pendingVoteOptions.Count)
            {
                _ = BroadcastMessage($"Invalid confirmation option. Choose 1-{_pendingVoteOptions.Count}.");
                return;
            }

            selectedTrack = _pendingVoteOptions[optionNumber - 1];
            laps = _pendingVoteLaps;
            _pendingVoteRequester = null;
            _pendingVoteLaps = 0;
            _pendingVoteOptions.Clear();
        }

        StartVote(playerName, selectedTrack.Id, laps);
    }

    private static List<string> FormatVoteConfirmationOptions(string label, string query, IReadOnlyList<AllowedVoteTrack> tracks)
    {
        var messages = new List<string> { $"{label} for '{query}':" };
        for (var i = 0; i < tracks.Count; i++)
        {
            messages.Add($"{i + 1}. {FormatTrackSearchResult(tracks[i])}");
        }

        messages.Add("Type !confirm <number> to start vote.");
        return messages;
    }

    private List<string> GetConfigMessages()
    {
        var inputMode = GetConfiguredValue("WreckfestServer:InputMode", ServerInputModes.ConsoleWriter);
        var outputMode = GetConfiguredValue("WreckfestServer:OutputMode", GetConfiguredOutputModeFallback());
        var hookConnected = _serverManager.IsConsoleHookConnected ? "yes" : "no";
        var outputPrimary = _serverManager.ProcessConsoleHookOutput ? "yes" : "no";
        var votingEnabled = VotingEnabled ? "enabled" : "disabled";
        var allowedTrackCount = GetAllowedTracks().Count;

        return
        [
            $"Config: input={inputMode}, output={outputMode}, hookConnected={hookConnected}, outputPrimary={outputPrimary}",
            $"Config: voting={votingEnabled}, maxLaps={MaxLapsAllowed}, voteTimeout={VoteTimeoutSeconds}s, messageDelay={MessageDelayMs}ms, allowedTracks={allowedTrackCount}"
        ];
    }

    private List<string> GetDebugMessages()
    {
        var (humans, total) = _playerTracker.GetPlayerCount();
        var players = _playerTracker.GetPlayers();
        var bots = Math.Max(0, total - humans);
        var humanPlayers = players
            .Where(player => !player.IsBot)
            .Take(8)
            .Select(player => $"{player.Slot?.ToString() ?? "?"}:{player.Name}")
            .ToList();

        string voteState;
        int yesCount;
        int noCount;
        lock (_stateLock)
        {
            voteState = _state == VoteState.Active
                ? $"active track={_votedTrackId} laps={_votedLaps}"
                : "idle";
            yesCount = _yesVoters.Count;
            noCount = _noVoters.Count;
        }

        int pendingConfirmCount;
        lock (_pendingVoteLock)
        {
            pendingConfirmCount = _pendingVoteOptions.Count;
        }

        var messages = new List<string>
        {
            $"Debug: players humans={humans}, total={total}, bots={bots}; vote={voteState}, yes={yesCount}, no={noCount}",
            $"Debug: pendingConfirm={pendingConfirmCount}, searchBuffer={GetBufferedSearchResultCount()}"
        };

        messages.Add(humanPlayers.Count == 0
            ? "Debug: humanPlayers=none"
            : $"Debug: humanPlayers={string.Join(", ", humanPlayers)}");

        return messages;
    }

    private string GetConfiguredValue(string key, string fallback)
    {
        var value = _configuration[key];
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private string GetConfiguredOutputModeFallback()
    {
        return _configuration.GetValue("WreckfestServer:UseConsoleMonitoring", true)
            ? ServerOutputModes.ConsoleReader
            : ServerOutputModes.LogFile;
    }

    private static bool TrackContainsNormalizedQuery(AllowedVoteTrack track, string normalizedQuery)
    {
        return NormalizeTrackText(track.Id).Contains(normalizedQuery, StringComparison.Ordinal) ||
               NormalizeTrackText(track.Name).Contains(normalizedQuery, StringComparison.Ordinal);
    }

    private static int GetSubstringMatchSortScore(AllowedVoteTrack track, string normalizedQuery)
    {
        var normalizedName = NormalizeTrackText(track.Name);
        var normalizedId = NormalizeTrackText(track.Id);
        if (normalizedName.Contains(normalizedQuery, StringComparison.Ordinal))
        {
            if (normalizedName.EndsWith(normalizedQuery, StringComparison.Ordinal))
            {
                return normalizedName.Length;
            }

            if (normalizedName.Contains("reverse", StringComparison.Ordinal))
            {
                return 100 + normalizedName.Length;
            }

            return 200 + normalizedName.Length;
        }

        if (normalizedId.Contains("reverse", StringComparison.Ordinal))
        {
            return 300 + normalizedId.Length;
        }

        return 400 + normalizedId.Length;
    }

    private static int GetFuzzyMatchScore(AllowedVoteTrack track, string normalizedQuery)
    {
        var queryTokens = GetTrackTokens(normalizedQuery)
            .Where(token => token.Length >= 3)
            .ToList();
        if (queryTokens.Count == 0)
        {
            return int.MaxValue;
        }

        var trackTokens = GetTrackTokens($"{NormalizeTrackText(track.Id)} {NormalizeTrackText(track.Name)}")
            .Where(token => token.Length >= 3)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (trackTokens.Count == 0)
        {
            return int.MaxValue;
        }

        var totalScore = 0;
        foreach (var queryToken in queryTokens)
        {
            var bestTokenScore = trackTokens.Min(trackToken => GetTokenMatchScore(queryToken, trackToken));
            if (bestTokenScore > 3)
            {
                return int.MaxValue;
            }

            totalScore += bestTokenScore;
        }

        return totalScore;
    }

    private static int GetTokenMatchScore(string queryToken, string trackToken)
    {
        if (queryToken == trackToken)
        {
            return 0;
        }

        if (trackToken.Contains(queryToken, StringComparison.Ordinal) ||
            queryToken.Contains(trackToken, StringComparison.Ordinal))
        {
            return 1;
        }

        var distance = GetLevenshteinDistance(queryToken, trackToken);
        var allowedDistance = queryToken.Length <= 5 ? 1 : 2;
        if (distance <= allowedDistance)
        {
            return distance + 1;
        }

        return GetSoundex(queryToken) == GetSoundex(trackToken) ? 3 : int.MaxValue;
    }

    private static string NormalizeTrackText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = new char[value.Length];
        for (var i = 0; i < value.Length; i++)
        {
            var c = char.ToLowerInvariant(value[i]);
            normalized[i] = char.IsLetterOrDigit(c) ? c : ' ';
        }

        return string.Join(' ', new string(normalized).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static List<string> GetTrackTokens(string normalizedText)
    {
        return normalizedText
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToList();
    }

    private static int GetLevenshteinDistance(string left, string right)
    {
        if (left.Length == 0)
        {
            return right.Length;
        }

        if (right.Length == 0)
        {
            return left.Length;
        }

        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];

        for (var j = 0; j <= right.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                var cost = left[i - 1] == right[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    private static string GetSoundex(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var firstLetter = char.ToUpperInvariant(value[0]);
        var digits = new List<char> { firstLetter };
        var previousDigit = GetSoundexDigit(firstLetter);

        foreach (var c in value.Skip(1).Select(char.ToUpperInvariant))
        {
            var digit = GetSoundexDigit(c);
            if (digit == '0')
            {
                previousDigit = digit;
                continue;
            }

            if (digit != previousDigit)
            {
                digits.Add(digit);
            }

            previousDigit = digit;
            if (digits.Count == 4)
            {
                break;
            }
        }

        while (digits.Count < 4)
        {
            digits.Add('0');
        }

        return new string(digits.ToArray());
    }

    private static char GetSoundexDigit(char c)
    {
        return c switch
        {
            'B' or 'F' or 'P' or 'V' => '1',
            'C' or 'G' or 'J' or 'K' or 'Q' or 'S' or 'X' or 'Z' => '2',
            'D' or 'T' => '3',
            'L' => '4',
            'M' or 'N' => '5',
            'R' => '6',
            _ => '0'
        };
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
        var passedImmediately = false;
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

            var humanCount = _playerTracker.GetPlayerCount().online;
            passedImmediately = HasMajority(_yesVoters.Count, humanCount);
            if (passedImmediately)
            {
                ResetVoteState();
            }
            else
            {
                var timeout = VoteTimeoutSeconds;
                _voteTimer?.Dispose();
                _voteTimer = new System.Threading.Timer(_ => TallyVotes(), null,
                    TimeSpan.FromSeconds(timeout), Timeout.InfiniteTimeSpan);
                ScheduleVoteStatusTimers(timeout);
            }

            _logger.LogInformation("Vote started by {Initiator}: {TrackId} for {Laps} laps ({Timeout}s timeout)",
                initiator, trackId, laps, VoteTimeoutSeconds);
        }

        _ = BroadcastMessages(FormatVoteStartedMessages(initiator, trackId, laps));

        if (passedImmediately)
        {
            _ = ApplyVotedTrack(trackId, laps);
        }
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

    private List<string> FormatVoteStartedMessages(string initiator, string trackId, int laps)
    {
        var trackDisplayName = GetTrackDisplayName(trackId);
        var suffix = $" - {laps} laps";
        var firstLine = $"Vote: {TruncateToFit(trackDisplayName, ChatMessageCharacterLimit - "Vote: ".Length - suffix.Length)}{suffix}";
        var secondLine = FormatVoteStartedInstructionLine(initiator);

        return [firstLine, secondLine];
    }

    private string GetTrackDisplayName(string trackId)
    {
        var track = GetAllowedTracks()
            .FirstOrDefault(track => string.Equals(track.Id, trackId, StringComparison.OrdinalIgnoreCase));

        return string.IsNullOrWhiteSpace(track?.Name) ? trackId : track.Name.Trim();
    }

    private string FormatVoteStartedInstructionLine(string initiator)
    {
        const string prefix = "By ";
        var suffix = $". Type !yes or !no. Ends in {VoteTimeoutSeconds}s.";
        return $"{prefix}{TruncateToFit(initiator, ChatMessageCharacterLimit - prefix.Length - suffix.Length)}{suffix}";
    }

    private static string TruncateToFit(string value, int maxLength)
    {
        if (maxLength <= 0)
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        if (trimmed.Length <= maxLength)
        {
            return trimmed;
        }

        return maxLength <= 3
            ? trimmed[..maxLength]
            : $"{trimmed[..(maxLength - 3)]}...";
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
        var messageList = messages.ToList();
        for (var i = 0; i < messageList.Count; i++)
        {
            await BroadcastMessage(messageList[i]);

            if (i < messageList.Count - 1 && MessageDelayMs > 0)
            {
                await Task.Delay(MessageDelayMs);
            }
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
