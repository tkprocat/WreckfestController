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
}
