using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WreckfestController.Data;
using WreckfestController.Data.Catalogue;
using WreckfestController.Data.Cups;
using WreckfestController.Data.Races;
using WreckfestController.Models;
using WreckfestController.Services.Catalogue;
using WreckfestController.Services.Hook;
using WreckfestController.Tests.Services.Catalogue;

namespace WreckfestController.Tests.Api;

/// <summary>#261: the public track pages' data, for anyone, without hidden rows or Steam ids.</summary>
public class PublicTrackEndpointsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTime Day = new(2026, 10, 10, 18, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Tracks_ListsWhatIsNotHidden_ForAnyone()
    {
        await using var host = await ApiTestHost.StartAsync();
        await WithDbAsync(host, async db =>
        {
            (await db.Tracks.SingleAsync(t => t.Key == "fairfield_county", Ct)).IsHidden = true;
            (await db.TrackVariants.SingleAsync(v => v.VariantId == "bigstadium_demolition_arena", Ct)).IsHidden = true;
            await db.SaveChangesAsync(Ct);
        });
        using var client = host.CreateClient();

        var tracks = await client.GetFromJsonAsync<JsonElement>("/api/public/tracks", Ct);

        var keys = tracks.EnumerateArray().Select(t => t.GetProperty("key").GetString()).ToList();
        Assert.Contains("madman_stadium", keys);
        Assert.DoesNotContain("fairfield_county", keys);
        var madman = tracks.EnumerateArray().Single(t => t.GetProperty("key").GetString() == "madman_stadium");
        Assert.Equal(["bigstadium_figure_8"], madman.GetProperty("variants").EnumerateArray().Select(v => v.GetProperty("variantId").GetString()));

        // No admin fields.
        var text = tracks.GetRawText();
        foreach (var field in new[] { "allowedForVoting", "isBuiltIn", "isHidden", "tags", "\"id\"", "version" })
        {
            Assert.DoesNotContain(field, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Track_IsFoundByKey_AndAHiddenOneIs404()
    {
        await using var host = await ApiTestHost.StartAsync();
        await WithDbAsync(host, async db =>
        {
            (await db.Tracks.SingleAsync(t => t.Key == "fairfield_county", Ct)).IsHidden = true;
            await db.SaveChangesAsync(Ct);
        });
        await AddRaceAsync(host, "BIGSTADIUM_FIGURE_8", Day, ("Alice", 1001, 1, 60_000, 19_000));
        using var client = host.CreateClient();

        var track = await client.GetFromJsonAsync<JsonElement>("/api/public/tracks/MADMAN_STADIUM", Ct);
        using var hidden = await client.GetAsync("/api/public/tracks/fairfield_county", Ct);
        using var unknown = await client.GetAsync("/api/public/tracks/nowhere", Ct);

        Assert.Equal("madman_stadium", track.GetProperty("key").GetString());
        var figure8 = track.GetProperty("variants").EnumerateArray().Single(v => v.GetProperty("variantId").GetString() == "bigstadium_figure_8");
        Assert.Equal(1, figure8.GetProperty("raceCount").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Track_GivesTheHashOfEachImageItsLinksServe()
    {
        await using var host = await ApiTestHost.StartAsync();
        var picture = TestImages.RgbaPng(10, 5, (_, _) => 255);
        var outline = TestImages.Outline(40, 20);
        await WithDbAsync(host, async db =>
        {
            var track = await db.Tracks.SingleAsync(t => t.Key == "madman_stadium", Ct);
            var variant = await db.TrackVariants.SingleAsync(v => v.VariantId == "bigstadium_figure_8", Ct);
            await CatalogueImageStore.PutAsync(db, ImageOwner.Track(track.Id), ImageSize.Large, ImageOrigin.Import, picture, CatalogueImageStore.Png, Day, Ct);
            await CatalogueImageStore.PutAsync(db, ImageOwner.Variant(variant.Id), ImageSize.Small, ImageOrigin.Import, outline, CatalogueImageStore.Png, Day, Ct);
            await db.SaveChangesAsync(Ct);
        });
        using var client = host.CreateClient();

        var track = await client.GetFromJsonAsync<JsonElement>("/api/public/tracks/madman_stadium", Ct);

        var hash = CatalogueImageStore.Hash(picture);
        var images = track.GetProperty("images");
        Assert.Equal(hash, images.GetProperty("small").GetString());
        Assert.Equal(hash, images.GetProperty("large").GetString());
        var variantImages = track.GetProperty("variants").EnumerateArray()
            .Single(v => v.GetProperty("variantId").GetString() == "bigstadium_figure_8").GetProperty("images");
        Assert.Equal(hash, variantImages.GetProperty("small").GetString());
        Assert.Equal(CatalogueImageStore.Hash(outline), variantImages.GetProperty("layout").GetString());

        // The link with that hash answers with that image, cached for good.
        using var served = await client.GetAsync($"/api/images/variants/bigstadium_figure_8/small?v={hash}", Ct);
        Assert.Equal(picture, await served.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal(TimeSpan.FromDays(365), served.Headers.CacheControl?.MaxAge);

        var arena = track.GetProperty("variants").EnumerateArray()
            .Single(v => v.GetProperty("variantId").GetString() == "bigstadium_demolition_arena").GetProperty("images");
        Assert.Equal(JsonValueKind.Null, arena.GetProperty("layout").ValueKind);
    }

    [Fact]
    public async Task Variant_HasItsRaces_RecordsAndATypicalLap_WithoutSteamIds()
    {
        await using var host = await ApiTestHost.StartAsync();
        // Alice wins twice, once as "Alice (old name)"; Bob once with the fastest lap; bots set no records.
        await AddRaceAsync(host, "bigstadium_figure_8", Day, ("Alice (old name)", 1001, 1, 90_000, 29_000), ("Bob", 1002, 2, 93_000, 28_500), ("Bot Ann", null, 3, 80_000, 20_000));
        await AddRaceAsync(host, "bigstadium_figure_8", Day.AddHours(1), ("Alice", 1001, 1, 60_000, 19_500), ("Bob", 1002, 2, 66_000, 21_000));
        await AddRaceAsync(host, "BIGSTADIUM_FIGURE_8", Day.AddHours(2), ("Bob", 1002, 1, 63_000, 19_000));
        await AddRaceAsync(host, "speedway2_figure_8", Day, ("Carol", 1003, 1, 50_000, 15_000));
        using var client = host.CreateClient();

        var detail = await client.GetFromJsonAsync<JsonElement>("/api/public/variants/bigstadium_figure_8", Ct);

        Assert.Equal(3, detail.GetProperty("raceCount").GetInt32());
        Assert.Equal(3, detail.GetProperty("recentRaces").GetArrayLength());
        Assert.Equal("Madman Stadium - Figure 8", detail.GetProperty("recentRaces")[0].GetProperty("track").GetProperty("name").GetString());
        Assert.Equal(
            ["Bob 19000", "Alice 19500"],
            detail.GetProperty("fastestLaps").EnumerateArray().Select(r => $"{r.GetProperty("name").GetString()} {r.GetProperty("lapMs").GetInt32()}"));
        Assert.Equal(
            ["Alice 2/2", "Bob 1/3"],
            detail.GetProperty("mostWins").EnumerateArray().Select(r => $"{r.GetProperty("name").GetString()} {r.GetProperty("wins").GetInt32()}/{r.GetProperty("races").GetInt32()}"));

        // Every finish's time over its 3 laps, the bot's too: 20 000, 21 000, 22 000, 26 667,
        // 30 000, 31 000. The median of an even count is the mean of the middle two.
        Assert.Equal(24_333, detail.GetProperty("typicalLapMs").GetInt32());
        Assert.Equal(6, detail.GetProperty("typicalLapFinishes").GetInt32());

        Assert.DoesNotContain("1001", detail.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("steam", detail.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Variant_ListsTheCupsThatRunIt_AndAHiddenOneIs404()
    {
        await using var host = await ApiTestHost.StartAsync();
        await WithDbAsync(host, async db =>
        {
            db.Cups.Add(new Cup { Name = "Figure 8 Friday", StartTime = Day, NextOccurrence = Day.AddDays(1), Tracks = [new EventLoopTrack { Track = "BigStadium_Figure_8" }] });
            db.Cups.Add(new Cup { Name = "Ovals", StartTime = Day, Tracks = [new EventLoopTrack { Track = "speedway2_figure_8" }] });
            (await db.TrackVariants.SingleAsync(v => v.VariantId == "bigstadium_demolition_arena", Ct)).IsHidden = true;
            await db.SaveChangesAsync(Ct);
        });
        using var client = host.CreateClient();

        var detail = await client.GetFromJsonAsync<JsonElement>("/api/public/variants/bigstadium_figure_8", Ct);
        using var hidden = await client.GetAsync("/api/public/variants/bigstadium_demolition_arena", Ct);

        var cup = Assert.Single(detail.GetProperty("cups").EnumerateArray());
        Assert.Equal("Figure 8 Friday", cup.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("typicalLapMs").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
    }

    [Fact]
    public async Task Races_LinkToTheTrackPage_UnlessItIsHidden()
    {
        await using var host = await ApiTestHost.StartAsync();
        await AddRaceAsync(host, "bigstadium_figure_8", Day, ("Alice", 1001, 1, 60_000, 19_000));
        await AddRaceAsync(host, "speedway2_figure_8", Day.AddHours(1), ("Alice", 1001, 1, 60_000, 19_000));
        await AddRaceAsync(host, "not_in_the_catalogue", Day.AddHours(2), ("Alice", 1001, 1, 60_000, 19_000));
        await WithDbAsync(host, async db =>
        {
            (await db.TrackVariants.SingleAsync(v => v.VariantId == "speedway2_figure_8", Ct)).IsHidden = true;
            await db.SaveChangesAsync(Ct);
        });
        using var client = host.CreateClient();

        var races = await client.GetFromJsonAsync<JsonElement>("/api/public/races", Ct);

        Assert.Equal(
            ["not_in_the_catalogue -", "speedway2_figure_8 -", "bigstadium_figure_8 madman_stadium"],
            races.EnumerateArray().Select(r => r.GetProperty("track")).Select(t => $"{t.GetProperty("id").GetString()} {(t.GetProperty("trackKey").ValueKind == JsonValueKind.Null ? "-" : t.GetProperty("trackKey").GetString())}"));
    }

    [Fact]
    public async Task ARaceWithoutASteamId_CountsForThePlayerWhoUsedThatName()
    {
        await using var host = await ApiTestHost.StartAsync();
        // Alice's second win lost its Steam id; "Sam" is used by two Steam ids, so a
        // name-only Sam stays apart.
        await AddRaceAsync(host, "bigstadium_figure_8", Day, ("Alice", 1001, 1, 60_000, 19_000), ("Sam", 2001, 2, 61_000, 20_000));
        await AddRaceAsync(host, "bigstadium_figure_8", Day.AddHours(1), ("Alice", NoSteamId, 1, 60_000, 19_500), ("Sam", 2002, 2, 61_000, 20_000));
        await AddRaceAsync(host, "bigstadium_figure_8", Day.AddHours(2), ("Sam", NoSteamId, 1, 60_000, 18_000));
        using var client = host.CreateClient();

        var detail = await client.GetFromJsonAsync<JsonElement>("/api/public/variants/bigstadium_figure_8", Ct);

        Assert.Equal(
            ["Alice 2/2", "Sam 1/1"],
            detail.GetProperty("mostWins").EnumerateArray().Select(r => $"{r.GetProperty("name").GetString()} {r.GetProperty("wins").GetInt32()}/{r.GetProperty("races").GetInt32()}"));
        var laps = detail.GetProperty("fastestLaps").EnumerateArray().Select(r => $"{r.GetProperty("name").GetString()} {r.GetProperty("lapMs").GetInt32()} {r.GetProperty("vehicleName").GetString()}").ToList();
        Assert.Equal(["Sam 18000 Roadslayer", "Alice 19000 Roadslayer", "Sam 20000 Roadslayer", "Sam 20000 Roadslayer"], laps);
    }

    // The all-time standings join players the same way, over every layout, and leave bots out.
    [Fact]
    public async Task Players_RanksEveryLayoutsRaces_ByWinsThenPodiums_WithoutSteamIdsOrBots()
    {
        await using var host = await ApiTestHost.StartAsync();
        // Alice raced as "Ally" last; one of her wins lost its Steam id. Bob has as many
        // wins as Cat but more podiums. The bot wins the last race and is still left out.
        await AddRaceAsync(host, "bigstadium_figure_8", Day, ("Ally", 1001, 1, 60_000, 19_000), ("Bob", 1002, 2, 61_000, 20_000), ("Cat", NoSteamId, 4, 62_000, 21_000));
        await AddRaceAsync(host, "bigstadium_demolition_arena", Day.AddHours(1), ("Bob", 1002, 1, 60_000, 19_000), ("Ally", NoSteamId, 3, 61_000, 20_000));
        await AddRaceAsync(host, "fields14_1", Day.AddHours(2), ("Cat", NoSteamId, 1, 60_000, 19_000), ("Alice", 1001, 5, 61_000, 20_000));
        await AddRaceAsync(host, "fields14_1", Day.AddHours(3), ("Bot Carl", null, 1, 60_000, 19_000), ("Alice", NoSteamId, 2, 61_000, 20_000));
        using var client = host.CreateClient();

        var players = await client.GetFromJsonAsync<JsonElement>("/api/public/players", Ct);

        Assert.Equal(
            ["Alice 1/3/4", "Bob 1/2/2", "Cat 1/1/2"],
            players.EnumerateArray().Select(p => $"{p.GetProperty("name").GetString()} {p.GetProperty("wins").GetInt32()}/{p.GetProperty("podiums").GetInt32()}/{p.GetProperty("races").GetInt32()}"));
        Assert.Equal(Day.AddHours(3), players[0].GetProperty("lastRaced").GetDateTime());
        Assert.DoesNotContain("1001", players.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("steamId", players.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALinkedCollectionEntry_CountsForItsVariant_NotForTheIdItWasSavedWith()
    {
        await using var host = await ApiTestHost.StartAsync();
        await WithDbAsync(host, async db =>
        {
            var figure8 = await db.TrackVariants.SingleAsync(v => v.VariantId == "bigstadium_figure_8", Ct);
            var collection = new WreckfestController.Data.Collections.TrackCollection { Name = "Renamed", Entries = [new() { Position = 0, TrackId = "speedway2_figure_8", TrackVariantId = figure8.Id }] };
            db.TrackCollections.Add(collection);
            await db.SaveChangesAsync(Ct);
            db.Cups.Add(new Cup { Name = "Linked", StartTime = Day, CollectionId = collection.Id });
            await db.SaveChangesAsync(Ct);
        });
        using var client = host.CreateClient();

        var figure8 = await client.GetFromJsonAsync<JsonElement>("/api/public/variants/bigstadium_figure_8", Ct);
        var speedway = await client.GetFromJsonAsync<JsonElement>("/api/public/variants/speedway2_figure_8", Ct);

        Assert.Equal("Linked", Assert.Single(figure8.GetProperty("cups").EnumerateArray()).GetProperty("name").GetString());
        Assert.Empty(speedway.GetProperty("cups").EnumerateArray());
    }

    [Fact]
    public async Task ALayoutsRaces_AreFoundThroughTheNoCaseIndex()
    {
        await using var host = await ApiTestHost.StartAsync();
        await WithDbAsync(host, async db =>
        {
            var sql = db.Races.Where(r => EF.Functions.Collate(r.TrackId, "NOCASE") == "x").ToQueryString();
            Assert.Contains("COLLATE NOCASE", sql, StringComparison.Ordinal);

            var connection = db.Database.GetDbConnection();
            await connection.OpenAsync(Ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "EXPLAIN QUERY PLAN SELECT COUNT(*) FROM \"Races\" AS \"r\" WHERE \"r\".\"TrackId\" COLLATE NOCASE = 'x'";
            await using var reader = await command.ExecuteReaderAsync(Ct);
            var plan = new List<string>();
            while (await reader.ReadAsync(Ct))
            {
                plan.Add(reader.GetString(3));
            }

            Assert.Contains(plan, line => line.Contains("IX_Races_TrackId_NoCase", StringComparison.Ordinal));
        });
    }

    private const long NoSteamId = -1;

    private static async Task AddRaceAsync(ApiTestHost host, string trackId, DateTime endedAt, params (string Name, long? SteamId, int Position, int TimeMs, int BestLapMs)[] cars) =>
        await WithDbAsync(host, async db =>
        {
            db.Races.Add(new Race
            {
                TrackId = trackId,
                EndedAt = endedAt,
                Laps = 3,
                Entries = cars.Select(c => new RaceEntry
                {
                    Name = c.Name,
                    SteamId = c.SteamId == NoSteamId ? null : c.SteamId,
                    IsBot = c.SteamId is null,
                    Position = c.Position,
                    Outcome = RaceOutcome.Finished,
                    TimeMs = c.TimeMs,
                    BestLapMs = c.BestLapMs,
                    VehicleName = "Roadslayer",
                }).ToList(),
            });
            await db.SaveChangesAsync(Ct);
        });

    private static async Task WithDbAsync(ApiTestHost host, Func<ControllerDbContext, Task> action)
    {
        var contexts = host.MainServices.GetRequiredService<IDbContextFactory<ControllerDbContext>>();
        await using var db = await contexts.CreateDbContextAsync(Ct);
        await action(db);
    }
}
