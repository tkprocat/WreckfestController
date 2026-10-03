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
    /// Saves <paramref name="record"/>, linked to <paramref name="cup"/>: the cup that was
    /// active when the race ended, which the caller notes at that moment rather than here,
    /// since the race may have waited in a queue while an admin switched cups.
    /// <paramref name="fallbackTrackId"/> is used only when the hook could not read the track.
    /// </summary>
    public async Task<Race> SaveAsync(
        HookRaceRecord record,
        ActiveCupSnapshot? cup = null,
        string? fallbackTrackId = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken);

        // A cup deleted since the race ended cannot be referenced, but the race still
        // happened under it, so it keeps the name and the evening.
        var cupExists = cup != null && await db.Cups.AnyAsync(c => c.Id == cup.Id, cancellationToken);

        var trackId = string.IsNullOrEmpty(record.TrackId) ? fallbackTrackId ?? string.Empty : record.TrackId;

        var race = new Race
        {
            StartedAt = record.StartedAt?.UtcDateTime,
            EndedAt = record.EndedAt.UtcDateTime,
            TrackId = Truncate(trackId, Race.TrackIdMaxLength),
            Laps = record.Laps,
            GameMode = record.GameMode,
            EventCounter = record.EventCounter,
            CupId = cupExists ? cup!.Id : null,
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
