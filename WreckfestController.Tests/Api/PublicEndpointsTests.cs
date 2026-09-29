using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using WreckfestController.Services.Auth;
using WreckfestController.Services.Cups;
using WreckfestController.Services.Tracking;
using WreckfestController.Tests.Services.Tracking;

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
    public async Task Overview_ShowsPlayersAndCups()
    {
        await using var host = await ApiTestHost.StartAsync(Settings);
        using (var admin = host.CreateAuthenticatedClient())
        {
            await CreateCupAsync(admin, "Tonight", DateTime.UtcNow.AddHours(3), repeat: null);
            await CreateCupAsync(admin, "Weekly", DateTime.UtcNow.AddDays(2), repeat: new { frequency = "weekly", days = new[] { 5 }, time = "20:00" });
        }

        var store = host.MainServices.GetRequiredService<CupStore>();
        var tonight = (await store.ListAsync(Ct)).Single(c => c.Name == "Tonight");
        Assert.True(await store.SetActiveAsync(tonight.Id, Ct));
        host.MainServices.GetRequiredService<PlayerTracker>().Seed("Alice", "Bob");

        using var client = host.CreateClient();
        var body = await client.GetFromJsonAsync<JsonElement>("/api/public/overview", Ct);

        var players = body.GetProperty("players");
        Assert.Equal(2, players.GetProperty("humans").GetInt32());
        Assert.Equal(["Alice", "Bob"], players.GetProperty("list").EnumerateArray().Select(p => p.GetProperty("name").GetString()).Order());
        Assert.Equal(["isBot", "name"], Names(players.GetProperty("list")[0]));

        var active = body.GetProperty("activeCup");
        Assert.Equal(["activatedAt", "name"], Names(active));
        Assert.Equal("Tonight", active.GetProperty("name").GetString());

        var upcoming = body.GetProperty("upcomingCups");
        Assert.Equal(["description", "name", "nextOccurrence", "repeat"], Names(upcoming[0]));
        Assert.Equal(["Tonight", "Weekly"], upcoming.EnumerateArray().Select(c => c.GetProperty("name").GetString()));
        Assert.Equal(JsonValueKind.Null, upcoming[0].GetProperty("repeat").ValueKind);
        Assert.StartsWith("Weekly on Fri at 20:00", upcoming[1].GetProperty("repeat").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADatabaseFailure_IsA503_WithNothingFromTheException()
    {
        var failure = new FailingCupQueryInterceptor();
        await using var host = await ApiTestHost.StartAsync(Settings, configureDatabase: options => options.AddInterceptors(failure));
        using var client = host.CreateClient();

        failure.Armed = true;
        using var response = await client.GetAsync("/api/public/overview", Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("unavailable right now", text, StringComparison.Ordinal);
        Assert.DoesNotContain(FailingCupQueryInterceptor.Secret, text, StringComparison.Ordinal);
        Assert.DoesNotContain("Cups", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Overview_IsRateLimitedPerClientIp_WithoutTouchingSignIn()
    {
        await using var host = await ApiTestHost.StartAsync(Settings);
        using var first = Browser(host, "203.0.113.5");
        using var second = Browser(host, "203.0.113.6");

        for (var i = 0; i < RateLimits.PublicPermitsPerWindow; i++)
        {
            using var ok = await first.Http.GetAsync("/api/public/overview", Ct);
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        using var limited = await first.Http.GetAsync("/api/public/overview", Ct);
        using var otherClient = await second.Http.GetAsync("/api/public/overview", Ct);
        using var signIn = await first.LoginAsync("nobody", "wrong password");

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Contains("Too many requests", await limited.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, otherClient.StatusCode);

        // Sign-in has its own bucket: a wrong password, not a rate limit.
        Assert.Equal(HttpStatusCode.Unauthorized, signIn.StatusCode);
    }

    private static BrowserClient Browser(ApiTestHost host, string peer)
    {
        var browser = host.CreateBrowser();
        browser.Http.DefaultRequestHeaders.Add(ApiTestHost.PeerAddressHeader, peer);
        return browser;
    }

    private static async Task CreateCupAsync(HttpClient admin, string name, DateTime start, object? repeat)
    {
        using var created = await admin.PostAsJsonAsync("/api/cups", new
        {
            name,
            startTime = start.ToString("yyyy-MM-ddTHH:mm:00Z", CultureInfo.InvariantCulture),
            repeat,
        }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    /// <summary>Fails the next query of the Cups table, once armed, with a message that must not leak.</summary>
    private sealed class FailingCupQueryInterceptor : DbCommandInterceptor
    {
        public const string Secret = "C:/secret/controller.db is locked";

        public volatile bool Armed;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default) =>
            Armed && command.CommandText.Contains("\"Cups\"", StringComparison.Ordinal)
                ? throw new InvalidOperationException(Secret)
                : ValueTask.FromResult(result);
    }

    private static List<string> Names(JsonElement element) =>
        element.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToList();
}
