using System.Net;
using Microsoft.Extensions.Configuration;
using WreckfestController.Services.Auth;
using WreckfestController.Services.Hosting;

namespace WreckfestController.Tests.Api;

/// <summary>
/// A controller under <c>Web:PathBase</c>, so several can share one domain behind a proxy
/// that forwards the full path: the site lives under the base, and nothing outside it.
/// </summary>
public sealed class PathBaseHostingTests : IDisposable
{
    private const string Base = "/tvtp2";
    private const string IndexMarker = "<!-- the web app -->";

    private readonly string _root = ApiTestHost.NewDataDirectory();

    public PathBaseHostingTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "assets"));
        File.WriteAllText(Path.Combine(_root, "index.html"), $"<!doctype html><html><head>{WebApp.BasePlaceholder}</head>{IndexMarker}</html>");
        File.WriteAllText(Path.Combine(_root, "assets", "app-123.js"), "console.log('app')");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Dictionary<string, string?> Settings => new()
    {
        [WebApp.RootKey] = _root,
        [WebApp.PathBaseKey] = Base,
    };

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("/tvtp2/")]
    [InlineData("/tvtp2/index.html")]
    [InlineData("/tvtp2/tracks/fields14")]
    [InlineData("/TVTP2/login")]
    public async Task AppRoutes_AreTheIndexPage_WithTheBaseWrittenIn(string path)
    {
        await using var host = await ApiTestHost.StartAsync(Settings);
        using var client = host.CreateClient();

        using var response = await client.GetAsync(path, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync(Ct);
        Assert.Contains(IndexMarker, html, StringComparison.Ordinal);
        Assert.Contains("<base href=\"/tvtp2/\" />", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheBaseWithoutItsSlash_IsSentToTheSlash()
    {
        await using var host = await ApiTestHost.StartAsync(Settings);
        using var client = host.CreateClient();

        using var response = await client.GetAsync("/tvtp2?x=1", Ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/tvtp2/?x=1", response.Headers.Location?.OriginalString);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/login")]
    [InlineData("/assets/app-123.js")]
    [InlineData("/api/auth/state")]
    [InlineData("/hubs/server/negotiate")]
    [InlineData("/tvtp2x/api/auth/state")]
    public async Task OutsideTheBase_NothingAnswers(string path)
    {
        await using var host = await ApiTestHost.StartAsync(Settings);
        using var client = host.CreateAuthenticatedClient();

        using var response = await client.GetAsync(path, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TheApiAssetsAndHub_AnswerUnderTheBase()
    {
        await using var host = await ApiTestHost.StartAsync(Settings);
        using var client = host.CreateClient();

        using var state = await client.GetAsync("/tvtp2/api/auth/state", Ct);
        using var asset = await client.GetAsync("/tvtp2/assets/app-123.js", Ct);
        using var negotiate = await client.PostAsync("/tvtp2/hubs/server/negotiate?negotiateVersion=1", content: null, Ct);
        using var missingApi = await client.GetAsync("/tvtp2/api/no-such-endpoint", Ct);

        Assert.Equal(HttpStatusCode.OK, state.StatusCode);
        Assert.Equal("console.log('app')", await asset.Content.ReadAsStringAsync(Ct));
        Assert.Equal(HttpStatusCode.OK, negotiate.StatusCode);
        Assert.DoesNotContain(IndexMarker, await missingApi.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cookies_BelongToTheBase_AndSignInWorksUnderIt()
    {
        await using var host = await ApiTestHost.StartAsync(Settings);
        await host.CreateUserAsync("admin");
        using var browser = host.CreateBrowser(Base);

        await browser.SignInAsync("admin");
        using var me = await browser.Http.GetAsync("/tvtp2/api/auth/me", Ct);

        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var cookies = browser.Cookies.GetAllCookies();
        Assert.Contains(cookies, c => c.Name == ApiAuthentication.XsrfCookieName);
        Assert.All(cookies, c => Assert.Equal(Base, c.Path));
    }

    [Fact]
    public async Task AnotherBaseOnTheSameHost_GetsNoneOfTheCookies()
    {
        await using var host = await ApiTestHost.StartAsync(Settings);
        await host.CreateUserAsync("admin");
        using var browser = host.CreateBrowser(Base);

        await browser.SignInAsync("admin");

        Assert.Empty(browser.Cookies.GetCookies(new Uri(browser.Http.BaseAddress!, "/vanilla/api/auth/me")));
    }

    [Theory]
    [InlineData("tvtp2")]
    [InlineData("/tv tp2")]
    [InlineData("/tvtp2?x")]
    [InlineData("/a//b")]
    [InlineData("/a/../tvtp2")]
    [InlineData("/./tvtp2")]
    [InlineData("/..")]
    public void AnUnusableBase_SaysWhy(string value)
    {
        var error = Assert.Throws<PathBaseException>(() => WebApp.ResolvePathBase(Configuration(value)));
        Assert.Contains(WebApp.PathBaseKey, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("/", "")]
    [InlineData("/tvtp2", "/tvtp2")]
    [InlineData(" /tvtp2/ ", "/tvtp2")]
    [InlineData("/mnw/tvtp2", "/mnw/tvtp2")]
    [InlineData("/v2.0", "/v2.0")]
    public void AUsableBase_IsTidied(string? value, string expected) =>
        Assert.Equal(expected, WebApp.ResolvePathBase(Configuration(value)).Value ?? string.Empty);

    private static IConfiguration Configuration(string? pathBase) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [WebApp.PathBaseKey] = pathBase })
            .Build();
}
