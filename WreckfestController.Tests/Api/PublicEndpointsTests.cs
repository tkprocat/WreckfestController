using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using WreckfestController.Services.Auth;

namespace WreckfestController.Tests.Api;

/// <summary>/api/public/overview: anonymous, rate-limited, and never a secret.</summary>
public sealed class PublicEndpointsTests : IDisposable
{
    private const string Password = "hunter2-server-password";
    private const string AdminSteamId = "76561197985810610";
    private const string OpSteamId = "76561198134883566";
    private const string CupPassword = "cup-only-password";

    private readonly string _directory = ApiTestHost.NewDataDirectory();

    public PublicEndpointsTests()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllLines(Path.Combine(_directory, "server_config.cfg"),
        [
            "server_name=Friday Wrecks",
            $"password={Password}",
            "max_players=16",
            $"admin_steam_ids={AdminSteamId}",
            $"op_steam_ids={OpSteamId}",
            "",
            "# Event Loop",
            "#CollectionName Ovals",
            "el_add=bigstadium_demolition_arena",
            "el_laps=5",
            "el_add=somebodys_workshop_track",
            "el_gamemode=derby",
        ]);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Dictionary<string, string?> Settings => new()
    {
        ["WreckfestServer:WorkingDirectory"] = _directory,
        ["WreckfestServer:ServerArguments"] = "-s server_config=server_config.cfg",
    };

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task Overview_IsAnonymous_WithExactlyTheseFields()
    {
        await using var host = await ApiTestHost.StartAsync(Settings);
        using var client = host.CreateClient();

        var body = await client.GetFromJsonAsync<JsonElement>("/api/public/overview", Ct);

        // A new field must be added here on purpose: this is what anyone can read.
        Assert.Equal(
            ["activeCup", "currentTrack", "maxPlayers", "players", "rotation", "serverName", "status", "upcomingCups", "updatedAt"],
            Names(body));
        Assert.Equal(["isRunning", "uptimeSeconds"], Names(body.GetProperty("status")));
        Assert.Equal(["bots", "humans", "list"], Names(body.GetProperty("players")));
        Assert.Equal(["name", "tracks"], Names(body.GetProperty("rotation")));
        Assert.Equal(["gameMode", "id", "laps", "name"], Names(body.GetProperty("rotation").GetProperty("tracks")[0]));

        Assert.Equal("Friday Wrecks", body.GetProperty("serverName").GetString());
        Assert.Equal(16, body.GetProperty("maxPlayers").GetInt32());
        Assert.False(body.GetProperty("status").GetProperty("isRunning").GetBoolean());
    }

    [Fact]
    public async Task Rotation_UsesCatalogueNames_AndTheIdForUnknownTracks()
    {
        await using var host = await ApiTestHost.StartAsync(Settings);
        using var client = host.CreateClient();

        var rotation = (await client.GetFromJsonAsync<JsonElement>("/api/public/overview", Ct)).GetProperty("rotation");

        Assert.Equal("Ovals", rotation.GetProperty("name").GetString());
        var tracks = rotation.GetProperty("tracks");
        Assert.Equal("Madman Stadium - Demolition Arena", tracks[0].GetProperty("name").GetString());
        Assert.Equal(5, tracks[0].GetProperty("laps").GetInt32());
        Assert.Equal("somebodys_workshop_track", tracks[1].GetProperty("name").GetString());
        Assert.Equal("derby", tracks[1].GetProperty("gameMode").GetString());
    }

    [Fact]
    public async Task Overview_NeverShowsASecret()
    {
        await using var host = await ApiTestHost.StartAsync(Settings);
        using (var admin = host.CreateAuthenticatedClient())
        {
            using var created = await admin.PostAsJsonAsync("/api/cups", new
            {
                name = "Race night",
                description = "Every Friday",
                startTime = DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-ddTHH:mm:00Z", CultureInfo.InvariantCulture),
                serverConfig = new { password = CupPassword, serverName = "Cup name" },
            }, Ct);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        using var client = host.CreateClient();
        var text = await client.GetStringAsync("/api/public/overview", Ct);

        Assert.Contains("Race night", text, StringComparison.Ordinal);
        foreach (var secret in new[] { Password, AdminSteamId, OpSteamId, CupPassword })
        {
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        }

        foreach (var field in new[] { "password", "adminSteamIds", "opSteamIds", "serverConfig", "createdBy" })
        {
            Assert.DoesNotContain($"\"{field}\"", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Overview_WithoutAServerConfig_StillAnswers()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateClient();

        var body = await client.GetFromJsonAsync<JsonElement>("/api/public/overview", Ct);

        Assert.Equal(JsonValueKind.Null, body.GetProperty("serverName").ValueKind);
        Assert.Empty(body.GetProperty("rotation").GetProperty("tracks").EnumerateArray());
    }

    [Fact]
    public async Task Overview_IsRateLimitedPerClient()
    {
        await using var host = await ApiTestHost.StartAsync(Settings);
        using var client = host.CreateClient();

        for (var i = 0; i < RateLimits.PublicPermitsPerWindow; i++)
        {
            using var ok = await client.GetAsync("/api/public/overview", Ct);
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        using var limited = await client.GetAsync("/api/public/overview", Ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Contains("Too many requests", await limited.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    private static List<string> Names(JsonElement element) =>
        element.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToList();
}
