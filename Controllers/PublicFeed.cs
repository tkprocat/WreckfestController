using Microsoft.EntityFrameworkCore;
using WreckfestController.Data;
using WreckfestController.Data.Cups;
using WreckfestController.Data.Races;
using WreckfestController.Services.Cups;

namespace WreckfestController.Controllers;

/// <summary>
/// GET /api/public/feed: the front page's results, newest first. A race outside a cup is
/// one item; a cup run - one activation of a cup, all its races - is one item too, so a
/// cup stays on the page until that many newer items push it off.
/// </summary>
/// <remarks>
/// A run is told apart by <see cref="Race.CupActivatedAt"/> alone, not the cup's name or
/// id: a renamed or deleted cup keeps its run together, and only one cup is ever active.
/// </remarks>
public static class PublicFeed
{
    /// <summary>How many races a cup run lists, newest first. Its standings count all of them.</summary>
    public const int CupRaceLimit = 50;

    /// <summary>How many race headers one step of the scan reads.</summary>
    private const int ScanBatch = 200;

    /// <summary>One car's cup points total after one race, for <see cref="Standings"/>.</summary>
    public sealed record StandingRow(int RaceId, DateTime EndedAt, long? SteamId, string Name, bool IsBot, int? Position, int CupPointsTotal);

    public static async Task<List<PublicFeedItem>> BuildAsync(
        ControllerDbContext db,
        CupSummary? active,
        int count,
        Func<List<string>, CancellationToken, Task<Dictionary<string, string>>> trackNames,
        CancellationToken cancellationToken)
    {
        // The items in order: a loop race by its id, a cup run by its activation.
        var keys = new List<(int? RaceId, DateTime? Run)>();
        var runs = new HashSet<DateTime>();

        // The active cup leads even before its first race has finished: any race after
        // it began is the cup's.
        if (active?.ActivatedAt is { } activeRun)
        {
            keys.Add((null, activeRun));
            runs.Add(activeRun);
        }

        for (var skip = 0; keys.Count < count; skip += ScanBatch)
        {
            var batch = await db.Races
                .AsNoTracking()
                .OrderByDescending(r => r.EndedAt)
                .ThenByDescending(r => r.Id)
                .Skip(skip)
                .Take(ScanBatch)
                .Select(r => new { r.Id, r.CupActivatedAt })
                .ToListAsync(cancellationToken);

            foreach (var race in batch)
            {
                if (keys.Count >= count)
                {
                    break;
                }

                if (race.CupActivatedAt is not { } run)
                {
                    keys.Add((race.Id, null));
                }
                else if (runs.Add(run))
                {
                    keys.Add((null, run));
                }
            }

            if (batch.Count < ScanBatch)
            {
                break;
            }
        }

        var loopIds = keys.Where(k => k.RaceId != null).Select(k => k.RaceId!.Value).ToList();
        var races = await Races(db.Races.Where(r => loopIds.Contains(r.Id)))
            .ToDictionaryAsync(r => r.Id, cancellationToken);

        var cupRaces = new Dictionary<DateTime, (List<RaceRow> Races, int Count, List<StandingRow> Rows)>();
        foreach (var run in keys.Where(k => k.Run != null).Select(k => k.Run!.Value))
        {
            var ofRun = db.Races.Where(r => r.CupActivatedAt == run);
            var listed = await Races(ofRun.OrderByDescending(r => r.EndedAt).ThenByDescending(r => r.Id).Take(CupRaceLimit))
                .ToListAsync(cancellationToken);
            var total = await ofRun.CountAsync(cancellationToken);

            // Only the races once the cup started: the warmup's points are reset at the start.
            var rows = await db.RaceEntries
                .AsNoTracking()
                .Where(e => e.Race.CupActivatedAt == run && e.Race.CupPhase == CupPhase.Running)
                .Select(e => new StandingRow(e.RaceId, e.Race.EndedAt, e.SteamId, e.Name, e.IsBot, e.Position, e.CupPointsTotal))
                .ToListAsync(cancellationToken);
            cupRaces[run] = (listed, total, rows);
        }

        var names = await trackNames(
            races.Values.Concat(cupRaces.Values.SelectMany(c => c.Races)).Select(r => r.TrackId).Distinct().ToList(),
            cancellationToken);

        var items = new List<PublicFeedItem>();
        foreach (var key in keys)
        {
            if (key.RaceId is { } id)
            {
                if (races.TryGetValue(id, out var race))
                {
                    items.Add(new PublicFeedItem(PublicFeedItemKind.Race, ToPublic(race, names), null));
                }

                continue;
            }

            var run = key.Run!.Value;
            var (listed, raceCount, standingRows) = cupRaces[run];
            var isActive = active?.ActivatedAt == run;
            items.Add(new PublicFeedItem(
                PublicFeedItemKind.Cup,
                null,
                new PublicCupRun(
                    // The name the latest race recorded, or the active cup's own.
                    isActive ? active!.Name : listed.FirstOrDefault()?.CupName ?? string.Empty,
                    run,
                    !isActive ? PublicCupRunStatus.Ended
                        : active!.Phase is CupPhase.Warmup or CupPhase.Starting ? PublicCupRunStatus.Warmup
                        : PublicCupRunStatus.Running,
                    isActive ? active!.StartsAt : null,
                    isActive ? active!.EndsAt : null,
                    Standings(standingRows),
                    raceCount,
                    listed.Select(r => ToPublic(r, names)).ToList())));
        }

        return items;
    }

    /// <summary>
    /// The cup points table: each car's total after the last race it was in, as the game
    /// keeps it. Humans are told apart by Steam ID (they rename), bots by name. Empty when
    /// nobody has a point: a cup run without cup points has no table to show.
    /// </summary>
    /// <remarks>
    /// Equal totals share a place (1, 2, 2, 4), listed by who raced last, then by their
    /// place in that race, then by name.
    /// </remarks>
    public static List<PublicCupStanding> Standings(IEnumerable<StandingRow> rows)
    {
        var latest = rows
            .GroupBy(r => r.SteamId is { } steamId ? "steam:" + steamId : (r.IsBot ? "bot:" : "name:") + r.Name)
            .Select(g => g.OrderByDescending(r => r.EndedAt).ThenByDescending(r => r.RaceId).First())
            .ToList();
        if (latest.All(r => r.CupPointsTotal <= 0))
        {
            return [];
        }

        var ordered = latest
            .OrderByDescending(r => r.CupPointsTotal)
            .ThenByDescending(r => r.EndedAt)
            .ThenBy(r => r.Position ?? int.MaxValue)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var standings = new List<PublicCupStanding>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            var place = i > 0 && ordered[i].CupPointsTotal == ordered[i - 1].CupPointsTotal ? standings[i - 1].Position : i + 1;
            standings.Add(new PublicCupStanding(place, ordered[i].Name, ordered[i].IsBot, ordered[i].CupPointsTotal));
        }

        return standings;
    }

    /// <summary>A race as the public lists show it, from <paramref name="query"/>'s races.</summary>
    internal static IQueryable<RaceRow> Races(IQueryable<Race> query) => query
        .AsNoTracking()
        .Select(r => new RaceRow(
            r.Id,
            r.StartedAt,
            r.EndedAt,
            r.TrackId,
            r.Laps,
            r.CupName,
            r.CupPhase == CupPhase.Warmup || r.CupPhase == CupPhase.Starting,
            r.Entries
                .OrderBy(e => e.Position == null)
                .ThenBy(e => e.Position)
                .ThenBy(e => e.Id)
                .Select(e => new PublicRaceEntry(e.Position, e.Name, e.IsBot, e.VehicleName, e.Outcome, e.TimeMs, e.BestLapMs))
                .ToList()));

    internal static PublicRace ToPublic(RaceRow race, Dictionary<string, string> names) => new(
        race.Id,
        race.StartedAt,
        race.EndedAt,
        new PublicTrack(race.TrackId, names.TryGetValue(race.TrackId, out var name) ? name : race.TrackId),
        race.Laps,
        string.IsNullOrEmpty(race.CupName) ? null : race.CupName,
        race.Warmup,
        race.Entries);

    internal sealed record RaceRow(
        int Id,
        DateTime? StartedAt,
        DateTime EndedAt,
        string TrackId,
        int Laps,
        string CupName,
        bool Warmup,
        List<PublicRaceEntry> Entries);
}
