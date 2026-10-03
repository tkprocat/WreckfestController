using Microsoft.EntityFrameworkCore;
using WreckfestController.Data;
using WreckfestController.Data.Races;
using WreckfestController.Services.Cups;
using WreckfestController.Services.Hook;

namespace WreckfestController.Services.Races;

/// <summary>Stores finished races. Rows are history: written once, never edited.</summary>
public sealed class RaceResultStore
{
    private readonly IDbContextFactory<ControllerDbContext> _contexts;

    public RaceResultStore(IDbContextFactory<ControllerDbContext> contexts)
    {
        _contexts = contexts;
    }

    /// <summary>
    /// Saves <paramref name="record"/>, linked to the cup that was active when the race
    /// ended. <paramref name="notedCup"/> is the active cup as the caller noted it at that
    /// moment; it decides only when the database no longer can (see <see cref="CupAtEnd"/>).
    /// <paramref name="fallbackTrackId"/> is used only when the hook could not read the track.
    /// </summary>
    public async Task<Race> SaveAsync(
        HookRaceRecord record,
        ActiveCupSnapshot? notedCup = null,
        string? fallbackTrackId = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken);

        var endedAt = record.EndedAt.UtcDateTime;
        var active = await db.Cups
            .AsNoTracking()
            .Where(c => c.IsActive && c.ActivatedAt != null)
            .Select(c => new ActiveCupSnapshot(c.Id, c.Name, c.ActivatedAt!.Value))
            .FirstOrDefaultAsync(cancellationToken);

        var cup = CupAtEnd(active, notedCup, endedAt);

        // Linked only while the cup exists, under its name now. A cup deleted since the
        // race ended keeps the name it had: the race still happened under it.
        var current = cup == null
            ? null
            : await db.Cups.Where(c => c.Id == cup.Id).Select(c => c.Name).FirstOrDefaultAsync(cancellationToken);

        var trackId = string.IsNullOrEmpty(record.TrackId) ? fallbackTrackId ?? string.Empty : record.TrackId;

        var race = new Race
        {
            StartedAt = record.StartedAt?.UtcDateTime,
            EndedAt = record.EndedAt.UtcDateTime,
            TrackId = Truncate(trackId, Race.TrackIdMaxLength),
            Laps = record.Laps,
            GameMode = record.GameMode,
            EventCounter = record.EventCounter,
            CupId = current != null ? cup!.Id : null,
            CupName = current ?? cup?.Name ?? string.Empty,
            CupActivatedAt = cup?.ActivatedAt,
            Entries = record.Cars.Select(ToEntry).ToList(),
        };

        db.Races.Add(race);
        await db.SaveChangesAsync(cancellationToken);
        return race;
    }

    /// <summary>
    /// Which cup a race that ended at <paramref name="endedAt"/> belongs to.
    /// </summary>
    /// <remarks>
    /// The database decides when it can. One cup is active at a time, and activating a cup
    /// resets its <c>ActivatedAt</c>, so a cup active now that was activated before the race
    /// ended was the active cup when it ended - even if the caller's note was taken in the
    /// moment between that activation committing and the note catching up. The note
    /// decides only when the active cup was activated after the race ended, which is the
    /// case it exists for: an admin switched cups while the race waited to be saved.
    /// <para>
    /// Known gaps, left deliberately (PR #192): each needs an admin to change cups in the
    /// same moment a race ends, and closing them would mean sharing a lock or transaction
    /// between cup activation and race recording.
    /// </para>
    /// <list type="bullet">
    /// <item><c>ActivatedAt</c> is stamped just before the switch commits, so a race ending
    /// in between is credited to the new cup.</item>
    /// <item>Deleting the active cup as a race ends can leave the note naming the deleted
    /// cup, which is then credited; likewise if the startup load reads the cup just before
    /// it is deleted.</item>
    /// <item>A cup renamed, then a race, then the cup deleted before the save, keeps the
    /// old name.</item>
    /// </list>
    /// </remarks>
    internal static ActiveCupSnapshot? CupAtEnd(ActiveCupSnapshot? active, ActiveCupSnapshot? noted, DateTime endedAt)
    {
        if (active != null && active.ActivatedAt <= endedAt)
        {
            return active;
        }

        return noted != null && noted.ActivatedAt <= endedAt ? noted : null;
    }

    /// <summary>The latest races with their entries, newest first.</summary>
    public async Task<List<Race>> LatestAsync(int count, CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken);
        return await db.Races
            .AsNoTracking()
            .Include(r => r.Entries.OrderBy(e => e.Position == null).ThenBy(e => e.Position))
            .OrderByDescending(r => r.EndedAt)
            .ThenByDescending(r => r.Id)
            .Take(count)
            .ToListAsync(cancellationToken);
    }

    private static RaceEntry ToEntry(HookRaceCar car) => new()
    {
        Position = car.Position,
        Name = Truncate(car.Name, RaceEntry.NameMaxLength),
        IsBot = car.IsBot,
        // SQLite has no unsigned integer; HookRaceCar only passes IDs that fit a long.
        SteamId = car.SteamId is { } id ? (long)id : null,
        VehicleKey = Truncate(car.VehicleKey, RaceEntry.VehicleMaxLength),
        VehicleName = Truncate(car.VehicleName, RaceEntry.VehicleMaxLength),
        Outcome = car.Outcome,
        ClassIndex = car.ClassIndex,
        Rating = car.Rating,
        TimeMs = car.TimeMs,
        BestLapMs = car.BestLapMs,
        CupPointsTotal = car.CupPoints,
        Slot = car.Slot,
        Lap = car.Lap,
        CarFlags = car.CarFlags,
        FinishMs = car.FinishMs,
        PlayerStatus = car.PlayerStatus,
        PlayerFlags = car.PlayerFlags,
    };

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
