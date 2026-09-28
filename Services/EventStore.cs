using Microsoft.EntityFrameworkCore;
using WreckfestController.Data;
using WreckfestController.Data.Events;
using WreckfestController.Models;

namespace WreckfestController.Services;

/// <summary>What an admin sets on an event. Validated by <see cref="EventRules"/> before it gets here.</summary>
public sealed record EventDefinition(
    string Name,
    string Description,
    DateTime StartTime,
    string TimeZone,
    RepeatSchedule? Repeat,
    EventServerConfig? ServerConfig,
    int? CollectionId,
    IReadOnlyList<EventLoopTrack> Tracks,
    string CollectionName);

public enum EventWriteStatus
{
    Saved,
    NotFound,

    /// <summary>The row is no longer at the version the caller read.</summary>
    Conflict,

    /// <summary>The linked collection does not exist.</summary>
    UnknownCollection,
}

/// <summary>
/// Scheduled events in the database. Every write touches one event, and none of them
/// loads the schedule and saves it back, so the scheduler, the API and the WPF window
/// can all write at once without one silently undoing another.
/// </summary>
/// <remarks>
/// Admin edits are checked against <see cref="ScheduledEvent.Version"/>. The scheduler's
/// writes are compare-and-set on the columns they own: advancing past an occurrence
/// only happens while the event still waits for that occurrence, so an admin who
/// rescheduled in the meantime keeps their new time.
/// </remarks>
public sealed class EventStore
{
    private readonly IDbContextFactory<ControllerDbContext> _contexts;
    private readonly TimeProvider _time;

    public EventStore(IDbContextFactory<ControllerDbContext> contexts, TimeProvider time)
    {
        _contexts = contexts;
        _time = time;
    }

    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    /// <summary>Every event, with its linked collection's tracks loaded, in order of next occurrence.</summary>
    public async Task<List<ScheduledEvent>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken);
        var events = await WithCollection(db.ScheduledEvents.AsNoTracking()).ToListAsync(cancellationToken);

        // Upcoming first, soonest first; finished ones after, most recent first.
        return events
            .OrderBy(e => e.NextOccurrence is null)
            .ThenBy(e => e.NextOccurrence)
            .ThenByDescending(e => e.LastOccurrence ?? e.StartTime)
            .ThenBy(e => e.Id)
            .ToList();
    }

    public async Task<ScheduledEvent?> GetAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken);
        return await WithCollection(db.ScheduledEvents.AsNoTracking()).SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
    }

    public async Task<(EventWriteStatus Status, ScheduledEvent? Event)> CreateAsync(
        EventDefinition definition,
        string? createdById,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken);
        var now = _time.GetUtcNow();
        var evt = new ScheduledEvent { CreatedById = createdById, CreatedAt = now };

        if (!await ApplyAsync(db, evt, definition, cancellationToken))
        {
            return (EventWriteStatus.UnknownCollection, null);
        }

        evt.UpdatedAt = now;
        evt.NextOccurrence = FirstOccurrence(evt);
        db.ScheduledEvents.Add(evt);
        await db.SaveChangesAsync(cancellationToken);

        return (EventWriteStatus.Saved, await GetAsync(evt.Id, cancellationToken));
    }

    /// <summary>
    /// Replaces what an admin sets, if the event is still at <paramref name="expectedVersion"/>.
    /// The next occurrence is recomputed only when the start, zone or repeat changed; any
    /// other edit leaves the scheduler's columns as they are.
    /// </summary>
    public async Task<(EventWriteStatus Status, ScheduledEvent? Event)> UpdateAsync(
        int id,
        EventDefinition definition,
        int expectedVersion,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken);
        var evt = await db.ScheduledEvents.SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (evt is null)
        {
            return (EventWriteStatus.NotFound, null);
        }

        if (evt.Version != expectedVersion)
        {
            return (EventWriteStatus.Conflict, await GetAsync(id, cancellationToken));
        }

        var schedule = (evt.StartTime, evt.TimeZone, Repeat: Serialize(evt.Repeat));
        if (!await ApplyAsync(db, evt, definition, cancellationToken))
        {
            return (EventWriteStatus.UnknownCollection, null);
        }

        if (schedule != (evt.StartTime, evt.TimeZone, Serialize(evt.Repeat)))
        {
            evt.NextOccurrence = FirstOccurrence(evt);
        }

        evt.UpdatedAt = _time.GetUtcNow();

        // The UPDATE checks the version the caller read, not only the one loaded above.
        db.Entry(evt).Property(e => e.Version).OriginalValue = expectedVersion;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            var current = await GetAsync(id, cancellationToken);
            return (current is null ? EventWriteStatus.NotFound : EventWriteStatus.Conflict, current);
        }

        return (EventWriteStatus.Saved, await GetAsync(id, cancellationToken));
    }

    /// <summary>Deletes the event if it is still at <paramref name="expectedVersion"/>.</summary>
    public async Task<(EventWriteStatus Status, ScheduledEvent? Current)> DeleteAsync(
        int id,
        int expectedVersion,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken);
        var deleted = await db.ScheduledEvents
            .Where(e => e.Id == id && e.Version == expectedVersion)
            .ExecuteDeleteAsync(cancellationToken);
        if (deleted > 0)
        {
            return (EventWriteStatus.Saved, null);
        }

        var current = await GetAsync(id, cancellationToken);
        return (current is null ? EventWriteStatus.NotFound : EventWriteStatus.Conflict, current);
    }

    /// <summary>The event whose next occurrence is earliest, if it is at or before <paramref name="dueBy"/>.</summary>
    public async Task<ScheduledEvent?> NextDueAsync(DateTime dueBy, CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken);
        return await WithCollection(db.ScheduledEvents.AsNoTracking())
            .Where(e => e.NextOccurrence != null && e.NextOccurrence <= dueBy)
            .OrderBy(e => e.NextOccurrence)
            .ThenBy(e => e.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Makes <paramref name="id"/> the active event and every other event inactive, in one
    /// transaction. False, changing nothing, when the event no longer exists.
    /// </summary>
    public async Task<bool> SetActiveAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Deactivate first: the unique index allows one active row at a time.
        await db.ScheduledEvents
            .Where(e => e.IsActive && e.Id != id)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.IsActive, false), cancellationToken);

        var now = UtcNow;
        var activated = await db.ScheduledEvents
            .Where(e => e.Id == id)
            .ExecuteUpdateAsync(
                s => s.SetProperty(e => e.IsActive, true).SetProperty(e => e.ActivatedAt, now),
                cancellationToken);

        if (activated == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Records <paramref name="occurrence"/> as dealt with, and how, and moves the event to what
    /// follows it: the next occurrence after it and after now, or none for a one-off.
    /// Does nothing, returning false, if the event no longer waits for that occurrence -
    /// it was rescheduled or deleted since.
    /// </summary>
    public async Task<bool> AdvanceAsync(
        int id,
        DateTime occurrence,
        OccurrenceOutcome outcome,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken);
        var schedule = await db.ScheduledEvents
            .AsNoTracking()
            .Where(e => e.Id == id)
            .Select(e => new { e.Repeat, e.TimeZone, e.NextOccurrence })
            .SingleOrDefaultAsync(cancellationToken);
        if (schedule?.NextOccurrence != occurrence)
        {
            return false;
        }

        var next = EventRecurrence.After(occurrence, schedule.Repeat, ZoneOf(schedule.TimeZone), UtcNow);

        // Compare-and-set, so an edit between the read above and here wins.
        var updated = await db.ScheduledEvents
            .Where(e => e.Id == id && e.NextOccurrence == occurrence)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(e => e.NextOccurrence, next)
                    .SetProperty(e => e.LastOccurrence, occurrence)
                    .SetProperty(e => e.LastOutcome, outcome),
                cancellationToken);
        return updated > 0;
    }

    /// <summary>What activation deploys: the linked collection's tracks as they are now, else the event's own.</summary>
    public static Event ToRestartEvent(ScheduledEvent evt)
    {
        if (evt.Collection is { } collection)
        {
            return new Event
            {
                Id = evt.Id,
                Name = evt.Name,
                ServerConfig = evt.ServerConfig,
                Tracks = collection.Entries
                    .OrderBy(e => e.Position)
                    .Select(Controllers.CollectionMapping.ToEventLoopTrack)
                    .ToList(),
                CollectionName = collection.Name,
            };
        }

        return new Event
        {
            Id = evt.Id,
            Name = evt.Name,
            ServerConfig = evt.ServerConfig,
            Tracks = evt.Tracks,
            CollectionName = evt.CollectionName,
        };
    }

    /// <summary>Unknown zones fall back to UTC; <see cref="EventRules"/> keeps them out.</summary>
    public static TimeZoneInfo ZoneOf(string timeZone) => EventRecurrence.FindZone(timeZone) ?? TimeZoneInfo.Utc;

    private DateTime? FirstOccurrence(ScheduledEvent evt) =>
        EventRecurrence.FirstOccurrence(evt.StartTime, evt.Repeat, ZoneOf(evt.TimeZone), UtcNow);

    /// <summary>
    /// Copies <paramref name="definition"/> onto <paramref name="evt"/>. A linked event also
    /// keeps a snapshot of the collection's tracks. False when the collection is missing.
    /// </summary>
    private static async Task<bool> ApplyAsync(
        ControllerDbContext db,
        ScheduledEvent evt,
        EventDefinition definition,
        CancellationToken cancellationToken)
    {
        evt.Name = definition.Name;
        evt.Description = definition.Description;
        evt.StartTime = definition.StartTime;
        evt.TimeZone = definition.TimeZone;
        evt.Repeat = definition.Repeat;
        evt.ServerConfig = definition.ServerConfig;
        evt.CollectionId = definition.CollectionId;

        if (definition.CollectionId is not { } collectionId)
        {
            evt.Tracks = definition.Tracks.ToList();
            evt.CollectionName = definition.CollectionName;
            return true;
        }

        var collection = await db.TrackCollections
            .AsNoTracking()
            .Include(c => c.Entries)
            .ThenInclude(e => e.TrackVariant)
            .SingleOrDefaultAsync(c => c.Id == collectionId, cancellationToken);
        if (collection is null)
        {
            return false;
        }

        evt.Tracks = collection.Entries
            .OrderBy(e => e.Position)
            .Select(Controllers.CollectionMapping.ToEventLoopTrack)
            .ToList();
        evt.CollectionName = collection.Name;
        return true;
    }

    private static IQueryable<ScheduledEvent> WithCollection(IQueryable<ScheduledEvent> query) =>
        query
            .AsSplitQuery()
            .Include(e => e.CreatedBy)
            .Include(e => e.Collection)
            .ThenInclude(c => c!.Entries)
            .ThenInclude(e => e.TrackVariant);

    private static string Serialize(RepeatSchedule? repeat) => System.Text.Json.JsonSerializer.Serialize(repeat);
}
