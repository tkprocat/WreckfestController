using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WreckfestController.Data;
using WreckfestController.Data.Catalogue;

namespace WreckfestController.Tests.Data;

/// <summary>The catalogue the InitialCatalogue migration ships, and the versioning of its rows.</summary>
public sealed class CatalogueDataTests : IAsyncLifetime
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

    [Fact]
    public async Task Migration_ShipsTheWholeCatalogue_AsBuiltIn()
    {
        await using var db = CreateContext();

        var tracks = await db.Tracks.Include(t => t.Variants).Include(t => t.WeatherConditions).ToListAsync(Ct);
        var variants = tracks.SelectMany(t => t.Variants).ToList();

        Assert.Equal(InitialCatalogueData.Tracks.Count, tracks.Count);
        Assert.Equal(InitialCatalogueData.Tracks.Sum(t => t.Variants.Count), variants.Count);
        Assert.All(tracks, t => Assert.True(t.IsBuiltIn && !t.IsHidden && t.WeatherConditions.Count > 0, t.Key));
        Assert.All(variants, v => Assert.True(v.IsBuiltIn && !v.IsHidden, v.VariantId));
        Assert.Equal(5, await db.WeatherConditions.CountAsync(Ct));
        Assert.Equal(InitialCatalogueData.Tags.Count, await db.Tags.CountAsync(Ct));
    }

    [Fact]
    public void ShippedData_HasUniqueIds_AndOnlyKnownTagsAndWeather()
    {
        var variantIds = InitialCatalogueData.Tracks.SelectMany(t => t.Variants).Select(v => v.VariantId).ToList();
        var keys = InitialCatalogueData.Tracks.Select(t => t.Key).ToList();
        var slugs = InitialCatalogueData.Tags.Select(t => t.Slug).ToHashSet();

        Assert.Equal(variantIds.Count, variantIds.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(variantIds, id => Assert.Matches(TrackVariant.VariantIdPattern, id));
        Assert.All(InitialCatalogueData.Tracks.SelectMany(t => t.Variants).SelectMany(v => v.Tags),
            slug => Assert.Contains(slug, slugs));
        Assert.All(InitialCatalogueData.Tracks.SelectMany(t => t.Weather),
            weather => Assert.Contains(weather, InitialCatalogueData.Weather));
    }

    [Fact]
    public async Task EveryVoteTrackFrom1x_IsInTheCatalogue_AndAllowedForVoting()
    {
        // 1.x shipped these in appsettings.json; 2.0 votes from the catalogue instead.
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "Fixtures", "vote-tracks-1x.json")));
        var allowed = fixture.RootElement.GetProperty("ids").EnumerateArray().Select(id => id.GetString()!).ToList();
        Assert.Equal(284, allowed.Count);

        await using var db = CreateContext();
        var voting = await db.TrackVariants
            .Where(v => v.AllowedForVoting)
            .Select(v => v.VariantId)
            .ToListAsync(Ct);

        Assert.Empty(allowed.Except(voting, StringComparer.Ordinal));
    }

    [Fact]
    public async Task VariantIds_AreUniqueIgnoringCase()
    {
        await using var db = CreateContext();
        var track = await db.Tracks.FirstAsync(Ct);
        db.TrackVariants.Add(new TrackVariant
        {
            TrackId = track.Id,
            VariantId = "MISC_RBRACE",
            Name = "Clash",
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task Saving_BumpsTheVersion_AndAStaleVersionFails()
    {
        await using (var first = CreateContext())
        await using (var second = CreateContext())
        {
            var mine = await first.Tracks.SingleAsync(t => t.Key == "madman_stadium", Ct);
            var theirs = await second.Tracks.SingleAsync(t => t.Key == "madman_stadium", Ct);

            theirs.Name = "Their name";
            await second.SaveChangesAsync(Ct);
            Assert.Equal(2, theirs.Version);

            mine.Name = "My name";
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => first.SaveChangesAsync(Ct));
        }

        await using var check = CreateContext();
        var saved = await check.Tracks.SingleAsync(t => t.Key == "madman_stadium", Ct);
        Assert.Equal("Their name", saved.Name);
        Assert.Equal(2, saved.Version);
    }

    [Fact]
    public async Task SavingWithoutChanges_KeepsTheVersion()
    {
        await using var db = CreateContext();
        var track = await db.Tracks.SingleAsync(t => t.Key == "madman_stadium", Ct);

        track.Name = track.Name;
        await db.SaveChangesAsync(Ct);

        Assert.Equal(1, track.Version);
    }

    [Fact]
    public void BuiltInCatalogue_FindsEveryShippedRow_IgnoringCase()
    {
        foreach (var track in InitialCatalogueData.Tracks)
        {
            Assert.Same(track, BuiltInCatalogue.FindTrack(track.Key.ToUpperInvariant()));
            foreach (var variant in track.Variants)
            {
                Assert.Same(variant, BuiltInCatalogue.FindVariant(variant.VariantId));
            }
        }

        Assert.Null(BuiltInCatalogue.FindVariant("not_a_track"));
    }

    private ControllerDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ControllerDbContext>();
        ControllerDbContext.Configure(options, Path.Combine(_directory, "controller.db"));
        return new ControllerDbContext(options.Options);
    }
}
