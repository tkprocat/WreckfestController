using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using WreckfestController.Models;

namespace WreckfestController.Services;

public class PlayerTracker
{
    private readonly ConcurrentDictionary<string, Player> _players = new();
    private readonly ILogger<PlayerTracker> _logger;
    private readonly WreckfestWebWebhookService _webhookService;
    private readonly object _lock = new();

    /// <summary>
    /// Event raised when player data changes (join, leave, update)
    /// </summary>
    public event Action<PlayerTrackerEvent>? PlayerEvent;

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

        // Events carry the same joins and quits with more detail, so skip the text
        // parsing entirely rather than processing each one twice.
        if (UseServerEvents)
        {
            return;
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
    }

    /// <summary>
    /// Set once the server-event ring is being read successfully. While true the
    /// join/quit/kick text parsing is skipped, because the events carry the same
    /// facts plus the role and quit reason that console lines cannot express.
    /// Left false when the ring is unavailable, so text parsing still covers us.
    /// </summary>
    public bool UseServerEvents { get; set; }

    /// <summary>
    /// Applies one server event. This is the authoritative path: unlike a console
    /// line, the event carries why a player left and announces privilege changes.
    /// </summary>
    public void ProcessServerEvent(ServerEvent serverEvent)
    {
        var name = serverEvent.Name.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var isBot = name.StartsWith('*');
        var playerName = name.TrimStart('*').Trim();
        if (string.IsNullOrWhiteSpace(playerName))
        {
            return;
        }

        switch (serverEvent.Id)
        {
            case ServerEvent.PlayerHasJoined:
                PlayerJoined(playerName, isBot);
                break;

            case ServerEvent.QuitNormal:
            case ServerEvent.QuitTimeout:
            case ServerEvent.QuitKicked:
            case ServerEvent.QuitIdleKick:
            case ServerEvent.QuitBanned:
            case ServerEvent.QuitInvalid:
            case ServerEvent.QuitBot:
                _logger.LogInformation("{Player} left: {Reason}", playerName, serverEvent.QuitReason);
                if (serverEvent.Id == ServerEvent.QuitKicked || serverEvent.Id == ServerEvent.QuitIdleKick)
                {
                    PlayerKicked(playerName);
                }
                else
                {
                    PlayerLeft(playerName);
                }
                break;

            case ServerEvent.NewModerator:
                SetPrivilege(playerName, admin: false, moderator: true);
                break;

            case ServerEvent.NewAdmin:
                SetPrivilege(playerName, admin: true, moderator: false);
                break;

            case ServerEvent.Demoted:
                SetPrivilege(playerName, admin: false, moderator: false);
                break;
        }
    }

    /// <summary>
    /// Applies a privilege change as it happens, rather than waiting for the next
    /// snapshot. Without this a freshly promoted moderator looks unprivileged until
    /// something else refreshes the roster.
    /// </summary>
    private void SetPrivilege(string playerName, bool admin, bool moderator)
    {
        lock (_lock)
        {
            if (!_players.TryGetValue(playerName, out var player))
            {
                return;
            }

            player.IsAdmin = admin;
            player.IsModerator = moderator;
        }

        var role = admin ? "admin" : moderator ? "moderator" : "player";
        _logger.LogInformation("{Player} is now {Role}", playerName, role);
    }

    public void MarkPlayerSeen(string playerName, bool isBot)
    {
        playerName = playerName.Trim();
        if (string.IsNullOrWhiteSpace(playerName))
        {
            return;
        }

        lock (_lock)
        {
            if (_players.TryGetValue(playerName, out var player))
            {
                player.IsBot = isBot;
                return;
            }

            var seenPlayer = new Player
            {
                Name = playerName,
                JoinedAt = DateTime.UtcNow,
                IsBot = isBot
            };

            _players[playerName] = seenPlayer;
            NotifyPlayerEvent(new PlayerTrackerEvent("Join", seenPlayer));
            _logger.LogInformation("{Type} discovered via chat command: {PlayerName}", isBot ? "Bot" : "Player", playerName);
        }

        _ = SendPlayerListUpdate();
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
                    player.IsModerator = snapshotPlayer.IsModerator;
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
            _logger.LogInformation("Player tracking cleared");
        }
    }

    private void NotifyPlayerEvent(PlayerTrackerEvent playerTrackerEvent)
    {
        PlayerEvent?.Invoke(playerTrackerEvent);
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
