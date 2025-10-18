using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using WreckfestController.Models;

namespace WreckfestController.Services;

public class PlayerTracker
{
    private readonly ConcurrentDictionary<string, Player> _players = new();
    private readonly ILogger<PlayerTracker> _logger;
    private DateTime _lastListUpdate = DateTime.MinValue;
    private readonly object _lock = new();

    public PlayerTracker(ILogger<PlayerTracker> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Parse a log line and update player tracking
    /// </summary>
    public void ProcessLogLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

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

        // Parse timeout events: "Player 0 timeout (ping: 30320ms), status: ready"
        // followed by "- *eRacer has quit (ping timeout)."
        // The quit line is more reliable, so we rely on that
    }

    /// <summary>
    /// Mark a player as joined
    /// </summary>
    private void PlayerJoined(string playerName, bool isBot)
    {
        lock (_lock)
        {
            if (_players.TryGetValue(playerName, out var existingPlayer))
            {
                // Player rejoined
                existingPlayer.IsOnline = true;
                existingPlayer.LastSeenAt = DateTime.Now;
                _logger.LogInformation("{Type} rejoined: {PlayerName}", isBot ? "Bot" : "Player", playerName);
            }
            else
            {
                // New player
                var player = new Player
                {
                    Name = playerName,
                    JoinedAt = DateTime.Now,
                    LastSeenAt = DateTime.Now,
                    IsOnline = true,
                    IsBot = isBot
                };
                _players[playerName] = player;
                _logger.LogInformation("{Type} joined: {PlayerName}", isBot ? "Bot" : "Player", playerName);
            }
        }
    }

    /// <summary>
    /// Mark a player as left
    /// </summary>
    private void PlayerLeft(string playerName)
    {
        lock (_lock)
        {
            if (_players.TryGetValue(playerName, out var player))
            {
                player.IsOnline = false;
                player.LastSeenAt = DateTime.Now;
                _logger.LogInformation("Player left: {PlayerName}", playerName);
            }
        }
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
            // Track which players are in the current list
            var playersInList = new HashSet<string>();

            foreach (var line in lines)
            {
                // Parse player count: "Players: 3/24"
                var countMatch = Regex.Match(line, @"Players:\s*(\d+)/(\d+)");
                if (countMatch.Success)
                {
                    _logger.LogDebug("Server has {Current}/{Max} players", countMatch.Groups[1].Value, countMatch.Groups[2].Value);
                    continue;
                }

                // Parse player entries: "0: *PlayerName" (bot) or "0: PlayerName" (human)
                var playerMatch = Regex.Match(line, @"^(\d+):\s*(\*?)(.+)$");
                if (playerMatch.Success)
                {
                    var slot = int.Parse(playerMatch.Groups[1].Value);
                    var isBot = playerMatch.Groups[2].Value == "*";
                    var playerName = playerMatch.Groups[3].Value.Trim();

                    playersInList.Add(playerName);

                    if (_players.TryGetValue(playerName, out var player))
                    {
                        player.IsOnline = true;
                        player.Slot = slot;
                        player.LastSeenAt = DateTime.Now;
                    }
                    else
                    {
                        // Player in list but not in our tracking (shouldn't happen if we catch joins)
                        _players[playerName] = new Player
                        {
                            Name = playerName,
                            JoinedAt = DateTime.Now,
                            LastSeenAt = DateTime.Now,
                            IsOnline = true,
                            IsBot = isBot,
                            Slot = slot
                        };
                        _logger.LogInformation("{Type} discovered via list command: {PlayerName}", isBot ? "Bot" : "Player", playerName);
                    }
                }
            }

            // Mark players not in the list as offline
            foreach (var player in _players.Values.Where(p => p.IsOnline).ToList())
            {
                if (!playersInList.Contains(player.Name))
                {
                    player.IsOnline = false;
                    player.LastSeenAt = DateTime.Now;
                    _logger.LogDebug("Player marked offline after list command: {PlayerName}", player.Name);
                }
            }

            _lastListUpdate = DateTime.Now;
        }
    }

    /// <summary>
    /// Get current online players
    /// </summary>
    public List<Player> GetOnlinePlayers()
    {
        lock (_lock)
        {
            return _players.Values
                .Where(p => p.IsOnline)
                .OrderBy(p => p.Slot ?? int.MaxValue)
                .ThenBy(p => p.JoinedAt)
                .ToList();
        }
    }

    /// <summary>
    /// Get all players (including offline)
    /// </summary>
    public List<Player> GetAllPlayers()
    {
        lock (_lock)
        {
            return _players.Values
                .OrderByDescending(p => p.IsOnline)
                .ThenByDescending(p => p.LastSeenAt)
                .ToList();
        }
    }

    /// <summary>
    /// Get player count
    /// </summary>
    public (int online, int total) GetPlayerCount()
    {
        lock (_lock)
        {
            return (_players.Values.Count(p => p.IsOnline), _players.Count);
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
        return DateTime.Now - _lastListUpdate;
    }
}
