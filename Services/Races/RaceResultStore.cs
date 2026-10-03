using Microsoft.EntityFrameworkCore;
using WreckfestController.Data;
using WreckfestController.Data.Races;
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
    /// Saves <paramref name="record"/>, linked to whichever cup is active now.
    /// <paramref name="fallbackTrackId"/> is used only when the hook could not read the track.
    /// </summary>
    public async Task<Race> SaveAsync(
        HookRaceRecord record,
        string? fallbackTrackId = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken);

        // The cup active now, but only if it was already active when the race ended: a
        // race that waited in the recorder's queue while an admin switched cups must not
        // be credited to the new one. Better no link than a wrong one.
        var endedAt = record.EndedAt.UtcDateTime;
        var cup = await db.Cups
            .AsNoTracking()
            .Where(c => c.IsActive && c.ActivatedAt != null && c.ActivatedAt <= endedAt)
            .Select(c => new { c.Id, c.Name, c.ActivatedAt })
            .FirstOrDefaultAsync(cancellationToken);

        var trackId = string.IsNullOrEmpty(record.TrackId) ? fallbackTrackId ?? string.Empty : record.TrackId;

        var race = new Race
        {
            StartedAt = record.StartedAt?.UtcDateTime,
            EndedAt = record.EndedAt.UtcDateTime,
            TrackId = Truncate(trackId, Race.TrackIdMaxLength),
            Laps = record.Laps,
            GameMode = record.GameMode,
            EventCounter = record.EventCounter,
            CupId = cup?.Id,
            CupName = cup?.Name ?? string.Empty,
            CupActivatedAt = cup?.ActivatedAt,
            Entries = record.Cars.Select(ToEntry).ToList(),
        };

        db.Races.Add(race);
        await db.SaveChangesAsync(cancellationToken);
        return race;
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
        // SQLite has no unsigned integer. Real Steam IDs fit in a long anyway; the
        // unchecked cast keeps every bit, so any ID the hook sends keeps its identity.
        SteamId = car.SteamId is { } id ? unchecked((long)id) : null,
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
