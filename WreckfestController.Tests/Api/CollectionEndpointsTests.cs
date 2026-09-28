using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace WreckfestController.Tests.Api;

/// <summary>/api/collections: named rotations, their links to the catalogue, and deploying one.</summary>
public class CollectionEndpointsTests
{
    private const string UnknownTrack = "workshop_not_in_catalogue";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Anonymous_CannotReadCollections()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateClient();

        using var response = await client.GetAsync("/api/collections", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Create_LinksKnownTracks_AndKeepsUnknownOnes()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateAuthenticatedClient();

        using var created = await client.PostAsJsonAsync(
            "/api/collections",
            new
            {
                name = "Derby Night",
                tracks = new object[]
                {
                    new { track = "BIGSTADIUM_FIGURE_8", gamemode = "racing", laps = 5, carResetDisabled = 1 },
                    new { track = UnknownTrack, gamemode = "derby", bots = 4 },
                },
            },
            Ct);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("\"1\"", created.Headers.ETag?.Tag);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var tracks = body.GetProperty("tracks").EnumerateArray().ToList();
        Assert.Equal(2, tracks.Count);

        // A linked entry takes the catalogue's spelling of the id.
        Assert.Equal("bigstadium_figure_8", tracks[0].GetProperty("track").GetString());
        Assert.Equal("Madman Stadium", tracks[0].GetProperty("variant").GetProperty("trackName").GetString());
        Assert.Equal(1, tracks[0].GetProperty("carResetDisabled").GetInt32());
        Assert.Equal(5, tracks[0].GetProperty("laps").GetInt32());

        Assert.Equal(UnknownTrack, tracks[1].GetProperty("track").GetString());
        Assert.Equal(JsonValueKind.Null, tracks[1].GetProperty("variant").ValueKind);
        Assert.Equal(JsonValueKind.Null, tracks[1].GetProperty("carResetDisabled").ValueKind);

        var list = await GetArrayAsync(client, "/api/collections");
        var summary = Assert.Single(list);
        Assert.Equal("Derby Night", summary.GetProperty("name").GetString());
        Assert.Equal(2, summary.GetProperty("trackCount").GetInt32());
    }

    [Fact]
    public async Task Names_AreUniqueIgnoringCase()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateAuthenticatedClient();
        await CreateAsync(client, "Ovals", "speedway2_figure_8");

        using var duplicate = await client.PostAsJsonAsync(
            "/api/collections", new { name = " OVALS ", tracks = Array.Empty<object>() }, Ct);

        await AuthEndpointsTests.AssertFieldErrorAsync(duplicate, "name");
    }

    public static TheoryData<object, string> InvalidTracks => new()
    {
        { new { track = "bad id" }, "tracks[0].track" },
        { new { track = "" }, "tracks[0].track" },
        { new { track = "bigstadium_figure_8", weather = "rain\nel_laps=99" }, "tracks[0]" },
        { new { track = "bigstadium_figure_8", carResetDisabled = 2 }, "tracks[0]" },
        { new { track = "bigstadium_figure_8", laps = -1 }, "tracks[0]" },
    };

    [Theory]
    [MemberData(nameof(InvalidTracks))]
    public async Task Create_RejectsATrackTheServerCouldNotLoad(object track, string field)
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateAuthenticatedClient();

        using var response = await client.PostAsJsonAsync(
            "/api/collections", new { name = "Broken", tracks = new[] { track } }, Ct);

        await AuthEndpointsTests.AssertFieldErrorAsync(response, field);
        Assert.Empty(await GetArrayAsync(client, "/api/collections"));
    }

    [Fact]
    public async Task Update_NeedsIfMatch_ReordersAndRejectsAStaleVersion()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateAuthenticatedClient();
        var id = await CreateAsync(client, "Rotation", "speedway2_figure_8", "bigstadium_figure_8", UnknownTrack);
        var reordered = new
        {
            name = "Rotation",
            tracks = new[] { new { track = UnknownTrack }, new { track = "speedway2_figure_8" }, new { track = "bigstadium_figure_8" } },
        };

        using var missing = await client.PutAsJsonAsync($"/api/collections/{id}", reordered, Ct);
        Assert.Equal(HttpStatusCode.PreconditionRequired, missing.StatusCode);

        using var first = await PutAsync(client, $"/api/collections/{id}", reordered, "\"1\"");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("\"2\"", first.Headers.ETag?.Tag);

        using var stale = await PutAsync(client, $"/api/collections/{id}", new { name = "Lost", tracks = Array.Empty<object>() }, "\"1\"");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var current = await stale.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("Rotation", current.GetProperty("name").GetString());
        Assert.Equal(2, current.GetProperty("version").GetInt32());

        var saved = await GetAsync(client, id);
        Assert.Equal(
            new[] { UnknownTrack, "speedway2_figure_8", "bigstadium_figure_8" },
            TrackIds(saved));
    }

    [Fact]
    public async Task Duplicate_CopiesTheTracks_UnderAFreeName()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateAuthenticatedClient();
        var id = await CreateAsync(client, "Weekend", "bigstadium_figure_8", UnknownTrack);

        using var first = await client.PostAsync($"/api/collections/{id}/duplicate", null, Ct);
        using var second = await client.PostAsync($"/api/collections/{id}/duplicate", null, Ct);
        using var named = await client.PostAsJsonAsync($"/api/collections/{id}/duplicate", new { name = "Sunday" }, Ct);
        using var taken = await client.PostAsJsonAsync($"/api/collections/{id}/duplicate", new { name = "weekend" }, Ct);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var copy = await first.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("Weekend (copy)", copy.GetProperty("name").GetString());
        Assert.Equal(new[] { "bigstadium_figure_8", UnknownTrack }, TrackIds(copy));
        Assert.Equal(
            "Weekend (copy 2)",
            (await second.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.Created, named.StatusCode);
        await AuthEndpointsTests.AssertFieldErrorAsync(taken, "name");
    }

    [Fact]
    public async Task Deploy_WritesTheRotationToTheServerConfig()
    {
        using var server = new ServerConfigFile();
        await using var host = await ApiTestHost.StartAsync(server.Settings);
        using var client = host.CreateAuthenticatedClient();

        using var created = await client.PostAsJsonAsync(
            "/api/collections",
            new
            {
                name = "Friday Night",
                tracks = new object[]
                {
                    new { track = "bigstadium_figure_8", gamemode = "racing", laps = 3, wrongWayLimiterDisabled = 0 },
                    new { track = UnknownTrack, weather = "fog" },
                },
            },
            Ct);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetInt32();

        using var deploy = await client.PostAsync($"/api/collections/{id}/deploy", null, Ct);

        Assert.Equal(HttpStatusCode.OK, deploy.StatusCode);
        var lines = File.ReadAllLines(server.Path).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        Assert.Contains("#CollectionName Friday Night", lines);
        var loop = lines.Where(l => l.StartsWith("el_", StringComparison.Ordinal)).ToList();
        Assert.Equal(
            new[]
            {
                "el_add=bigstadium_figure_8", "el_gamemode=racing", "el_laps=3", "el_wrong_way_limiter_disabled=0",
                $"el_add={UnknownTrack}", "el_weather=fog",
            },
            loop);
        Assert.Contains("server_name=Test", lines);
    }

    [Fact]
    public async Task Deploy_RefusesAnEmptyCollection()
    {
        using var server = new ServerConfigFile();
        await using var host = await ApiTestHost.StartAsync(server.Settings);
        using var client = host.CreateAuthenticatedClient();
        var id = await CreateAsync(client, "Empty");
        var before = File.ReadAllText(server.Path);

        using var deploy = await client.PostAsync($"/api/collections/{id}/deploy", null, Ct);

        Assert.Equal(HttpStatusCode.Conflict, deploy.StatusCode);
        Assert.Equal(before, File.ReadAllText(server.Path));
    }

    [Fact]
    public async Task Deploy_ToAReadOnlyConfig_SaysAccessWasDenied_AndChangesNothing()
    {
        using var server = new ServerConfigFile();
        await using var host = await ApiTestHost.StartAsync(server.Settings);
        using var client = host.CreateAuthenticatedClient();
        var id = await CreateAsync(client, "Locked out", "bigstadium_figure_8");
        var before = File.ReadAllText(server.Path);
        File.SetAttributes(server.Path, FileAttributes.ReadOnly);

        using var deploy = await client.PostAsync($"/api/collections/{id}/deploy", null, Ct);

        await AssertRefusedAsync(deploy, "accessDenied", "write permission");
        Assert.Equal(before, File.ReadAllText(server.Path));
    }

    [Fact]
    public async Task Deploy_WhileAnotherProgramHoldsTheConfig_SaysItIsInUse()
    {
        using var server = new ServerConfigFile();
        await using var host = await ApiTestHost.StartAsync(server.Settings);
        using var client = host.CreateAuthenticatedClient();
        var id = await CreateAsync(client, "Busy", "bigstadium_figure_8");

        HttpResponseMessage deploy;
        using (new FileStream(server.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            deploy = await client.PostAsync($"/api/collections/{id}/deploy", null, Ct);
        }

        using (deploy)
        {
            await AssertRefusedAsync(deploy, "fileInUse", "Close it and try again");
        }
    }

    [Fact]
    public async Task Deploy_WithoutAServerConfig_SaysWhereToLook()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateAuthenticatedClient();
        var id = await CreateAsync(client, "Nowhere", "bigstadium_figure_8");

        using var deploy = await client.PostAsync($"/api/collections/{id}/deploy", null, Ct);

        await AssertRefusedAsync(deploy, "notConfigured", "not set up");
    }

    [Fact]
    public async Task UsedVariantsAndTracks_CannotBeDeleted_UntilNoCollectionUsesThem()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateAuthenticatedClient();
        var (trackId, variantId) = await AddTrackAsync(client, "custom_oval", "custom_oval_1");
        var collectionId = await CreateAsync(client, "Customs", "custom_oval_1");

        using var deleteVariant = await client.DeleteAsync($"/api/catalogue/variants/{variantId}", Ct);
        using var deleteTrack = await client.DeleteAsync($"/api/catalogue/tracks/{trackId}", Ct);

        Assert.Equal(HttpStatusCode.Conflict, deleteVariant.StatusCode);
        var problem = await deleteVariant.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("Customs", Assert.Single(problem.GetProperty("collections").EnumerateArray()).GetString());
        Assert.Equal(HttpStatusCode.Conflict, deleteTrack.StatusCode);

        using var deleteCollection = await client.DeleteAsync($"/api/collections/{collectionId}", Ct);
        Assert.Equal(HttpStatusCode.NoContent, deleteCollection.StatusCode);

        using var retry = await client.DeleteAsync($"/api/catalogue/tracks/{trackId}", Ct);
        Assert.Equal(HttpStatusCode.NoContent, retry.StatusCode);
    }

    [Fact]
    public async Task AddingAVariant_LinksEntriesSavedBeforeTheCatalogueKnewIt_AndRenamesCarryThem()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateAuthenticatedClient();
        var collectionId = await CreateAsync(client, "Workshop", "Custom_Oval_1");
        Assert.Equal(JsonValueKind.Null, Entry(await GetAsync(client, collectionId)).GetProperty("variant").ValueKind);

        var (_, variantId) = await AddTrackAsync(client, "custom_oval", "custom_oval_1");

        var linked = Entry(await GetAsync(client, collectionId));
        Assert.Equal(variantId, linked.GetProperty("variant").GetProperty("id").GetInt32());

        using var rename = await PutAsync(
            client,
            $"/api/catalogue/variants/{variantId}",
            new { variantId = "custom_oval_2", name = "Custom Oval", gameMode = "Racing" },
            "\"1\"");
        rename.EnsureSuccessStatusCode();

        Assert.Equal("custom_oval_2", Entry(await GetAsync(client, collectionId)).GetProperty("track").GetString());
    }

    [Fact]
    public async Task RenamingAVariant_BumpsTheCollectionsItChanges_SoAStaleEditorCannotUndoIt()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateAuthenticatedClient();
        var (_, variantId) = await AddTrackAsync(client, "custom_oval", "custom_oval_1");
        var linked = await CreateAsync(client, "Linked", "custom_oval_1");
        var waiting = await CreateAsync(client, "Waiting", "custom_oval_2");
        var untouched = await CreateAsync(client, "Untouched", "bigstadium_figure_8");

        using var rename = await PutAsync(
            client,
            $"/api/catalogue/variants/{variantId}",
            new { variantId = "custom_oval_2", name = "Custom Oval", gameMode = "Racing" },
            "\"1\"");
        rename.EnsureSuccessStatusCode();

        // The editor loaded "Linked" at version 1, before the rename, and saves the old id back.
        using var stale = await PutAsync(
            client,
            $"/api/collections/{linked}",
            new { name = "Linked", tracks = new[] { new { track = "custom_oval_1" } } },
            "\"1\"");

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var current = await GetAsync(client, linked);
        Assert.Equal(2, current.GetProperty("version").GetInt32());
        Assert.Equal("custom_oval_2", Entry(current).GetProperty("track").GetString());
        Assert.Equal(variantId, Entry(current).GetProperty("variant").GetProperty("id").GetInt32());

        // Linking an entry that named the new id changes that collection too.
        Assert.Equal(2, (await GetAsync(client, waiting)).GetProperty("version").GetInt32());
        Assert.Equal(1, (await GetAsync(client, untouched)).GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task HiddenVariant_StaysInTheCollection_Flagged()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateAuthenticatedClient();
        var collectionId = await CreateAsync(client, "Old favourites", "bigstadium_figure_8");
        var variantId = Entry(await GetAsync(client, collectionId)).GetProperty("variant").GetProperty("id").GetInt32();

        using var hide = await client.PostAsync($"/api/catalogue/variants/{variantId}/hide", null, Ct);
        hide.EnsureSuccessStatusCode();

        var entry = Entry(await GetAsync(client, collectionId));
        Assert.Equal("bigstadium_figure_8", entry.GetProperty("track").GetString());
        Assert.True(entry.GetProperty("variant").GetProperty("isHidden").GetBoolean());
    }

    private static async Task AssertRefusedAsync(HttpResponseMessage response, string reason, string hint)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(reason, problem.GetProperty("reason").GetString());
        Assert.Contains(hint, problem.GetProperty("title").GetString());
    }

    private static JsonElement Entry(JsonElement collection) =>
        Assert.Single(collection.GetProperty("tracks").EnumerateArray());

    private static string?[] TrackIds(JsonElement collection) =>
        collection.GetProperty("tracks").EnumerateArray().Select(t => t.GetProperty("track").GetString()).ToArray();

    private static async Task<int> CreateAsync(HttpClient client, string name, params string[] tracks)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/collections", new { name, tracks = tracks.Select(t => new { track = t }) }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetInt32();
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, int id) =>
        await client.GetFromJsonAsync<JsonElement>($"/api/collections/{id}", Ct);

    private static async Task<List<JsonElement>> GetArrayAsync(HttpClient client, string url) =>
        (await client.GetFromJsonAsync<JsonElement>(url, Ct)).EnumerateArray().ToList();

    /// <summary>Adds an admin track with one variant, returning both catalogue ids.</summary>
    private static async Task<(int TrackId, int VariantId)> AddTrackAsync(HttpClient client, string key, string variantId)
    {
        using var track = await client.PostAsJsonAsync(
            "/api/catalogue/tracks", new { key, name = "Custom Oval", origin = "Custom" }, Ct);
        Assert.Equal(HttpStatusCode.Created, track.StatusCode);
        var trackId = (await track.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetInt32();

        using var variant = await client.PostAsJsonAsync(
            "/api/catalogue/variants",
            new { trackId, variantId, name = "Custom Oval", gameMode = "Racing" },
            Ct);
        Assert.Equal(HttpStatusCode.Created, variant.StatusCode);
        return (trackId, (await variant.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetInt32());
    }

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, string url, object body, string ifMatch)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = JsonContent.Create(body) };
        request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return client.SendAsync(request, Ct);
    }

    /// <summary>A server_config.cfg with an event loop section, and the settings that point ConfigService at it.</summary>
    internal sealed class ServerConfigFile : IDisposable
    {
        private readonly string _directory = ApiTestHost.NewDataDirectory();

        public ServerConfigFile()
        {
            Directory.CreateDirectory(_directory);
            Path = System.IO.Path.Combine(_directory, "server_config.cfg");
            File.WriteAllLines(Path,
            [
                "server_name=Test",
                "",
                "# Event Loop",
                "# el_add adds a track to the loop",
                "",
                "## Add event 1 to Loop",
                "el_add=old_track",
                "el_laps=9",
            ]);
        }

        public string Path { get; }

        public Dictionary<string, string?> Settings => new()
        {
            ["WreckfestServer:WorkingDirectory"] = _directory,
            ["WreckfestServer:ServerArguments"] = "-s server_config=server_config.cfg",
        };

        public void Dispose()
        {
            // A test may leave it read-only, which would stop the folder being deleted.
            if (File.Exists(Path))
            {
                File.SetAttributes(Path, FileAttributes.Normal);
            }

            ApiTestHost.DeleteDataDirectory(_directory);
        }
    }
}
