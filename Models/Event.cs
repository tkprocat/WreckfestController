using System.Text.Json.Serialization;

namespace WreckfestController.Models;

/// <summary>
/// What a smart restart applies when a cup activates: its server settings, scoring and
/// the rotation to deploy. Built from a <see cref="Data.Cups.Cup"/> at the
/// moment of activation, with a linked collection's tracks as they are then.
/// </summary>
public class Event
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Only the fields that are set are applied.</summary>
    public EventServerConfig? ServerConfig { get; set; }

    /// <summary><c>session_mode</c>; null keeps the server's own.</summary>
    public string? SessionMode { get; set; }

    /// <summary><c>grid_order</c>; null keeps the server's own.</summary>
    public string? GridOrder { get; set; }

    /// <summary>The rotation to deploy. Empty leaves the server's rotation alone.</summary>
    public List<EventLoopTrack> Tracks { get; set; } = new();

    /// <summary>The <c>#CollectionName</c> line; "Cup: &lt;name&gt;" when empty.</summary>
    public string CollectionName { get; set; } = string.Empty;
}

/// <summary>
/// Server configuration settings that can be overridden by a cup.
/// Only the fields that are set (not null/empty) will be applied during cup activation.
/// </summary>
public class EventServerConfig
{
    /// <summary>
    /// Server name override
    /// </summary>
    [JsonPropertyName("serverName")]
    public string? ServerName { get; set; }

    /// <summary>
    /// Welcome message override
    /// </summary>
    [JsonPropertyName("welcomeMessage")]
    public string? WelcomeMessage { get; set; }

    /// <summary>
    /// Password override (use empty string to remove password)
    /// </summary>
    [JsonPropertyName("password")]
    public string? Password { get; set; }

    /// <summary>
    /// Max players override
    /// </summary>
    [JsonPropertyName("maxPlayers")]
    public int? MaxPlayers { get; set; }

    /// <summary>
    /// Default number of bots for tracks that don't specify
    /// </summary>
    [JsonPropertyName("bots")]
    public int? Bots { get; set; }

    /// <summary>
    /// AI difficulty override (novice, intermediate, expert, champion)
    /// </summary>
    [JsonPropertyName("aiDifficulty")]
    public string? AiDifficulty { get; set; }

    /// <summary>
    /// Default number of laps for racing tracks that don't specify
    /// </summary>
    [JsonPropertyName("laps")]
    public int? Laps { get; set; }

    /// <summary>
    /// Vehicle damage setting (realistic, normal, reduced)
    /// </summary>
    [JsonPropertyName("vehicleDamage")]
    public string? VehicleDamage { get; set; }

    /// <summary>
    /// Lobby countdown duration in seconds
    /// </summary>
    [JsonPropertyName("lobbyCountdown")]
    public int? LobbyCountdown { get; set; }

    // Additional fields can be added as needed
}

/// <summary>
/// How an event recurs. <see cref="Time"/> and <see cref="Days"/> are wall-clock values
/// in the event's time zone, so "20:00 on Fridays" stays 20:00 across daylight saving.
/// </summary>
public class RepeatSchedule
{
    /// <summary>
    /// Frequency of recurrence: "daily" or "weekly"
    /// </summary>
    [JsonPropertyName("frequency")]
    public string Frequency { get; set; } = "weekly";

    /// <summary>
    /// For weekly recurrence: list of days (0=Sunday, 1=Monday, ..., 6=Saturday)
    /// For daily recurrence: can be empty or omitted
    /// </summary>
    [JsonPropertyName("days")]
    public List<int>? Days { get; set; }

    /// <summary>
    /// Time of day when event should activate (format: "HH:MM"), in the event's time zone
    /// </summary>
    [JsonPropertyName("time")]
    public string Time { get; set; } = "00:00";

    /// <summary>
    /// Parses the Time string into a TimeSpan
    /// </summary>
    [JsonIgnore]
    public TimeSpan TimeAsTimeSpan
    {
        get
        {
            if (TimeSpan.TryParse(Time, System.Globalization.CultureInfo.InvariantCulture, out var result))
                return result;
            return TimeSpan.Zero;
        }
    }

    /// <summary>
    /// Returns true if this is a daily recurring schedule
    /// </summary>
    [JsonIgnore]
    public bool IsDaily => Frequency?.Equals("daily", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Returns true if this is a weekly recurring schedule
    /// </summary>
    [JsonIgnore]
    public bool IsWeekly => Frequency?.Equals("weekly", StringComparison.OrdinalIgnoreCase) == true;
}
