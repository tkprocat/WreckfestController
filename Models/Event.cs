using System.Text.Json.Serialization;

namespace WreckfestController.Models;

/// <summary>
/// Represents a scheduled server event that can be automatically activated at a specific time.
/// Events can override server configuration and deploy custom track rotations.
/// </summary>
public class Event
{
    /// <summary>
    /// Unique identifier for the event (matches Laravel database ID)
    /// </summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>
    /// Display name of the event
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Detailed description of the event
    /// </summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// UTC timestamp when the event should be activated
    /// </summary>
    [JsonPropertyName("startTime")]
    public DateTime StartTime { get; set; }

    /// <summary>
    /// Indicates whether this event is currently active on the server
    /// </summary>
    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; }

    /// <summary>
    /// Server configuration overrides to apply when event activates.
    /// Only populated fields will be applied; null/default values are ignored.
    /// </summary>
    [JsonPropertyName("serverConfig")]
    public EventServerConfig? ServerConfig { get; set; }

    /// <summary>
    /// Track rotation to deploy when event activates
    /// </summary>
    [JsonPropertyName("tracks")]
    public List<EventLoopTrack> Tracks { get; set; } = new();

    /// <summary>
    /// Name of the track collection being deployed
    /// </summary>
    [JsonPropertyName("collectionName")]
    public string CollectionName { get; set; } = string.Empty;

    /// <summary>
    /// Optional recurring pattern for automatic rescheduling after activation
    /// </summary>
    [JsonPropertyName("recurringPattern")]
    public RecurringPattern? RecurringPattern { get; set; }
}

/// <summary>
/// Server configuration settings that can be overridden by an event.
/// Only the fields that are set (not null/empty) will be applied during event activation.
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
/// Defines how an event should recur after activation
/// </summary>
public class RecurringPattern
{
    /// <summary>
    /// Type of recurrence (daily, weekly)
    /// </summary>
    [JsonPropertyName("type")]
    public RecurringType Type { get; set; }

    /// <summary>
    /// For weekly recurrence: list of days (0=Sunday, 1=Monday, ..., 6=Saturday)
    /// For daily recurrence: empty or all days
    /// </summary>
    [JsonPropertyName("days")]
    public List<int> Days { get; set; } = new();

    /// <summary>
    /// Time of day (local time) when event should activate
    /// </summary>
    [JsonPropertyName("time")]
    public TimeSpan Time { get; set; }

    /// <summary>
    /// Number of times to recur (null = infinite)
    /// </summary>
    [JsonPropertyName("occurrences")]
    public int? Occurrences { get; set; }
}

/// <summary>
/// Type of recurring pattern
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RecurringType
{
    /// <summary>
    /// Event recurs every day at the specified time
    /// </summary>
    Daily,

    /// <summary>
    /// Event recurs on specific days of the week
    /// </summary>
    Weekly
}
