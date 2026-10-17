using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace WreckfestPortal.Tests;

/// <summary>The portal as it runs: settings, <c>/api/servers</c> and the page.</summary>
public sealed class PortalHostTests : IDisposable
{
    private const string ApiUrl = "http://10.9.8.7:5102";
    private const string PageMarker = "<!-- the portal -->";

    private readonly string _webRoot = Path.Combine(Path.GetTempPath(), "wfc-portal-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeController _controllers = new();

    private static readonly string DatabasePath = Path.Combine(Path.GetTempPath(), "wfc-portal-tests", Guid.NewGuid().ToString("N"), "portal.db");

    public PortalHostTests()
    {
        Directory.CreateDirectory(Path.Combine(_webRoot, "assets"));
        File.WriteAllText(Path.Combine(_webRoot, PortalApp.PageFile), $"<!doctype html>{PageMarker}");
        File.WriteAllText(Path.Combine(_webRoot, "assets", "portal-1.js"), "console.log('portal')");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        Directory.Delete(_webRoot, recursive: true);
        TestDatabase.Delete(DatabasePath);
    }

    private static Dictionary<string, string?> Settings(string id = "tvtp2") => new()
    {
        ["Portal:Title"] = "Monday Night Wrecking",
        ["Portal:Servers:0:Id"] = id,
        ["Portal:Servers:0:Name"] = "TVTP2",
        ["Portal:Servers:0:ApiUrl"] = ApiUrl,
        // Nothing there: these tests are about the portal's own page.
        ["Portal:AppRoot"] = Path.Combine(Path.GetTempPath(), "wfc-portal-tests", "no-app-here"),
        ["Portal:DatabasePath"] = DatabasePath,
    };

    private async Task<WebApplication> StartAsync(Dictionary<string, string?>? settings = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { WebRootPath = _webRoot });
        builder.WebHost.UseTestServer();
        // Only these: the portal's own appsettings.json is copied next to the tests too.
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(settings ?? Settings());
        PortalApp.ConfigureServices(builder.Services, builder.Configuration);
        builder.Services.AddHttpClient(ServerPoller.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _controllers);

        var app = builder.Build();
        PortalApp.ConfigurePipeline(app);
        await app.StartAsync(Ct);
        return app;
    }

    [Fact]
    public async Task TheServerList_HasTheCards_ButNeverTheControllersAddress()
    {
        _controllers.Answer(ApiUrl, FakeController.Overview());
        await using var app = await StartAsync();
        await app.Services.GetRequiredService<ServerPoller>().PollOnceAsync(Ct);
        using var client = app.GetTestClient();

        var body = await client.GetStringAsync("/api/servers", Ct);

        using var json = JsonDocument.Parse(body);
        Assert.Equal("Monday Night Wrecking", json.RootElement.GetProperty("title").GetString());
        var server = json.RootElement.GetProperty("servers")[0];
        Assert.Equal("online", server.GetProperty("state").GetString());
        Assert.Equal("/tvtp2/", server.GetProperty("link").GetString());
        Assert.Equal(3, server.GetProperty("humans").GetInt32());
        Assert.DoesNotContain("10.9.8.7", body, StringComparison.Ordinal);
        Assert.DoesNotContain("apiUrl", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AStoppedServer_IsSpelledForThePage()
    {
        _controllers.Answer(ApiUrl, FakeController.Overview(isRunning: false));
        await using var app = await StartAsync();
        await app.Services.GetRequiredService<ServerPoller>().PollOnceAsync(Ct);
        using var client = app.GetTestClient();

        var body = await client.GetStringAsync("/api/servers", Ct);

        Assert.Contains("\"state\":\"server-stopped\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePageAndItsAssets_AreServed()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var page = await client.GetAsync("/", Ct);
        using var asset = await client.GetAsync("/assets/portal-1.js", Ct);
        using var other = await client.GetAsync("/vanilla/", Ct);

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains(PageMarker, await page.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal("no-cache", page.Headers.CacheControl?.ToString());
        Assert.Equal("console.log('portal')", await asset.Content.ReadAsStringAsync(Ct));
        // A server the portal does not list: like any path nothing answers, it needs a
        // signed-in caller first, as on the controller.
        Assert.Equal(HttpStatusCode.Unauthorized, other.StatusCode);
    }

    [Fact]
    public async Task UnusableSettings_StopTheStart_SayingWhy()
    {
        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => StartAsync(Settings(id: "tv/tp2")));

        Assert.Contains("Portal:Servers:0:Id", error.Message, StringComparison.Ordinal);
    }
}
