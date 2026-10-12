using Microsoft.EntityFrameworkCore;
using WreckfestController.Data;
using WreckfestController.Data.Catalogue;
using WreckfestController.Services.Catalogue;

namespace WreckfestController.Tests.Services.Catalogue;

/// <summary>
/// #260: menu images from <c>wftracks imagedump</c> go to the tracks and variants of their
/// tracks.json, on a migrated database with the shipped catalogue, and the rules for which
/// image a row shows.
/// </summary>
public sealed class TrackDumpImageImporterTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 10, 12, 20, 0, 0, DateTimeKind.Utc);

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

    /// <summary>Madman Stadium as a dump of the game client has it: folder bigstadium, which is not the catalogue's key.</summary>
    private static TrackDumpFixture BaseGame() => new TrackDumpFixture()
        .Base()
        .Track("base", "bigstadium", "Madman Stadium",
            ("bigstadium_demolition_arena", "Demolition Arena", 4, 1000),
            ("bigstadium_figure_8", "Figure 8", 1, 800))
        .Images("base/bigstadium", "env_bigstadium_small", "env_bigstadium_large")
        .Images("bigstadium_figure_8", "event_bigstadium_figure_8_small", null);

    private static Dictionary<string, byte[]> Files(params (string Key, byte[] Data)[] files) =>
        files.ToDictionary(f => f.Key, f => f.Data, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public async Task BuiltInTrack_FindsItsImages_ThroughItsVariants()
    {
        var report = await ImportAsync(BaseGame(), Files(
            ("base/env_bigstadium_small.png", TestImages.Png(1)),
            ("base/env_bigstadium_large.png", TestImages.Png(2)),
            ("base/event_bigstadium_figure_8_small.png", TestImages.Png(3))));

        Assert.Equal(new TrackDumpImageReport(Stored: 3, Unchanged: 0, Removed: 0, NotUsed: 0, Refused: 0, Unmatched: 0), report);
        await using var db = CreateContext();
        Assert.Equal(TestImages.Png(2), (await CatalogueImageStore.ForTrackAsync(db, "madman_stadium", ImageSize.Large, Ct))!.Data);
        Assert.Equal(TestImages.Png(3), (await CatalogueImageStore.ForVariantAsync(db, "bigstadium_figure_8", ImageSize.Small, Ct))!.Data);

        // The variant has no large image of its own, so it shows its track's; the arena has none at all.
        Assert.Equal(TestImages.Png(2), (await CatalogueImageStore.ForVariantAsync(db, "bigstadium_figure_8", ImageSize.Large, Ct))!.Data);
        Assert.Equal(TestImages.Png(1), (await CatalogueImageStore.ForVariantAsync(db, "bigstadium_demolition_arena", ImageSize.Small, Ct))!.Data);
    }

    [Fact]
    public async Task Batches_AddUp_AndAReimportChangesNothing()
    {
        await ImportAsync(BaseGame(), Files(("base/env_bigstadium_small.png", TestImages.Png(1))));
        var second = await ImportAsync(BaseGame(), Files(("BASE/ENV_BIGSTADIUM_LARGE.PNG", TestImages.Png(2))));
        Assert.Equal(1, second.Stored);

        var again = await ImportAsync(BaseGame(), Files(
            ("base/env_bigstadium_small.png", TestImages.Png(1)),
            ("base/env_bigstadium_large.png", TestImages.Png(2))));

        Assert.Equal(new TrackDumpImageReport(0, 2, 0, 0, 0, 0), again);
        await using var db = CreateContext();
        Assert.Equal(2, await db.CatalogueImages.CountAsync(Ct));
    }

    [Fact]
    public async Task ASizeTheDumpNoLongerHas_IsRemoved_ButAnUploadStays()
    {
        await ImportAsync(BaseGame(), Files(
            ("base/env_bigstadium_small.png", TestImages.Png(1)),
            ("base/env_bigstadium_large.png", TestImages.Png(2))));
        var trackId = await TrackIdAsync("madman_stadium");
        await using (var db = CreateContext())
        {
            await CatalogueImageStore.PutAsync(db, ImageOwner.Track(trackId), ImageSize.Large, ImageOrigin.Upload, TestImages.Png(9), CatalogueImageStore.Png, Now, Ct);
            await db.SaveChangesAsync(Ct);
        }

        var report = await ImportAsync(BaseGame().Images("base/bigstadium", "env_bigstadium_small", null), Files());

        Assert.Equal(1, report.Removed);
        await using var check = CreateContext();
        var slots = await CatalogueImageStore.SlotsAsync(check, ImageOwner.Track(trackId), Ct);
        Assert.Equal(
            [(ImageSize.Large, ImageOrigin.Upload), (ImageSize.Small, ImageOrigin.Import)],
            slots.Select(s => (s.Size, s.Origin)).OrderBy(s => s.Size == ImageSize.Small));
    }

    [Fact]
    public async Task FilesNoRowUses_AndFilesThatAreNotPng_AreCountedNotStored()
    {
        var report = await ImportAsync(BaseGame(), Files(
            ("base/env_bigstadium_small.png", "<svg onload=alert(1)>"u8.ToArray()),
            ("base/env_bigstadium_large.png", TestImages.Jpeg()),
            ("base/somewhere_else.png", TestImages.Png(4)),
            ("workshop_1/env_bigstadium_small.png", TestImages.Png(5))));

        Assert.Equal(new TrackDumpImageReport(0, 0, 0, NotUsed: 2, Refused: 2, Unmatched: 0), report);
        await using var db = CreateContext();
        Assert.Equal(0, await db.CatalogueImages.CountAsync(Ct));
    }

    [Fact]
    public async Task ATrackTheCatalogueLacks_IsCountedAsUnmatched()
    {
        var dump = new TrackDumpFixture()
            .Workshop("2276098616", "The Very TrackPack 2")
            .Track("workshop:2276098616", "asan", "Asan Circuit", ("asan_main", "Main", 1, 3912.5))
            .Images("workshop:2276098616/asan", "env_asan_small", null)
            .Images("asan_main", "event_asan_main_small", null);

        var report = await ImportAsync(dump, Files(("workshop_2276098616/env_asan_small.png", TestImages.Png(1))));

        Assert.Equal(new TrackDumpImageReport(0, 0, 0, NotUsed: 1, Refused: 0, Unmatched: 2), report);
    }

    [Fact]
    public async Task AnImportedModTrack_IsFoundByItsDumpId()
    {
        var dump = new TrackDumpFixture()
            .Workshop("2276098616", "The Very TrackPack 2")
            .Track("workshop:2276098616", "asan", "Asan Circuit", ("asan_main", "Main", 1, 3912.5))
            .Images("workshop:2276098616/asan", "env_asan_small", null);
        await using (var db = CreateContext())
        {
            await TrackDumpImporter.ImportAsync(db, TrackDump.Read(dump.ToElement(), out _)!, Ct);
        }

        var report = await ImportAsync(dump, Files(("workshop_2276098616/env_asan_small.png", TestImages.Png(1))));

        Assert.Equal(1, report.Stored);
        await using var check = CreateContext();
        Assert.NotNull(await CatalogueImageStore.ForTrackAsync(check, "asan", ImageSize.Small, Ct));
    }

    [Theory]
    [InlineData("base", "data/menu/textures/env_x_small_250x116_raw.bmap", "base/env_x_small_250x116_raw.png")]
    [InlineData("workshop:123", "data/menu/textures/E.BMAP", "workshop_123/E.png")]
    [InlineData("mod:my mod", "data/menu/textures/e.bmap", "mod_my mod/e.png")]
    [InlineData("base", "data/menu/textures/../x.bmap", null)]
    [InlineData("base", "data/menu/x.bmap", null)]
    [InlineData("C:/x", "data/menu/textures/x.bmap", null)]
    [InlineData("mod:a/b", "data/menu/textures/x.bmap", null)]
    public void FileKey_IsWhereImagedumpWritesIt(string source, string path, string? expected) =>
        Assert.Equal(expected, TrackDumpImageImporter.FileKey(source, path));

    [Theory]
    [InlineData("base/a.png", "base/a.png")]
    [InlineData("dump/base/a.png", "base/a.png")]
    [InlineData(@"C:\dump\workshop_1\a.png", "workshop_1/a.png")]
    [InlineData("a.png", null)]
    [InlineData("", null)]
    public void UploadKey_IsTheLastTwoSegments(string fileName, string? expected) =>
        Assert.Equal(expected, TrackDumpImageImporter.UploadKey(fileName));

    [Fact]
    public void Sniff_KnowsPngJpegAndWebP_AndNothingElse()
    {
        Assert.Equal(CatalogueImageStore.Png, CatalogueImageStore.Sniff(TestImages.Png(1)));
        Assert.Equal(CatalogueImageStore.Jpeg, CatalogueImageStore.Sniff(TestImages.Jpeg()));
        Assert.Equal(CatalogueImageStore.WebP, CatalogueImageStore.Sniff("RIFF\0\0\0\0WEBPVP8 "u8));
        Assert.Null(CatalogueImageStore.Sniff("<svg xmlns=\"http://www.w3.org/2000/svg\"/>"u8));
        Assert.Null(CatalogueImageStore.Sniff("GIF89a"u8));
        Assert.Null(CatalogueImageStore.Sniff([]));
    }

    [Fact]
    public void Pick_PrefersTheSizeThenTheVariantThenAnUpload()
    {
        ImageSlot Slot(int? track, int? variant, ImageSize size, ImageOrigin origin) =>
            new(Id: 0, track, variant, size, origin, $"{track}{variant}{size}{origin}", 1, IsOverlay: false);
        var trackSmall = Slot(1, null, ImageSize.Small, ImageOrigin.Import);
        var variantLargeImport = Slot(null, 7, ImageSize.Large, ImageOrigin.Import);
        var variantLargeUpload = Slot(null, 7, ImageSize.Large, ImageOrigin.Upload);
        var trackLarge = Slot(1, null, ImageSize.Large, ImageOrigin.Import);
        ImageSlot[] all = [trackSmall, variantLargeImport, variantLargeUpload, trackLarge];

        // The track's small image, before the variant's own large one.
        Assert.Same(trackSmall, CatalogueImageStore.Pick(all, ImageSize.Small, trackId: 1, variantId: 7));
        Assert.Same(variantLargeUpload, CatalogueImageStore.Pick(all, ImageSize.Large, trackId: 1, variantId: 7));
        Assert.Same(trackLarge, CatalogueImageStore.Pick(all, ImageSize.Large, trackId: 1, variantId: null));

        // Small falls back to large; large never falls back to small.
        Assert.Same(trackLarge, CatalogueImageStore.Pick([trackLarge], ImageSize.Small, trackId: 1, variantId: null));
        Assert.Null(CatalogueImageStore.Pick([trackSmall], ImageSize.Large, trackId: 1, variantId: null));
    }

    [Fact]
    public void Pick_NeverShowsAnOutlineAsAPicture()
    {
        var outline = new ImageSlot(1, null, 7, ImageSize.Small, ImageOrigin.Import, "o", 1, IsOverlay: true);
        var picture = new ImageSlot(2, 1, null, ImageSize.Large, ImageOrigin.Import, "p", 1, IsOverlay: false);

        Assert.Same(picture, CatalogueImageStore.Pick([outline, picture], ImageSize.Small, trackId: 1, variantId: 7));
        Assert.Null(CatalogueImageStore.Pick([outline], ImageSize.Small, trackId: 1, variantId: 7));
    }

    [Fact]
    public async Task TheGamesVariantOutlines_AreLayouts_AndTheVariantShowsItsTracksPicture()
    {
        var opaque = TestImages.RgbaPng(25, 12, (_, _) => 255);
        await ImportAsync(BaseGame(), Files(
            ("base/env_bigstadium_small.png", opaque),
            ("base/event_bigstadium_figure_8_small.png", TestImages.Outline(25, 12))));

        await using var db = CreateContext();
        var slots = await CatalogueImageStore.SlotsAsync(db, Ct);
        Assert.Equal([false, true], slots.OrderBy(s => s.TrackVariantId.HasValue).Select(s => s.IsOverlay));
        Assert.Equal(opaque, (await CatalogueImageStore.ForVariantAsync(db, "bigstadium_figure_8", ImageSize.Small, Ct))!.Data);
        Assert.Equal(TestImages.Outline(25, 12), (await CatalogueImageStore.ForVariantLayoutAsync(db, "bigstadium_figure_8", Ct))!.Data);
        Assert.Null(await CatalogueImageStore.ForVariantLayoutAsync(db, "bigstadium_demolition_arena", Ct));
    }

    [Theory]
    [InlineData(0, 1.0)]
    [InlineData(255, 0.0)]
    public void ClearShare_CountsClearPixels(byte alpha, double expected) =>
        Assert.Equal(expected, PngTransparency.ClearShare(TestImages.RgbaPng(9, 5, (_, _) => alpha)));

    [Fact]
    public void ClearShare_ReadsEveryFilter_AndIsZeroForWhatItCannotRead()
    {
        // Each row a different filter: the share comes out the same as unfiltered.
        var share = PngTransparency.ClearShare(TestImages.Outline(40, 20));
        Assert.InRange(share, 0.6, 0.95);
        Assert.Equal(share, PngTransparency.ClearShare(TestImages.Outline(40, 20, filters: true)));

        Assert.Equal(0, PngTransparency.ClearShare(TestImages.Png(1)));
        Assert.Equal(0, PngTransparency.ClearShare(TestImages.Jpeg()));
        Assert.Equal(0, PngTransparency.ClearShare([]));
        var truncated = TestImages.Outline(40, 20);
        Assert.Equal(0, PngTransparency.ClearShare(truncated.AsSpan(0, truncated.Length - 30)));
    }

    [Fact]
    public async Task ATrackWithoutImages_ShowsItsFirstVariantsImage()
    {
        await ImportAsync(
            BaseGame().Images("base/bigstadium", null, null),
            Files(("base/event_bigstadium_figure_8_small.png", TestImages.Png(3))));

        await using var db = CreateContext();
        Assert.Equal(TestImages.Png(3), (await CatalogueImageStore.ForTrackAsync(db, "madman_stadium", ImageSize.Small, Ct))!.Data);
        Assert.Null(await CatalogueImageStore.ForTrackAsync(db, "madman_stadium", ImageSize.Large, Ct));
    }

    [Fact]
    public async Task DeletingAVariant_DeletesItsImages()
    {
        var dump = new TrackDumpFixture()
            .Workshop("2276098616", "The Very TrackPack 2")
            .Track("workshop:2276098616", "asan", "Asan Circuit", ("asan_main", "Main", 1, 3912.5))
            .Images("asan_main", "event_asan_main_small", null);
        await using (var db = CreateContext())
        {
            await TrackDumpImporter.ImportAsync(db, TrackDump.Read(dump.ToElement(), out _)!, Ct);
        }

        await ImportAsync(dump, Files(("workshop_2276098616/event_asan_main_small.png", TestImages.Png(1))));
        await using var check = CreateContext();
        await check.Tracks.Where(t => t.Key == "asan").ExecuteDeleteAsync(Ct);

        Assert.Equal(0, await check.CatalogueImages.CountAsync(Ct));
    }

    private async Task<TrackDumpImageReport> ImportAsync(TrackDumpFixture fixture, Dictionary<string, byte[]> files)
    {
        var dump = TrackDump.Read(fixture.ToElement(), out var refusal);
        Assert.Null(refusal);
        await using var db = CreateContext();
        return await TrackDumpImageImporter.ImportAsync(db, dump!, files, Now, Ct);
    }

    private async Task<int> TrackIdAsync(string key)
    {
        await using var db = CreateContext();
        return await db.Tracks.Where(t => t.Key == key).Select(t => t.Id).SingleAsync(Ct);
    }

    private ControllerDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ControllerDbContext>();
        ControllerDbContext.Configure(options, Path.Combine(_directory, "controller.db"));
        return new ControllerDbContext(options.Options);
    }
}

/// <summary>Image bytes for tests.</summary>
public static class TestImages
{
    /// <summary>Passes for a PNG by its signature, with a marker so two differ. Not a readable image.</summary>
    public static byte[] Png(byte marker) => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, marker];

    public static byte[] Jpeg() => [0xFF, 0xD8, 0xFF, 0xE0, 0, 16];

    /// <summary>A white ring on a clear background, like the game's variant images.</summary>
    public static byte[] Outline(int width, int height, bool filters = false) =>
        RgbaPng(width, height, (x, y) => x == 1 || y == 1 || x == width - 2 || y == height - 2 ? (byte)255 : (byte)0, filters);

    /// <summary>A real 8-bit RGBA PNG with the given alpha per pixel.</summary>
    /// <param name="filters">Filter each row with the next of PNG's five filters, instead of none.</param>
    public static byte[] RgbaPng(int width, int height, Func<int, int, byte> alpha, bool filters = false)
    {
        var stride = width * 4;
        var raw = new byte[(stride + 1) * height];
        var previous = new byte[stride];
        for (var y = 0; y < height; y++)
        {
            var line = new byte[stride];
            for (var x = 0; x < width; x++)
            {
                line[(x * 4) + 0] = 255;
                line[(x * 4) + 1] = 255;
                line[(x * 4) + 2] = (byte)(x * 7);
                line[(x * 4) + 3] = alpha(x, y);
            }

            var filter = filters ? (byte)(y % 5) : (byte)0;
            raw[y * (stride + 1)] = filter;
            for (var i = 0; i < stride; i++)
            {
                int left = i >= 4 ? line[i - 4] : 0, up = previous[i], upLeft = i >= 4 ? previous[i - 4] : 0;
                var predicted = filter switch
                {
                    1 => left,
                    2 => up,
                    3 => (left + up) / 2,
                    4 => Paeth(left, up, upLeft),
                    _ => 0,
                };
                raw[(y * (stride + 1)) + 1 + i] = (byte)(line[i] - predicted);
            }

            previous = line;
        }

        using var compressed = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = 6;
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", compressed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();

        static int Paeth(int a, int b, int c)
        {
            var p = a + b - c;
            int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }
    }

    /// <summary>A chunk with a zero CRC: the reader under test does not check CRCs.</summary>
    private static void Chunk(Stream png, string type, byte[] data)
    {
        var length = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        png.Write(length);
        png.Write(System.Text.Encoding.ASCII.GetBytes(type));
        png.Write(data);
        png.Write(new byte[4]);
    }
}
