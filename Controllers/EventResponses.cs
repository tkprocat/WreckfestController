using System.Text.RegularExpressions;
using WreckfestController.Data.Events;
using WreckfestController.Models;
using WreckfestController.Services;

namespace WreckfestController.Controllers;

/// <summary>
/// An event as the API returns it. <see cref="Tracks"/> is what activation would deploy
/// now: a linked collection's current tracks, else the event's own.
/// <see cref="Version"/> (and the ETag) covers what an admin edits; the scheduler's
/// fields from <see cref="NextOccurrence"/> on change without it.
/// </summary>
public sealed record EventResponse(
    int Id,
    string Name,
    string Description,
    DateTime StartTime,
    string TimeZone,
    RepeatSchedule? Repeat,
    string RepeatDescription,
    EventServerConfig? ServerConfig,
    int? CollectionId,
    string CollectionName,
    IReadOnlyList<EventLoopTrack> Tracks,
    DateTime? NextOccurrence,
    DateTime? LastOccurrence,
    OccurrenceOutcome? LastOutcome,
    bool IsActive,
    DateTime? ActivatedAt,
    string? CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int Version)
{
    /// <summary>Expects the linked collection's entries, with their variants, and the creator loaded.</summary>
    public static EventResponse From(ScheduledEvent evt)
    {
        var deployed = EventStore.ToRestartEvent(evt);
        return new(
            evt.Id,
            evt.Name,
            evt.Description,
            evt.StartTime,
            evt.TimeZone,
            evt.Repeat,
            EventRecurrence.Describe(evt.Repeat),
            evt.ServerConfig,
            evt.CollectionId,
            deployed.CollectionName,
            deployed.Tracks,
            evt.NextOccurrence,
            evt.LastOccurrence,
            evt.LastOutcome,
            evt.IsActive,
            evt.ActivatedAt,
            evt.CreatedBy is { } user ? user.DisplayName ?? user.UserName : null,
            evt.CreatedAt,
            evt.UpdatedAt,
            evt.Version);
    }
}

public sealed record EventListResponse(int Count, IReadOnlyList<EventResponse> Events);

public sealed record EventSummaryResponse(
    int TotalEvents,
    int ActiveEvents,
    int UpcomingEvents,
    int DueEvents,
    DateTimeOffset? LastUpdated);

/// <summary>
/// Create or replace an event. Either link a collection with <see cref="CollectionId"/>,
/// or give the rotation inline in <see cref="Tracks"/> and <see cref="CollectionName"/>;
/// no tracks at all leaves the server's rotation alone on activation.
/// </summary>
public sealed class EventRequest
{
    public string? Name { get; init; }

    public string? Description { get; init; }

    /// <summary>With a zone: "2026-10-02T18:00:00Z" or "2026-10-02T20:00:00+02:00".</summary>
    public DateTime? StartTime { get; init; }

    /// <summary>IANA id such as "Europe/Copenhagen" for the repeat's wall-clock time. Defaults to UTC.</summary>
    public string? TimeZone { get; init; }

    public RepeatSchedule? Repeat { get; init; }

    public EventServerConfig? ServerConfig { get; init; }

    public int? CollectionId { get; init; }

    public List<EventLoopTrack?>? Tracks { get; init; }

    public string? CollectionName { get; init; }
}

/// <summary>What an event request must satisfy. Everything here ends up in server_config.cfg or the schedule.</summary>
public static class EventRules
{
    private const int ServerTextMaxLength = 256;
    private static readonly Regex TimeOfDay = new("^([01][0-9]|2[0-3]):[0-5][0-9]$", RegexOptions.CultureInvariant);

    public static EventLoopError? Validate(EventRequest request, out EventDefinition? definition)
    {
        definition = null;

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return new("name", "name is required.");
        }

        var name = request.Name.Trim();

        // Becomes "#CollectionName Event: <name>" when no collection name is given.
        if (name.AsSpan().IndexOfAny('\r', '\n') >= 0)
        {
            return new("name", "name must not contain line breaks.");
        }

        if (name.Length > ScheduledEvent.NameMaxLength)
        {
            return new("name", $"name must be at most {ScheduledEvent.NameMaxLength} characters.");
        }

        var description = request.Description?.Trim() ?? string.Empty;
        if (description.Length > ScheduledEvent.DescriptionMaxLength)
        {
            return new("description", $"description must be at most {ScheduledEvent.DescriptionMaxLength} characters.");
        }

        if (request.StartTime is not { } startTime)
        {
            return new("startTime", "startTime is required.");
        }

        // An unzoned time is ambiguous; 1.x guessed UTC and moved events by the machine's offset.
        if (startTime.Kind == DateTimeKind.Unspecified)
        {
            return new("startTime", "startTime needs a UTC offset, such as 2026-10-02T18:00:00Z.");
        }

        var timeZone = string.IsNullOrWhiteSpace(request.TimeZone) ? ScheduledEvent.DefaultTimeZone : request.TimeZone.Trim();
        if (timeZone.Length > ScheduledEvent.TimeZoneMaxLength || EventRecurrence.FindZone(timeZone) is null)
        {
            return new("timeZone", $"timeZone '{timeZone}' is not a known time zone, such as Europe/Copenhagen.");
        }

        if (ValidateRepeat(request.Repeat, out var repeat) is { } repeatError)
        {
            return repeatError;
        }

        if (ValidateServerConfig(request.ServerConfig) is { } configError)
        {
            return configError;
        }

        var tracks = new List<EventLoopTrack>();
        var collectionName = string.Empty;
        if (request.CollectionId is not null)
        {
            if (request.Tracks is { Count: > 0 })
            {
                return new("tracks", "Give either collectionId or tracks, not both.");
            }
        }
        else if (request.Tracks is { Count: > 0 })
        {
            // The name is optional: activation falls back to "Event: <name>".
            var tracksError = EventLoopTrackRules.ValidateTracks(request.Tracks)
                ?? (string.IsNullOrWhiteSpace(request.CollectionName)
                    ? null
                    : EventLoopTrackRules.ValidateName("collectionName", request.CollectionName));
            if (tracksError is not null)
            {
                return tracksError;
            }

            tracks = request.Tracks.Select((t, i) => CollectionMapping.ToEventLoopTrack(CollectionMapping.ToEntry(t!, i))).ToList();
            collectionName = request.CollectionName?.Trim() ?? string.Empty;
        }

        definition = new EventDefinition(
            name,
            description,
            startTime.ToUniversalTime(),
            timeZone,
            repeat,
            Normalize(request.ServerConfig),
            request.CollectionId,
            tracks,
            collectionName);
        return null;
    }

    private static EventLoopError? ValidateRepeat(RepeatSchedule? repeat, out RepeatSchedule? normalized)
    {
        normalized = null;
        if (repeat is null)
        {
            return null;
        }

        if (!repeat.IsDaily && !repeat.IsWeekly)
        {
            return new("repeat.frequency", "repeat.frequency must be daily or weekly.");
        }

        if (repeat.Time is null || !TimeOfDay.IsMatch(repeat.Time))
        {
            return new("repeat.time", "repeat.time must be HH:MM, 00:00 to 23:59.");
        }

        List<int>? days = null;
        if (repeat.IsWeekly)
        {
            if (repeat.Days is not { Count: > 0 })
            {
                return new("repeat.days", "A weekly repeat needs at least one day.");
            }

            if (repeat.Days.Any(d => d is < 0 or > 6))
            {
                return new("repeat.days", "repeat.days are 0 (Sunday) to 6 (Saturday).");
            }

            days = repeat.Days.Distinct().Order().ToList();
        }

        normalized = new RepeatSchedule
        {
            Frequency = repeat.IsDaily ? "daily" : "weekly",
            Days = days,
            Time = repeat.Time,
        };
        return null;
    }

    private static EventLoopError? ValidateServerConfig(EventServerConfig? config)
    {
        if (config is null)
        {
            return null;
        }

        // Each becomes one line of server_config.cfg.
        (string Field, string? Value)[] texts =
        [
            ("serverConfig.serverName", config.ServerName),
            ("serverConfig.welcomeMessage", config.WelcomeMessage),
            ("serverConfig.password", config.Password),
            ("serverConfig.aiDifficulty", config.AiDifficulty),
            ("serverConfig.vehicleDamage", config.VehicleDamage),
        ];
        foreach (var (field, value) in texts)
        {
            if (value is null)
            {
                continue;
            }

            if (value.AsSpan().IndexOfAny('\r', '\n') >= 0)
            {
                return new(field, $"{field} must not contain line breaks.");
            }

            if (value.Length > ServerTextMaxLength)
            {
                return new(field, $"{field} must be at most {ServerTextMaxLength} characters.");
            }
        }

        if (config.MaxPlayers is < 1 || config.Bots is < 0 || config.Laps is < 1 || config.LobbyCountdown is < 0)
        {
            return new("serverConfig", "serverConfig maxPlayers and laps must be at least 1, and bots and lobbyCountdown not negative.");
        }

        return null;
    }

    /// <summary>Null when nothing is set, so an empty object does not read as "overrides settings".</summary>
    private static EventServerConfig? Normalize(EventServerConfig? config) =>
        config is null
        || (config.ServerName is null && config.WelcomeMessage is null && config.Password is null
            && config.MaxPlayers is null && config.Bots is null && config.AiDifficulty is null
            && config.Laps is null && config.VehicleDamage is null && config.LobbyCountdown is null)
            ? null
            : config;
}
