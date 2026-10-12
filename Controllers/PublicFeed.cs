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

        // Newest first, a batch at a time, each starting after the last one read (no Skip,
        // which would walk every earlier row again). A run already found is left out of
        // later batches, so a cup with thousands of races costs one batch, not hundreds.
        (DateTime EndedAt, int Id)? after = null;
        while (keys.Count < count)
        {
            var known = runs.ToList();
            var query = db.Races
                .AsNoTracking()
                .Where(r => r.CupActivatedAt == null || !known.Contains(r.CupActivatedAt.Value));
            if (after is { } last)
            {
                query = query.Where(r => r.EndedAt < last.EndedAt || (r.EndedAt == last.EndedAt && r.Id < last.Id));
            }

            var batch = await query
                .OrderByDescending(r => r.EndedAt)
                .ThenByDescending(r => r.Id)
                .Take(ScanBatch)
                .Select(r => new { r.Id, r.EndedAt, r.CupActivatedAt })
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

            after = (batch[^1].EndedAt, batch[^1].Id);
        }

        var loopIds = keys.Where(k => k.RaceId != null).Select(k => k.RaceId!.Value).ToList();
        var races = await Races(db.Races.Where(r => loopIds.Contains(r.Id)))
            .ToDictionaryAsync(r => r.Id, cancellationToken);

        var cupRaces = new Dictionary<DateTime, (List<RaceRow> Races, int Count, List<StandingRow> Rows, List<(long SteamId, string Name)> NamesUsed)>();
        foreach (var run in keys.Where(k => k.Run != null).Select(k => k.Run!.Value))
        {
            var ofRun = db.Races.Where(r => r.CupActivatedAt == run);
            var listed = await Races(ofRun.OrderByDescending(r => r.EndedAt).ThenByDescending(r => r.Id).Take(CupRaceLimit))
                .ToListAsync(cancellationToken);
            var total = await ofRun.CountAsync(cancellationToken);

            // Every name each Steam ID raced under, so a row without an ID can be matched
            // to a player who has since renamed. Distinct pairs: a few per player.
            var namesUsed = await db.RaceEntries
                .AsNoTracking()
                .Where(e => e.Race.CupActivatedAt == run && e.Race.CupPhase == CupPhase.Running && e.SteamId != null && !e.IsBot)
                .Select(e => new { SteamId = e.SteamId!.Value, e.Name })
                .Distinct()
                .ToListAsync(cancellationToken);
            cupRaces[run] = (listed, total, await LatestRowsAsync(db, run, cancellationToken), namesUsed.Select(n => (n.SteamId, n.Name)).ToList());
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
            var (listed, raceCount, standingRows, namesUsed) = cupRaces[run];
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
                    Standings(standingRows, namesUsed),
                    raceCount,
                    listed.Select(r => ToPublic(r, names)).ToList())));
        }

        return items;
    }

    /// <summary>
    /// Each car's row from the last race it was in during <paramref name="run"/>, once the
    /// cup started (the warmup's points are reset at the start): a few dozen rows, however
    /// long the run. Cars are told apart as <see cref="Standings"/> does.
    /// </summary>
    private static async Task<List<StandingRow>> LatestRowsAsync(ControllerDbContext db, DateTime run, CancellationToken cancellationToken)
    {
        // Stored as the column's converter writes it: UTC, and the phase by name.
        var activatedAt = DateTime.SpecifyKind(run, DateTimeKind.Utc);
        var running = nameof(CupPhase.Running);
        var rows = await db.Database
            .SqlQuery<LatestRow>($"""
                SELECT "RaceId", "EndedAt", "SteamId", "Name", "IsBot", "Position", "CupPointsTotal"
                FROM (
                    SELECT e."RaceId", r."EndedAt", e."SteamId", e."Name", e."IsBot", e."Position", e."CupPointsTotal",
                        ROW_NUMBER() OVER (
                            PARTITION BY CASE
                                WHEN e."SteamId" IS NOT NULL THEN 's' || e."SteamId"
                                WHEN e."IsBot" THEN 'b' || e."Name"
                                ELSE 'n' || e."Name"
                            END
                            ORDER BY r."EndedAt" DESC, r."Id" DESC) AS "Latest"
                    FROM "RaceEntries" AS e
                    JOIN "Races" AS r ON r."Id" = e."RaceId"
                    WHERE r."CupActivatedAt" = {activatedAt} AND r."CupPhase" = {running}
                )
                WHERE "Latest" = 1
                """)
            .ToListAsync(cancellationToken);
        return rows.Select(r => new StandingRow(r.RaceId, r.EndedAt, r.SteamId, r.Name, r.IsBot, r.Position, r.CupPointsTotal)).ToList();
    }

    /// <summary>A row of <see cref="LatestRowsAsync"/>'s query.</summary>
    private sealed class LatestRow
    {
        public int RaceId { get; set; }

        public DateTime EndedAt { get; set; }

        public long? SteamId { get; set; }

        public string Name { get; set; } = string.Empty;

        public bool IsBot { get; set; }

        public int? Position { get; set; }

        public int CupPointsTotal { get; set; }
    }

    /// <summary>
    /// The cup points table: each car's total after the last race it was in, as the game
    /// keeps it. Humans are told apart by Steam ID (they rename), bots by name. A human
    /// recorded without a Steam ID joins the one Steam ID that raced under that name, in
    /// <paramref name="rows"/> or <paramref name="namesUsed"/>; with none, or several, they
    /// stay a row of their own. Empty when nobody has a point:
    /// a cup run without cup points has no table to show.
    /// </summary>
    /// <remarks>
    /// Equal totals share a place (1, 2, 2, 4), listed by who raced last, then by their
    /// place in that race, then by name.
    /// </remarks>
    public static List<PublicCupStanding> Standings(
        IEnumerable<StandingRow> rows,
        IEnumerable<(long SteamId, string Name)>? namesUsed = null)
    {
        var all = rows.ToList();
        var used = all
            .Where(r => r.SteamId is not null && !r.IsBot)
            .Select(r => (SteamId: r.SteamId!.Value, r.Name))
            .Concat(namesUsed ?? [])
            .ToList();
        var latest = all
            .GroupBy(r => r.SteamId is { } steamId ? "steam:" + steamId : (r.IsBot ? "bot:" : "name:") + r.Name)
            .Select(g => g.OrderByDescending(r => r.EndedAt).ThenByDescending(r => r.RaceId).First())
            .ToList();

        foreach (var nameless in latest.Where(r => r.SteamId is null && !r.IsBot).ToList())
        {
            var ids = used.Where(u => u.Name == nameless.Name).Select(u => u.SteamId).Distinct().ToList();
            if (ids.Count == 1 && latest.FirstOrDefault(r => r.SteamId == ids[0]) is { } player)
            {
                var older = IsNewer(nameless, player) ? player : nameless;
                latest.Remove(older);
            }
        }

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

    private static bool IsNewer(StandingRow a, StandingRow b) =>
        a.EndedAt > b.EndedAt || (a.EndedAt == b.EndedAt && a.RaceId > b.RaceId);

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
