using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace WreckfestController.Tests.Api;

/// <summary>/api/catalogue/*: the track catalogue the migration ships, and admin edits to it.</summary>
public class CatalogueEndpointsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Anonymous_CannotReadTheCatalogue()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateClient();

        using var response = await client.GetAsync("/api/catalogue/tracks", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Tracks_FilterByOriginModeAndTag()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateAuthenticatedClient();

        var workshop = await GetArrayAsync(client, "/api/catalogue/tracks?origin=Workshop");
        Assert.Equal(11, workshop.Count);
        Assert.All(workshop, t => Assert.Null(t.GetProperty("mod").GetString()));

        var derby = await GetArrayAsync(client, "/api/catalogue/tracks?gameMode=Derby");
        Assert.Contains(derby, t => Key(t) == "madman_stadium");
        Assert.DoesNotContain(derby, t => Key(t) == "bonebreaker_valley");

        var ovals = await GetArrayAsync(client, "/api/catalogue/tracks?tag=OVAL");
        Assert.Contains(ovals, t => Key(t) == "bloomfield_speedway");
        Assert.DoesNotContain(ovals, t => Key(t) == "crash_canyon");

        var fog = await GetArrayAsync(client, "/api/catalogue/tracks?weather=fog");
        Assert.Contains(fog, t => Key(t) == "fairfield_county");
        Assert.DoesNotContain(fog, t => Key(t) == "madman_stadium");
    }

    [Fact]
    public async Task HiddenTrack_LeavesTheList_UnlessAskedFor()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateAuthenticatedClient();
        var id = await TrackIdAsync(client, "madman_stadium");

        using var hide = await client.PostAsync($"/api/catalogue/tracks/{id}/hide", null, Ct);
        hide.EnsureSuccessStatusCode();

        Assert.DoesNotContain(await GetArrayAsync(client, "/api/catalogue/tracks"), t => Key(t) == "madman_stadium");
        Assert.Contains(
            await GetArrayAsync(client, "/api/catalogue/tracks?includeHidden=true"),
            t => Key(t) == "madman_stadium");
        Assert.DoesNotContain(
            await GetArrayAsync(client, "/api/catalogue/variants"),
            v => v.GetProperty("variantId").GetString() == "bigstadium_figure_8");

        using var unhide = await client.PostAsync($"/api/catalogue/tracks/{id}/unhide", null, Ct);
        unhide.EnsureSuccessStatusCode();
        Assert.Contains(await GetArrayAsync(client, "/api/catalogue/tracks"), t => Key(t) == "madman_stadium");
    }

    [Fact]
    public async Task Update_NeedsIfMatch_AndRejectsAStaleVersion()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateAuthenticatedClient();
        var id = await TrackIdAsync(client, "madman_stadium");
        var body = new { key = "madman_stadium", name = "Madman Arena", origin = "BaseGame" };

        using var missing = await client.PutAsJsonAsync($"/api/catalogue/tracks/{id}", body, Ct);
        Assert.Equal(HttpStatusCode.PreconditionRequired, missing.StatusCode);

        using var first = await PutAsync(client, $"/api/catalogue/tracks/{id}", body, "\"1\"");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("\"2\"", first.Headers.ETag?.Tag);

        using var stale = await PutAsync(client, $"/api/catalogue/tracks/{id}", body with { name = "Lost" }, "\"1\"");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var current = await stale.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("Madman Arena", current.GetProperty("name").GetString());
        Assert.Equal(2, current.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task BuiltIn_KeepsItsKey_AndCannotBeDeleted()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateAuthenticatedClient();
        var id = await TrackIdAsync(client, "madman_stadium");

        using var rename = await PutAsync(
            client,
            $"/api/catalogue/tracks/{id}",
            new { key = "renamed", name = "Madman Stadium", origin = "BaseGame" },
            "\"1\"");
        await AuthEndpointsTests.AssertFieldErrorAsync(rename, "key");

        using var delete = await client.DeleteAsync($"/api/catalogue/tracks/{id}", Ct);
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
    }

    [Fact]
    public async Task AdminAddedTrackAndVariant_CanBeCreatedAndDeleted()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateAuthenticatedClient();

        using var mod = await client.PostAsJsonAsync(
            "/api/catalogue/mods", new { name = "Custom Pack", folderName = "123456", workshopId = "123456" }, Ct);
        Assert.Equal(HttpStatusCode.Created, mod.StatusCode);
        var modId = (await mod.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetInt32();

        using var track = await client.PostAsJsonAsync(
            "/api/catalogue/tracks",
            new { key = "custom_speedway", name = "Custom Speedway", origin = "Workshop", modId },
            Ct);
        Assert.Equal(HttpStatusCode.Created, track.StatusCode);
        var created = await track.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var trackId = created.GetProperty("id").GetInt32();
        Assert.False(created.GetProperty("isBuiltIn").GetBoolean());
        Assert.Equal(5, created.GetProperty("weather").GetArrayLength());

        using var variant = await client.PostAsJsonAsync(
            "/api/catalogue/variants",
            new { trackId, variantId = "custom_oval", name = "Oval", gameMode = "Racing", allowedForVoting = true },
            Ct);
        Assert.Equal(HttpStatusCode.Created, variant.StatusCode);

        using var duplicate = await client.PostAsJsonAsync(
            "/api/catalogue/variants",
            new { trackId, variantId = "CUSTOM_OVAL", name = "Again", gameMode = "Racing" },
            Ct);
        await AuthEndpointsTests.AssertFieldErrorAsync(duplicate, "variantId");

        using var modInUse = await client.DeleteAsync($"/api/catalogue/mods/{modId}", Ct);
        Assert.Equal(HttpStatusCode.Conflict, modInUse.StatusCode);

        using var delete = await client.DeleteAsync($"/api/catalogue/tracks/{trackId}", Ct);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Empty(await GetArrayAsync(client, "/api/catalogue/variants?search=custom_oval"));

        using var modFree = await client.DeleteAsync($"/api/catalogue/mods/{modId}", Ct);
        Assert.Equal(HttpStatusCode.NoContent, modFree.StatusCode);
    }

    [Fact]
    public async Task Track_RejectsAModOrDlcNameThatDoesNotFitItsOrigin()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateAuthenticatedClient();

        using var dlc = await client.PostAsJsonAsync(
            "/api/catalogue/tracks", new { key = "a_track", name = "A", origin = "BaseGame", dlcName = "Pack" }, Ct);
        await AuthEndpointsTests.AssertFieldErrorAsync(dlc, "dlcName");

        using var mod = await client.PostAsJsonAsync(
            "/api/catalogue/tracks", new { key = "a_track", name = "A", origin = "Workshop", modId = 999 }, Ct);
        await AuthEndpointsTests.AssertFieldErrorAsync(mod, "modId");
    }

    [Fact]
    public async Task VariantSearch_IgnoresCase_AndTreatsUnderscoreLiterally()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateAuthenticatedClient();

        var found = await GetArrayAsync(client, "/api/catalogue/variants?search=RBRACE");
        Assert.Equal("misc_rbRace", Assert.Single(found).GetProperty("variantId").GetString());

        // "_" is a LIKE wildcard: unescaped, "t1_" would also match "forest11_1".
        var literal = await GetArrayAsync(client, "/api/catalogue/variants?search=t1_");
        Assert.All(literal, v => Assert.Contains("t1_", v.GetProperty("variantId").GetString()));
        Assert.NotEmpty(literal);
    }

    [Fact]
    public async Task ResetVariant_RestoresOnlyThatRow_AndRecreatesADeletedShippedTag()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateAuthenticatedClient();
        var target = await VariantAsync(client, "bigstadium_figure_8");
        var other = await VariantAsync(client, "bigstadium_demolition_arena");

        // Edit both, and delete a tag the target shipped with.
        foreach (var (variant, name) in new[] { (target, "Edited"), (other, "Also edited") })
        {
            using var edit = await PutAsync(
                client,
                $"/api/catalogue/variants/{variant.GetProperty("id").GetInt32()}",
                new { variantId = variant.GetProperty("variantId").GetString(), name, gameMode = "Derby" },
                $"\"{variant.GetProperty("version").GetInt32()}\"");
            edit.EnsureSuccessStatusCode();
        }

        var tags = await GetArrayAsync(client, "/api/catalogue/tags");
        var figure8Tag = tags.Single(t => t.GetProperty("slug").GetString() == "figure-8");
        using var deleteTag = await client.DeleteAsync($"/api/catalogue/tags/{figure8Tag.GetProperty("id").GetInt32()}", Ct);
        Assert.Equal(HttpStatusCode.NoContent, deleteTag.StatusCode);

        using var reset = await client.PostAsync($"/api/catalogue/variants/{target.GetProperty("id").GetInt32()}/reset", null, Ct);
        reset.EnsureSuccessStatusCode();

        var restored = await VariantAsync(client, "bigstadium_figure_8");
        Assert.Equal("Figure 8", restored.GetProperty("name").GetString());
        Assert.Equal("Racing", restored.GetProperty("gameMode").GetString());
        Assert.Contains(restored.GetProperty("tags").EnumerateArray(), t => t.GetProperty("slug").GetString() == "figure-8");
        Assert.Equal("Also edited", (await VariantAsync(client, "bigstadium_demolition_arena")).GetProperty("name").GetString());
    }

    [Fact]
    public async Task ResetTrack_RestoresNameAndWeather()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateAuthenticatedClient();
        var id = await TrackIdAsync(client, "madman_stadium");

        using var weather = await client.PutAsJsonAsync(
            $"/api/catalogue/tracks/{id}/weather", new { weather = new[] { "storm" } }, Ct);
        weather.EnsureSuccessStatusCode();
        using var badWeather = await client.PutAsJsonAsync(
            $"/api/catalogue/tracks/{id}/weather", new { weather = new[] { "snow" } }, Ct);
        await AuthEndpointsTests.AssertFieldErrorAsync(badWeather, "weather");

        using var reset = await client.PostAsync($"/api/catalogue/tracks/{id}/reset", null, Ct);
        var track = await reset.Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.Equal(
            new[] { "clear", "overcast" },
            track.GetProperty("weather").EnumerateArray().Select(w => w.GetString()).ToArray());
    }

    [Fact]
    public async Task VotingAndTags_CanBeSetOnAVariant()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateAuthenticatedClient();
        var id = (await VariantAsync(client, "bigstadium_figure_8")).GetProperty("id").GetInt32();

        using var voting = await client.PutAsJsonAsync($"/api/catalogue/variants/{id}/voting", new { allowed = false }, Ct);
        voting.EnsureSuccessStatusCode();
        Assert.DoesNotContain(
            await GetArrayAsync(client, "/api/catalogue/variants?votingOnly=true"),
            v => v.GetProperty("id").GetInt32() == id);

        using var tags = await client.PutAsJsonAsync($"/api/catalogue/variants/{id}/tags", new { tags = new[] { "jump" } }, Ct);
        var variant = await tags.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("jump", Assert.Single(variant.GetProperty("tags").EnumerateArray()).GetProperty("slug").GetString());

        using var unknown = await client.PutAsJsonAsync($"/api/catalogue/variants/{id}/tags", new { tags = new[] { "nope" } }, Ct);
        await AuthEndpointsTests.AssertFieldErrorAsync(unknown, "tags");
    }

    // Weather and tags are the track's and variant's own data: changing them moves the
    // version, and a writer that sends If-Match loses to a save made since it read.
    [Fact]
    public async Task WeatherAndTags_MoveTheVersion_AndHonourIfMatch()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateAuthenticatedClient();
        var trackId = await TrackIdAsync(client, "madman_stadium");
        var track = await client.GetFromJsonAsync<JsonElement>($"/api/catalogue/tracks/{trackId}", Ct);
        var trackVersion = track.GetProperty("version").GetInt32();

        using var weather = await PutAsync(client, $"/api/catalogue/tracks/{trackId}/weather", new { weather = new[] { "storm" } }, $"\"{trackVersion}\"");
        var changed = await weather.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(trackVersion + 1, changed.GetProperty("version").GetInt32());

        using var staleWeather = await PutAsync(client, $"/api/catalogue/tracks/{trackId}/weather", new { weather = new[] { "fog" } }, $"\"{trackVersion}\"");
        Assert.Equal(HttpStatusCode.Conflict, staleWeather.StatusCode);
        var current = await staleWeather.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(new[] { "storm" }, current.GetProperty("weather").EnumerateArray().Select(w => w.GetString()).ToArray());

        var variant = await VariantAsync(client, "bigstadium_figure_8");
        var variantId = variant.GetProperty("id").GetInt32();
        var variantVersion = variant.GetProperty("version").GetInt32();

        using var tags = await PutAsync(client, $"/api/catalogue/variants/{variantId}/tags", new { tags = new[] { "jump" } }, $"\"{variantVersion}\"");
        Assert.Equal(variantVersion + 1, (await tags.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("version").GetInt32());

        using var staleTags = await PutAsync(client, $"/api/catalogue/variants/{variantId}/tags", new { tags = new[] { "oval" } }, $"\"{variantVersion}\"");
        Assert.Equal(HttpStatusCode.Conflict, staleTags.StatusCode);
    }

    [Fact]
    public async Task Tags_RejectADuplicateSlug()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateAuthenticatedClient();

        using var duplicate = await client.PostAsJsonAsync(
            "/api/catalogue/tags", new { name = "Oval again", slug = "oval" }, Ct);

        await AuthEndpointsTests.AssertFieldErrorAsync(duplicate, "slug");
    }

    [Fact]
    public async Task AvailableOnly_KeepsTracksFromActiveModsOnly()
    {
        var directory = ApiTestHost.NewDataDirectory();
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "server_config.cfg"), "mods=active_mod\n", Ct);
        try
        {
            await using var host = await ApiTestHost.StartAsync(
                new Dictionary<string, string?>
                {
                    ["WreckfestServer:WorkingDirectory"] = directory,
                    ["WreckfestServer:ServerArguments"] = "--server-set server_config=server_config.cfg",
                },
                dataDirectory: directory);
            using var client = host.CreateAuthenticatedClient();

            foreach (var (folder, key) in new[] { ("active_mod", "active_track"), ("inactive_mod", "inactive_track") })
            {
                using var mod = await client.PostAsJsonAsync("/api/catalogue/mods", new { name = folder, folderName = folder }, Ct);
                var modId = (await mod.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetInt32();
                using var track = await client.PostAsJsonAsync(
                    "/api/catalogue/tracks", new { key, name = key, origin = "Workshop", modId }, Ct);
                track.EnsureSuccessStatusCode();
            }

            var available = await GetArrayAsync(client, "/api/catalogue/tracks?availableOnly=true");

            Assert.Contains(available, t => Key(t) == "active_track");
            Assert.Contains(available, t => Key(t) == "madman_stadium");
            Assert.DoesNotContain(available, t => Key(t) == "inactive_track");
        }
        finally
        {
            ApiTestHost.DeleteDataDirectory(directory);
        }
    }

    [Fact]
    public async Task Weather_ListsTheGamesNames()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateAuthenticatedClient();

        var weather = await client.GetFromJsonAsync<string[]>("/api/catalogue/weather", Ct);

        Assert.Equal(new[] { "clear", "overcast", "fog", "rain", "storm" }, weather);
    }

    private static string? Key(JsonElement track) => track.GetProperty("key").GetString();

    private static async Task<List<JsonElement>> GetArrayAsync(HttpClient client, string url)
    {
        var json = await client.GetFromJsonAsync<JsonElement>(url, Ct);
        return json.EnumerateArray().ToList();
    }

    private static async Task<int> TrackIdAsync(HttpClient client, string key)
    {
        var tracks = await GetArrayAsync(client, "/api/catalogue/tracks?includeHidden=true");
        return tracks.Single(t => Key(t) == key).GetProperty("id").GetInt32();
    }

    private static async Task<JsonElement> VariantAsync(HttpClient client, string variantId)
    {
        var variants = await GetArrayAsync(client, $"/api/catalogue/variants?includeHidden=true&search={variantId}");
        return variants.Single(v => v.GetProperty("variantId").GetString() == variantId);
    }

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, string url, object body, string ifMatch)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = JsonContent.Create(body) };
        request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return client.SendAsync(request, Ct);
    }
}
