using System.Text.RegularExpressions;
using WreckfestController.Data.Cups;
using WreckfestController.Models;
using WreckfestController.Services.Cups;

namespace WreckfestController.Controllers;

/// <summary>
/// A cup as the API returns it. <see cref="Tracks"/> is what activation would deploy
/// now: a linked collection's current tracks, else the cup's own.
/// <see cref="Version"/> (and the ETag) covers what an admin edits; the scheduler's
/// fields from <see cref="NextOccurrence"/> on change without it.
/// <see cref="NextWarmup"/> and <see cref="NextEnd"/> are the next occurrence's window;
/// <see cref="CurrentStart"/> and <see cref="CurrentEnd"/> the window of the run the active
/// cup is in, whose occurrence the scheduler has already moved past.
/// </summary>
public sealed record CupResponse(
    int Id,
    string Name,
    string Description,
    DateTime StartTime,
    string TimeZone,
    RepeatSchedule? Repeat,
    string RepeatDescription,
    EventServerConfig? ServerConfig,
    string? SessionMode,
    string? GridOrder,
    int? CollectionId,
    string CollectionName,
    IReadOnlyList<EventLoopTrack> Tracks,
    DateTime? NextOccurrence,
    DateTime? LastOccurrence,
    OccurrenceOutcome? LastOutcome,
    bool IsActive,
    DateTime? ActivatedAt,
    string? WarmupTime,
    string? EndTime,
    bool RestartRotationAtStart,
    DateTime? NextWarmup,
    DateTime? NextEnd,
    CupPhase? Phase,
    DateTime? CurrentStart,
    DateTime? CurrentEnd,
    string? CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int Version)
{
    /// <summary>Expects the linked collection's entries, with their variants, and the creator loaded.</summary>
    public static CupResponse From(Cup cup)
    {
        var deployed = CupStore.ToRestartEvent(cup);
        var next = CupStore.WindowOf(cup, cup.NextOccurrence);

        return new(
            cup.Id,
            cup.Name,
            cup.Description,
            cup.StartTime,
            cup.TimeZone,
            cup.Repeat,
            CupRecurrence.Describe(cup.Repeat),
            cup.ServerConfig,
            cup.SessionMode,
            cup.GridOrder,
            cup.CollectionId,
            deployed.CollectionName,
            deployed.Tracks,
            cup.NextOccurrence,
            cup.LastOccurrence,
            cup.LastOutcome,
            cup.IsActive,
            cup.ActivatedAt,
            CupRules.Format(cup.WarmupTime),
            CupRules.Format(cup.EndTime),
            cup.RestartRotationAtStart,
            next?.Warmup,
            next?.End,
            cup.IsActive ? CupStore.Shown(cup.Phase) : null,
            cup.IsActive ? cup.CurrentOccurrence : null,
            cup.IsActive ? cup.CurrentEnd : null,
            cup.CreatedBy is { } user ? user.DisplayName ?? user.UserName : null,
            cup.CreatedAt,
            cup.UpdatedAt,
            cup.Version);
    }
}

public sealed record CupListResponse(int Count, IReadOnlyList<CupResponse> Cups);

public sealed record CupSummaryResponse(
    int TotalCups,
    int ActiveCups,
    int UpcomingCups,
    int DueCups,
    DateTimeOffset? LastUpdated);

/// <summary>
/// Create or replace a cup. Either link a collection with <see cref="CollectionId"/>,
/// or give the rotation inline in <see cref="Tracks"/> and <see cref="CollectionName"/>;
/// no tracks at all leaves the server's rotation alone on activation.
/// </summary>
public sealed class CupRequest
{
    public string? Name { get; init; }

    public string? Description { get; init; }

    /// <summary>With a zone: "2026-10-02T18:00:00Z" or "2026-10-02T20:00:00+02:00".</summary>
    public DateTime? StartTime { get; init; }

    /// <summary>IANA id such as "Europe/Copenhagen" for the repeat's wall-clock time. Defaults to UTC.</summary>
    public string? TimeZone { get; init; }

    public RepeatSchedule? Repeat { get; init; }

    public EventServerConfig? ServerConfig { get; init; }

    /// <summary>
    /// <c>session_mode</c>: a cup points system such as "30p-aggr", "normal" for no cup
    /// points, or a qualifying session. Omit to keep the server's own.
    /// </summary>
    public string? SessionMode { get; init; }

    /// <summary><c>grid_order</c>, such as "cup_reverse". Omit to keep the server's own.</summary>
    public string? GridOrder { get; init; }

    /// <summary>
    /// "HH:MM" in <see cref="TimeZone"/>: when the warmup begins, which is when the server
    /// restarts into the cup. At most 12 hours before the start. Omit for no warmup.
    /// </summary>
    public string? WarmupTime { get; init; }

    /// <summary>"HH:MM" in <see cref="TimeZone"/>: when the cup ends. Omit for no end.</summary>
    public string? EndTime { get; init; }

    /// <summary>At the start, send the event loop back to the beginning of the rotation.</summary>
    public bool? RestartRotationAtStart { get; init; }

    public int? CollectionId { get; init; }

    public List<EventLoopTrack?>? Tracks { get; init; }

    public string? CollectionName { get; init; }
}

/// <summary>What a cup request must satisfy. Everything here ends up in server_config.cfg or the schedule.</summary>
public static class CupRules
{
    private const int ServerTextMaxLength = 256;

    /// <summary>How far before its start a warmup may begin.</summary>
    public static readonly TimeSpan MaxWarmup = CupRecurrence.MaxWarmup;
    private static readonly Regex TimeOfDay = new("^([01][0-9]|2[0-3]):[0-5][0-9]$", RegexOptions.CultureInvariant);

    public static EventLoopError? Validate(CupRequest request, out CupDefinition? definition)
    {
        definition = null;

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return new("name", "name is required.");
        }

        var name = request.Name.Trim();

        // Becomes "#CollectionName Cup: <name>" when no collection name is given.
        if (name.AsSpan().IndexOfAny('\r', '\n') >= 0)
        {
            return new("name", "name must not contain line breaks.");
        }

        if (name.Length > Cup.NameMaxLength)
        {
            return new("name", $"name must be at most {Cup.NameMaxLength} characters.");
        }

        var description = request.Description?.Trim() ?? string.Empty;
        if (description.Length > Cup.DescriptionMaxLength)
        {
            return new("description", $"description must be at most {Cup.DescriptionMaxLength} characters.");
        }

        if (request.StartTime is not { } startTime)
        {
            return new("startTime", "startTime is required.");
        }

        // An unzoned time is ambiguous; 1.x guessed UTC and moved cups by the machine's offset.
        if (startTime.Kind == DateTimeKind.Unspecified)
        {
            return new("startTime", "startTime needs a UTC offset, such as 2026-10-02T18:00:00Z.");
        }

        var timeZone = string.IsNullOrWhiteSpace(request.TimeZone) ? Cup.DefaultTimeZone : request.TimeZone.Trim();
        if (timeZone.Length > Cup.TimeZoneMaxLength || CupRecurrence.FindZone(timeZone) is null)
        {
            return new("timeZone", $"timeZone '{timeZone}' is not a known time zone, such as Europe/Copenhagen.");
        }

        if (ValidateRepeat(request.Repeat, out var repeat) is { } repeatError)
        {
            return repeatError;
        }

        if (ValidateWindow(request, startTime, timeZone, repeat, out var warmupTime, out var endTime) is { } windowError)
        {
            return windowError;
        }

        if (ValidateServerConfig(request.ServerConfig) is { } configError)
        {
            return configError;
        }

        if (ValidateChoice("sessionMode", request.SessionMode, CupScoring.SessionModes, out var sessionMode) is { } sessionModeError)
        {
            return sessionModeError;
        }

        if (ValidateChoice("gridOrder", request.GridOrder, CupScoring.GridOrders, out var gridOrder) is { } gridOrderError)
        {
            return gridOrderError;
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
            // The name is optional: activation falls back to "Cup: <name>".
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

        definition = new CupDefinition(
            name,
            description,
            startTime.ToUniversalTime(),
            timeZone,
            repeat,
            Normalize(request.ServerConfig),
            request.CollectionId,
            tracks,
            collectionName,
            sessionMode,
            gridOrder,
            warmupTime,
            endTime,
            request.RestartRotationAtStart ?? false);
        return null;
    }

    /// <summary>"HH:MM", or null.</summary>
    public static string? Format(TimeOnly? time) => time?.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The warmup and end clock times, checked against the start's clock time: the repeat's
    /// time, or a one-off start in the cup's zone. A warmup at the start is no warmup.
    /// </summary>
    private static EventLoopError? ValidateWindow(
        CupRequest request,
        DateTime startTime,
        string timeZone,
        RepeatSchedule? repeat,
        out TimeOnly? warmupTime,
        out TimeOnly? endTime)
    {
        warmupTime = null;
        endTime = null;
        if (ParseTime("warmupTime", request.WarmupTime, out var warmup) is { } warmupError)
        {
            return warmupError;
        }

        if (ParseTime("endTime", request.EndTime, out var end) is { } endError)
        {
            return endError;
        }

        var start = repeat is not null
            ? TimeOnly.ParseExact(repeat.Time!, "HH:mm", System.Globalization.CultureInfo.InvariantCulture)
            : TimeOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(startTime.ToUniversalTime(), CupRecurrence.FindZone(timeZone)!));
        var (warmupSpan, duration) = CupRecurrence.ClockSpans(start, warmup, end);

        if (warmupSpan > MaxWarmup)
        {
            return new("warmupTime", $"warmupTime must be at most {MaxWarmup.TotalHours:0} hours before the start ({Format(start)}).");
        }

        if (duration == TimeSpan.Zero)
        {
            return new("endTime", $"endTime must differ from the start ({Format(start)}).");
        }

        warmupTime = warmupSpan == TimeSpan.Zero ? null : warmup;
        endTime = end;
        return null;
    }

    private static EventLoopError? ParseTime(string field, string? value, out TimeOnly? time)
    {
        time = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!TimeOfDay.IsMatch(value.Trim()))
        {
            return new(field, $"{field} must be HH:MM, 00:00 to 23:59.");
        }

        time = TimeOnly.ParseExact(value.Trim(), "HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        return null;
    }

    /// <summary>
    /// Blank is "not set". Anything else must be one of <paramref name="allowed"/>, ignoring
    /// case, and is stored as the server spells it: it becomes a line of server_config.cfg.
    /// </summary>
    private static EventLoopError? ValidateChoice(string field, string? value, IReadOnlyList<string> allowed, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        normalized = allowed.FirstOrDefault(a => string.Equals(a, value.Trim(), StringComparison.OrdinalIgnoreCase));
        return normalized is null
            ? new(field, $"{field} must be one of: {string.Join(", ", allowed)}.")
            : null;
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
