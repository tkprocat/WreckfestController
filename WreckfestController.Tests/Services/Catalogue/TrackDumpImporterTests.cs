using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using WreckfestController.Data;
using WreckfestController.Data.Catalogue;
using WreckfestController.Data.Collections;
using WreckfestController.Services.Catalogue;

namespace WreckfestController.Tests.Services.Catalogue;

/// <summary>
/// #121: a WreckfestTrackDump import adds what the catalogue lacks and never changes what
/// is there, the INSERT OR IGNORE rule of #79, on a migrated database with the shipped catalogue.
/// </summary>
public sealed class TrackDumpImporterTests : IAsyncLifetime
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "wfc-db-tests", Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        await using var db = CreateContext();
        await db.Database.MigrateAsync(Ct);
    }

    public ValueTask DisposeAsync()
    {
        SqlitePools.ReleaseFolder(_directory);
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    private static TrackDumpFixture AsanPack() => new TrackDumpFixture()
        .Workshop("2276098616", "The Very TrackPack 2")
        .Track("workshop:2276098616", "asan", "Asan Circuit",
            ("asan_main", "Main", 1, 3912.5),
            ("asan_reverse", "Reverse", 1, 3912.5),
            ("asan_arena", "Arena", 4, 21120));

    [Fact]
    public async Task NewWorkshopTrack_AddsItsModTrackAndVariants_NotBuiltInAndNotVotable()
    {
        var report = await ImportAsync(AsanPack());

        Assert.Equal(new ImportCounts(1, 0, 0, 0), report.Mods);
        Assert.Equal(new ImportCounts(1, 0, 0, 0), report.Tracks);
        Assert.Equal(new ImportCounts(3, 0, 0, 0), report.Variants);

        await using var db = CreateContext();
        var track = await db.Tracks.Include(t => t.Mod).Include(t => t.Variants).Include(t => t.WeatherConditions)
            .SingleAsync(t => t.Key == "asan", Ct);
        Assert.Equal("Asan Circuit", track.Name);
        Assert.Equal(TrackOrigin.Workshop, track.Origin);
        Assert.False(track.IsBuiltIn);
        Assert.Equal(5, track.WeatherConditions.Count);
        Assert.Equal(("The Very TrackPack 2", "2276098616", "2276098616"), (track.Mod!.Name, track.Mod.FolderName, track.Mod.WorkshopId));

        Assert.All(track.Variants, v => Assert.False(v.IsBuiltIn || v.AllowedForVoting || v.IsHidden, v.VariantId));
        var main = track.Variants.Single(v => v.VariantId == "asan_main");
        Assert.Equal(("Main", GameMode.Racing, 3912.5), (main.Name, main.GameMode, main.TrackLengthM));
        var arena = track.Variants.Single(v => v.VariantId == "asan_arena");
        Assert.Equal(GameMode.Derby, arena.GameMode);
        Assert.Null(arena.TrackLengthM);
    }

    // The mods' own strings: colour codes would show as text, and a layout named after its
    // track would read "Tomelilla - Tomelilla 01" in the catalogue.
    [Fact]
    public async Task Names_LoseColourCodes_AndTheirTracksNameInFront()
    {
        var report = await ImportAsync(new TrackDumpFixture()
            .Workshop("2276098616", "^5The Very ^7TrackPack 2")
            .Track("workshop:2276098616", "tomelilla", "^2Tomelilla",
                ("tomelilla_01", "Tomelilla 01", 1, null),
                ("tomelilla_05", "tomelilla - 05  Rain", 1, null),
                ("tomelilla", "Tomelilla", 1, null),
                ("tomelillaby", "Tomelillaby", 1, null),
                ("tomelilla_drag", "Dragstrip ^8(PTP)", 1, null)));

        Assert.Equal(new ImportCounts(5, 0, 0, 0), report.Variants);
        await using var db = CreateContext();
        var track = await db.Tracks.Include(t => t.Mod).Include(t => t.Variants).SingleAsync(t => t.Key == "tomelilla", Ct);
        Assert.Equal(("Tomelilla", "The Very TrackPack 2"), (track.Name, track.Mod!.Name));
        Assert.Equal(
            ["01", "05 Rain", "Dragstrip (PTP)", "Tomelilla", "Tomelillaby"],
            track.Variants.Select(v => v.Name).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("Tomelilla 01", "Tomelilla", "01")]
    [InlineData("Gardemoen Rallycross Reverse", "Gardemoen", "Rallycross Reverse")]
    [InlineData("Wild Horse Pass", "Wild Horse Pass", "Wild Horse Pass")]
    [InlineData("Hayville Farm Dash", "Hayville Farm", "Dash")]
    [InlineData("Lanker", "Lank", "Lanker")]
    [InlineData("Main", "Asan Circuit", "Main")]
    [InlineData("Asan Circuit: Main", "Asan Circuit", "Main")]
    public void VariantName_DropsOnlyAWholeLeadingTrackName(string variant, string track, string expected) =>
        Assert.Equal(expected, TrackDumpImporter.VariantName(variant, track));

    [Theory]
    [InlineData("Dragstrip ^8(PTP)", "Dragstrip (PTP)")]
    [InlineData("^5Monday ^2Night", "Monday Night")]
    [InlineData("  Spaced   out ", "Spaced out")]
    [InlineData("Price ^ 5", "Price ^ 5")]
    [InlineData(null, "")]
    public void CleanName_RemovesColourCodesAndExtraSpaces(string? text, string expected) =>
        Assert.Equal(expected, TrackDumpImporter.CleanName(text));

    [Fact]
    public async Task ImportingTheSameFileAgain_ChangesNothing()
    {
        await ImportAsync(AsanPack());
        var before = await SnapshotAsync();

        var report = await ImportAsync(AsanPack());

        Assert.Equal(new ImportCounts(0, 0, 1, 0), report.Mods);
        Assert.Equal(new ImportCounts(0, 0, 1, 0), report.Tracks);
        Assert.Equal(new ImportCounts(0, 0, 3, 0), report.Variants);
        Assert.Equal(before, await SnapshotAsync());
    }

    [Fact]
    public async Task AnEditedBuiltIn_KeepsEveryAdminValue_AndOnlyGetsItsTrackLength()
    {
        await using (var db = CreateContext())
        {
            var edited = await db.TrackVariants.SingleAsync(v => v.VariantId == "bigstadium_figure_8", Ct);
            edited.Name = "Admin's name";
            edited.GameMode = GameMode.Derby;
            edited.AllowedForVoting = false;
            edited.IsHidden = true;
            await db.SaveChangesAsync(Ct);
        }

        var report = await ImportAsync(new TrackDumpFixture().Base()
            .Track("base", "bigstadium", "Big Stadium", ("bigstadium_figure_8", "Figure 8 from the dump", 1, 512.25)));

        Assert.Equal(new ImportCounts(0, 1, 0, 0), report.Variants);
        Assert.Equal(new ImportCounts(0, 0, 1, 0), report.Tracks);

        await using var check = CreateContext();
        var variant = await check.TrackVariants.Include(v => v.Track).SingleAsync(v => v.VariantId == "bigstadium_figure_8", Ct);
        Assert.Equal(("Admin's name", GameMode.Derby, false, true, true), (variant.Name, variant.GameMode, variant.AllowedForVoting, variant.IsHidden, variant.IsBuiltIn));
        Assert.Equal(512.25, variant.TrackLengthM);
        Assert.Equal("madman_stadium", variant.Track.Key);
        Assert.Equal("Madman Stadium", variant.Track.Name);
        Assert.False(await check.Tracks.AnyAsync(t => t.Key == "bigstadium", Ct));
    }

    [Fact]
    public async Task ANewLayoutOfABuiltInTrack_JoinsTheTrackItsSiblingsAreIn()
    {
        var report = await ImportAsync(new TrackDumpFixture().Base()
            .Track("base", "bigstadium", "Big Stadium",
                ("bigstadium_figure_8", "Figure 8", 1, null),
                ("bigstadium_new_loop", "New Loop", 1, 700)));

        Assert.Equal(new ImportCounts(0, 0, 1, 0), report.Tracks);
        Assert.Equal(new ImportCounts(1, 0, 1, 0), report.Variants);

        await using var db = CreateContext();
        var added = await db.TrackVariants.Include(v => v.Track).SingleAsync(v => v.VariantId == "bigstadium_new_loop", Ct);
        Assert.Equal("madman_stadium", added.Track.Key);
        Assert.False(added.IsBuiltIn);
        Assert.True(added.Track.IsBuiltIn);
    }

    [Fact]
    public async Task AnAdminAddedVariantWithTheSameId_IsLeftWhereItIs()
    {
        await using (var db = CreateContext())
        {
            var track = new Track { Key = "my_track", Name = "Mine", Origin = TrackOrigin.Custom };
            track.Variants.Add(new TrackVariant { VariantId = "ASAN_MAIN", Name = "Mine too", GameMode = GameMode.Derby, AllowedForVoting = true });
            db.Tracks.Add(track);
            await db.SaveChangesAsync(Ct);
        }

        var report = await ImportAsync(AsanPack());

        Assert.Equal(new ImportCounts(2, 1, 0, 0), report.Variants);
        await using var check = CreateContext();
        var mine = await check.TrackVariants.Include(v => v.Track).SingleAsync(v => v.VariantId == "asan_main", Ct);
        Assert.Equal(("ASAN_MAIN", "Mine too", GameMode.Derby, true, "my_track"), (mine.VariantId, mine.Name, mine.GameMode, mine.AllowedForVoting, mine.Track.Key));
        Assert.Equal(3912.5, mine.TrackLengthM);
        Assert.Equal(1, await check.TrackVariants.CountAsync(v => v.VariantId == "asan_main", Ct));

        // Its siblings follow it, rather than starting a second Asan track.
        var reverse = await check.TrackVariants.Include(v => v.Track).SingleAsync(v => v.VariantId == "asan_reverse", Ct);
        Assert.Equal("my_track", reverse.Track.Key);
        Assert.False(await check.Tracks.AnyAsync(t => t.Key == "asan", Ct));
    }

    [Fact]
    public async Task AnExistingMod_KeepsItsName_AndOnlyGetsAMissingWorkshopId()
    {
        await using (var db = CreateContext())
        {
            db.Mods.Add(new Mod { Name = "TVTP2 (admin)", FolderName = "2276098616" });
            await db.SaveChangesAsync(Ct);
        }

        var report = await ImportAsync(AsanPack());

        Assert.Equal(new ImportCounts(0, 1, 0, 0), report.Mods);
        await using var check = CreateContext();
        var mod = await check.Mods.Include(m => m.Tracks).SingleAsync(Ct);
        Assert.Equal(("TVTP2 (admin)", "2276098616"), (mod.Name, mod.WorkshopId));
        Assert.Equal("asan", Assert.Single(mod.Tracks).Key);
    }

    [Fact]
    public async Task VariantsTheCatalogueCannotHold_AreSkippedWithAReason()
    {
        var report = await ImportAsync(new TrackDumpFixture().Base().Workshop("2276098616", "Pack")
            .Track("base", "fields", "Fields", ("fields_loop", "Loop", 1, 1000))
            .Track("workshop:2276098616", "gardemoen", "Gardemoen",
                ("gardemoen_head-on", "Head-on", 1, 900),
                ("gardemoen_odd", "Odd mode", 2, 900),
                ("FIELDS_LOOP", "A mod's copy", 1, 1100),
                ("gardemoen_main", "Main", 1, 900)));

        Assert.Equal(new ImportCounts(2, 0, 0, 3), report.Variants);
        Assert.Equal(3, report.Skipped.Count);
        Assert.Contains(report.Skipped, s => s.Id.EndsWith("gardemoen_head-on", StringComparison.Ordinal) && s.Reason.Contains("letters, digits"));
        Assert.Contains(report.Skipped, s => s.Id.EndsWith("gardemoen_odd", StringComparison.Ordinal) && s.Reason.Contains("game mode 2"));
        Assert.Contains(report.Skipped, s => s.Id.EndsWith("fields_loop", StringComparison.OrdinalIgnoreCase) && s.Reason.Contains("first one"));

        // The base game's copy of a shared id is the one kept.
        await using var db = CreateContext();
        var loop = await db.TrackVariants.Include(v => v.Track).SingleAsync(v => v.VariantId == "fields_loop", Ct);
        Assert.Equal(("Loop", "fields", TrackOrigin.BaseGame), (loop.Name, loop.Track.Key, loop.Track.Origin));
    }

    [Fact]
    public async Task ATrackWithNothingToImport_IsSkipped()
    {
        var report = await ImportAsync(new TrackDumpFixture().Workshop("1", "Pack")
            .Track("workshop:1", "broken", "Broken", ("bad-id", "Bad", 1, null)));

        Assert.Equal(new ImportCounts(0, 0, 0, 1), report.Tracks);
        await using var db = CreateContext();
        Assert.False(await db.Tracks.AnyAsync(t => t.Key == "broken", Ct));
    }

    [Fact]
    public async Task TwoModsWithTheSameTrackFolder_GetTracksOfTheirOwn()
    {
        var report = await ImportAsync(new TrackDumpFixture().Workshop("111", "First").Workshop("222", "Second")
            .Track("workshop:111", "gardemoen", "Gardemoen", ("gardemoen_a", "A", 1, null))
            .Track("workshop:222", "gardemoen", "Gardemoen", ("gardemoen_b", "B", 1, null)));

        Assert.Equal(new ImportCounts(2, 0, 0, 0), report.Tracks);
        await using var db = CreateContext();
        var keys = await db.Tracks.Include(t => t.Mod).Where(t => t.Key.StartsWith("gardemoen"))
            .Select(t => new { t.Key, t.Mod!.FolderName }).OrderBy(t => t.Key).ToListAsync(Ct);
        Assert.Equal(["gardemoen:111", "gardemoen_222:222"], keys.Select(k => $"{k.Key}:{k.FolderName}"));

        // Again: each finds its own track by key and mod.
        var again = await ImportAsync(new TrackDumpFixture().Workshop("111", "First").Workshop("222", "Second")
            .Track("workshop:111", "gardemoen", "Gardemoen", ("gardemoen_a2", "A2", 1, null))
            .Track("workshop:222", "gardemoen", "Gardemoen", ("gardemoen_b2", "B2", 1, null)));
        Assert.Equal(new ImportCounts(0, 0, 2, 0), again.Tracks);
        var b2 = await db.TrackVariants.Include(v => v.Track).SingleAsync(v => v.VariantId == "gardemoen_b2", Ct);
        Assert.Equal("gardemoen_222", b2.Track.Key);
    }

    [Fact]
    public async Task ACollectionEntryForAnUnknownId_IsLinkedOnceImported()
    {
        await using (var db = CreateContext())
        {
            var collection = new TrackCollection { Name = "Workshop night" };
            collection.Entries.Add(new TrackCollectionEntry { Position = 0, TrackId = "asan_main" });
            db.TrackCollections.Add(collection);
            await db.SaveChangesAsync(Ct);
        }

        await ImportAsync(AsanPack());

        await using var check = CreateContext();
        var entry = await check.TrackCollectionEntries.Include(e => e.TrackVariant).SingleAsync(Ct);
        Assert.Equal("asan_main", entry.TrackVariant?.VariantId);
    }

    [Fact]
    public async Task TheReport_SummarisesTheDumpsDiagnosticsByCode()
    {
        var report = await ImportAsync(AsanPack()
            .Diagnostic("missing-value", "A blank description.")
            .Diagnostic("missing-value", "Another.")
            .Diagnostic("malformed", "The pack did not decode.", "error"));

        Assert.Equal(["missing-value", "malformed"], report.Diagnostics.Select(d => d.Code));
        Assert.Equal([2, 1], report.Diagnostics.Select(d => d.Count));
        Assert.Equal(["warning", "error"], report.Diagnostics.Select(d => d.Severity));
        // Our words for each code, never the dump's message.
        Assert.All(report.Diagnostics, d => Assert.DoesNotContain(d.Meaning, new[] { "A blank description.", "The pack did not decode." }));
        Assert.All(report.Diagnostics, d => Assert.False(string.IsNullOrEmpty(d.Meaning)));
        Assert.Equal("WreckfestTrackDump 1.0.0", report.Tool);
        Assert.Equal("2026-10-06T14:03:12Z", report.GeneratedAt);
    }

    /// <summary>A dump can carry local paths: duplicate-source names both folders, a read failure its exception text.</summary>
    public static readonly string[] Paths = [@"C:\Users\allan\steamapps", "/home/allan/.steam", @"\\nas\games\wreckfest", "D:/SteamLibrary"];

    [Fact]
    public async Task NoTextFromTheDump_ThatCouldHoldAPath_ReachesTheReport()
    {
        var json = AsanPack()
            .Diagnostic("duplicate-source", $"Two folders give workshop:1: {Paths[0]}\\1 and {Paths[1]}/1. Only the first was read.", "error")
            .Diagnostic("malformed", $"Could not read {Paths[2]}\\x.envi: The process cannot access the file.")
            .Diagnostic($"{Paths[3]}/made-up-code", $"Unknown at {Paths[3]}")
            .ToJsonObject();
        json["tool"] = new JsonObject { ["name"] = Paths[0], ["version"] = Paths[1] };
        json["generatedAt"] = Paths[2];
        // A track id and a game id with path text in them: skipped, and listed without it.
        json["tracks"]!.AsArray().Add(new JsonObject
        {
            ["id"] = "workshop:2276098616/" + Paths[1], ["source"] = "workshop:2276098616", ["title"] = null, ["variants"] = new JsonArray(),
        });
        json["tracks"]![0]!["variants"]!.AsArray().Add(new JsonObject
        {
            ["id"] = "x", ["gameId"] = Paths[0], ["name"] = null, ["gameMode"] = 1, ["trackData"] = null,
        });
        using var document = JsonDocument.Parse(json.ToJsonString());
        var dump = TrackDump.Read(document.RootElement, out var refusal);
        Assert.Null(refusal);

        TrackDumpImportReport report;
        await using (var db = CreateContext())
        {
            report = await TrackDumpImporter.ImportAsync(db, dump!, Ct);
        }

        var text = JsonSerializer.Serialize(report);
        foreach (var fragment in Paths.Concat(["Users", "home", "nas", "SteamLibrary", "steamapps"]))
        {
            Assert.DoesNotContain(fragment, text, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(["duplicate-source", "malformed", "other"], report.Diagnostics.Select(d => d.Code).Order());
        Assert.Null(report.Tool);
        Assert.Null(report.GeneratedAt);
        Assert.Contains(report.Skipped, s => s.Id == "workshop:2276098616/?");
        Assert.Contains(report.Skipped, s => s.Id == "workshop:2276098616/asan/?");
    }

    [Fact]
    public async Task FoldersThatCollideOnlyAsKeys_GetTracksOfTheirOwn()
    {
        var report = await ImportAsync(new TrackDumpFixture().Workshop("111", "Pack")
            .Track("workshop:111", "foo-bar", "Foo Bar (dash)", ("foobar_dash", "Dash", 1, null))
            .Track("workshop:111", "foo_bar", "Foo Bar (underscore)", ("foobar_under", "Under", 1, null)));

        Assert.Equal(new ImportCounts(2, 0, 0, 0), report.Tracks);
        await using var db = CreateContext();
        var dash = await db.TrackVariants.Include(v => v.Track).SingleAsync(v => v.VariantId == "foobar_dash", Ct);
        var under = await db.TrackVariants.Include(v => v.Track).SingleAsync(v => v.VariantId == "foobar_under", Ct);
        Assert.NotEqual(dash.TrackId, under.TrackId);
        Assert.Equal(("Foo Bar (dash)", "Foo Bar (underscore)"), (dash.Track.Name, under.Track.Name));

        // A later import, in the other order, puts each new variant with its own folder's track.
        await ImportAsync(new TrackDumpFixture().Workshop("111", "Pack")
            .Track("workshop:111", "foo_bar", "Foo Bar (underscore)", ("foobar_under2", "Under 2", 1, null))
            .Track("workshop:111", "foo-bar", "Foo Bar (dash)", ("foobar_dash2", "Dash 2", 1, null)));
        Assert.Equal(dash.TrackId, (await db.TrackVariants.AsNoTracking().SingleAsync(v => v.VariantId == "foobar_dash2", Ct)).TrackId);
        Assert.Equal(under.TrackId, (await db.TrackVariants.AsNoTracking().SingleAsync(v => v.VariantId == "foobar_under2", Ct)).TrackId);
    }

    [Fact]
    public async Task AFolderNamedLikeAGeneratedKey_DoesNotJoinThatTrack()
    {
        // 222's "gardemoen" becomes gardemoen_222, and then 222 has a real folder of that name.
        var report = await ImportAsync(new TrackDumpFixture().Workshop("111", "First").Workshop("222", "Second")
            .Track("workshop:111", "gardemoen", "Gardemoen", ("g_first", "First", 1, null))
            .Track("workshop:222", "gardemoen", "Gardemoen", ("g_second", "Second", 1, null))
            .Track("workshop:222", "gardemoen_222", "Gardemoen 222", ("g_real", "Real", 1, null)));

        Assert.Equal(new ImportCounts(3, 0, 0, 0), report.Tracks);
        await using var db = CreateContext();
        var tracks = await db.TrackVariants.Include(v => v.Track).Where(v => v.VariantId.StartsWith("g_"))
            .ToDictionaryAsync(v => v.VariantId, v => v.Track.Name, Ct);
        Assert.Equal(3, await db.TrackVariants.Where(v => v.VariantId.StartsWith("g_")).Select(v => v.TrackId).Distinct().CountAsync(Ct));
        Assert.Equal("Gardemoen 222", tracks["g_real"]);
    }

    [Fact]
    public async Task ATrackHoldingOneFoldersVariants_IsNotReusedByKeyForAnother()
    {
        // An admin's foo_bar track already holds a layout of the dump's foo-bar folder.
        int adminTrackId;
        await using (var db = CreateContext())
        {
            var track = new Track { Key = "foo_bar", Name = "Admin's Foo Bar", Origin = TrackOrigin.Custom };
            track.Variants.Add(new TrackVariant { VariantId = "foobar_dash", Name = "Dash", GameMode = GameMode.Racing });
            db.Tracks.Add(track);
            await db.SaveChangesAsync(Ct);
            adminTrackId = track.Id;
        }

        // foo_bar comes first, so only a claim made before any track is imported keeps it out.
        var report = await ImportAsync(new TrackDumpFixture().Base()
            .Track("base", "foo_bar", "Foo Bar (underscore)", ("foobar_under", "Under", 1, null))
            .Track("base", "foo-bar", "Foo Bar (dash)", ("foobar_dash", "Dash", 1, null), ("foobar_dash2", "Dash 2", 1, null)));

        Assert.Equal(new ImportCounts(1, 0, 1, 0), report.Tracks);
        await using var check = CreateContext();
        var dash2 = await check.TrackVariants.SingleAsync(v => v.VariantId == "foobar_dash2", Ct);
        var under = await check.TrackVariants.Include(v => v.Track).SingleAsync(v => v.VariantId == "foobar_under", Ct);
        Assert.Equal(adminTrackId, dash2.TrackId);
        Assert.NotEqual(adminTrackId, under.TrackId);
        Assert.Equal("Foo Bar (underscore)", under.Track.Name);
        Assert.Equal("Admin's Foo Bar", (await check.Tracks.SingleAsync(t => t.Id == adminTrackId, Ct)).Name);
    }

    [Fact]
    public async Task ATrackWhoseIdIsTooLongToKeep_IsSkippedWhole()
    {
        // "base/" and 196 characters: 201, one more than Track.DumpId holds.
        var folder = new string('l', 196);
        var fixture = new TrackDumpFixture().Base().Track("base", folder, "Long", ("long_main", "Main", 1, 1000), ("long_rev", "Reverse", 1, 1000));

        var report = await ImportAsync(fixture);
        var again = await ImportAsync(fixture);

        Assert.Equal(new ImportCounts(0, 0, 0, 1), report.Tracks);
        Assert.Equal(new ImportCounts(0, 0, 0, 2), report.Variants);
        Assert.Contains(report.Skipped, s => s.Reason.Contains("too long", StringComparison.Ordinal));
        Assert.Equal(report.Tracks, again.Tracks);
        await using var db = CreateContext();
        Assert.False(await db.TrackVariants.AnyAsync(v => v.VariantId.StartsWith("long_"), Ct));
        Assert.False(await db.Tracks.AnyAsync(t => t.Name == "Long", Ct));
    }

    [Fact]
    public async Task AnAdminTrackKeyedByTheFolder_IsUsed_ButNeverChanged()
    {
        await using (var db = CreateContext())
        {
            db.Tracks.Add(new Track { Key = "fields", Name = "Admin's Fields", Origin = TrackOrigin.Custom });
            await db.SaveChangesAsync(Ct);
        }

        await ImportAsync(new TrackDumpFixture().Base().Track("base", "fields", "Fields", ("fields_loop", "Loop", 1, null)));

        await using var check = CreateContext();
        var loop = await check.TrackVariants.Include(v => v.Track).SingleAsync(v => v.VariantId == "fields_loop", Ct);
        Assert.Equal(("fields", "Admin's Fields", null as string), (loop.Track.Key, loop.Track.Name, loop.Track.DumpId));
    }

    [Theory]
    [InlineData("""{"formatVersion": 2, "sources": [], "tracks": []}""", "formatVersion 2")]
    [InlineData("""{"formatVersion": 1.5, "sources": [], "tracks": []}""", "unknown formatVersion")]
    [InlineData("""{"sources": [], "tracks": []}""", "no formatVersion")]
    [InlineData("""[1, 2]""", "not a JSON object")]
    [InlineData("""{"formatVersion": 1, "sources": [], "tracks": [{"id": 5}]}""", "$.tracks[0].id")]
    [InlineData("""{"formatVersion": 1, "sources": []}""", "no sources or tracks")]
    public void Read_RefusesWhatIsNotADumpItReads(string json, string expected)
    {
        using var document = JsonDocument.Parse(json);

        var dump = TrackDump.Read(document.RootElement, out var refusal);

        Assert.Null(dump);
        Assert.Contains(expected, refusal);
    }

    [Fact]
    public void Read_IgnoresPropertiesItDoesNotKnow()
    {
        var json = AsanPack().ToJsonObject();
        json["somethingNew"] = new System.Text.Json.Nodes.JsonObject { ["x"] = 1 };
        using var document = JsonDocument.Parse(json.ToJsonString());

        var dump = TrackDump.Read(document.RootElement, out var refusal);

        Assert.Null(refusal);
        Assert.Equal(3, Assert.Single(dump!.Tracks!).Variants!.Count);
    }

    private async Task<TrackDumpImportReport> ImportAsync(TrackDumpFixture fixture)
    {
        var dump = TrackDump.Read(fixture.ToElement(), out var refusal);
        Assert.Null(refusal);
        await using var db = CreateContext();
        return await TrackDumpImporter.ImportAsync(db, dump!, Ct);
    }

    /// <summary>Every catalogue row's values and version, to show an import touched nothing.</summary>
    private async Task<string> SnapshotAsync()
    {
        await using var db = CreateContext();
        var tracks = await db.Tracks.AsNoTracking().OrderBy(t => t.Id)
            .Select(t => $"{t.Id}|{t.Key}|{t.Name}|{t.Origin}|{t.ModId}|{t.Version}").ToListAsync(Ct);
        var variants = await db.TrackVariants.AsNoTracking().OrderBy(v => v.Id)
            .Select(v => $"{v.Id}|{v.VariantId}|{v.Name}|{v.GameMode}|{v.TrackId}|{v.AllowedForVoting}|{v.TrackLengthM}|{v.Version}").ToListAsync(Ct);
        var mods = await db.Mods.AsNoTracking().OrderBy(m => m.Id).Select(m => $"{m.Id}|{m.Name}|{m.FolderName}|{m.WorkshopId}").ToListAsync(Ct);
        return string.Join("\n", tracks.Concat(variants).Concat(mods));
    }

    private ControllerDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ControllerDbContext>();
        ControllerDbContext.Configure(options, Path.Combine(_directory, "controller.db"));
        return new ControllerDbContext(options.Options);
    }
}
