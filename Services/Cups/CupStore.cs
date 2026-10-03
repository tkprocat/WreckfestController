using Microsoft.EntityFrameworkCore;
using WreckfestController.Data;
using WreckfestController.Data.Cups;
using WreckfestController.Models;

namespace WreckfestController.Services.Cups;

/// <summary>What an admin sets on a cup. Validated by <see cref="CupRules"/> before it gets here.</summary>
public sealed record CupDefinition(
    string Name,
    string Description,
    DateTime StartTime,
    string TimeZone,
    RepeatSchedule? Repeat,
    EventServerConfig? ServerConfig,
    int? CollectionId,
    IReadOnlyList<EventLoopTrack> Tracks,
    string CollectionName,
    string? SessionMode = null,
    string? GridOrder = null);

/// <summary>What the public page shows of a cup.</summary>
public sealed record CupSummary(
    string Name,
    string Description,
    DateTime? NextOccurrence,
    RepeatSchedule? Repeat,
    DateTime? ActivatedAt);

/// <summary>Which cup is active, and since when (UTC).</summary>
public sealed record ActiveCupSnapshot(int Id, string Name, DateTime ActivatedAt);

public enum CupWriteStatus
{
    Saved,
    NotFound,

    /// <summary>The row is no longer at the version the caller read.</summary>
    Conflict,

    /// <summary>The linked collection does not exist.</summary>
    UnknownCollection,
}

/// <summary>
/// Scheduled cups in the database. Every write touches one cup, and none of them
/// loads the schedule and saves it back, so the scheduler, the API and the WPF window
/// can all write at once without one silently undoing another.
/// </summary>
/// <remarks>
/// Admin edits are checked against <see cref="Cup.Version"/>. An edit and the
/// scheduler's advance each read and write inside one transaction, which SQLite opens
/// with <c>BEGIN IMMEDIATE</c> (Microsoft.Data.Sqlite's default), so they run one after
/// the other and each sees the other's result: an advance never uses a repeat an admin
/// has just removed, and an edit never keeps an occurrence the scheduler has just moved.
/// </remarks>
public sealed class CupStore
{
    private readonly IDbContextFactory<ControllerDbContext> _contexts;
    private readonly TimeProvider _time;

    public CupStore(IDbContextFactory<ControllerDbContext> contexts, TimeProvider time)
    {
        _contexts = contexts;
        _time = time;
    }

    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    private volatile ActiveCupSnapshot? _activeCup;

    /// <summary>
    /// The active cup as of its last change through this store, or as loaded by
    /// <see cref="LoadActiveCupAsync"/>. For callers that must not wait on the database,
    /// such as the hook pipe thread noting which cup a race ended under.
    /// </summary>
    public ActiveCupSnapshot? CachedActiveCup => _activeCup;

    /// <summary>Loads <see cref="CachedActiveCup"/> from the database.</summary>
    public async Task LoadActiveCupAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken);
        _activeCup = await db.Cups
            .AsNoTracking()
            .Where(e => e.IsActive && e.ActivatedAt != null)
            .Select(e => new ActiveCupSnapshot(e.Id, e.Name, e.ActivatedAt!.Value))
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// The active cup and the next <paramref name="upcoming"/>, as the public page shows
    /// them: a few columns each, and no settings, tracks or accounts. Two small queries,
    /// however many cups have run before.
    /// </summary>
    public async Task<(CupSummary? Active, List<CupSummary> Upcoming)> ScheduleAsync(
        int upcoming,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken);
        var active = await db.Cups
            .AsNoTracking()
            .Where(c => c.IsActive)
            .Select(c => new CupSummary(c.Name, c.Description, c.NextOccurrence, c.Repeat, c.ActivatedAt))
            .FirstOrDefaultAsync(cancellationToken);
        var next = await db.Cups
            .AsNoTracking()
            .Where(c => c.NextOccurrence != null)
            .OrderBy(c => c.NextOccurrence)
            .ThenBy(c => c.Id)
            .Take(upcoming)
            .Select(c => new CupSummary(c.Name, c.Description, c.NextOccurrence, c.Repeat, c.ActivatedAt))
            .ToListAsync(cancellationToken);
        return (active, next);
    }

    /// <summary>Every cup, with its linked collection's tracks loaded, in order of next occurrence.</summary>
    public async Task<List<Cup>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken);
        var cups = await WithCollection(db.Cups.AsNoTracking()).ToListAsync(cancellationToken);

        // Upcoming first, soonest first; finished ones after, most recent first.
        return cups
            .OrderBy(e => e.NextOccurrence is null)
            .ThenBy(e => e.NextOccurrence)
            .ThenByDescending(e => e.LastOccurrence ?? e.StartTime)
            .ThenBy(e => e.Id)
            .ToList();
    }

    public async Task<Cup?> GetAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken);
        return await WithCollection(db.Cups.AsNoTracking()).SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
    }

    public async Task<(CupWriteStatus Status, Cup? Cup)> CreateAsync(
        CupDefinition definition,
        string? createdById,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken);
        var now = _time.GetUtcNow();
        var cup = new Cup { CreatedById = createdById, CreatedAt = now };

        if (!await ApplyAsync(db, cup, definition, cancellationToken))
        {
            return (CupWriteStatus.UnknownCollection, null);
        }

        cup.UpdatedAt = now;
        cup.NextOccurrence = FirstOccurrence(cup);
        db.Cups.Add(cup);
        await db.SaveChangesAsync(cancellationToken);

        return (CupWriteStatus.Saved, await GetAsync(cup.Id, cancellationToken));
    }

    /// <summary>
    /// Replaces what an admin sets, if the cup is still at <paramref name="expectedVersion"/>.
    /// The next occurrence is recomputed only when the start, zone or repeat changed; any
    /// other edit leaves the scheduler's columns as they are. The recomputed occurrence is
    /// the first under the new schedule that has not been dealt with already.
    /// </summary>
    public async Task<(CupWriteStatus Status, Cup? Cup)> UpdateAsync(
        int id,
        CupDefinition definition,
        int expectedVersion,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var cup = await db.Cups.SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (cup is null)
        {
            return (CupWriteStatus.NotFound, null);
        }

        if (cup.Version != expectedVersion)
        {
            await transaction.RollbackAsync(cancellationToken);
            return (CupWriteStatus.Conflict, await GetAsync(id, cancellationToken));
        }

        var schedule = (cup.StartTime, cup.TimeZone, Repeat: Serialize(cup.Repeat));
        if (!await ApplyAsync(db, cup, definition, cancellationToken))
        {
            return (CupWriteStatus.UnknownCollection, null);
        }

        if (schedule != (cup.StartTime, cup.TimeZone, Serialize(cup.Repeat)))
        {
            // Removing or changing a repeat can give back occurrences that have already
            // run or been cancelled. The history says exactly which, so skip those.
            var handled = await HandledAsync(db, id, cancellationToken);
            cup.NextOccurrence = SkipHandled(FirstOccurrence(cup), handled, cup.Repeat, ZoneOf(cup.TimeZone));

            // Written even when unchanged from what was loaded: it is this edit's answer.
            db.Entry(cup).Property(e => e.NextOccurrence).IsModified = true;
        }

        cup.UpdatedAt = _time.GetUtcNow();

        // The UPDATE checks the version the caller read, not only the one loaded above.
        db.Entry(cup).Property(e => e.Version).OriginalValue = expectedVersion;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(cancellationToken);
            var current = await GetAsync(id, cancellationToken);
            return (current is null ? CupWriteStatus.NotFound : CupWriteStatus.Conflict, current);
        }

        return (CupWriteStatus.Saved, await GetAsync(id, cancellationToken));
    }

    /// <summary>Deletes the cup if it is still at <paramref name="expectedVersion"/>.</summary>
    public async Task<(CupWriteStatus Status, Cup? Current)> DeleteAsync(
        int id,
        int expectedVersion,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken);
        var deleted = await db.Cups
            .Where(e => e.Id == id && e.Version == expectedVersion)
            .ExecuteDeleteAsync(cancellationToken);
        if (deleted > 0)
        {
            if (_activeCup?.Id == id)
            {
                _activeCup = null;
            }

            return (CupWriteStatus.Saved, null);
        }

        var current = await GetAsync(id, cancellationToken);
        return (current is null ? CupWriteStatus.NotFound : CupWriteStatus.Conflict, current);
    }

    /// <summary>The cup whose next occurrence is earliest, if it is at or before <paramref name="dueBy"/>.</summary>
    public async Task<Cup?> NextDueAsync(DateTime dueBy, CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken);
        return await WithCollection(db.Cups.AsNoTracking())
            .Where(e => e.NextOccurrence != null && e.NextOccurrence <= dueBy)
            .OrderBy(e => e.NextOccurrence)
            .ThenBy(e => e.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Makes <paramref name="id"/> the active cup and every other cup inactive, in one
    /// transaction. False, changing nothing, when the cup no longer exists.
    /// </summary>
    public async Task<bool> SetActiveAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Deactivate first: the unique index allows one active row at a time.
        await db.Cups
            .Where(e => e.IsActive && e.Id != id)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.IsActive, false), cancellationToken);

        var now = UtcNow;
        var activated = await db.Cups
            .Where(e => e.Id == id)
            .ExecuteUpdateAsync(
                s => s.SetProperty(e => e.IsActive, true).SetProperty(e => e.ActivatedAt, now),
                cancellationToken);

        if (activated == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        var name = await db.Cups.Where(e => e.Id == id).Select(e => e.Name).FirstAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        _activeCup = new ActiveCupSnapshot(id, name, now);
        return true;
    }

    /// <summary>
    /// Records <paramref name="occurrence"/> as dealt with, and how, in the cup's history,
    /// and moves the cup to what
    /// follows it: the next occurrence after it and after now, or none for a one-off.
    /// Does nothing, returning false, if the cup no longer waits for that occurrence -
    /// it was rescheduled or deleted since.
    /// </summary>
    public async Task<bool> AdvanceAsync(
        int id,
        DateTime occurrence,
        OccurrenceOutcome outcome,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken);

        // Read and write under one lock, so the repeat used is the one in force.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var schedule = await db.Cups
            .AsNoTracking()
            .Where(e => e.Id == id)
            .Select(e => new { e.Repeat, e.TimeZone, e.NextOccurrence })
            .SingleOrDefaultAsync(cancellationToken);
        if (schedule?.NextOccurrence != occurrence)
        {
            return false;
        }

        // The history as well as the repeat: an earlier reschedule can put occurrences
        // already dealt with after this one.
        var zone = ZoneOf(schedule.TimeZone);
        var handled = await HandledAsync(db, id, cancellationToken);
        handled.Add(occurrence);
        var next = SkipHandled(CupRecurrence.After(occurrence, schedule.Repeat, zone, UtcNow), handled, schedule.Repeat, zone);

        // Still compare-and-set, as a second line of defence.
        var updated = await db.Cups
            .Where(e => e.Id == id && e.NextOccurrence == occurrence)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(e => e.NextOccurrence, next)
                    .SetProperty(e => e.LastOccurrence, occurrence)
                    .SetProperty(e => e.LastOutcome, outcome),
                cancellationToken);
        if (updated == 0)
        {
            return false;
        }

        // Should never be there already, since the next occurrence skips the history. If it
        // somehow is, the advance must still go through: a failing insert would roll it back
        // and leave the occurrence due, blocking the scheduler on every check.
        if (!await db.CupOccurrences.AnyAsync(
                o => o.CupId == id && o.Occurrence == occurrence, cancellationToken))
        {
            db.CupOccurrences.Add(new CupOccurrenceRecord
            {
                CupId = id,
                Occurrence = occurrence,
                Outcome = outcome,
                RecordedAt = UtcNow,
            });
            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static async Task<HashSet<DateTime>> HandledAsync(
        ControllerDbContext db,
        int id,
        CancellationToken cancellationToken) =>
        (await db.CupOccurrences
            .Where(o => o.CupId == id)
            .Select(o => o.Occurrence)
            .ToListAsync(cancellationToken))
        .ToHashSet();

    /// <summary>
    /// <paramref name="candidate"/>, or the first occurrence after it that is not in
    /// <paramref name="handled"/>. Null once a one-off cup has none left.
    /// </summary>
    private DateTime? SkipHandled(DateTime? candidate, HashSet<DateTime> handled, RepeatSchedule? repeat, TimeZoneInfo zone)
    {
        // Bounded: each step moves strictly later, and the history is finite.
        for (var i = 0; i <= handled.Count && candidate is { } at && handled.Contains(at); i++)
        {
            candidate = CupRecurrence.After(at, repeat, zone, UtcNow);
        }

        return candidate;
    }

    /// <summary>What activation deploys: the linked collection's tracks as they are now, else the cup's own.</summary>
    public static Event ToRestartEvent(Cup cup)
    {
        if (cup.Collection is { } collection)
        {
            return new Event
            {
                Id = cup.Id,
                Name = cup.Name,
                ServerConfig = cup.ServerConfig,
                SessionMode = cup.SessionMode,
                GridOrder = cup.GridOrder,
                Tracks = collection.Entries
                    .OrderBy(e => e.Position)
                    .Select(Controllers.CollectionMapping.ToEventLoopTrack)
                    .ToList(),
                CollectionName = collection.Name,
            };
        }

        return new Event
        {
            Id = cup.Id,
            Name = cup.Name,
            ServerConfig = cup.ServerConfig,
            SessionMode = cup.SessionMode,
            GridOrder = cup.GridOrder,
            Tracks = cup.Tracks,
            CollectionName = cup.CollectionName,
        };
    }

    /// <summary>Unknown zones fall back to UTC; <see cref="CupRules"/> keeps them out.</summary>
    public static TimeZoneInfo ZoneOf(string timeZone) => CupRecurrence.FindZone(timeZone) ?? TimeZoneInfo.Utc;

    private DateTime? FirstOccurrence(Cup cup) =>
        CupRecurrence.FirstOccurrence(cup.StartTime, cup.Repeat, ZoneOf(cup.TimeZone), UtcNow);

    /// <summary>
    /// Copies <paramref name="definition"/> onto <paramref name="cup"/>. A linked cup also
    /// keeps a snapshot of the collection's tracks. False when the collection is missing.
    /// </summary>
    private static async Task<bool> ApplyAsync(
        ControllerDbContext db,
        Cup cup,
        CupDefinition definition,
        CancellationToken cancellationToken)
    {
        cup.Name = definition.Name;
        cup.Description = definition.Description;
        cup.StartTime = definition.StartTime;
        cup.TimeZone = definition.TimeZone;
        cup.Repeat = definition.Repeat;
        cup.ServerConfig = definition.ServerConfig;
        cup.SessionMode = definition.SessionMode;
        cup.GridOrder = definition.GridOrder;
        cup.CollectionId = definition.CollectionId;

        if (definition.CollectionId is not { } collectionId)
        {
            cup.Tracks = definition.Tracks.ToList();
            cup.CollectionName = definition.CollectionName;
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

        cup.Tracks = collection.Entries
            .OrderBy(e => e.Position)
            .Select(Controllers.CollectionMapping.ToEventLoopTrack)
            .ToList();
        cup.CollectionName = collection.Name;
        return true;
    }

    private static IQueryable<Cup> WithCollection(IQueryable<Cup> query) =>
        query
            .AsSplitQuery()
            .Include(e => e.CreatedBy)
            .Include(e => e.Collection)
            .ThenInclude(c => c!.Entries)
            .ThenInclude(e => e.TrackVariant);

    private static string Serialize(RepeatSchedule? repeat) => System.Text.Json.JsonSerializer.Serialize(repeat);
}
