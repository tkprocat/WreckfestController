using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace WreckfestController.Tests.Api;

/// <summary>/api/cups: create, conflict-checked edits and deletes, collection links and activation.</summary>
public class CupEndpointsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string InOneDay => DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-ddTHH:mm:00Z", CultureInfo.InvariantCulture);

    private static object CupBody(string name = "Race night", string? startTime = null, object? extra = null)
    {
        var body = new Dictionary<string, object?> { ["name"] = name, ["startTime"] = startTime ?? InOneDay };
        if (extra is not null)
        {
            foreach (var property in extra.GetType().GetProperties())
            {
                body[property.Name] = property.GetValue(extra);
            }
        }

        return body;
    }

    [Fact]
    public async Task Anonymous_CannotReadCups()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateClient();

        using var response = await client.GetAsync("/api/cups", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Create_ReturnsTheCupWithItsNextOccurrence()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateAuthenticatedClient();

        using var created = await client.PostAsJsonAsync("/api/cups", CupBody(extra: new
        {
            timeZone = "Europe/Copenhagen",
            repeat = new { frequency = "Weekly", days = new[] { 5, 5, 1 }, time = "20:00" },
            serverConfig = new { serverName = "Friday Night" },
            sessionMode = "30P-Aggr",
            gridOrder = "cup_reverse",
            collectionName = "Ovals",
            tracks = new[] { new { track = "speedway2_figure_8", laps = 5 } },
        }), Ct);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("\"1\"", created.Headers.ETag?.Tag);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("Europe/Copenhagen", body.GetProperty("timeZone").GetString());
        Assert.Equal("weekly", body.GetProperty("repeat").GetProperty("frequency").GetString());
        Assert.Equal([1, 5], body.GetProperty("repeat").GetProperty("days").EnumerateArray().Select(d => d.GetInt32()));
        Assert.Equal("Weekly on Mon, Fri at 20:00", body.GetProperty("repeatDescription").GetString());
        Assert.Equal("Friday Night", body.GetProperty("serverConfig").GetProperty("serverName").GetString());

        // Stored as the server spells it: it becomes a line of server_config.cfg.
        Assert.Equal("30p-aggr", body.GetProperty("sessionMode").GetString());
        Assert.Equal("cup_reverse", body.GetProperty("gridOrder").GetString());
        Assert.Equal("Ovals", body.GetProperty("collectionName").GetString());
        Assert.Equal(5, body.GetProperty("tracks")[0].GetProperty("laps").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, body.GetProperty("nextOccurrence").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("createdBy").ValueKind);
        Assert.False(body.GetProperty("isActive").GetBoolean());

        var list = await client.GetFromJsonAsync<JsonElement>("/api/cups", Ct);
        Assert.Equal(1, list.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Create_BySignedInUser_RecordsWhoCreatedIt()
    {
        await using var host = await ApiTestHost.StartAsync();
        await host.CreateUserAsync();
        using var browser = host.CreateBrowser();
        await browser.SignInAsync("admin");

        using var created = await browser.Http.PostAsJsonAsync("/api/cups", CupBody(), Ct);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("admin", body.GetProperty("createdBy").GetString());
    }

    public static TheoryData<object, string> InvalidCups => new()
    {
        { CupBody(name: " "), "name" },
        { CupBody(name: "Race\nel_add=invalid_track"), "name" },
        { CupBody(startTime: "2026-10-02T20:00:00"), "startTime" },
        { CupBody(extra: new { timeZone = "Mars/Olympus_Mons" }), "timeZone" },
        { CupBody(extra: new { repeat = new { frequency = "monthly", time = "20:00" } }), "repeat.frequency" },
        { CupBody(extra: new { repeat = new { frequency = "daily", time = "8pm" } }), "repeat.time" },
        { CupBody(extra: new { repeat = new { frequency = "weekly", time = "20:00" } }), "repeat.days" },
        { CupBody(extra: new { repeat = new { frequency = "weekly", time = "20:00", days = new[] { 7 } } }), "repeat.days" },
        { CupBody(extra: new { serverConfig = new { serverName = "Name\nserver_password=x" } }), "serverConfig.serverName" },
        { CupBody(extra: new { serverConfig = new { maxPlayers = 0 } }), "serverConfig" },
        { CupBody(extra: new { sessionMode = "cup" }), "sessionMode" },
        { CupBody(extra: new { sessionMode = "normal\nel_add=invalid_track" }), "sessionMode" },
        { CupBody(extra: new { gridOrder = "fastest_first" }), "gridOrder" },
        { CupBody(extra: new { tracks = new[] { new { track = "bad id" } } }), "tracks[0].track" },
        { CupBody(extra: new { collectionId = 1, tracks = new[] { new { track = "urban09_1" } } }), "tracks" },
        { CupBody(extra: new { collectionId = 999 }), "collectionId" },
    };

    [Theory]
    [MemberData(nameof(InvalidCups))]
    public async Task Create_RejectsInvalidCups(object body, string field)
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateAuthenticatedClient();

        using var response = await client.PostAsJsonAsync("/api/cups", body, Ct);

        await AuthEndpointsTests.AssertFieldErrorAsync(response, field);
    }

    [Fact]
    public async Task Update_NeedsIfMatch_AndRejectsAStaleVersion()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateAuthenticatedClient();
        var id = await CreateAsync(client, CupBody());

        using var missing = await SendAsync(client, HttpMethod.Put, $"/api/cups/{id}", CupBody("Renamed"), ifMatch: null);
        Assert.Equal(HttpStatusCode.PreconditionRequired, missing.StatusCode);

        using var saved = await SendAsync(client, HttpMethod.Put, $"/api/cups/{id}", CupBody("Renamed"), "\"1\"");
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal("\"2\"", saved.Headers.ETag?.Tag);

        using var stale = await SendAsync(client, HttpMethod.Put, $"/api/cups/{id}", CupBody("Overwrite"), "\"1\"");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("\"2\"", stale.Headers.ETag?.Tag);
        Assert.Equal("Renamed", (await stale.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("name").GetString());

        using var get = await client.GetAsync($"/api/cups/{id}", Ct);
        Assert.Equal("\"2\"", get.Headers.ETag?.Tag);
    }

    [Fact]
    public async Task Delete_NeedsIfMatch_AndRejectsAStaleVersion()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateAuthenticatedClient();
        var id = await CreateAsync(client, CupBody());
        using (await SendAsync(client, HttpMethod.Put, $"/api/cups/{id}", CupBody("Renamed"), "\"1\"")) { }

        using var missing = await SendAsync(client, HttpMethod.Delete, $"/api/cups/{id}", null, ifMatch: null);
        using var stale = await SendAsync(client, HttpMethod.Delete, $"/api/cups/{id}", null, "\"1\"");
        using var deleted = await SendAsync(client, HttpMethod.Delete, $"/api/cups/{id}", null, "\"2\"");
        using var gone = await client.GetAsync($"/api/cups/{id}", Ct);

        Assert.Equal(HttpStatusCode.PreconditionRequired, missing.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task ALinkedCup_ShowsItsCollectionsCurrentTracks_AndKeepsThemWhenTheCollectionIsDeleted()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateAuthenticatedClient();
        using var collection = await client.PostAsJsonAsync(
            "/api/collections", new { name = "Ovals", tracks = new[] { new { track = "speedway2_figure_8" } } }, Ct);
        var collectionId = (await collection.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetInt32();
        var id = await CreateAsync(client, CupBody(extra: new { collectionId }));

        using (await SendAsync(
            client,
            HttpMethod.Put,
            $"/api/collections/{collectionId}",
            new { name = "Ovals", tracks = new[] { new { track = "speedway2_figure_8" }, new { track = "urban09_1" } } },
            "\"1\""))
        {
        }

        var linked = await client.GetFromJsonAsync<JsonElement>($"/api/cups/{id}", Ct);
        Assert.Equal(collectionId, linked.GetProperty("collectionId").GetInt32());
        Assert.Equal(2, linked.GetProperty("tracks").GetArrayLength());

        using var deleted = await client.DeleteAsync($"/api/collections/{collectionId}", Ct);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var kept = await client.GetFromJsonAsync<JsonElement>($"/api/cups/{id}", Ct);
        Assert.Equal(JsonValueKind.Null, kept.GetProperty("collectionId").ValueKind);
        Assert.Equal("Ovals", kept.GetProperty("collectionName").GetString());
        Assert.Equal(
            ["speedway2_figure_8", "urban09_1"],
            kept.GetProperty("tracks").EnumerateArray().Select(t => t.GetProperty("track").GetString()));
    }

    [Fact]
    public async Task Activate_WritesTheCupToTheServerConfig()
    {
        using var server = new CollectionEndpointsTests.ServerConfigFile();
        await using var host = await ApiTestHost.StartAsync(server.Settings);
        using var client = host.CreateAuthenticatedClient();
        var id = await CreateAsync(client, CupBody(extra: new
        {
            collectionName = "Event Rotation",
            tracks = new[] { new { track = "urban09_1" } },
        }));

        using var activated = await client.PostAsync($"/api/cups/{id}/activate", null, Ct);
        using var missing = await client.PostAsync($"/api/cups/{id + 1}/activate", null, Ct);

        Assert.Equal(HttpStatusCode.Accepted, activated.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var lines = File.ReadAllLines(server.Path).Select(l => l.Trim()).ToList();
        Assert.Contains("#CollectionName Event Rotation", lines);
        Assert.Contains("el_add=urban09_1", lines);
    }

    [Fact]
    public async Task Activate_WhenTheConfigCannotBeWritten_Is409WithAReason()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateAuthenticatedClient();
        var id = await CreateAsync(client, CupBody(extra: new { tracks = new[] { new { track = "urban09_1" } } }));

        using var response = await client.PostAsync($"/api/cups/{id}/activate", null, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.True(problem.TryGetProperty("reason", out _));
    }

    [Fact]
    public async Task Views_SplitCupsByWhenTheyAreDue()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateAuthenticatedClient();
        await CreateAsync(client, CupBody("Later"));
        await CreateAsync(client, CupBody("Soon", DateTime.UtcNow.AddMinutes(2).ToString("O", CultureInfo.InvariantCulture)));

        var upcoming = await client.GetFromJsonAsync<JsonElement>("/api/cups/upcoming", Ct);
        var due = await client.GetFromJsonAsync<JsonElement>("/api/cups/due", Ct);
        var summary = await client.GetFromJsonAsync<JsonElement>("/api/cups/summary", Ct);
        using var current = await client.GetAsync("/api/cups/current", Ct);

        Assert.Equal("Later", upcoming.GetProperty("cups")[0].GetProperty("name").GetString());
        Assert.Equal("Soon", due.GetProperty("cups")[0].GetProperty("name").GetString());
        Assert.Equal(2, summary.GetProperty("totalCups").GetInt32());
        Assert.Equal(1, summary.GetProperty("dueCups").GetInt32());
        Assert.Equal(HttpStatusCode.NoContent, current.StatusCode);
    }

    [Fact]
    public async Task TheLaravelBulkPush_IsGone()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateAuthenticatedClient();

        using var response = await client.PostAsJsonAsync("/api/cups/schedule", new { cups = Array.Empty<object>() }, Ct);

        Assert.True(
            response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
            $"expected no such endpoint, got {response.StatusCode}");
    }

    private static async Task<int> CreateAsync(HttpClient client, object body)
    {
        using var response = await client.PostAsJsonAsync("/api/cups", body, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetInt32();
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string url, object? body, string? ifMatch)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return client.SendAsync(request, Ct);
    }
}
