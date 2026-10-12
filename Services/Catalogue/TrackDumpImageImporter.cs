using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using WreckfestController.Data;
using WreckfestController.Data.Catalogue;

namespace WreckfestController.Services.Catalogue;

/// <summary>What one batch of a track dump image import did.</summary>
/// <param name="Stored">Images added or replaced.</param>
/// <param name="Unchanged">Images the catalogue already had, byte for byte.</param>
/// <param name="Removed">Imported images dropped because the dump has none of that size now.</param>
/// <param name="NotUsed">Files sent that no matched track or variant of the dump names.</param>
/// <param name="Refused">Files that were not a PNG, or over <see cref="CatalogueImageStore.MaxBytes"/>.</param>
/// <param name="Unmatched">
/// Dump tracks and variants with images but no catalogue row: import the tracks.json first.
/// The same in every batch of one dump, since it depends only on the dump and the catalogue.
/// </param>
public sealed record TrackDumpImageReport(int Stored, int Unchanged, int Removed, int NotUsed, int Refused, int Unmatched);

/// <summary>
/// Stores the menu images a WreckfestTrackDump <c>wftracks imagedump</c> wrote, for the
/// tracks and variants of the <c>tracks.json</c> they came with (#260). The images are sent
/// in batches; each batch carries the dump, and is complete on its own.
/// <list type="bullet">
/// <item><b>Files</b> are named as imagedump writes them, <c>&lt;source id, ":" as "_"&gt;/&lt;name&gt;.png</c>,
/// and are found through the dump's image paths. The name is only ever looked up, never used as a path.</item>
/// <item><b>Variants</b> match by <c>gameId</c>, as the tracks.json import does; the base game
/// first, so a mod reusing a base-game id does not take its images.</item>
/// <item><b>Tracks</b> match by <see cref="Track.DumpId"/>, else the track holding most of the
/// dump track's variants. That is how a built-in track, which has no dump id, finds its
/// images in a dump of the game client.</item>
/// <item>Only an <see cref="ImageOrigin.Import"/> image is replaced, or removed where the dump
/// now has no image of that size. An admin's upload is never touched.</item>
/// </list>
/// </summary>
public static class TrackDumpImageImporter
{
    /// <summary>The image path shape the dump format allows: one file under data/menu/textures.</summary>
    private static readonly Regex ImagePath = new(
        @"^data/menu/textures/(?<name>[^/\\:]+)\.bmap$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex SourceId = new(
        @"^(base|workshop:[0-9]{1,20}|mod:[^/\\:]{1,128})$",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// The file name imagedump gives this image, such as <c>workshop_123/env_x_small_250x116_raw.png</c>,
    /// or null for a source or path outside the dump format.
    /// </summary>
    public static string? FileKey(string? sourceId, string? imagePath)
    {
        if (sourceId is null || imagePath is null || !SourceId.IsMatch(sourceId))
        {
            return null;
        }

        var match = ImagePath.Match(imagePath);
        return match.Success ? $"{sourceId.Replace(':', '_')}/{match.Groups["name"].Value}.png" : null;
    }

    /// <summary>
    /// The key an uploaded file is looked up by: its last two path segments, as imagedump
    /// laid them out. A browser may send the chosen folder's own name in front.
    /// </summary>
    public static string? UploadKey(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return null;
        }

        var parts = fileName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? $"{parts[^2]}/{parts[^1]}" : null;
    }

    /// <param name="files">The batch's files by <see cref="UploadKey"/>, ignoring case.</param>
    public static async Task<TrackDumpImageReport> ImportAsync(
        ControllerDbContext db,
        TrackDump dump,
        IReadOnlyDictionary<string, byte[]> files,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        var tracks = await db.Tracks.AsNoTracking().Select(t => new { t.Id, t.DumpId }).ToListAsync(cancellationToken);
        var variants = await db.TrackVariants.AsNoTracking().Select(v => new { v.Id, v.VariantId, v.TrackId }).ToListAsync(cancellationToken);
        var tracksByDumpId = tracks
            .Where(t => t.DumpId is not null)
            .GroupBy(t => t.DumpId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Min(t => t.Id), StringComparer.OrdinalIgnoreCase);
        var variantsById = variants.ToDictionary(v => v.VariantId, StringComparer.OrdinalIgnoreCase);

        // Every image the dump places, by the row it belongs to. A row is placed once: the
        // base game first, then the order of the file.
        var targets = new List<(ImageOwner Owner, string Source, TrackDumpImages? Images)>();
        var claimed = new HashSet<ImageOwner>();
        var unmatched = 0;
        foreach (var track in dump.Tracks!.Where(t => t?.Source is not null).OrderBy(t => t.Source == "base" ? 0 : 1))
        {
            var source = track.Source!;
            var matchedVariants = new List<int>();
            foreach (var variant in track.Variants ?? [])
            {
                if (variant?.GameId?.Trim() is not { } gameId || !variantsById.TryGetValue(gameId, out var row))
                {
                    unmatched += HasImages(variant?.Images) ? 1 : 0;
                    continue;
                }

                matchedVariants.Add(row.TrackId);
                if (claimed.Add(ImageOwner.Variant(row.Id)))
                {
                    targets.Add((ImageOwner.Variant(row.Id), source, variant.Images));
                }
            }

            int? trackId = track.Id is not null && tracksByDumpId.TryGetValue(track.Id, out var byDumpId)
                ? byDumpId
                : matchedVariants
                    .GroupBy(id => id)
                    .OrderByDescending(g => g.Count())
                    .ThenBy(g => g.Key)
                    .Select(g => (int?)g.Key)
                    .FirstOrDefault();
            if (trackId is null)
            {
                unmatched += HasImages(track.Images) ? 1 : 0;
            }
            else if (claimed.Add(ImageOwner.Track(trackId.Value)))
            {
                targets.Add((ImageOwner.Track(trackId.Value), source, track.Images));
            }
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        int stored = 0, unchanged = 0, removed = 0;
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Whether each file is a PNG within the size limit, checked once however many rows use it.
        var usable = new Usable();
        foreach (var (owner, source, images) in targets)
        {
            foreach (var (size, path) in new[] { (ImageSize.Small, images?.Small), (ImageSize.Large, images?.Large) })
            {
                if (path is null)
                {
                    if (await CatalogueImageStore.RemoveAsync(db, owner, size, ImageOrigin.Import, cancellationToken))
                    {
                        removed++;
                    }

                    continue;
                }

                if (FileKey(source, path) is not { } key || !files.TryGetValue(key, out var data))
                {
                    // Not in this batch: another batch may carry it.
                    continue;
                }

                used.Add(key);
                if (!usable.GetOrAdd(key, data))
                {
                    continue;
                }

                if (await CatalogueImageStore.PutAsync(db, owner, size, ImageOrigin.Import, data, CatalogueImageStore.Png, now, cancellationToken))
                {
                    stored++;
                }
                else
                {
                    unchanged++;
                }
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var notUsed = files.Keys.Count(k => !used.Contains(k));
        return new TrackDumpImageReport(stored, unchanged, removed, notUsed, usable.Refused, unmatched);
    }

    private sealed class Usable
    {
        private readonly Dictionary<string, bool> _checked = new(StringComparer.OrdinalIgnoreCase);

        public int Refused => _checked.Values.Count(ok => !ok);

        public bool GetOrAdd(string key, byte[] data)
        {
            if (!_checked.TryGetValue(key, out var ok))
            {
                _checked[key] = ok = data.Length <= CatalogueImageStore.MaxBytes && CatalogueImageStore.Sniff(data) == CatalogueImageStore.Png;
            }

            return ok;
        }
    }

    private static bool HasImages(TrackDumpImages? images) => images?.Small is not null || images?.Large is not null;
}
