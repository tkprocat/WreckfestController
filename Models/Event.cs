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

    /// <summary>The scheduled occurrence most recently activated, retained after deactivation.</summary>
    [JsonPropertyName("lastActivatedStartTime")]
    public DateTime? LastActivatedStartTime { get; set; }

    [JsonIgnore]
    public bool IsOccurrenceCompleted =>
        LastActivatedStartTime is { } last && AsUtcInstant(last) == AsUtcInstant(StartTime);

    /// <summary>
    /// The UTC instant a schedule timestamp denotes. A refresh may express the same
    /// occurrence as UTC, as a local offset, or with no zone at all - PHP emits
    /// "2026-09-06T20:00:00", which deserializes as Unspecified. Every scheduler
    /// comparison is against DateTime.UtcNow, so an unzoned value is already a UTC
    /// instant; ToUniversalTime alone would reinterpret it as local and shift the
    /// event by the machine's offset.
    /// </summary>
    public static DateTime AsUtcInstant(DateTime value) => value.Kind == DateTimeKind.Unspecified
        ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
        : value.ToUniversalTime();

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
    /// Optional recurring schedule for automatic rescheduling after activation.
    /// Null for single-occurrence events.
    /// </summary>
    [JsonPropertyName("repeat")]
    public RepeatSchedule? Repeat { get; set; }
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
/// Defines how an event should recur after activation.
/// Matches the Laravel event schedule format.
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
    /// Time of day when event should activate (format: "HH:MM")
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
            if (TimeSpan.TryParse(Time, out var result))
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
