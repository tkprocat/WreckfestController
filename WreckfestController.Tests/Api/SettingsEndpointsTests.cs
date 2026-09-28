using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace WreckfestController.Tests.Api;

/// <summary>/api/settings: the database sections, partial edits with If-Match, and no startup settings.</summary>
public class SettingsEndpointsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Anonymous_CannotReadSettings()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateClient();

        using var response = await client.GetAsync("/api/settings", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task List_HasTheThreeSections_WithoutInternalFields()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateAuthenticatedClient();

        var body = await client.GetFromJsonAsync<JsonElement>("/api/settings", Ct);

        Assert.Equal(["steamCmd", "vote", "wreckfestServer"], body.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal("-s server_config=server_config.cfg", body.GetProperty("wreckfestServer").GetProperty("serverArguments").GetString());
        Assert.Equal(1, body.GetProperty("vote").GetProperty("version").GetInt32());

        // Hook-only I/O, and a legacy flag that only mirrors mode.
        Assert.False(body.GetProperty("wreckfestServer").TryGetProperty("outputMode", out _));
        Assert.False(body.GetProperty("vote").TryGetProperty("enabled", out _));
    }

    [Theory]
    [InlineData("api")]
    [InlineData("Api")]
    [InlineData("database")]
    [InlineData("userSettingsPath")]
    public async Task StartupSettings_HaveNoRoute(string section)
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateAuthenticatedClient();

        using var get = await client.GetAsync($"/api/settings/{section}", Ct);
        using var put = await SendAsync(client, $"/api/settings/{section}", new { key = "taken over" }, "\"1\"");

        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
    }

    [Fact]
    public async Task Put_ChangesOnlyTheFieldsSent_AndNeedsACurrentIfMatch()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateAuthenticatedClient();

        using var get = await client.GetAsync("/api/settings/vote", Ct);
        Assert.Equal("\"1\"", get.Headers.ETag?.Tag);
        var before = await get.Content.ReadFromJsonAsync<JsonElement>(Ct);

        using var missing = await SendAsync(client, "/api/settings/vote", new { mode = "direct" }, ifMatch: null);
        Assert.Equal(HttpStatusCode.PreconditionRequired, missing.StatusCode);

        using var saved = await SendAsync(client, "/api/settings/vote", new { mode = "direct", messageDelayMs = 100 }, "\"1\"");
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal("\"2\"", saved.Headers.ETag?.Tag);
        var after = await saved.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("Direct", after.GetProperty("mode").GetString());
        Assert.Equal(100, after.GetProperty("messageDelayMs").GetInt32());
        Assert.Equal(before.GetProperty("maxLapsAllowed").GetInt32(), after.GetProperty("maxLapsAllowed").GetInt32());

        // Someone still holding version 1.
        using var stale = await SendAsync(client, "/api/settings/vote", new { mode = "off" }, "\"1\"");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("\"2\"", stale.Headers.ETag?.Tag);
        Assert.Equal("Direct", (await stale.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("mode").GetString());
    }

    [Fact]
    public async Task Put_AcceptsWhatGetReturned()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateAuthenticatedClient();
        var read = await client.GetFromJsonAsync<JsonElement>("/api/settings/wreckfestServer", Ct);

        using var saved = await SendAsync(client, "/api/settings/wreckfestServer", read, "\"1\"");

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
    }

    public static TheoryData<string, object, string> InvalidEdits => new()
    {
        { "vote", new { mode = "sometimes" }, "mode" },
        { "vote", new { maxLapsAllowed = 0 }, "maxLapsAllowed" },
        { "vote", new { messageDelayMs = 60_000 }, "messageDelayMs" },
        { "vote", new { voteTimeoutSeconds = "thirty" }, "voteTimeoutSeconds" },
        { "vote", new { suppressCommandsDuringRace = "yes" }, "suppressCommandsDuringRace" },
        { "vote", new { enabled = false }, "enabled" },
        { "vote", new { allowedTracks = Array.Empty<object>() }, "allowedTracks" },
        { "wreckfestServer", new { outputMode = "ConsoleReader" }, "outputMode" },
        { "wreckfestServer", new { serverArguments = "-s server_config=a.cfg\n-other" }, "serverArguments" },
        { "wreckfestServer", new { serverPath = (string?)null }, "serverPath" },
        { "steamCmd", new { wreckfestAppId = "361580; rm" }, "wreckfestAppId" },
    };

    [Theory]
    [MemberData(nameof(InvalidEdits))]
    public async Task Put_RejectsInvalidEdits_AndSavesNothing(string section, object body, string field)
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateAuthenticatedClient();

        using var response = await SendAsync(client, $"/api/settings/{section}", body, "\"1\"");

        await AuthEndpointsTests.AssertFieldErrorAsync(response, field);
        using var after = await client.GetAsync($"/api/settings/{section}", Ct);
        Assert.Equal("\"1\"", after.Headers.ETag?.Tag);
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string url, object? body, string? ifMatch)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, url);
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
