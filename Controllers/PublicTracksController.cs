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

    private const string NoCase = "NOCASE";

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

        // The race log keeps the id as the game sent it; it is matched ignoring case, through
        // the NOCASE index on Races.TrackId (migration RaceTrackIdNoCase). Everything below is
        // counted in the database: this is anonymous, and a layout can have years of races.
        var races = _db.Races.AsNoTracking().Where(r => EF.Functions.Collate(r.TrackId, NoCase) == variant.VariantId);
        var entries = _db.RaceEntries.AsNoTracking().Where(e => EF.Functions.Collate(e.Race.TrackId, NoCase) == variant.VariantId);

        var recent = await PublicFeed.Races(races.OrderByDescending(r => r.EndedAt).ThenByDescending(r => r.Id).Take(RaceLimit))
            .ToListAsync(cancellationToken);
        var name = new CatalogueTrackName($"{variant.TrackName} - {variant.Name}", variant.TrackKey);
        var names = recent.Select(r => r.TrackId)
            .Append(variant.VariantId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(id => id, _ => name, StringComparer.OrdinalIgnoreCase);

        // Records are players' only: a bot's lap is no record. One row per name a player raced
        // under, then joined into players (PlayerRecords.Join).
        var rows = await entries
            .Where(e => !e.IsBot)
            .Select(e => new { e.SteamId, e.Name, e.BestLapMs, e.Position, e.Race.EndedAt })
            .GroupBy(e => new { e.SteamId, e.Name })
            .Select(g => new PlayerRecords.Row(
                g.Key.SteamId,
                g.Key.Name,
                g.Min(e => e.BestLapMs > 0 ? e.BestLapMs : null),
                g.Count(e => e.Position == 1),
                g.Count(),
                g.Max(e => e.EndedAt)))
            .ToListAsync(cancellationToken);
        var players = PlayerRecords.Join(rows);

        var fastestLaps = new List<PublicLapRecord>();
        foreach (var player in players.Where(p => p.BestLapMs is not null).OrderBy(p => p.BestLapMs).ThenBy(p => p.Name, StringComparer.Ordinal).Take(RecordLimit))
        {
            // The race of that lap, for its car and date: the earliest, when it was matched.
            var lap = player.BestLapMs!.Value;
            var steamId = player.SteamId;
            var nameOnly = player.NameOnlyNames;
            var best = await entries
                .Where(e => !e.IsBot && e.BestLapMs == lap
                    && ((steamId != null && e.SteamId == steamId) || (e.SteamId == null && nameOnly.Contains(e.Name))))
                .OrderBy(e => e.Race.EndedAt)
                .Select(e => new { e.VehicleName, e.Race.EndedAt })
                .FirstOrDefaultAsync(cancellationToken);
            fastestLaps.Add(new PublicLapRecord(player.Name, best?.VehicleName ?? string.Empty, lap, best?.EndedAt ?? player.LastRaced));
        }

        var mostWins = players
            .Where(p => p.Wins > 0)
            .OrderByDescending(p => p.Wins)
            .ThenBy(p => p.Races)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Take(RecordLimit)
            .Select(p => new PublicWinRecord(p.Name, p.Wins, p.Races))
            .ToList();

        // A race's time over its laps, for everyone who crossed the line: a pace that
        // includes the standing start and traffic, as a race does. The median, read from
        // the middle of the sorted list rather than the whole of it.
        var paces = entries
            .Where(e => e.Outcome == RaceOutcome.Finished && e.TimeMs > 0 && e.Race.Laps > 0)
            .Select(e => (double)e.TimeMs!.Value / e.Race.Laps);
        var finishes = await paces.CountAsync(cancellationToken);
        int? typicalLapMs = null;
        if (finishes > 0)
        {
            var middle = await paces.OrderBy(p => p).Skip((finishes - 1) / 2).Take(2 - (finishes % 2)).ToListAsync(cancellationToken);
            typicalLapMs = (int)Math.Round(middle.Average());
        }

        return new PublicVariantDetail(
            variant.VariantId,
            await races.CountAsync(cancellationToken),
            typicalLapMs,
            finishes,
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

    /// <summary>Races logged on each of these variant ids, matched ignoring case, by the catalogue's spelling.</summary>
    private async Task<Dictionary<string, int>> RaceCountsAsync(List<string> variantIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in variantIds)
        {
            result[id] = await _db.Races.AsNoTracking().CountAsync(r => EF.Functions.Collate(r.TrackId, NoCase) == id, cancellationToken);
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
                ? c.Linked.Any(e => e.TrackVariantId is { } linked
                    ? linked == variantRowId
                    : string.Equals(e.TrackId, variantId, StringComparison.OrdinalIgnoreCase))
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

            // A track with no picture of its own shows its first visible variant's, in the
            // order the image endpoint uses.
            return (CatalogueImageStore.Pick(slots, size, trackId, variantId: null)
                ?? CatalogueImageStore.FallbackOrder(variants.Select(v => (v.Id, v.Name)))
                    .Select(id => CatalogueImageStore.Pick(slots, size, trackId: null, id))
                    .FirstOrDefault(s => s is not null))?.Hash;
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
