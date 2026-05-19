using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using WreckfestController.Models;

namespace WreckfestController.Services;

public class PlayerTracker
{
    private readonly ConcurrentDictionary<string, Player> _players = new();
    private readonly ILogger<PlayerTracker> _logger;
    private readonly WreckfestWebWebhookService _webhookService;
    private DateTime _lastListUpdate = DateTime.MinValue;
    private readonly object _lock = new();

    /// <summary>
    /// Event raised when a list command should be sent to refresh player data
    /// </summary>
    public event Action? OnListCommandRequested;

    /// <summary>
    /// Event raised when player data changes (join, leave, update)
    /// </summary>
    public event Action<PlayerTrackerEvent>? PlayerEvent;

    // State for parsing multi-line list command responses
    private bool _collectingListResponse = false;
    private readonly List<string> _listResponseLines = new();
    private DateTime _listResponseStartTime = DateTime.MinValue;

    // Debounce timer for list command requests
    private Timer? _listCommandDebounceTimer;
    private readonly object _debounceTimerLock = new();

    public PlayerTracker(ILogger<PlayerTracker> logger, WreckfestWebWebhookService webhookService)
    {
        _logger = logger;
        _webhookService = webhookService;
    }

    /// <summary>
    /// Parse a log line and update player tracking
    /// </summary>
    public void ProcessLogLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        lock (_lock)
        {
            // Check if we're collecting list response
            if (_collectingListResponse)
            {
                // Check for player entry line: "  1:    0 [0] *PlayerName" or similar format
                // The line starts with whitespace, then a number, then a colon
                if (Regex.IsMatch(line, @"^\s+\d+:"))
                {
                    _listResponseLines.Add(line);
                    _logger.LogDebug("[LIST] Added player line ({Count} total): {Line}", _listResponseLines.Count, line);
                    return;
                }
                // Check for empty line or non-list content - end of list
                else if (string.IsNullOrWhiteSpace(line.Trim()) ||
                         (!line.Contains("Players:") && !line.Trim().Equals("list") && !Regex.IsMatch(line, @"^\s+\d+:")))
                {
                    _logger.LogDebug("[LIST] End of list detected. Reason: {Reason}, Line: '{Line}'",
                        string.IsNullOrWhiteSpace(line.Trim()) ? "empty/whitespace" : "non-list content",
                        line);

                    // Process the collected list
                    if (_listResponseLines.Count > 1) // Need at least "list" command + one player entry
                    {
                        _logger.LogInformation("[LIST] Processing {Count} collected lines", _listResponseLines.Count);
                        ProcessListResponse(_listResponseLines.ToArray());
                        _ = SendPlayerListUpdate(); // Send webhook after processing list
                    }
                    else
                    {
                        _logger.LogWarning("[LIST] Not enough lines collected ({Count}), skipping processing", _listResponseLines.Count);
                    }
                    _collectingListResponse = false;
                    _listResponseLines.Clear();
                }
            }

            // Check for start of list response: "Players: 3/24" or just "list" command
            if (Regex.IsMatch(line, @"Players:\s*\d+/\d+") || line.Trim() == "list")
            {
                _collectingListResponse = true;
                _listResponseStartTime = DateTime.UtcNow;
                _listResponseLines.Clear();
                _listResponseLines.Add(line);
                _logger.LogDebug("[LIST] Started collecting list response with: {Line}", line);
                return;
            }

            // If we've been collecting for too long (>2 seconds), abandon it
            if (_collectingListResponse && (DateTime.UtcNow - _listResponseStartTime).TotalSeconds > 2)
            {
                _logger.LogWarning("[LIST] Collection timed out after {Elapsed:F1}s, abandoning. Collected {Count} lines",
                    (DateTime.UtcNow - _listResponseStartTime).TotalSeconds, _listResponseLines.Count);
                _collectingListResponse = false;
                _listResponseLines.Clear();
            }
        }

        // Parse join events: "16:53:14 - *eRacer has joined." (bot) or "16:53:14 - Player123 has joined." (human)
        var joinMatch = Regex.Match(line, @"- (\*?)(.+?) has joined\.");
        if (joinMatch.Success)
        {
            var isBot = joinMatch.Groups[1].Value == "*";
            var playerName = joinMatch.Groups[2].Value;
            PlayerJoined(playerName, isBot);
            return;
        }

        // Parse quit/leave events: "16:53:14 - *eRacer has quit (ping timeout)." (bot) or "16:53:14 - Player123 has quit." (human)
        var quitMatch = Regex.Match(line, @"- (\*?)(.+?) has quit");
        if (quitMatch.Success)
        {
            var playerName = quitMatch.Groups[2].Value;
            PlayerLeft(playerName);
            return;
        }

        // Parse kick events: "* 08:38:42 - *AleXi8293 kicked." (bot) or "* 08:38:42 - Player123 kicked." (human)
        var kickMatch = Regex.Match(line, @"- (\*?)(.+?) kicked\.");
        if (kickMatch.Success)
        {
            var playerName = kickMatch.Groups[2].Value;
            PlayerKicked(playerName);
            return;
        }

        // Parse timeout events: "Player 0 timeout (ping: 30320ms), status: ready"
        // followed by "- *eRacer has quit (ping timeout)."
        // The quit line is more reliable, so we rely on that

    }

    /// <summary>
    /// Add a player who joined
    /// </summary>
    private void PlayerJoined(string playerName, bool isBot)
    {
        lock (_lock)
        {
            if (_players.ContainsKey(playerName))
            {
                // Player rejoined - update join time
                _players[playerName].JoinedAt = DateTime.UtcNow;
                _logger.LogInformation("{Type} rejoined: {PlayerName}", isBot ? "Bot" : "Player", playerName);
            }
            else
            {
                // New player
                var player = new Player
                {
                    Name = playerName,
                    JoinedAt = DateTime.UtcNow,
                    IsBot = isBot
                };
                _players[playerName] = player;
                NotifyPlayerEvent(new PlayerTrackerEvent("Join", player));
                _logger.LogInformation("{Type} joined: {PlayerName}", isBot ? "Bot" : "Player", playerName);

                // Send updated player list to Laravel
                _ = SendPlayerListUpdate();
            }
        }

        // Request a list command to get full player details (slot, admin status, etc.)
        RequestListCommand();
    }

    /// <summary>
    /// Remove a player who left
    /// </summary>
    private void PlayerLeft(string playerName)
    {
        lock (_lock)
        {
            if (_players.TryRemove(playerName, out var player))
            {
                NotifyPlayerEvent(new PlayerTrackerEvent("Left", player));
                _logger.LogInformation("Player left: {PlayerName}", playerName);

                // Send updated player list to Laravel
                _ = SendPlayerListUpdate();
            }
        }

        // Request a list command to refresh player data
        RequestListCommand();
    }

    /// <summary>
    /// Remove a player who was kicked
    /// </summary>
    private void PlayerKicked(string playerName)
    {
        lock (_lock)
        {
            if (_players.TryRemove(playerName, out var player))
            {
                NotifyPlayerEvent(new PlayerTrackerEvent("Kicked", player));
                _logger.LogInformation("Player kicked: {PlayerName}", playerName);

                // Send updated player list to Laravel
                _ = SendPlayerListUpdate();
            }
        }

        // Request a list command to refresh player data
        RequestListCommand();
    }

    /// <summary>
    /// Parse the response from "list" command
    /// Example output:
    /// "Players: 3/24"
    /// "0: PlayerName1"
    /// "1: PlayerName2"
    /// "2: PlayerName3"
    /// </summary>
    public void ProcessListResponse(string[] lines)
    {
        lock (_lock)
        {
            _logger.LogDebug("[LIST PARSE] Processing {Count} lines", lines.Length);

            // Track which players are in the current list
            var playersInList = new HashSet<string>();

            foreach (var line in lines)
            {
                _logger.LogDebug("[LIST PARSE] Line: '{Line}'", line);

                // Parse player count: "Players: 3/24"
                var countMatch = Regex.Match(line, @"Players:\s*(\d+)/(\d+)");
                if (countMatch.Success)
                {
                    _logger.LogDebug("[LIST PARSE] Player count: {Current}/{Max}", countMatch.Groups[1].Value, countMatch.Groups[2].Value);
                    continue;
                }

                // Parse player entries: "  1:    0 [0] *Lord Bane        | [A] 244 Stellar      | ping: 0    | ready"
                //                   or: " 11:    0 [0] Procat A                         | [A] 237 Lawn Mower" (with admin A)
                // Format: whitespace + slot + ": " + other data + [bracket] + asterisk (optional) + playerName + optional role indicator
                // Handle both normal lines and wrapped lines (ending with >)
                // ANSI color codes might be present, so we need to handle them
                var playerMatch = Regex.Match(line, @"^\s+(\d+):\s+\d+\s+\[.\]\s+(\*?)(.+?)(?:\s*[\|>]|$)");
                if (playerMatch.Success)
                {
                    var slot = int.Parse(playerMatch.Groups[1].Value);
                    var isBot = playerMatch.Groups[2].Value == "*";
                    var fullNameWithRole = playerMatch.Groups[3].Value.Trim();

                    _logger.LogDebug("[LIST PARSE] Regex matched: Slot={Slot}, IsBot={IsBot}, FullName='{FullName}'",
                        slot, isBot, fullNameWithRole);

                    // Check for admin/moderator indicators (A or M at the end after spaces)
                    // ANSI color codes format: \x1b[38;2;R;G;Bm or similar escape sequences
                    // Orange A: might be surrounded by ANSI codes like \x1b[38;2;255;165;0mA\x1b[0m
                    var isAdmin = false;
                    var isModerator = false;
                    var playerName = fullNameWithRole;

                    // Remove ANSI color codes and check for role indicators
                    var cleanName = Regex.Replace(fullNameWithRole, @"\x1b\[[0-9;]*m", "");

                    // Check if there's a single letter (A or M) at the end separated by whitespace
                    var roleMatch = Regex.Match(cleanName, @"^(.+?)\s+([AM])\s*$");
                    if (roleMatch.Success)
                    {
                        playerName = roleMatch.Groups[1].Value.Trim();
                        var roleIndicator = roleMatch.Groups[2].Value;
                        isAdmin = roleIndicator == "A";
                        isModerator = roleIndicator == "M";
                        _logger.LogDebug("[LIST PARSE] Role detected: {Role} for '{PlayerName}'", roleIndicator, playerName);
                    }
                    else
                    {
                        playerName = cleanName.Trim();
                    }

                    // Skip empty names
                    if (string.IsNullOrWhiteSpace(playerName))
                    {
                        _logger.LogDebug("[LIST PARSE] Skipping empty player name");
                        continue;
                    }

                    _logger.LogDebug("[LIST PARSE] Final: PlayerName='{PlayerName}', IsAdmin={IsAdmin}, IsMod={IsMod}",
                        playerName, isAdmin, isModerator);

                    playersInList.Add(playerName);

                    if (_players.TryGetValue(playerName, out var player))
                    {
                        // Update existing player info
                        player.Slot = slot;
                        player.IsAdmin = isAdmin;
                    }
                    else
                    {
                        // Player in list but not in our tracking (shouldn't happen if we catch joins)
                        _players[playerName] = new Player
                        {
                            Name = playerName,
                            JoinedAt = DateTime.UtcNow,
                            IsBot = isBot,
                            IsAdmin = isAdmin,
                            Slot = slot
                        };
                        var typeDescription = isBot ? "Bot" : (isAdmin ? "Admin" : "Player");
                        _logger.LogInformation("{Type} discovered via list command: {PlayerName}", typeDescription, playerName);
                    }
                }
                else
                {
                    // Log lines that didn't match the player regex (excluding known non-player lines)
                    if (!line.Trim().Equals("list") && !line.Contains("Players:"))
                    {
                        _logger.LogDebug("[LIST PARSE] Line did not match player regex: '{Line}'", line);
                    }
                }
            }

            // Remove players not in the list (they must have left)
            foreach (var player in _players.Values.ToList())
            {
                if (!playersInList.Contains(player.Name))
                {
                    _players.TryRemove(player.Name, out _);
                    _logger.LogDebug("Player removed after list command (not in list): {PlayerName}", player.Name);
                }
            }

            _lastListUpdate = DateTime.UtcNow;

            // Notify subscribers that the player list was updated
            NotifyPlayerEvent(new PlayerTrackerEvent("PlayersUpdated", null));
            _logger.LogInformation("Player list updated via list command. Online players: {Count}", playersInList.Count);
        }
    }

    public void ProcessHookPlayerSnapshot(IReadOnlyList<Player> snapshotPlayers)
    {
        lock (_lock)
        {
            var playersInSnapshot = new HashSet<string>();

            foreach (var snapshotPlayer in snapshotPlayers)
            {
                if (string.IsNullOrWhiteSpace(snapshotPlayer.Name))
                {
                    continue;
                }

                playersInSnapshot.Add(snapshotPlayer.Name);

                if (_players.TryGetValue(snapshotPlayer.Name, out var player))
                {
                    player.Slot = snapshotPlayer.Slot;
                    player.IsBot = snapshotPlayer.IsBot;
                    player.IsAdmin = snapshotPlayer.IsAdmin;
                }
                else
                {
                    snapshotPlayer.JoinedAt = DateTime.UtcNow;
                    _players[snapshotPlayer.Name] = snapshotPlayer;
                    var typeDescription = snapshotPlayer.IsBot ? "Bot" : (snapshotPlayer.IsAdmin ? "Admin" : "Player");
                    _logger.LogInformation("{Type} discovered via injected hook player snapshot: {PlayerName}", typeDescription, snapshotPlayer.Name);
                }
            }

            foreach (var player in _players.Values.ToList())
            {
                if (!playersInSnapshot.Contains(player.Name))
                {
                    _players.TryRemove(player.Name, out _);
                    _logger.LogDebug("Player removed after injected hook player snapshot: {PlayerName}", player.Name);
                }
            }

            _lastListUpdate = DateTime.UtcNow;
            NotifyPlayerEvent(new PlayerTrackerEvent("PlayersUpdated", null));
            _logger.LogInformation("Player list updated via injected hook player snapshot. Online players: {Count}", playersInSnapshot.Count);
        }

        _ = SendPlayerListUpdate();
    }

    /// <summary>
    /// Get current players
    /// </summary>
    public List<Player> GetPlayers()
    {
        lock (_lock)
        {
            return _players.Values
                .OrderBy(p => p.Slot ?? int.MaxValue)
                .ThenBy(p => p.JoinedAt)
                .ToList();
        }
    }

    /// <summary>
    /// Get player count: online = real players (excluding bots), total = all players including bots
    /// </summary>
    public (int online, int total) GetPlayerCount()
    {
        lock (_lock)
        {
            return (_players.Values.Count(p => !p.IsBot), _players.Count);
        }
    }

    /// <summary>
    /// Checks if any real players (excluding bots) are currently online
    /// </summary>
    /// <returns>True if one or more real players are online, false otherwise</returns>
    public bool HasPlayersOnline()
    {
        lock (_lock)
        {
            return _players.Values.Any(p => !p.IsBot);
        }
    }

    /// <summary>
    /// Clear all player data (used when server stops)
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _players.Clear();
            _lastListUpdate = DateTime.MinValue;
            _logger.LogInformation("Player tracking cleared");
        }
    }

    /// <summary>
    /// Get time since last list update
    /// </summary>
    public TimeSpan TimeSinceLastListUpdate()
    {
        return DateTime.UtcNow - _lastListUpdate;
    }

    private void NotifyPlayerEvent(PlayerTrackerEvent playerTrackerEvent)
    {
        PlayerEvent?.Invoke(playerTrackerEvent);
    }

    /// <summary>
    /// Request a list command to be sent to the server.
    /// Debounces requests to avoid spamming the server - if a request comes in during
    /// the debounce period, it schedules another request after the period expires.
    /// </summary>
    /// <param name="force">If true, bypasses the debounce check and sends immediately</param>
    public void RequestListCommand(bool force = false)
    {
        if (force)
        {
            _logger.LogInformation("Requesting list command to refresh player data (forced)");
            OnListCommandRequested?.Invoke();
            return;
        }

        // Debounce: only request if we haven't requested recently (within 2 seconds)
        var timeSinceLastUpdate = TimeSinceLastListUpdate();
        if (timeSinceLastUpdate.TotalSeconds >= 2)
        {
            _logger.LogInformation("Requesting list command to refresh player data");
            OnListCommandRequested?.Invoke();
        }
        else
        {
            // Schedule a request after the debounce period expires
            lock (_debounceTimerLock)
            {
                // Cancel any existing timer and create a new one
                _listCommandDebounceTimer?.Dispose();

                var delayMs = (int)((2 - timeSinceLastUpdate.TotalSeconds) * 1000) + 100; // Add 100ms buffer
                _logger.LogDebug("Debouncing list command request - will fire in {DelayMs}ms", delayMs);

                _listCommandDebounceTimer = new Timer(_ =>
                {
                    _logger.LogInformation("Requesting list command to refresh player data (debounced)");
                    OnListCommandRequested?.Invoke();
                }, null, delayMs, Timeout.Infinite);
            }
        }
    }

    /// <summary>
    /// Send the current player list to Laravel webhook
    /// </summary>
    private async Task SendPlayerListUpdate()
    {
        try
        {
            var onlinePlayers = GetPlayers();
            await _webhookService.SendPlayersUpdatedAsync(onlinePlayers);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send player list update");
        }
    }
}

public class PlayerTrackerEvent
{
    public string EventType { get; set; } = string.Empty; // "Join", "Left", "Kicked", or "PlayersUpdated"
    public Player? Player { get; set; }

    public PlayerTrackerEvent(string eventType, Player? player)
    {
        EventType = eventType;
        Player = player;
    }
}
