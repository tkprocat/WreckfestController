using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WreckfestController.Data;
using WreckfestController.Models;
using WreckfestController.Services.Config;
using WreckfestController.Services.Voting;

namespace WreckfestController.Tests.Api;

/// <summary>
/// /api/settings: the database sections, partial edits with If-Match, and neither startup
/// nor launch settings.
/// </summary>
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
    public async Task List_HasOnlyTheVoteSection_WithoutInternalFields()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateAuthenticatedClient();

        var body = await client.GetFromJsonAsync<JsonElement>("/api/settings", Ct);

        Assert.Equal(["vote"], body.EnumerateObject().Select(p => p.Name));
        Assert.Equal(1, body.GetProperty("vote").GetProperty("version").GetInt32());

        // A legacy flag that only mirrors mode.
        Assert.False(body.GetProperty("vote").TryGetProperty("enabled", out _));
    }

    // Which programs run, from where, and which files the controller reads and writes are
    // set in the desktop app only: a web admin who could change them could run any
    // program, or read or overwrite any file, as the controller's Windows user.
    [Theory]
    [InlineData("serverPath")]
    [InlineData("serverArguments")]
    [InlineData("workingDirectory")]
    [InlineData("logFilePath")]
    [InlineData("steamCmdPath")]
    [InlineData("wreckfestAppId")]
    public async Task LaunchSettings_AreNeitherReadNorWritten(string field)
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateAuthenticatedClient();

        var all = await client.GetStringAsync("/api/settings", Ct);
        using var put = await SendAsync(
            client,
            "/api/settings/vote",
            new Dictionary<string, string> { [field] = @"C:\Windows\System32\cmd.exe" },
            "\"1\"");

        Assert.DoesNotContain(field, all, StringComparison.OrdinalIgnoreCase);
        await AuthEndpointsTests.AssertFieldErrorAsync(put, field);
        using var after = await client.GetAsync("/api/settings/vote", Ct);
        Assert.Equal("\"1\"", after.Headers.ETag?.Tag);
    }

    [Theory]
    [InlineData("api")]
    [InlineData("Api")]
    [InlineData("database")]
    [InlineData("userSettingsPath")]
    [InlineData("wreckfestServer")]
    [InlineData("steamCmd")]
    public async Task StartupAndLaunchSettings_HaveNoRoute(string section)
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
        var read = await client.GetFromJsonAsync<JsonElement>("/api/settings/vote", Ct);

        using var saved = await SendAsync(client, "/api/settings/vote", read, "\"1\"");

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
    }

    // GET answers with the typed VoteSettingsResponse, PUT with SettingsApi's body for the
    // same section: the web app is typed against the first, so the two must match field
    // for field, a 409's body included.
    [Fact]
    public async Task GetAndPut_AnswerWithTheSameFields()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateAuthenticatedClient();
        var read = await client.GetFromJsonAsync<JsonElement>("/api/settings/vote", Ct);
        var list = await client.GetFromJsonAsync<JsonElement>("/api/settings", Ct);

        using var saved = await SendAsync(client, "/api/settings/vote", new { maxLapsAllowed = 12 }, "\"1\"");
        using var stale = await SendAsync(client, "/api/settings/vote", new { maxLapsAllowed = 13 }, "\"1\"");

        var expected = FieldsOf(read);
        Assert.Equal(expected, FieldsOf(list.GetProperty("vote")));
        Assert.Equal(expected, FieldsOf(await saved.Content.ReadFromJsonAsync<JsonElement>(Ct)));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(expected, FieldsOf(await stale.Content.ReadFromJsonAsync<JsonElement>(Ct)));
        Assert.Equal(
            ["directCooldownSeconds", "maxLapsAllowed", "messageDelayMs", "mode", "suppressCommandsDuringRace", "version", "voteTimeoutSeconds"],
            expected);
    }

    private static List<string> FieldsOf(JsonElement body) => body.EnumerateObject().Select(p => p.Name).Order().ToList();

    public static TheoryData<string, object, string> InvalidEdits => new()
    {
        { "vote", new { mode = "sometimes" }, "mode" },
        { "vote", new { maxLapsAllowed = 0 }, "maxLapsAllowed" },
        { "vote", new { messageDelayMs = 60_000 }, "messageDelayMs" },
        { "vote", new { voteTimeoutSeconds = "thirty" }, "voteTimeoutSeconds" },
        { "vote", new { suppressCommandsDuringRace = "yes" }, "suppressCommandsDuringRace" },
        { "vote", new { enabled = false }, "enabled" },
        { "vote", new { allowedTracks = Array.Empty<object>() }, "allowedTracks" },
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

    // Two stores on one database: a partial edit must merge into the version the client
    // named, never into an older cached copy that would put back a field changed since.
    [Fact]
    public async Task Put_DoesNotMergeIntoAnOlderCachedCopy()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateAuthenticatedClient();
        using var first = await client.GetAsync("/api/settings/vote", Ct);
        Assert.Equal("\"1\"", first.Headers.ETag?.Tag);

        var other = OtherStore(host);
        var vote = other.GetEntry<VoteSettings>();
        vote.Value.Mode = VoteModes.Direct;
        var saved = await other.SaveAsync(vote.Value, vote.Version, Ct);
        Assert.Equal(2, saved.Current.Version);

        using var put = await SendAsync(client, "/api/settings/vote", new { messageDelayMs = 100 }, "\"2\"");

        Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);
        var stored = OtherStore(host).GetEntry<VoteSettings>();
        Assert.Equal(VoteModes.Direct, stored.Value.Mode);
        Assert.Equal(2, stored.Version);
    }

    private static SettingsStore OtherStore(ApiTestHost host) => new(
        host.MainServices.GetRequiredService<IDbContextFactory<ControllerDbContext>>(),
        host.MainServices.GetRequiredService<DatabaseState>(),
        new ShippedSettings(new ConfigurationBuilder().Build()),
        NullLogger<SettingsStore>.Instance);

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
