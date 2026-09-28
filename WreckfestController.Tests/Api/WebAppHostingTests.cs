using System.Net;
using WreckfestController.Data;
using WreckfestController.Services.Hosting;

namespace WreckfestController.Tests.Api;

/// <summary>The API host serving the built web app: files, deep links, and what they must not swallow.</summary>
public sealed class WebAppHostingTests : IDisposable
{
    private const string IndexMarker = "<!-- the web app -->";

    private readonly string _root = ApiTestHost.NewDataDirectory();

    public WebAppHostingTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "assets"));
        File.WriteAllText(Path.Combine(_root, "index.html"), $"<!doctype html><html>{IndexMarker}</html>");
        File.WriteAllText(Path.Combine(_root, "assets", "app-123.js"), "console.log('app')");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Dictionary<string, string?> Settings => new() { [WebApp.RootKey] = _root };

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("/")]
    [InlineData("/login")]
    [InlineData("/settings/voting")]
    public async Task AppRoutes_AreTheIndexPage_ForAnyone(string path)
    {
        await using var host = await ApiTestHost.StartAsync(Settings);
        using var client = host.CreateClient();

        using var response = await client.GetAsync(path, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(IndexMarker, await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Assets_AreServed_AndAMissingOneIsNotTheIndexPage()
    {
        await using var host = await ApiTestHost.StartAsync(Settings);
        using var client = host.CreateClient();

        using var asset = await client.GetAsync("/assets/app-123.js", Ct);
        using var missing = await client.GetAsync("/assets/app-old.js", Ct);

        Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
        Assert.Equal("console.log('app')", await asset.Content.ReadAsStringAsync(Ct));
        // Nothing answers it, so deny-by-default does (401 for anyone signed out). The
        // point: a stale asset link must not get index.html back as JavaScript.
        Assert.NotEqual(HttpStatusCode.OK, missing.StatusCode);
        Assert.DoesNotContain(IndexMarker, await missing.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/api/no-such-endpoint")]
    [InlineData("/api")]
    [InlineData("/hubs/nothing")]
    public async Task ApiAndHubPaths_AreNeverTheIndexPage(string path)
    {
        await using var host = await ApiTestHost.StartAsync(Settings);
        using var client = host.CreateAuthenticatedClient();

        using var response = await client.GetAsync(path, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain(IndexMarker, await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InRecoveryMode_ThePageStillLoads_ButTheApiDoesNot()
    {
        var state = new DatabaseState(Path.Combine(_root, "unused.db"));
        state.MarkFailed("disk gone", null);
        await using var host = await ApiTestHost.StartAsync(Settings, databaseState: state);
        using var client = host.CreateClient();

        using var page = await client.GetAsync("/settings", Ct);
        using var api = await client.GetAsync("/api/cups", Ct);

        Assert.Contains(IndexMarker, await page.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, api.StatusCode);
    }

    [Fact]
    public async Task WithoutABuiltApp_OnlyTheApiIsServed()
    {
        await using var host = await ApiTestHost.StartAsync(new Dictionary<string, string?>
        {
            [WebApp.RootKey] = Path.Combine(_root, "nothing-here"),
        });
        using var client = host.CreateAuthenticatedClient();

        using var page = await client.GetAsync("/", Ct);
        using var api = await client.GetAsync("/api/cups", Ct);

        Assert.Equal(HttpStatusCode.NotFound, page.StatusCode);
        Assert.Equal(HttpStatusCode.OK, api.StatusCode);
    }
}
