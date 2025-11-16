using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using WreckfestController.Models;

namespace WreckfestController.Services;

public class ServerInfoTracker
{
    private readonly ILogger<ServerInfoTracker> _logger;
    private readonly object _lock = new();

    // State for parsing multi-line ? command responses
    private bool _collectingInfoResponse = false;
    private readonly List<string> _infoResponseLines = new();
    private DateTime _infoResponseStartTime = DateTime.MinValue;
    private TaskCompletionSource<ServerConfig>? _infoResponseTask;

    public ServerInfoTracker(ILogger<ServerInfoTracker> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Parse a log line and update server info tracking
    /// </summary>
    public void ProcessLogLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        lock (_lock)
        {
            // Check if we're collecting ? command response
            if (_collectingInfoResponse)
            {
                // Check for config lines: "server_name=value" or similar
                if (Regex.IsMatch(line, @"^\s*\w+\s*="))
                {
                    _infoResponseLines.Add(line);
                    return;
                }
                // Check for end of response (empty line or non-config content)
                else if (string.IsNullOrWhiteSpace(line.Trim()) ||
                         (!line.Trim().Contains("=") && !line.Trim().Equals("?")))
                {
                    // Process the collected response
                    if (_infoResponseLines.Count > 0)
                    {
                        var config = ParseInfoResponse(_infoResponseLines.ToArray());
                        _infoResponseTask?.TrySetResult(config);
                    }
                    else
                    {
                        _infoResponseTask?.TrySetException(new InvalidOperationException("No config lines received"));
                    }
                    _collectingInfoResponse = false;
                    _infoResponseLines.Clear();
                    _infoResponseTask = null;
                }
            }

            // Check for start of ? command response
            // The server might echo the command or start with config directly
            if (line.Trim() == "?" || Regex.IsMatch(line, @"^\s*server_name\s*="))
            {
                _collectingInfoResponse = true;
                _infoResponseStartTime = DateTime.Now;
                _infoResponseLines.Clear();

                // If this line is already a config line, add it
                if (Regex.IsMatch(line, @"^\s*\w+\s*="))
                {
                    _infoResponseLines.Add(line);
                }
                return;
            }

            // If we've been collecting for too long (>2 seconds), abandon it
            if (_collectingInfoResponse && (DateTime.Now - _infoResponseStartTime).TotalSeconds > 2)
            {
                _logger.LogWarning("Server info response collection timed out, abandoning");
                _collectingInfoResponse = false;
                _infoResponseLines.Clear();
                _infoResponseTask?.TrySetException(new TimeoutException("Server info response collection timed out"));
                _infoResponseTask = null;
            }
        }
    }

    /// <summary>
    /// Request server info by sending ? command and waiting for response
    /// </summary>
    public async Task<ServerConfig> RequestServerInfoAsync(TimeSpan timeout)
    {
        lock (_lock)
        {
            _infoResponseTask = new TaskCompletionSource<ServerConfig>();
        }

        try
        {
            using var cts = new CancellationTokenSource(timeout);
            using var registration = cts.Token.Register(() =>
                _infoResponseTask?.TrySetException(new TimeoutException("Server info request timed out")));

            return await _infoResponseTask.Task;
        }
        catch
        {
            lock (_lock)
            {
                _collectingInfoResponse = false;
                _infoResponseLines.Clear();
                _infoResponseTask = null;
            }
            throw;
        }
    }

    /// <summary>
    /// Parse the response from "?" command
    /// Example output:
    /// "server_name=My Server"
    /// "max_players=24"
    /// "track=laajamaa"
    /// "gamemode=derby"
    /// etc.
    /// </summary>
    private ServerConfig ParseInfoResponse(string[] lines)
    {
        var config = new ServerConfig();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || !trimmed.Contains("="))
                continue;

            var parts = trimmed.Split('=', 2);
            if (parts.Length != 2) continue;

            var key = parts[0].Trim();
            var value = parts[1].Trim();

            switch (key)
            {
                case "server_name": config.ServerName = value; break;
                case "welcome_message": config.WelcomeMessage = value; break;
                case "password": config.Password = value; break;
                case "max_players": int.TryParse(value, out var maxPlayers); config.MaxPlayers = maxPlayers; break;
                case "lan": int.TryParse(value, out var lan); config.Lan = lan; break;
                case "steam_port": int.TryParse(value, out var steamPort); config.SteamPort = steamPort; break;
                case "game_port": int.TryParse(value, out var gamePort); config.GamePort = gamePort; break;
                case "query_port": int.TryParse(value, out var queryPort); config.QueryPort = queryPort; break;
                case "exclude_from_quickplay": int.TryParse(value, out var excludeFromQuickplay); config.ExcludeFromQuickplay = excludeFromQuickplay; break;
                case "clear_users": int.TryParse(value, out var clearUsers); config.ClearUsers = clearUsers; break;
                case "owner_disabled": int.TryParse(value, out var ownerDisabled); config.OwnerDisabled = ownerDisabled; break;
                case "admin_control": int.TryParse(value, out var adminControl); config.AdminControl = adminControl; break;
                case "lobby_countdown": int.TryParse(value, out var lobbyCountdown); config.LobbyCountdown = lobbyCountdown; break;
                case "ready_players_required": int.TryParse(value, out var readyPlayersRequired); config.ReadyPlayersRequired = readyPlayersRequired; break;
                case "admin_steam_ids": config.AdminSteamIds = value; break;
                case "op_steam_ids": config.OpSteamIds = value; break;
                case "session_mode": config.SessionMode = value; break;
                case "grid_order": config.GridOrder = value; break;
                case "enable_track_vote": int.TryParse(value, out var enableTrackVote); config.EnableTrackVote = enableTrackVote; break;
                case "disable_idle_kick": int.TryParse(value, out var disableIdleKick); config.DisableIdleKick = disableIdleKick; break;
                case "track": config.Track = value; break;
                case "gamemode": config.Gamemode = value; break;
                case "bots": int.TryParse(value, out var bots); config.Bots = bots; break;
                case "ai_difficulty": config.AiDifficulty = value; break;
                case "num_teams": int.TryParse(value, out var numTeams); config.NumTeams = numTeams; break;
                case "laps": int.TryParse(value, out var laps); config.Laps = laps; break;
                case "time_limit": int.TryParse(value, out var timeLimit); config.TimeLimit = timeLimit; break;
                case "elimination_interval": int.TryParse(value, out var eliminationInterval); config.EliminationInterval = eliminationInterval; break;
                case "vehicle_damage": config.VehicleDamage = value; break;
                case "car_class_restriction": config.CarClassRestriction = value; break;
                case "car_restriction": config.CarRestriction = value; break;
                case "special_vehicles_disabled": int.TryParse(value, out var specialVehiclesDisabled); config.SpecialVehiclesDisabled = specialVehiclesDisabled; break;
                case "car_reset_disabled": int.TryParse(value, out var carResetDisabled); config.CarResetDisabled = carResetDisabled; break;
                case "car_reset_delay": int.TryParse(value, out var carResetDelay); config.CarResetDelay = carResetDelay; break;
                case "wrong_way_limiter_disabled": int.TryParse(value, out var wrongWayLimiterDisabled); config.WrongWayLimiterDisabled = wrongWayLimiterDisabled; break;
                case "weather": config.Weather = value; break;
                case "frequency": config.Frequency = value; break;
                case "mods": config.Mods = value; break;
                case "log": config.Log = value; break;
            }
        }

        _logger.LogInformation("Parsed server info: {ServerName}, Track: {Track}, Gamemode: {Gamemode}",
            config.ServerName, config.Track, config.Gamemode);

        return config;
    }
}
