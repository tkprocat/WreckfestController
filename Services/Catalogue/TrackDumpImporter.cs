using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using WreckfestController.Data;
using WreckfestController.Data.Catalogue;
using WreckfestController.Data.Collections;

namespace WreckfestController.Services.Catalogue;

/// <summary>How many rows of one kind an import added, changed, left as they were, or could not use.</summary>
public sealed record ImportCounts(int Added, int Updated, int Unchanged, int Skipped);

/// <summary>
/// Something in the dump the import did not use. <c>Id</c> is <c>source/folder/gameId</c>,
/// built from parts the import checked, so it never carries a path from the dump.
/// </summary>
public sealed record ImportSkip(string Id, string Reason);

/// <summary>
/// The dump's own diagnostics of one code: how many, the worst severity, and what the code
/// means, in our words. The dump's messages are not passed on: some name local folders.
/// </summary>
public sealed record ImportDiagnostic(string Code, string Severity, int Count, string Meaning);

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
/// joins it), else the track an earlier import made for the same dump track
/// (<see cref="Track.DumpId"/>), else a built-in or admin track keyed exactly by the folder
/// from the same source, else a new track: not built-in, every weather, origin BaseGame for
/// the base game (a DLC pack is not in the dump) or Workshop for a mod. A track is created
/// only for a new variant, with a key no other track has: two dump tracks are never merged
/// by a key collision.</item>
/// <item><b>Mods</b> are matched by folder name. A match keeps its name; only a missing
/// workshop id is filled in. A new mod is added for each mod source with tracks.</item>
/// </list>
/// Re-importing the same file changes nothing.
/// </summary>
public static class TrackDumpImporter
{
    public const int SkipListLimit = 100;

    /// <summary>What each WreckfestTrackDump diagnostic code means (its docs/output.md). Any other code counts as "other".</summary>
    private static readonly Dictionary<string, string> DiagnosticMeanings = new(StringComparer.Ordinal)
    {
        ["unsupported-schema-version"] = "A file's schema version is one the dump tool does not read, so what it held was left out.",
        ["encrypted"] = "A file is encrypted and could not be read.",
        ["malformed"] = "A file did not decode.",
        ["missing-track-data"] = "A variant's .tcat was not found, so it has no track length.",
        ["missing-environment"] = "A track folder has no .envi, so the track has no title.",
        ["missing-value"] = "A value that should be set was empty, such as a blank description.",
        ["invalid-value"] = "A value was unusable, such as a zero track length.",
        ["duplicate-track"] = "Two sources define the same track folder or variant.",
        ["duplicate-source"] = "Two folders would get the same source id; only the first was read.",
    };

    private const string OtherDiagnostic = "other";

    /// <summary>A part of an id that is safe to show: no separators, drive letters or anything else a path needs.</summary>
    private static readonly Regex SafePart = new(@"^[A-Za-z0-9_.\- ]{1,128}$", RegexOptions.CultureInvariant);

    private static readonly Regex SafeToolText = new(@"^[A-Za-z0-9_.\-+ ]{1,64}$", RegexOptions.CultureInvariant);

    private static readonly Regex GameIdPattern = new(TrackVariant.VariantIdPattern, RegexOptions.CultureInvariant);
    private static readonly Regex FolderNamePattern = new(Controllers.ModRequest.FolderNamePattern, RegexOptions.CultureInvariant);

    /// <summary>The game's colour codes, <c>^0</c> to <c>^9</c>, as mods put them in names.</summary>
    private static readonly Regex ColourCode = new(@"\^[0-9]", RegexOptions.CultureInvariant);

    private static readonly Regex Spaces = new(@"\s+", RegexOptions.CultureInvariant);

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
        var tracks = dump.Tracks!.Where(t => t is not null).OrderBy(t => t.Source == "base" ? 0 : 1).ToList();

        // Before any track is reused by key: each existing track that already holds a dump
        // folder's variants belongs to that folder, whichever order the folders come in.
        run.ClaimSiblings(tracks);
        foreach (var track in tracks)
        {
            run.Import(db, track);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new TrackDumpImportReport(
            Tool: ToolText(dump.Tool),
            GeneratedAt: DateTimeOffset.TryParse(dump.GeneratedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                ? at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
                : null,
            Mods: run.Mods.ToCounts(),
            Tracks: run.Tracks.ToCounts(),
            Variants: run.Variants.ToCounts(),
            Skipped: run.Skips,
            Diagnostics: Summarise(dump.Diagnostics));
    }

    /// <summary>The dump's diagnostics, counted by code. Only the code and severity are read, never the free text.</summary>
    private static IReadOnlyList<ImportDiagnostic> Summarise(IReadOnlyList<TrackDumpDiagnostic>? diagnostics) =>
        (diagnostics ?? [])
            .Where(d => d is not null)
            .GroupBy(d => d.Code is not null && DiagnosticMeanings.ContainsKey(d.Code) ? d.Code : OtherDiagnostic)
            .Select(g => new ImportDiagnostic(
                g.Key,
                g.Any(d => d.Severity == "error") ? "error" : "warning",
                g.Count(),
                DiagnosticMeanings.GetValueOrDefault(g.Key) ?? "Diagnostics of a kind this controller does not know."))
            .OrderByDescending(d => d.Count)
            .ThenBy(d => d.Code, StringComparer.Ordinal)
            .ToList();

    /// <summary>"WreckfestTrackDump 1.0.0", or null when the name or version is not plain text.</summary>
    private static string? ToolText(TrackDumpTool? tool)
    {
        var text = $"{tool?.Name} {tool?.Version}".Trim();
        return SafeToolText.IsMatch(text) ? text : null;
    }

    /// <summary>A source id to show: base, workshop:&lt;digits&gt;, or mod:&lt;a plain folder name&gt;.</summary>
    private static string ShowSource(string? source)
    {
        const string Workshop = "workshop:";
        const string ModPrefix = "mod:";
        if (source == "base")
        {
            return source;
        }

        if (source is not null && source.StartsWith(Workshop, StringComparison.Ordinal))
        {
            var digits = source[Workshop.Length..];
            return digits.Length is > 0 and <= Mod.WorkshopIdMaxLength && digits.All(char.IsAsciiDigit) ? source : "workshop:?";
        }

        return source is not null && source.StartsWith(ModPrefix, StringComparison.Ordinal)
            ? ModPrefix + ShowPart(source[ModPrefix.Length..])
            : "?";
    }

    /// <summary>One part of a dump id to show, or "?" when it holds anything a path could hide in.</summary>
    private static string ShowPart(string? part) => part is not null && SafePart.IsMatch(part) ? part : "?";

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length];

    /// <summary>
    /// A name as it should read in the catalogue: without the game's colour codes, which
    /// would show as text ("Dragstrip ^8(PTP)"), and with runs of spaces made one.
    /// </summary>
    public static string CleanName(string? text) =>
        text is null ? string.Empty : Spaces.Replace(ColourCode.Replace(text, string.Empty), " ").Trim();

    /// <summary>
    /// A variant's name without its track's name in front: the catalogue shows "Track -
    /// Variant", so "Tomelilla 01" on Tomelilla reads "Tomelilla - 01", not "Tomelilla -
    /// Tomelilla 01". Only a whole leading word match is dropped, and a name that would be
    /// left empty ("Gardemoen" on Gardemoen) is kept as it is.
    /// </summary>
    public static string VariantName(string variant, string track)
    {
        if (track.Length == 0 || !variant.StartsWith(track, StringComparison.OrdinalIgnoreCase))
        {
            return variant;
        }

        var rest = variant[track.Length..];
        if (rest.Length == 0 || !(char.IsWhiteSpace(rest[0]) || rest[0] is '-' or ':' or '\u2013'))
        {
            return variant;
        }

        rest = rest.TrimStart(' ', '-', ':', '\u2013').Trim();
        return rest.Length > 0 ? rest : variant;
    }

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
        private readonly Dictionary<string, Track> _tracksByDumpId;

        /// <summary>
        /// The dump tracks (by dump id) each existing track belongs to in this import: those
        /// whose variants it already holds, and the one that reused it by key. A track is
        /// reused by key only by a dump track it belongs to, or when it belongs to none.
        /// </summary>
        private readonly Dictionary<Track, HashSet<string>> _claimed = new();
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
            _tracksByDumpId = tracks
                .Where(t => t.DumpId is not null)
                .GroupBy(t => t.DumpId!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.OrderBy(t => t.Id).First(), StringComparer.OrdinalIgnoreCase);
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

        /// <summary>Claims each existing track for every dump track whose variants it already holds.</summary>
        public void ClaimSiblings(IEnumerable<TrackDumpTrack> dumpTracks)
        {
            foreach (var dumpTrack in dumpTracks.Where(t => t.Id is not null))
            {
                var dumpId = dumpTrack.Id!.ToLowerInvariant();
                foreach (var variant in dumpTrack.Variants ?? [])
                {
                    if (variant?.GameId?.Trim() is { } gameId && _variantsById.TryGetValue(gameId, out var existing))
                    {
                        Claim(existing.Track, dumpId);
                    }
                }
            }
        }

        private void Claim(Track track, string dumpId)
        {
            if (!_claimed.TryGetValue(track, out var owners))
            {
                _claimed[track] = owners = new HashSet<string>(StringComparer.Ordinal);
            }

            owners.Add(dumpId);
        }

        public void Import(ControllerDbContext db, TrackDumpTrack dumpTrack)
        {
            var folder = dumpTrack.Source is not null && dumpTrack.Id?.StartsWith(dumpTrack.Source + "/", StringComparison.Ordinal) == true
                ? dumpTrack.Id[(dumpTrack.Source.Length + 1)..]
                : null;
            var id = $"{ShowSource(dumpTrack.Source)}/{ShowPart(folder)}";
            var dumpId = dumpTrack.Id?.ToLowerInvariant() ?? string.Empty;
            if (dumpId.Length > Track.DumpIdMaxLength)
            {
                // A track made from it could not keep its identity, so a later import would
                // make a second one. None of it is imported instead.
                Skip(Tracks, id, $"Its id is longer than the {Track.DumpIdMaxLength} characters the catalogue keeps; its folder name is too long. None of its variants were imported.");
                Variants.Skipped += dumpTrack.Variants?.Count(v => v is not null) ?? 0;
                return;
            }

            var source = dumpTrack.Source is null ? null : Resolve(db, dumpTrack.Source);
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

                var gameId = variant.GameId?.Trim() ?? string.Empty;
                var variantId = $"{id}/{ShowPart(gameId)}";
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
                (target, created) = FindOrCreateTrack(db, source, dumpId, folder, dumpTrack.Title?.Text);
            }

            Claim(target, dumpId);

            foreach (var (dump, gameId, mode) in added)
            {
                var name = VariantName(CleanName(dump.Name?.Text), CleanName(target.Name));
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
        /// The track a new variant goes into when none of its siblings is in the catalogue.
        /// <list type="number">
        /// <item>The track an earlier import made for this dump track, by <see cref="Track.DumpId"/>.</item>
        /// <item>A built-in or admin track whose key is exactly the folder, from the same
        /// source, that holds no other dump folder's variants and no other dump track in this import has taken.</item>
        /// <item>A new track, with the first key no track has: the folder, then
        /// <c>&lt;folder&gt;_&lt;workshop id&gt;</c>, then numbered.</item>
        /// </list>
        /// So folders that only collide once made into a key (<c>foo-bar</c> and <c>foo_bar</c>),
        /// or a folder named like a generated key, get tracks of their own.
        /// </summary>
        private (Track Track, bool Created) FindOrCreateTrack(ControllerDbContext db, SourceInfo source, string dumpId, string folder, string? title)
        {
            if (_tracksByDumpId.TryGetValue(dumpId, out var earlier))
            {
                return (earlier, false);
            }

            var key = Key(folder, Track.KeyMaxLength);
            if (string.Equals(key, folder, StringComparison.OrdinalIgnoreCase)
                && _tracksByKey.TryGetValue(key, out var named)
                && named.DumpId is null
                && Fits(named, source)
                && (!_claimed.TryGetValue(named, out var owners) || owners.All(owner => owner == dumpId)))
            {
                return (named, false);
            }

            var free = _tracksByKey.ContainsKey(key) ? null : key;
            if (free is null && !source.IsBase)
            {
                var suffixed = Key(folder, Track.KeyMaxLength - source.Suffix.Length - 1) + "_" + source.Suffix;
                free = _tracksByKey.ContainsKey(suffixed) ? null : suffixed;
            }

            for (var n = 2; free is null; n++)
            {
                var number = "_" + n.ToString(CultureInfo.InvariantCulture);
                var numbered = Key(folder, Track.KeyMaxLength - number.Length) + number;
                free = _tracksByKey.ContainsKey(numbered) ? null : numbered;
            }

            var name = CleanName(title);
            var track = new Track
            {
                Key = free,
                Name = Truncate(string.IsNullOrEmpty(name) ? folder : name, Track.NameMaxLength),
                Origin = source.IsBase ? TrackOrigin.BaseGame : TrackOrigin.Workshop,
                Mod = source.Mod,
                IsBuiltIn = false,
                DumpId = dumpId,
                WeatherConditions = _weather.ToList(),
            };
            db.Tracks.Add(track);
            _tracksByKey[free] = track;
            _tracksByDumpId[dumpId] = track;
            return (track, true);
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
                Skip(Mods, ShowSource(sourceId), "Its folder name is not one the mods= setting can name.");
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

            var name = CleanName(_sources.GetValueOrDefault(sourceId)?.Name);
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
                _skips.Add(new ImportSkip(id, reason));
            }
        }
    }
}
