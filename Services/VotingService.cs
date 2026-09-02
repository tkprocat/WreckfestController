using Microsoft.Extensions.Configuration;
using WreckfestController.Models;

namespace WreckfestController.Services;

public class VotingService
{
    private enum VoteTrackResolutionKind { None, Exact, Ambiguous, Fuzzy }
    private sealed record VoteTrackResolution(VoteTrackResolutionKind Kind, AllowedVoteTrack? Track, List<AllowedVoteTrack> Options);
    private enum VotePlayerRefreshResult { UnavailableOrFailed, Refreshed, RefreshedNoHumans }

    private readonly ServerManager _serverManager;
    private readonly PlayerTracker _playerTracker;
    private readonly ConfigService _configService;
    private readonly ILogger<VotingService> _logger;
    private readonly IConfiguration _configuration;

    private enum VoteState { Idle, Active }
    private VoteState _state = VoteState.Idle;
    private string? _votedTrackId;
    private int? _votedLaps;
    private string? _voteInitiator;
    private DateTime _voteStartedUtc;
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
    private readonly object _directChangeLock = new();
    private DateTime _lastDirectChangeUtc = DateTime.MinValue;
    private string? _lastDirectChangeBy;
    private List<AllowedVoteTrack> _pendingVoteOptions = new();
    private string? _pendingVoteRequester;
    private int? _pendingVoteLaps;

    private string VoteMode => VoteModes.Normalize(
        _configuration["Vote:Mode"],
        _configuration.GetValue<bool?>("Vote:Enabled"));

    private bool VotingEnabled => VoteMode != VoteModes.Off;

    /// <summary>
    /// Whether a vote is currently running. One accessor so callers do not read
    /// <c>_state</c> without the lock.
    /// </summary>
    private bool VoteInProgress
    {
        get { lock (_stateLock) { return _state == VoteState.Active; } }
    }
    private bool DirectModeEnabled => VoteMode == VoteModes.Direct;
    private int DirectCooldownSeconds =>
        Math.Clamp(_configuration.GetValue<int?>("Vote:DirectCooldownSeconds") ?? 30, 0, 3600);
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

        _playerTracker.MarkPlayerSeen(playerName, isBot: false);

        var lower = message.ToLowerInvariant().Trim();

        // !track is an alias of !vote; the configured mode decides what the command
        // does. Rewriting to the canonical form keeps the Substring(6) arithmetic
        // below correct and means IsVotingCommand needs no knowledge of the alias.
        if (lower == "!track" || lower.StartsWith("!track "))
        {
            message = string.Concat("!vote", message.Trim().AsSpan("!track".Length));
            lower = message.ToLowerInvariant().Trim();
        }

        // Before any gating: if the mode changed out of Voting while a vote was live,
        // retire it now. Otherwise the disabled-command gate below would return first
        // and the stale vote would linger until its timer fired.
        CancelVoteIfModeChanged();

        // Every reply goes to all players via /message, so a command run mid-race
        // puts several lines across everyone's screen while they are driving. Suppress
        // the lot until the race is over. Blocking here is safe: this runs on
        // ServerManager's chat worker, not on the thread draining the hook pipe.
        if (IsRacingBlocking())
        {
            // The refusal is itself a broadcast, so rate-limit it - otherwise a few
            // players typing commands reproduces the spam we are preventing.
            var announce = false;
            lock (_serverStateLock)
            {
                if (DateTime.UtcNow - _lastRaceRefusalUtc > RaceRefusalWindow)
                {
                    _lastRaceRefusalUtc = DateTime.UtcNow;
                    announce = true;
                }
            }

            if (announce)
            {
                _ = BroadcastMessage("Chat commands are disabled during a race.");
            }

            return;
        }

        if (lower == "!help")
        {
            if (VotingEnabled)
            {
                _ = BroadcastMessages(GetHelpMessages());
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

        if (lower == "!eventloop" || lower.StartsWith("!eventloop "))
        {
            // Unlisted in !help and silent for everyone else: a hidden command that
            // answers back still advertises its own existence.
            if (IsPrivileged(playerName))
            {
                _ = HandleEventLoopCommandAsync(lower);
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
            // Checked before parsing or track resolution: resolution runs fuzzy matching
            // and StartVote blocks on a hook round-trip, so reaching the guard down there
            // wastes that work and can surface the wrong error first.
            if (RefuseWhileVoteInProgress(playerName))
            {
                return;
            }

            if (!TryParseTrackRequest(message.Substring(6), out var requestedTrack, out var laps, out var parseError))
            {
                _ = BroadcastMessage(parseError!);
                return;
            }

            var resolvedTrack = ResolveVoteTrack(requestedTrack);
            if (resolvedTrack.Kind == VoteTrackResolutionKind.Exact && resolvedTrack.Track != null)
            {
                ClearPendingVote();
                StartTrackChange(playerName, resolvedTrack.Track.Id, laps);
                return;
            }

            if (resolvedTrack.Options.Count > 0)
            {
                StorePendingVote(playerName, laps, resolvedTrack.Options);
                var label = resolvedTrack.Kind == VoteTrackResolutionKind.Fuzzy
                    ? "Possible matches"
                    : "Multiple matches";
                _ = BroadcastMessages(FormatVoteConfirmationOptions(
                    label,
                    requestedTrack,
                    resolvedTrack.Options,
                    DirectModeEnabled ? "change track" : "start vote"));
                return;
            }

            ClearPendingVote();
            _ = BroadcastMessage($"Track '{requestedTrack}' is not allowed for voting. Use !search <text> to find valid track IDs.");
        }
        else if (lower == "!vote")
        {
            _ = BroadcastMessage($"Usage: !track <trackId> [laps] (laps must be between 1 and {MaxLapsAllowed})");
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
               lower == "!vote" ||
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

    /// <summary>
    /// Parses "&lt;track query&gt; [laps]". Laps are optional: when omitted the server keeps
    /// its current lap count and no laps= command is sent.
    /// </summary>
    private bool TryParseTrackRequest(string arguments, out string trackQuery, out int? laps, out string? error)
    {
        trackQuery = string.Empty;
        laps = null;
        error = null;

        var parts = (arguments ?? string.Empty).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            error = $"Usage: !track <trackId> [laps] (laps must be between 1 and {MaxLapsAllowed})";
            return false;
        }

        // A trailing integer is a lap count only when something precedes it; a lone
        // number is not a meaningful track query.
        if (parts.Length > 1 && int.TryParse(parts[^1], out var parsedLaps))
        {
            if (parsedLaps < 1 || parsedLaps > MaxLapsAllowed)
            {
                error = $"Invalid laps: must be between 1 and {MaxLapsAllowed}.";
                return false;
            }

            laps = parsedLaps;
            parts = parts[..^1];
        }
        else if (parts.Length == 1 && int.TryParse(parts[0], out _))
        {
            error = $"Usage: !track <trackId> [laps] (laps must be between 1 and {MaxLapsAllowed})";
            return false;
        }

        trackQuery = string.Join(" ", parts);
        return true;
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
        if (RefuseWhileVoteInProgress(playerName))
        {
            return;
        }

        var tracks = GetAllowedTracks();
        if (tracks.Count == 0)
        {
            _ = BroadcastMessage("No tracks configured for lucky vote.");
            return;
        }

        var track = tracks[Random.Shared.Next(tracks.Count)];
        var laps = GetRandomLuckyLapCount();

        _ = BroadcastMessage($"Lucky pick: {FormatTrackSearchResult(track)}, {laps} laps.");
        StartTrackChange(playerName, track.Id, laps);
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

    private void StorePendingVote(string playerName, int? laps, List<AllowedVoteTrack> options)
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
            _pendingVoteLaps = null;
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
        int? laps;
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
            _pendingVoteLaps = null;
            _pendingVoteOptions.Clear();
        }

        StartTrackChange(playerName, selectedTrack.Id, laps);
    }

    private static List<string> FormatVoteConfirmationOptions(
        string label,
        string query,
        IReadOnlyList<AllowedVoteTrack> tracks,
        string confirmAction)
    {
        var messages = new List<string> { $"{label} for '{query}':" };
        for (var i = 0; i < tracks.Count; i++)
        {
            messages.Add($"{i + 1}. {FormatTrackSearchResult(tracks[i])}");
        }

        messages.Add($"Type !confirm <number> to {confirmAction}.");
        return messages;
    }

    private List<string> GetHelpMessages()
    {
        // !vote and !track are the same command; advertise whichever verb matches
        // what the configured mode will actually do.
        if (DirectModeEnabled)
        {
            return
            [
                $"Help: max laps is {MaxLapsAllowed}.",
                "Help: !track <trackId> [laps] - change the track now. Example: !track misc_bsv 6",
                $"Help: after a change, the next one waits {DirectCooldownSeconds}s (admins bypass).",
                "Help: !search <text> - find track IDs. Example: !search tvtp misc",
                "Help: !more - show the next search results.",
                "Help: !lucky - pick a random track/laps. Alias: !ifeellucky."
            ];
        }

        return
        [
            $"Help: max laps is {MaxLapsAllowed}.",
            "Help: !track <trackId> [laps] - start a vote. Example: !track misc_bsv 6",
            "Help: !yes - vote yes on the active vote.",
            "Help: !no - vote no on the active vote.",
            "Help: !search <text> - find track IDs. Example: !search tvtp misc",
            "Help: !more - show the next search results.",
            "Help: !lucky - vote on a random track/laps. Alias: !ifeellucky."
        ];
    }

    private List<string> GetConfigMessages()
    {
        var hookConnected = _serverManager.IsConsoleHookConnected ? "yes" : "no";
        var outputPrimary = _serverManager.ProcessConsoleHookOutput ? "yes" : "no";
        var allowedTrackCount = GetAllowedTracks().Count;
        var modeDetail = DirectModeEnabled
            ? $"cooldown={DirectCooldownSeconds}s"
            : $"voteTimeout={VoteTimeoutSeconds}s";

        return
        [
            $"Config: hookConnected={hookConnected}, outputPrimary={outputPrimary}",
            $"Config: mode={VoteMode.ToLowerInvariant()}, {modeDetail}, maxLaps={MaxLapsAllowed}, messageDelay={MessageDelayMs}ms, allowedTracks={allowedTrackCount}"
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

    /// <summary>
    /// Replies to the requester and returns true when a vote is already running.
    /// The authoritative check remains inside StartVote under _stateLock; this one
    /// exists to fail fast and to say something useful.
    /// </summary>
    private bool RefuseWhileVoteInProgress(string playerName)
    {
        string? trackId;
        int? laps;
        DateTime startedUtc;

        lock (_stateLock)
        {
            if (_state != VoteState.Active)
            {
                return false;
            }

            trackId = _votedTrackId;
            laps = _votedLaps;
            startedUtc = _voteStartedUtc;
        }

        var remaining = TimeSpan.FromSeconds(VoteTimeoutSeconds) - (DateTime.UtcNow - startedUtc);
        var seconds = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
        var lapsClause = laps is null ? string.Empty : $" for {laps} laps";
        var trackName = GetTrackDisplayName(trackId ?? string.Empty);

        var prefix = $"{playerName}: vote in progress - ";
        var suffix = $"{lapsClause}, {seconds}s left. Type !yes or !no.";
        var budget = ChatMessageCharacterLimit - prefix.Length - suffix.Length;

        _ = BroadcastMessage($"{prefix}{TruncateToFit(trackName, budget)}{suffix}");
        return true;
    }

    /// <summary>
    /// Single entry point for "make this the next track". Votes on it or applies it
    /// immediately depending on the configured mode.
    /// </summary>
    private void StartTrackChange(string playerName, string trackId, int? laps)
    {
        // The event loop owns track selection when it is running - Wreckfest rotates
        // and runs its own end-of-race track vote - so a track set here would just be
        // overwritten, or fight it. Refuse rather than race the rotation.
        var loop = ReadEventLoopBlocking();
        if (loop is { Enabled: true })
        {
            _ = BroadcastMessage(
                "Track changes are disabled while the event loop is running.");
            return;
        }

        if (!DirectModeEnabled)
        {
            StartVote(playerName, trackId, laps);
            return;
        }

        // A vote can still be running if the mode was switched to Direct while it was
        // live. Racing two track= writes would be worse than making the caller wait,
        // and admins do not bypass this - it is a consistency guard, not a permission.
        if (VoteInProgress && RefuseWhileVoteInProgress(playerName))
        {
            return;
        }

        _ = ApplyDirectTrackChangeAsync(playerName, trackId, laps);
    }

    /// <summary>
    /// Reserves the right to change the track now. Reserving optimistically (rather
    /// than stamping after the server call) closes the window where two players both
    /// pass the check; a failed apply rolls the reservation back.
    /// </summary>
    private bool TryReserveDirectChange(
        string playerName,
        out string? refusal,
        out DateTime previousUtc,
        out string? previousBy)
    {
        refusal = null;

        lock (_directChangeLock)
        {
            previousUtc = _lastDirectChangeUtc;
            previousBy = _lastDirectChangeBy;

            var cooldown = DirectCooldownSeconds;

            // A lone human has nobody to fight over the track with, so the cooldown
            // has nothing to protect against. Same threshold StartVote uses to pass a
            // vote immediately.
            var soloPlayer = _playerTracker.GetPlayerCount().online <= 1;

            if (cooldown > 0 && !soloPlayer && !IsPrivileged(playerName))
            {
                var remaining = TimeSpan.FromSeconds(cooldown) - (DateTime.UtcNow - _lastDirectChangeUtc);
                if (remaining > TimeSpan.Zero)
                {
                    // Round up: telling someone "2s" when 2.9s remain earns them a
                    // second refusal for waiting exactly as long as they were told.
                    var seconds = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
                    refusal = FormatCooldownRefusal(playerName, seconds);
                    return false;
                }
            }

            _lastDirectChangeUtc = DateTime.UtcNow;
            _lastDirectChangeBy = playerName;
            return true;
        }
    }

    private void RollBackDirectChange(DateTime previousUtc, string? previousBy)
    {
        lock (_directChangeLock)
        {
            _lastDirectChangeUtc = previousUtc;
            _lastDirectChangeBy = previousBy;
        }
    }

    private string FormatCooldownRefusal(string playerName, int seconds)
    {
        var samePlayer = string.Equals(_lastDirectChangeBy, playerName, StringComparison.OrdinalIgnoreCase);
        var suffix = $" Try again in {seconds}s.";

        if (samePlayer)
        {
            const string middle = ": you just changed the track.";
            var nameBudget = ChatMessageCharacterLimit - middle.Length - suffix.Length;
            return $"{TruncateToFit(playerName, nameBudget)}{middle}{suffix}";
        }

        var other = _lastDirectChangeBy ?? "someone";
        const string joiner = ": track was just changed by ";
        // Two unbounded names share the remaining budget.
        var budget = (ChatMessageCharacterLimit - joiner.Length - suffix.Length - 1) / 2;
        return $"{TruncateToFit(playerName, budget)}{joiner}{TruncateToFit(other, budget)}.{suffix}";
    }

    /// <summary>
    /// Moderators and admins alike bypass the direct-change cooldown and may override
    /// a change someone else just made.
    /// </summary>
    // Server-state globals, module-relative. Found by decoding the RIP-relative
    // operands inside the game's own "is the event loop enabled" getter
    // (FUN_1402dd490 at RVA 0x002DD490), then confirmed live by toggling
    // /eventloop and watching them move. Build-specific: a Wreckfest patch will
    // shift them, which is why every read is sanity-checked and falls open.
    private const uint RvaEventLoopCount = 0x1857630;   // int32: number of el_add entries
    private const uint RvaEventLoopIndex = 0x122B270;   // int32: current entry, -1 when off
    private const uint RvaSessionLobby = 0x19146E0;     // byte: 1 in lobby, 0 while racing
    private const uint RvaSessionRacing = 0x19146EC;    // byte: 1 while racing or voting

    private static readonly TimeSpan ServerStateCacheWindow = TimeSpan.FromSeconds(2);
    private readonly object _serverStateLock = new();
    private static readonly TimeSpan RaceRefusalWindow = TimeSpan.FromSeconds(30);
    private DateTime _lastRaceRefusalUtc = DateTime.MinValue;
    private DateTime _serverStateReadUtc = DateTime.MinValue;
    private (bool? EventLoopEnabled, int Index, int Count, bool? Racing) _serverState;

    private sealed record EventLoopState(bool Enabled, int Index, int Count);

    /// <summary>
    /// Reads event-loop and session state from the running server. Every field is
    /// nullable-by-convention: a failed or implausible read yields null so callers
    /// fail open rather than acting on a state we do not actually know.
    /// </summary>
    private async Task<(EventLoopState? Loop, bool? Racing)> ReadServerStateAsync()
    {
        lock (_serverStateLock)
        {
            if (DateTime.UtcNow - _serverStateReadUtc < ServerStateCacheWindow)
            {
                var cachedLoop = _serverState.EventLoopEnabled is bool enabled
                    ? new EventLoopState(enabled, _serverState.Index, _serverState.Count)
                    : null;
                return (cachedLoop, _serverState.Racing);
            }
        }

        EventLoopState? loop = null;
        bool? racing = null;

        var countBytes = await _serverManager.ReadHookMemoryAsync(RvaEventLoopCount, 4);
        var indexBytes = await _serverManager.ReadHookMemoryAsync(RvaEventLoopIndex, 4);
        if (countBytes?.Length == 4 && indexBytes?.Length == 4)
        {
            var count = BitConverter.ToInt32(countBytes);
            var index = BitConverter.ToInt32(indexBytes);

            // Reject implausible values rather than trusting a stale offset after a
            // game patch: an entry count outside 0..256, or an index that is neither
            // -1 nor a valid position, means we are not reading what we think.
            if (count >= 0 && count <= 256 && index >= -1 && index < Math.Max(count, 1))
            {
                loop = new EventLoopState(count > 0 && index > -1, index, count);
            }
        }

        var lobbyBytes = await _serverManager.ReadHookMemoryAsync(RvaSessionLobby, 1);
        var racingBytes = await _serverManager.ReadHookMemoryAsync(RvaSessionRacing, 1);
        if (lobbyBytes?.Length == 1 && racingBytes?.Length == 1)
        {
            // Only the combination positively observed while driving counts as
            // racing. Lobby, the post-race vote screen and any state not yet mapped
            // fall through as "not racing", so an unknown state never silences chat.
            racing = lobbyBytes[0] == 0 && racingBytes[0] == 1;
        }

        lock (_serverStateLock)
        {
            _serverState = (loop?.Enabled, loop?.Index ?? 0, loop?.Count ?? 0, racing);
            _serverStateReadUtc = DateTime.UtcNow;
        }

        return (loop, racing);
    }

    /// <summary>
    /// True only when the server is positively identified as racing. An unreadable
    /// or unmapped state returns false so chat keeps working.
    /// </summary>
    private bool IsRacingBlocking()
    {
        try
        {
            var (_, racing) = ReadServerStateAsync().ConfigureAwait(false).GetAwaiter().GetResult();
            return racing == true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read session state; allowing chat command");
            return false;
        }
    }

    private EventLoopState? ReadEventLoopBlocking()
    {
        try
        {
            var (loop, _) = ReadServerStateAsync().ConfigureAwait(false).GetAwaiter().GetResult();
            return loop;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read event loop state");
            return null;
        }
    }

    private void InvalidateServerState()
    {
        lock (_serverStateLock)
        {
            _serverStateReadUtc = DateTime.MinValue;
        }
    }

    private async Task HandleEventLoopCommandAsync(string lower)
    {
        const string prefix = "!eventloop";
        var argument = lower.Length > prefix.Length ? lower[prefix.Length..].Trim() : string.Empty;

        InvalidateServerState();
        var (loop, _) = await ReadServerStateAsync();
        if (loop is null)
        {
            await BroadcastMessage("Event loop state unavailable - is the console hook injected?");
            return;
        }

        if (argument.Length == 0)
        {
            await BroadcastMessage(FormatEventLoopStatus(loop));
            return;
        }

        bool desired;
        switch (argument)
        {
            case "on": desired = true; break;
            case "off": desired = false; break;
            default:
                await BroadcastMessage("Usage: !eventloop [on|off]");
                return;
        }

        if (loop.Enabled == desired)
        {
            await BroadcastMessage($"Event loop is already {(desired ? "on" : "off")}.");
            return;
        }

        // The server command is a plain toggle with no argument, so we only send it
        // once we know the current state differs from what was asked for.
        var result = await _serverManager.SendCommandAsync("/eventloop");
        if (!result.Success)
        {
            await BroadcastMessage("Failed to change the event loop.");
            return;
        }

        // Read back rather than assume: /rotate and the game itself can also change
        // this, and a toggle that silently did nothing would otherwise look like it
        // worked. The game does not apply it synchronously, so poll briefly instead
        // of reading once - a single immediate read sees the old value and wrongly
        // reports failure.
        var after = await WaitForEventLoopStateAsync(desired);
        if (after is null || after.Enabled != desired)
        {
            await BroadcastMessage($"Event loop did not change - it is still {(after?.Enabled == true ? "on" : "off")}.");
            return;
        }

        await BroadcastMessage(FormatEventLoopStatus(after));
    }

    private async Task<EventLoopState?> WaitForEventLoopStateAsync(bool desired)
    {
        EventLoopState? latest = null;

        for (var attempt = 0; attempt < 8; attempt++)
        {
            await Task.Delay(250);
            InvalidateServerState();

            var (loop, _) = await ReadServerStateAsync();
            latest = loop ?? latest;

            if (loop?.Enabled == desired)
            {
                return loop;
            }
        }

        return latest;
    }

    /// <summary>
    /// One line only. A real rotation does not fit in the 127-character chat limit,
    /// and truncating it just loses the tail; the entry position already says where
    /// the loop is, and server_config.cfg is where the full list lives.
    /// </summary>
    private static string FormatEventLoopStatus(EventLoopState loop)
    {
        var state = loop.Enabled ? "on" : "off";
        var position = loop.Enabled && loop.Count > 0
            ? $" (entry {loop.Index + 1}/{loop.Count})"
            : $" ({loop.Count} entries)";

        return $"Event loop: {state}{position}";
    }

    private bool IsPrivileged(string playerName)
    {
        RefreshPlayersFromHookIfAvailable();

        return _playerTracker.GetPlayers().Any(p =>
            p.IsPrivileged &&
            !p.IsBot &&
            string.Equals(p.Name, playerName, StringComparison.OrdinalIgnoreCase));
    }

    private async Task ApplyDirectTrackChangeAsync(string playerName, string trackId, int? laps)
    {
        if (!TryReserveDirectChange(playerName, out var refusal, out var previousUtc, out var previousBy))
        {
            await BroadcastMessage(refusal!);
            return;
        }

        var trackDisplayName = GetTrackDisplayName(trackId);

        // Attribution only earns its place when somebody else is around to read it.
        // Alone, "(set by you)" is noise on a line whose only job is confirming the
        // server took the request.
        var attributed = _playerTracker.GetPlayerCount().online > 1;

        var messages = new TrackChangeMessages(
            (_, appliedLaps) =>
            {
                const string prefix = "Next race: ";
                var lapsPart = appliedLaps is null ? string.Empty : $" ({appliedLaps} laps)";
                var byPart = attributed ? $" - set by {playerName}" : string.Empty;
                var budget = ChatMessageCharacterLimit - prefix.Length - lapsPart.Length - byPart.Length;
                return $"{prefix}{TruncateToFit(trackDisplayName, budget)}{lapsPart}{byPart}";
            },
            "Failed to change track.",
            "Track changed but failed to update laps.");

        if (!await ApplyTrackChange(trackId, laps, messages))
        {
            // The server rejected it, so this attempt should not consume the window.
            RollBackDirectChange(previousUtc, previousBy);
        }
    }

    private void StartVote(string initiator, string trackId, int? laps)
    {
        if (RefreshPlayersFromHookIfAvailable() == VotePlayerRefreshResult.RefreshedNoHumans)
        {
            _ = BroadcastMessage("Vote cancelled: no human players found. Try again in a moment.");
            return;
        }

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
            _voteStartedUtc = DateTime.UtcNow;
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

        _ = CompleteVoteStartAsync(initiator, trackId, laps, passedImmediately);
    }

    private async Task CompleteVoteStartAsync(string initiator, string trackId, int? laps, bool passedImmediately)
    {
        if (passedImmediately)
        {
            // passedImmediately can only be true when the initiator is the sole human
            // (1 yes is a majority only when humanCount == 1). A vote with one
            // participant is not a vote, so skip the announcement entirely and apply
            // it the way direct mode would - no "Type !yes or !no. Ends in 30s." for
            // something already decided. Note StartVote has already refreshed the
            // roster and confirmed at least one human, so 0 players still cancels.
            await ApplyDirectTrackChangeAsync(initiator, trackId, laps);
            return;
        }

        await BroadcastMessages(FormatVoteStartedMessages(initiator, trackId, laps));
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

    private List<string> FormatVoteStartedMessages(string initiator, string trackId, int? laps)
    {
        var trackDisplayName = GetTrackDisplayName(trackId);
        var suffix = laps is null ? string.Empty : $" - {laps} laps";
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
        if (RefreshPlayersFromHookIfAvailable() == VotePlayerRefreshResult.RefreshedNoHumans)
        {
            _ = BroadcastMessage("Vote ignored: no human players found. Try again in a moment.");
            return;
        }

        string? trackId;
        int? laps;
        bool earlyResult;
        bool earlyPassed = false;

        if (CancelVoteIfModeChanged())
        {
            return;
        }

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

    private VotePlayerRefreshResult RefreshPlayersFromHookIfAvailable()
    {
        try
        {
            var refreshTask = _serverManager.TryRefreshPlayersFromHookAsync();
            if (refreshTask != null)
            {
                var refreshed = refreshTask.ConfigureAwait(false).GetAwaiter().GetResult();
                if (refreshed)
                {
                    var playerCount = _playerTracker.GetPlayerCount();
                    return playerCount.online == 0
                        ? VotePlayerRefreshResult.RefreshedNoHumans
                        : VotePlayerRefreshResult.Refreshed;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to refresh players from injected hook before vote check");
        }

        return VotePlayerRefreshResult.UnavailableOrFailed;
    }

    /// <summary>
    /// Configuration is re-read live, so the mode can change while a vote is running.
    /// Without this a timer armed under Voting would fire later under Off or Direct and
    /// silently change the track.
    /// </summary>
    private bool CancelVoteIfModeChanged()
    {
        if (VoteMode == VoteModes.Voting)
        {
            return false;
        }

        lock (_stateLock)
        {
            if (_state != VoteState.Active)
            {
                return false;
            }

            ResetVoteState();
        }

        _ = BroadcastMessage("Vote cancelled: track change mode changed.");
        return true;
    }

    private void TallyVotes()
    {
        if (CancelVoteIfModeChanged())
        {
            return;
        }

        string? trackId;
        int? laps;
        int humanCount;
        int yesCount;
        int noCount;
        bool passed;

        lock (_stateLock)
        {
            if (_state != VoteState.Active)
                return;

            trackId = _votedTrackId!;
            laps = _votedLaps;
            yesCount = _yesVoters.Count;
            noCount = _noVoters.Count;
            humanCount = _playerTracker.GetPlayerCount().online;
            passed = HasMajority(yesCount, humanCount);
            ResetVoteState();
        }

        _logger.LogInformation("Vote tally for {TrackId}: {Result} ({YesVotes} yes, {NoVotes} no, {HumanCount} humans)",
            trackId, passed ? "passed" : "failed", yesCount, noCount, humanCount);

        if (passed)
            _ = ApplyVotedTrack(trackId!, laps);
        else
            _ = BroadcastMessage("Vote timed out: not enough yes votes. Next race unchanged.");
    }

    /// <summary>
    /// User-facing strings for a track change, so the vote and direct paths can share
    /// one implementation without sharing their wording.
    /// </summary>
    private sealed record TrackChangeMessages(
        Func<string, int?, string> Success,
        string TrackFailure,
        string LapsFailure);

    private static readonly TrackChangeMessages VotePassedMessages = new(
        (trackId, laps) => laps is null
            ? $"Vote passed! Next race: {trackId}."
            : $"Vote passed! Next race: {trackId} for {laps} laps.",
        "Vote passed but failed to update track settings.",
        "Vote passed but failed to update lap settings.");

    private Task ApplyVotedTrack(string trackId, int? laps) =>
        ApplyTrackChange(trackId, laps, VotePassedMessages);

    /// <summary>
    /// Sends the track (and optionally laps) to the server. Returns false when the
    /// server rejected either command, so callers can avoid recording a change that
    /// never happened.
    /// </summary>
    private async Task<bool> ApplyTrackChange(string trackId, int? laps, TrackChangeMessages messages)
    {
        try
        {
            var trackResult = await _serverManager.SendCommandAsync($"track={trackId}");
            if (!trackResult.Success)
            {
                _logger.LogWarning("Failed to apply track {TrackId}: {Message}", trackId, trackResult.Message);
                await BroadcastMessage(messages.TrackFailure);
                return false;
            }

            // Laps omitted: leave the server's current lap count alone.
            if (laps is int lapCount)
            {
                var lapsResult = await _serverManager.SendCommandAsync($"laps={lapCount}");
                if (!lapsResult.Success)
                {
                    _logger.LogWarning("Failed to apply laps {Laps}: {Message}", lapCount, lapsResult.Message);
                    await BroadcastMessage(messages.LapsFailure);
                    return false;
                }
            }

            _logger.LogInformation("Track change applied: {TrackId} laps={Laps}", trackId, laps);
            await BroadcastMessage(messages.Success(trackId, laps));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply track settings {TrackId}", trackId);
            await BroadcastMessage(messages.TrackFailure);
            return false;
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
