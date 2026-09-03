namespace WreckfestController.Models;

public class ServerConfig
{
    // Basic server info
    public string ServerName { get; set; } = string.Empty;
    public string WelcomeMessage { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public int MaxPlayers { get; set; } = 24;

    // Server ports and network
    public int Lan { get; set; } = 0;
    public int SteamPort { get; set; } = 27015;
    public int GamePort { get; set; } = 33540;
    public int QueryPort { get; set; } = 27016;

    // Server behavior
    public int ExcludeFromQuickplay { get; set; } = 0;
    public int ClearUsers { get; set; } = 0;
    public int OwnerDisabled { get; set; } = 0;
    public int AdminControl { get; set; } = 0;
    public int LobbyCountdown { get; set; } = 30;
    public int ReadyPlayersRequired { get; set; } = 50;
    public string AdminSteamIds { get; set; } = string.Empty;
    public string OpSteamIds { get; set; } = string.Empty;

    // Session and grid settings
    public string SessionMode { get; set; } = "normal";
    public string GridOrder { get; set; } = "perf_normal";
    public int EnableTrackVote { get; set; } = 1;
    public int DisableIdleKick { get; set; } = 0;

    // Game settings
    public string Track { get; set; } = string.Empty;
    public string Gamemode { get; set; } = string.Empty;
    public int Bots { get; set; } = 0;
    public string AiDifficulty { get; set; } = "expert";
    public int NumTeams { get; set; } = 2;
    public int Laps { get; set; } = 3;
    public int TimeLimit { get; set; } = 5;
    public int EliminationInterval { get; set; } = 0;

    // Vehicle settings
    public string VehicleDamage { get; set; } = "normal";
    public string CarClassRestriction { get; set; } = string.Empty;
    public string CarRestriction { get; set; } = string.Empty;
    public int SpecialVehiclesDisabled { get; set; } = 0;
    public int CarResetDisabled { get; set; } = 0;
    public int CarResetDelay { get; set; } = 2;
    public int WrongWayLimiterDisabled { get; set; } = 0;

    // Other settings
    public string Weather { get; set; } = string.Empty;
    public string Frequency { get; set; } = "high";
    public string Mods { get; set; } = string.Empty;
    public string Log { get; set; } = "log.txt";

    /// <summary>
    /// Applies a configuration key-value pair to the ServerConfig instance.
    /// Used for parsing server_config.cfg files.
    /// </summary>
    public void ApplyConfigValue(string key, string value)
    {
        switch (key)
        {
            case "server_name": ServerName = value; break;
            case "welcome_message": WelcomeMessage = value; break;
            case "password": Password = value; break;
            case "max_players": if (int.TryParse(value, out var maxPlayers)) MaxPlayers = maxPlayers; break;
            case "lan": if (int.TryParse(value, out var lan)) Lan = lan; break;
            case "steam_port": if (int.TryParse(value, out var steamPort)) SteamPort = steamPort; break;
            case "game_port": if (int.TryParse(value, out var gamePort)) GamePort = gamePort; break;
            case "query_port": if (int.TryParse(value, out var queryPort)) QueryPort = queryPort; break;
            case "exclude_from_quickplay": if (int.TryParse(value, out var excludeFromQuickplay)) ExcludeFromQuickplay = excludeFromQuickplay; break;
            case "clear_users": if (int.TryParse(value, out var clearUsers)) ClearUsers = clearUsers; break;
            case "owner_disabled": if (int.TryParse(value, out var ownerDisabled)) OwnerDisabled = ownerDisabled; break;
            case "admin_control": if (int.TryParse(value, out var adminControl)) AdminControl = adminControl; break;
            case "lobby_countdown": if (int.TryParse(value, out var lobbyCountdown)) LobbyCountdown = lobbyCountdown; break;
            case "ready_players_required": if (int.TryParse(value, out var readyPlayersRequired)) ReadyPlayersRequired = readyPlayersRequired; break;
            case "admin_steam_ids": AdminSteamIds = value; break;
            case "op_steam_ids": OpSteamIds = value; break;
            case "session_mode": SessionMode = value; break;
            case "grid_order": GridOrder = value; break;
            case "enable_track_vote": if (int.TryParse(value, out var enableTrackVote)) EnableTrackVote = enableTrackVote; break;
            case "disable_idle_kick": if (int.TryParse(value, out var disableIdleKick)) DisableIdleKick = disableIdleKick; break;
            case "track": Track = value; break;
            case "gamemode": Gamemode = value; break;
            case "bots": if (int.TryParse(value, out var bots)) Bots = bots; break;
            case "ai_difficulty": AiDifficulty = value; break;
            case "num_teams": if (int.TryParse(value, out var numTeams)) NumTeams = numTeams; break;
            case "laps": if (int.TryParse(value, out var laps)) Laps = laps; break;
            case "time_limit": if (int.TryParse(value, out var timeLimit)) TimeLimit = timeLimit; break;
            case "elimination_interval": if (int.TryParse(value, out var eliminationInterval)) EliminationInterval = eliminationInterval; break;
            case "vehicle_damage": VehicleDamage = value; break;
            case "car_class_restriction": CarClassRestriction = value; break;
            case "car_restriction": CarRestriction = value; break;
            case "special_vehicles_disabled": if (int.TryParse(value, out var specialVehiclesDisabled)) SpecialVehiclesDisabled = specialVehiclesDisabled; break;
            case "car_reset_disabled": if (int.TryParse(value, out var carResetDisabled)) CarResetDisabled = carResetDisabled; break;
            case "car_reset_delay": if (int.TryParse(value, out var carResetDelay)) CarResetDelay = carResetDelay; break;
            case "wrong_way_limiter_disabled": if (int.TryParse(value, out var wrongWayLimiterDisabled)) WrongWayLimiterDisabled = wrongWayLimiterDisabled; break;
            case "weather": Weather = value; break;
            case "frequency": Frequency = value; break;
            case "mods": Mods = value; break;
            case "log": Log = value; break;
        }
    }
}
