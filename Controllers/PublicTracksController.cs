using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using WreckfestController.Data;
using WreckfestController.Data.Catalogue;
using WreckfestController.Services.Auth;
using WreckfestController.Services.Catalogue;
using WreckfestController.Services.Cups;
using WreckfestController.Services.Hook;

namespace WreckfestController.Controllers;

/// <summary>
/// The public track pages (#261): every track and variant that is not hidden, and what the
/// race log says about each variant. Tracks and variants are named by <c>key</c> and
/// <c>variantId</c>, never database ids, so links survive a re-import.
/// </summary>
/// <remarks>
/// Built field by field into public records, as <see cref="PublicController"/> is: no
/// hidden rows, no admin fields (voting, tags, built-in), no Steam ids. A player in the
/// records is named as the results screen showed them.
/// </remarks>
[ApiController]
[AllowAnonymous]
[EnableRateLimiting(RateLimits.PublicPolicy)]
[ProducesResponseType(StatusCodes.Status200OK)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests, "application/problem+json")]
[Route("api/public")]
public class PublicTracksController : ControllerBase
{
    /// <summary>How many races a variant page lists, newest first.</summary>
    public const int RaceLimit = 10;

    /// <summary>How many players each of a variant's records lists.</summary>
    public const int RecordLimit = 5;

    private readonly ControllerDbContext _db;

    public PublicTracksController(ControllerDbContext db)
    {
        _db = db;
    }

    /// <summary>Every track that is not hidden and has a variant that is not, by name.</summary>
    [HttpGet("tracks")]
    public async Task<ActionResult<List<PublicTrackSummary>>> Tracks(CancellationToken cancellationToken)
    {
        var tracks = await VisibleTracks().OrderBy(t => t.Name).ToListAsync(cancellationToken);
        var slots = await CatalogueImageStore.SlotsAsync(_db, cancellationToken);
        return tracks
            .Where(t => t.Variants.Count > 0)
            .Select(t => new PublicTrackSummary(
                t.Key,
                t.Name,
                t.Origin,
                t.Mod?.Name,
                Images(slots, t.Id, variantId: null, t.Variants),
                t.Variants.OrderBy(v => v.Name).Select(v => Variant(v, slots, t.Variants)).ToList()))
            .ToList();
    }

    /// <summary>One track and its variants. 404 for a hidden or unknown track, or one with no visible variant.</summary>
    [HttpGet("tracks/{key}")]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PublicTrackDetail>> Track(string key, CancellationToken cancellationToken)
    {
        var track = await VisibleTracks().SingleOrDefaultAsync(t => t.Key == key, cancellationToken);
        if (track is null || track.Variants.Count == 0)
        {
            return NotFound();
        }

        var slots = await CatalogueImageStore.SlotsAsync(_db, cancellationToken);
        var raceCounts = await RaceCountsAsync(track.Variants.Select(v => v.VariantId).ToList(), cancellationToken);
        return new PublicTrackDetail(
            track.Key,
            track.Name,
            track.Origin,
            track.Mod?.Name,
            track.Mod?.WorkshopId,
            Images(slots, track.Id, variantId: null, track.Variants),
            track.Variants
                .OrderBy(v => v.Name)
                .Select(v => Variant(v, slots, track.Variants) with { RaceCount = raceCounts.GetValueOrDefault(v.VariantId) })
                .ToList());
    }

    /// <summary>
    /// What the race log says about one variant: its latest races, its records, a typical
    /// lap, and the cups that run it. 404 for a hidden or unknown variant, or one on a hidden track.
    /// </summary>
    [HttpGet("variants/{variantId}")]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PublicVariantDetail>> Variant(string variantId, CancellationToken cancellationToken)
    {
        var variant = await _db.TrackVariants
            .AsNoTracking()
            .Where(v => v.VariantId == variantId && !v.IsHidden && !v.Track.IsHidden)
            .Select(v => new { v.Id, v.VariantId, v.Name, TrackName = v.Track.Name, TrackKey = v.Track.Key })
            .SingleOrDefaultAsync(cancellationToken);
        if (variant is null)
        {
            return NotFound();
        }

        // The race log keeps the id as the game sent it; the catalogue matches ignoring case.
        var ids = await RaceTrackIdsAsync([variant.VariantId], cancellationToken);
        var races = _db.Races.Where(r => ids.Contains(r.TrackId));

        var recent = await PublicFeed.Races(races.OrderByDescending(r => r.EndedAt).ThenByDescending(r => r.Id).Take(RaceLimit))
            .ToListAsync(cancellationToken);
        var name = new CatalogueTrackName($"{variant.TrackName} - {variant.Name}", variant.TrackKey);
        var names = ids.Append(variant.VariantId).Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(id => id, _ => name, StringComparer.OrdinalIgnoreCase);

        var entries = await _db.RaceEntries
            .AsNoTracking()
            .Where(e => ids.Contains(e.Race.TrackId))
            .Select(e => new
            {
                e.SteamId,
                e.Name,
                e.IsBot,
                e.Position,
                e.Outcome,
                e.BestLapMs,
                e.TimeMs,
                e.VehicleName,
                e.Race.Laps,
                e.Race.EndedAt,
            })
            .ToListAsync(cancellationToken);

        // Records are players' only: a bot's lap is no record. A player is counted by Steam
        // id when the log has one, else by name, and shown by the name they last raced under.
        var players = entries
            .Where(e => !e.IsBot)
            .GroupBy(e => e.SteamId is { } steamId ? $"s{steamId}" : $"n{e.Name}")
            .Select(g => new { Entries = g.ToList(), Name = g.OrderByDescending(e => e.EndedAt).First().Name })
            .ToList();

        var fastestLaps = players
            .Select(p => (p.Name, Best: p.Entries.Where(e => e.BestLapMs is > 0).OrderBy(e => e.BestLapMs).ThenBy(e => e.EndedAt).FirstOrDefault()))
            .Where(p => p.Best is not null)
            .OrderBy(p => p.Best!.BestLapMs)
            .ThenBy(p => p.Best!.EndedAt)
            .Take(RecordLimit)
            .Select(p => new PublicLapRecord(p.Name, p.Best!.VehicleName, p.Best.BestLapMs!.Value, p.Best.EndedAt))
            .ToList();

        var mostWins = players
            .Select(p => (p.Name, Wins: p.Entries.Count(e => e.Position == 1), Races: p.Entries.Count))
            .Where(p => p.Wins > 0)
            .OrderByDescending(p => p.Wins)
            .ThenBy(p => p.Races)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Take(RecordLimit)
            .Select(p => new PublicWinRecord(p.Name, p.Wins, p.Races))
            .ToList();

        // A race's time over its laps, for everyone who crossed the line: a pace that
        // includes the standing start and traffic, as a race does.
        var laps = entries
            .Where(e => e.Outcome == RaceOutcome.Finished && e.TimeMs is > 0 && e.Laps > 0)
            .Select(e => (double)e.TimeMs!.Value / e.Laps)
            .Order()
            .ToList();
        int? typicalLapMs = laps.Count == 0
            ? null
            : (int)Math.Round(laps.Count % 2 == 1 ? laps[laps.Count / 2] : (laps[(laps.Count / 2) - 1] + laps[laps.Count / 2]) / 2);

        return new PublicVariantDetail(
            variant.VariantId,
            await races.CountAsync(cancellationToken),
            typicalLapMs,
            laps.Count,
            recent.Select(r => PublicFeed.ToPublic(r, names)).ToList(),
            fastestLaps,
            mostWins,
            await CupsRunningAsync(variant.Id, variant.VariantId, cancellationToken));
    }

    /// <summary>Tracks that are not hidden, with their mod and the variants that are not hidden.</summary>
    private IQueryable<Track> VisibleTracks() => _db.Tracks
        .AsNoTracking()
        .AsSplitQuery()
        .Include(t => t.Mod)
        .Include(t => t.Variants.Where(v => !v.IsHidden))
        .Where(t => !t.IsHidden);

    /// <summary>The race log's spellings of these variant ids, ignoring case: few, and each matched by its index.</summary>
    private async Task<List<string>> RaceTrackIdsAsync(IReadOnlyCollection<string> variantIds, CancellationToken cancellationToken)
    {
        var wanted = new HashSet<string>(variantIds, StringComparer.OrdinalIgnoreCase);
        var logged = await _db.Races.AsNoTracking().Select(r => r.TrackId).Distinct().ToListAsync(cancellationToken);
        return logged.Where(wanted.Contains).ToList();
    }

    private async Task<Dictionary<string, int>> RaceCountsAsync(List<string> variantIds, CancellationToken cancellationToken)
    {
        var ids = await RaceTrackIdsAsync(variantIds, cancellationToken);
        var counts = await _db.Races
            .AsNoTracking()
            .Where(r => ids.Contains(r.TrackId))
            .GroupBy(r => r.TrackId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var count in counts)
        {
            result[count.Key] = result.GetValueOrDefault(count.Key) + count.Count;
        }

        return result;
    }

    /// <summary>
    /// Cups whose rotation has this variant: a linked collection's entries, else the cup's
    /// own track list. Upcoming ones first. Only a cup's name and schedule are shown.
    /// </summary>
    private async Task<List<PublicTrackCup>> CupsRunningAsync(int variantRowId, string variantId, CancellationToken cancellationToken)
    {
        var cups = await _db.Cups
            .AsNoTracking()
            .Select(c => new
            {
                c.Name,
                c.NextOccurrence,
                c.Repeat,
                c.IsActive,
                c.CollectionId,
                Linked = c.Collection == null
                    ? null
                    : c.Collection.Entries.Select(e => new { e.TrackVariantId, e.TrackId }).ToList(),
                c.Tracks,
            })
            .ToListAsync(cancellationToken);

        return cups
            .Where(c => c.Linked is not null
                ? c.Linked.Any(e => e.TrackVariantId == variantRowId || string.Equals(e.TrackId, variantId, StringComparison.OrdinalIgnoreCase))
                : c.Tracks.Any(t => string.Equals(t.Track, variantId, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(c => c.IsActive)
            .ThenBy(c => c.NextOccurrence is null)
            .ThenBy(c => c.NextOccurrence)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => new PublicTrackCup(c.Name, c.IsActive, c.NextOccurrence, c.Repeat is null ? null : CupRecurrence.Describe(c.Repeat)))
            .ToList();
    }

    private static PublicVariantSummary Variant(TrackVariant variant, List<ImageSlot> slots, List<TrackVariant> siblings) => new(
        variant.VariantId,
        variant.Name,
        variant.GameMode,
        variant.TrackLengthM,
        Images(slots, variant.TrackId, variant.Id, siblings),
        RaceCount: 0);

    /// <summary>
    /// The hash of the image each link would get, or null where it would 404, by the same
    /// rule the image endpoints use. A page links with <c>?v=</c> so the image is cached for good.
    /// </summary>
    private static PublicImages Images(List<ImageSlot> slots, int trackId, int? variantId, List<TrackVariant> variants)
    {
        string? Picture(ImageSize size)
        {
            if (variantId is not null)
            {
                return CatalogueImageStore.Pick(slots, size, trackId, variantId)?.Hash;
            }

            // A track with no picture of its own shows its first visible variant's, by name.
            return (CatalogueImageStore.Pick(slots, size, trackId, variantId: null)
                ?? variants.OrderBy(v => v.Name).Select(v => CatalogueImageStore.Pick(slots, size, trackId: null, v.Id)).FirstOrDefault(s => s is not null))?.Hash;
        }

        var layout = variantId is null
            ? null
            : slots
                .Where(s => s.IsOverlay && s.TrackVariantId == variantId)
                .OrderBy(s => s.Origin == ImageOrigin.Upload ? 0 : 1)
                .ThenBy(s => s.Size == ImageSize.Small ? 0 : 1)
                .FirstOrDefault()?.Hash;
        return new PublicImages(Picture(ImageSize.Small), Picture(ImageSize.Large), layout);
    }
}
