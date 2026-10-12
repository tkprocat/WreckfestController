using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using WreckfestController.Data;
using WreckfestController.Data.Catalogue;
using WreckfestController.Data.Collections;

namespace WreckfestController.Services.Catalogue;

/// <summary>How many rows of one kind an import added, changed, left as they were, or could not use.</summary>
public sealed record ImportCounts(int Added, int Updated, int Unchanged, int Skipped);

/// <summary>Something in the dump the import did not use. <c>Id</c> is the dump's own id for it.</summary>
public sealed record ImportSkip(string Id, string Reason);

/// <summary>The dump's own diagnostics of one code: how many, the worst severity, and the first message.</summary>
public sealed record ImportDiagnostic(string Code, string Severity, int Count, string Example);

/// <summary>What a track dump import did. The counts are complete; <c>Skipped</c> lists the first <see cref="TrackDumpImporter.SkipListLimit"/>.</summary>
public sealed record TrackDumpImportReport(
    string? Tool,
    string? GeneratedAt,
    ImportCounts Mods,
    ImportCounts Tracks,
    ImportCounts Variants,
    IReadOnlyList<ImportSkip> Skipped,
    IReadOnlyList<ImportDiagnostic> Diagnostics);

/// <summary>
/// Adds what a WreckfestTrackDump <c>tracks.json</c> knows and the catalogue does not
/// (#121), by the same rule as the catalogue's own data migrations (#79): <b>insert what is
/// missing, never change what is there</b>. The catalogue is the admins'; the dump only
/// fills it in.
/// <list type="bullet">
/// <item><b>Variants</b> are matched by <c>gameId</c> against <see cref="TrackVariant.VariantId"/>,
/// ignoring case. A match is left exactly as it is (name, mode, voting, tags, hidden, its
/// track), built-in or not. The one exception is <see cref="TrackVariant.TrackLengthM"/>:
/// the game's value, which nothing else sets, so the import fills or refreshes it when the
/// dump has one. A new variant is added not built-in and not votable, for an admin to opt in.</item>
/// <item><b>Tracks</b> are never changed. A new variant goes under the track that already
/// holds most of its siblings from the same dump track (so a new layout of a built-in
/// joins it), else a track with the dump folder's key from the same mod, else a new
/// track: not built-in, every weather, origin BaseGame for the base game (a DLC pack is not
/// in the dump) or Workshop for a mod. A track is created only for a new variant.</item>
/// <item><b>Mods</b> are matched by folder name. A match keeps its name; only a missing
/// workshop id is filled in. A new mod is added for each mod source with tracks.</item>
/// </list>
/// Re-importing the same file changes nothing.
/// </summary>
public static class TrackDumpImporter
{
    public const int SkipListLimit = 100;

    private const int DiagnosticExampleMaxLength = 300;

    private static readonly Regex GameIdPattern = new(TrackVariant.VariantIdPattern, RegexOptions.CultureInvariant);
    private static readonly Regex FolderNamePattern = new(Controllers.ModRequest.FolderNamePattern, RegexOptions.CultureInvariant);

    public static async Task<TrackDumpImportReport> ImportAsync(
        ControllerDbContext db,
        TrackDump dump,
        CancellationToken cancellationToken = default)
    {
        // BEGIN IMMEDIATE: the catalogue is read and written under one write lock, so no
        // admin edit lands between what the import read and what it saves.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var run = new ImportRun(
            await db.Tracks.Include(t => t.Variants).ToListAsync(cancellationToken),
            await db.Mods.ToListAsync(cancellationToken),
            await db.WeatherConditions.OrderBy(w => w.Id).ToListAsync(cancellationToken),
            await db.TrackCollectionEntries.Where(e => e.TrackVariantId == null).ToListAsync(cancellationToken),
            dump.Sources!);

        // The base game first: when a mod also ships a base-game id, the base game's is the one kept.
        foreach (var track in dump.Tracks!.Where(t => t is not null).OrderBy(t => t.Source == "base" ? 0 : 1))
        {
            run.Import(db, track);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new TrackDumpImportReport(
            Tool: dump.Tool is { Name: { } name } ? Truncate($"{name} {dump.Tool.Version}".Trim(), 64) : null,
            GeneratedAt: dump.GeneratedAt is null ? null : Truncate(dump.GeneratedAt, 40),
            Mods: run.Mods.ToCounts(),
            Tracks: run.Tracks.ToCounts(),
            Variants: run.Variants.ToCounts(),
            Skipped: run.Skips,
            Diagnostics: Summarise(dump.Diagnostics));
    }

    private static IReadOnlyList<ImportDiagnostic> Summarise(IReadOnlyList<TrackDumpDiagnostic>? diagnostics) =>
        (diagnostics ?? [])
            .Where(d => d is not null)
            .GroupBy(d => string.IsNullOrWhiteSpace(d.Code) ? "unknown" : Truncate(d.Code, 64))
            .Select(g => new ImportDiagnostic(
                g.Key,
                g.Any(d => d.Severity == "error") ? "error" : "warning",
                g.Count(),
                Truncate(g.Select(d => d.Message).FirstOrDefault(m => !string.IsNullOrWhiteSpace(m)) ?? string.Empty, DiagnosticExampleMaxLength)))
            .OrderByDescending(d => d.Count)
            .ThenBy(d => d.Code, StringComparer.Ordinal)
            .ToList();

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length];

    private sealed class Tally
    {
        public int Added;
        public int Updated;
        public int Unchanged;
        public int Skipped;

        public ImportCounts ToCounts() => new(Added, Updated, Unchanged, Skipped);
    }

    /// <summary>A source as the import uses it: whether it is the base game, and its mod row if it has one.</summary>
    private sealed record SourceInfo(bool IsBase, string Suffix, Mod? Mod);

    private sealed class ImportRun
    {
        private readonly Dictionary<string, Track> _tracksByKey;
        private readonly Dictionary<string, TrackVariant> _variantsById;
        private readonly Dictionary<string, Mod> _modsByFolder;
        private readonly IReadOnlyList<WeatherCondition> _weather;
        private readonly ILookup<string, TrackCollectionEntry> _unlinked;
        private readonly Dictionary<string, TrackDumpSource> _sources;
        private readonly Dictionary<string, SourceInfo?> _resolved = new(StringComparer.Ordinal);
        private readonly HashSet<string> _seenGameIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<ImportSkip> _skips = [];

        public ImportRun(
            List<Track> tracks,
            List<Mod> mods,
            List<WeatherCondition> weather,
            List<TrackCollectionEntry> unlinked,
            IReadOnlyList<TrackDumpSource> sources)
        {
            _tracksByKey = tracks.ToDictionary(t => t.Key, StringComparer.OrdinalIgnoreCase);
            _variantsById = tracks.SelectMany(t => t.Variants).ToDictionary(v => v.VariantId, StringComparer.OrdinalIgnoreCase);
            _modsByFolder = mods.ToDictionary(m => m.FolderName, StringComparer.OrdinalIgnoreCase);
            _weather = weather;
            _unlinked = unlinked.ToLookup(e => e.TrackId, StringComparer.OrdinalIgnoreCase);
            _sources = sources
                .Where(s => s?.Id is not null)
                .GroupBy(s => s.Id!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        }

        public Tally Mods { get; } = new();

        public Tally Tracks { get; } = new();

        public Tally Variants { get; } = new();

        public IReadOnlyList<ImportSkip> Skips => _skips;

        public void Import(ControllerDbContext db, TrackDumpTrack dumpTrack)
        {
            var id = dumpTrack.Id ?? "(no id)";
            var source = dumpTrack.Source is null ? null : Resolve(db, dumpTrack.Source);
            var folder = dumpTrack.Source is not null && dumpTrack.Id?.StartsWith(dumpTrack.Source + "/", StringComparison.Ordinal) == true
                ? dumpTrack.Id[(dumpTrack.Source.Length + 1)..]
                : null;
            if (source is null || string.IsNullOrEmpty(folder))
            {
                Skip(Tracks, id, "Its source is not base, workshop:<id> or mod:<folder>, or its id is not under it.");
                return;
            }

            var siblings = new List<Track>();
            var added = new List<(TrackDumpVariant Dump, string GameId, GameMode Mode)>();
            foreach (var variant in dumpTrack.Variants ?? [])
            {
                if (variant is null)
                {
                    continue;
                }

                var variantId = variant.Id ?? $"{id}/{variant.GameId}";
                var gameId = variant.GameId?.Trim() ?? string.Empty;
                if (!GameIdPattern.IsMatch(gameId))
                {
                    Skip(Variants, variantId, "Its id has characters the catalogue does not allow: only letters, digits and _, up to 64.");
                    continue;
                }

                if (!_seenGameIds.Add(gameId))
                {
                    Skip(Variants, variantId, $"Another source in this file also has '{gameId}'; the first one was used.");
                    continue;
                }

                if (_variantsById.TryGetValue(gameId, out var existing))
                {
                    siblings.Add(existing.Track);
                    var length = UsableLength(variant);
                    if (length is not null && existing.TrackLengthM != length)
                    {
                        existing.TrackLengthM = length;
                        Variants.Updated++;
                    }
                    else
                    {
                        Variants.Unchanged++;
                    }

                    continue;
                }

                GameMode? mode = variant.GameMode switch
                {
                    1 => GameMode.Racing,
                    4 => GameMode.Derby,
                    _ => null,
                };
                if (mode is null)
                {
                    Skip(Variants, variantId, $"Its game mode {variant.GameMode} is not one the catalogue knows (1 racing, 4 derby).");
                    continue;
                }

                added.Add((variant, gameId, mode.Value));
            }

            if (added.Count == 0)
            {
                if (siblings.Count > 0)
                {
                    Tracks.Unchanged++;
                }
                else
                {
                    Skip(Tracks, id, "None of its variants could be imported.");
                }

                return;
            }

            // Where its siblings already are, so a new layout joins the track an admin knows.
            var target = siblings
                .GroupBy(t => t)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key.Id)
                .Select(g => g.Key)
                .FirstOrDefault();
            var created = false;
            if (target is null)
            {
                (target, created) = FindOrCreateTrack(db, source, folder, dumpTrack.Title?.Text);
            }

            foreach (var (dump, gameId, mode) in added)
            {
                var name = dump.Name?.Text?.Trim();
                var variant = new TrackVariant
                {
                    VariantId = gameId,
                    Name = Truncate(string.IsNullOrEmpty(name) ? gameId : name, TrackVariant.NameMaxLength),
                    GameMode = mode,
                    Track = target,
                    AllowedForVoting = false,
                    IsBuiltIn = false,
                    TrackLengthM = UsableLength(dump),
                };
                target.Variants.Add(variant);
                db.TrackVariants.Add(variant);
                _variantsById[gameId] = variant;

                // Collection entries saved before the catalogue knew this id now link to it.
                foreach (var entry in _unlinked[gameId])
                {
                    entry.TrackVariant = variant;
                }

                Variants.Added++;
            }

            if (created)
            {
                Tracks.Added++;
            }
            else
            {
                Tracks.Unchanged++;
            }
        }

        /// <summary>
        /// The track a new variant goes into when none of its siblings is in the catalogue:
        /// one with the folder's key from the same mod (an admin may have added it), else a
        /// new one. A key another mod's track already has gets the source as a suffix.
        /// </summary>
        private (Track Track, bool Created) FindOrCreateTrack(ControllerDbContext db, SourceInfo source, string folder, string? title)
        {
            var key = Key(folder, Track.KeyMaxLength);
            var candidates = new List<string> { key };
            if (!source.IsBase)
            {
                candidates.Add(Key(folder, Track.KeyMaxLength - source.Suffix.Length - 1) + "_" + source.Suffix);
            }

            for (var n = 2; candidates.Count < 50; n++)
            {
                var number = "_" + n;
                candidates.Add(Key(folder, Track.KeyMaxLength - number.Length) + number);
            }

            foreach (var candidate in candidates)
            {
                if (_tracksByKey.TryGetValue(candidate, out var existing))
                {
                    if (Fits(existing, source))
                    {
                        return (existing, false);
                    }

                    continue;
                }

                var name = title?.Trim();
                var track = new Track
                {
                    Key = candidate,
                    Name = Truncate(string.IsNullOrEmpty(name) ? folder : name, Track.NameMaxLength),
                    Origin = source.IsBase ? TrackOrigin.BaseGame : TrackOrigin.Workshop,
                    Mod = source.Mod,
                    IsBuiltIn = false,
                    WeatherConditions = _weather.ToList(),
                };
                db.Tracks.Add(track);
                _tracksByKey[candidate] = track;
                return (track, true);
            }

            // Fifty taken keys for one folder: not a real catalogue.
            throw new InvalidOperationException($"No free track key for '{folder}'.");
        }

        /// <summary>Whether an existing track with the right key is this source's: the same mod, or both from the base game.</summary>
        private static bool Fits(Track track, SourceInfo source) =>
            ReferenceEquals(track.Mod, source.Mod)
            && (source.Mod is not null || (track.Origin == TrackOrigin.Workshop) != source.IsBase);

        /// <summary>A folder name as a track key: only letters, digits and _, at most <paramref name="length"/>.</summary>
        private static string Key(string folder, int length)
        {
            var key = new string(folder.Select(c => char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_').ToArray());
            key = Truncate(key.Length == 0 ? "track" : key, Math.Max(1, length));
            return key;
        }

        private static double? UsableLength(TrackDumpVariant variant) =>
            variant.TrackData?.TrackLengthM is { } length && double.IsFinite(length) && length > 0 ? length : null;

        private SourceInfo? Resolve(ControllerDbContext db, string sourceId)
        {
            if (_resolved.TryGetValue(sourceId, out var known))
            {
                return known;
            }

            var info = ResolveNew(db, sourceId);
            _resolved[sourceId] = info;
            return info;
        }

        private SourceInfo? ResolveNew(ControllerDbContext db, string sourceId)
        {
            if (sourceId == "base")
            {
                return new SourceInfo(IsBase: true, Suffix: "base", Mod: null);
            }

            string folder;
            string? workshopId = null;
            if (sourceId.StartsWith("workshop:", StringComparison.Ordinal))
            {
                folder = sourceId["workshop:".Length..];
                if (folder.Length is 0 or > Mod.WorkshopIdMaxLength || !folder.All(char.IsAsciiDigit))
                {
                    return null;
                }

                workshopId = folder;
            }
            else if (sourceId.StartsWith("mod:", StringComparison.Ordinal))
            {
                folder = sourceId["mod:".Length..];
            }
            else
            {
                return null;
            }

            var suffix = Key(folder, 20);
            if (!FolderNamePattern.IsMatch(folder))
            {
                // The track can still be imported, as a workshop track without a mod.
                Skip(Mods, sourceId, "Its folder name is not one the mods= setting can name.");
                return new SourceInfo(IsBase: false, suffix, Mod: null);
            }

            if (_modsByFolder.TryGetValue(folder, out var mod))
            {
                if (workshopId is not null && mod.WorkshopId is null)
                {
                    mod.WorkshopId = workshopId;
                    Mods.Updated++;
                }
                else
                {
                    Mods.Unchanged++;
                }

                return new SourceInfo(IsBase: false, suffix, mod);
            }

            var name = _sources.GetValueOrDefault(sourceId)?.Name?.Trim();
            mod = new Mod
            {
                Name = Truncate(string.IsNullOrEmpty(name) ? folder : name, Mod.NameMaxLength),
                FolderName = folder,
                WorkshopId = workshopId,
            };
            db.Mods.Add(mod);
            _modsByFolder[folder] = mod;
            Mods.Added++;
            return new SourceInfo(IsBase: false, suffix, mod);
        }

        private void Skip(Tally tally, string id, string reason)
        {
            tally.Skipped++;
            if (_skips.Count < SkipListLimit)
            {
                _skips.Add(new ImportSkip(Truncate(id, 200), reason));
            }
        }
    }
}
