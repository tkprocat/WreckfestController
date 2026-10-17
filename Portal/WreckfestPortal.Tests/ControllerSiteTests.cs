using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace WreckfestPortal.Tests;

/// <summary>
/// A controller's site on the portal: the web app under <c>/{id}/</c>, and its API and hub
/// forwarded to the controller with the prefix taken off and sent as X-Forwarded-Prefix.
/// </summary>
public sealed class ControllerSiteTests : IDisposable
{
    private const string ApiUrl = "http://10.9.8.7:5102";
    private const string PeerHeader = "X-Test-Peer-Address";
    private const string IndexMarker = "<!-- the web app -->";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "wfc-portal-tests", Guid.NewGuid().ToString("N"));
    private readonly Controller _controller = new();

    public ControllerSiteTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "app", "assets"));
        Directory.CreateDirectory(Path.Combine(_root, "wwwroot"));
        File.WriteAllText(Path.Combine(_root, "app", "index.html"), $"<!doctype html><html><head>{ControllerSite.BasePlaceholder}</head>{IndexMarker}</html>");
        File.WriteAllText(Path.Combine(_root, "app", "assets", "app-1.js"), "console.log('app')");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        TestDatabase.Delete(Path.Combine(_root, "data", "portal.db"));
        Directory.Delete(_root, recursive: true);
    }

    private async Task<WebApplication> StartAsync(string? trustedProxy = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { WebRootPath = Path.Combine(_root, "wwwroot") });
        builder.WebHost.UseTestServer();
        builder.Configuration.Sources.Clear();
        var settings = new Dictionary<string, string?>
        {
            ["Portal:Servers:0:Id"] = "tvtp2",
            ["Portal:Servers:0:Name"] = "TVTP2",
            ["Portal:Servers:0:ApiUrl"] = ApiUrl,
            ["Portal:AppRoot"] = Path.Combine(_root, "app"),
            ["Portal:DatabasePath"] = Path.Combine(_root, "data", "portal.db"),
        };
        if (trustedProxy is not null)
        {
            settings["Portal:TrustedProxies:0"] = trustedProxy;
        }

        builder.Configuration.AddInMemoryCollection(settings);
        PortalApp.ConfigureServices(builder.Services, builder.Configuration);
        builder.Services.AddSingleton(new HttpMessageInvoker(_controller));
        // The poller has its own client; it must not reach for a real address either.
        builder.Services.AddHttpClient(ServerPoller.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new FakeController());
        builder.Services.AddSingleton<IStartupFilter, PeerAddressStartupFilter>();

        var app = builder.Build();
        PortalApp.ConfigurePipeline(app);
        await app.StartAsync(Ct);
        return app;
    }

    private static HttpClient Client(WebApplication app, string peer = "203.0.113.5")
    {
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add(PeerHeader, peer);
        return client;
    }

    [Fact]
    public async Task TheApi_IsForwarded_WithoutThePrefix_AndToldIt()
    {
        await using var app = await StartAsync();
        using var client = Client(app);

        using var response = await client.GetAsync("/tvtp2/api/auth/state?x=1", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("from the controller", await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(["sid=1; path=/tvtp2; httponly"], response.Headers.GetValues("Set-Cookie"));
        var forwarded = Assert.Single(_controller.Requests);
        Assert.Equal("http://10.9.8.7:5102/api/auth/state?x=1", forwarded.Uri);
        Assert.Equal("/tvtp2", forwarded.Header("X-Forwarded-Prefix"));
        Assert.Equal("203.0.113.5", forwarded.Header("X-Forwarded-For"));
        Assert.Equal("http", forwarded.Header("X-Forwarded-Proto"));
    }

    [Theory]
    [InlineData("/tvtp2/hubs/server/negotiate?negotiateVersion=1", "/hubs/server/negotiate?negotiateVersion=1")]
    [InlineData("/tvtp2/openapi/v1.json", "/openapi/v1.json")]
    [InlineData("/TVTP2/API/public/overview", "/API/public/overview")]
    public async Task TheHubAndTheOpenApiDocument_AreForwardedToo(string path, string expected)
    {
        await using var app = await StartAsync();
        using var client = Client(app);

        using var response = await client.PostAsync(path, new StringContent("{}"), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var forwarded = Assert.Single(_controller.Requests);
        Assert.Equal("POST", forwarded.Method);
        Assert.Equal("{}", forwarded.Body);
        Assert.Equal(ApiUrl + expected, forwarded.Uri);
        // Always the configured spelling: the cookies' path is what the page's links use.
        Assert.Equal("/tvtp2", forwarded.Header("X-Forwarded-Prefix"));
    }

    // The controller believes these from the portal, so a visitor must not choose them.
    [Fact]
    public async Task WhatAVisitorSaysAboutThemself_IsNotPassedOn()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "198.51.100.7");
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        client.DefaultRequestHeaders.Add("X-Forwarded-Prefix", "/vanilla");
        client.DefaultRequestHeaders.Add("Forwarded", "for=198.51.100.7");

        using var response = await client.GetAsync("/tvtp2/api/auth/state", Ct);

        var forwarded = Assert.Single(_controller.Requests);
        Assert.Equal("203.0.113.5", forwarded.Header("X-Forwarded-For"));
        Assert.Equal("http", forwarded.Header("X-Forwarded-Proto"));
        Assert.Equal("/tvtp2", forwarded.Header("X-Forwarded-Prefix"));
        Assert.Null(forwarded.Header("Forwarded"));
    }

    // The portal's sign-in is the portal's: a controller that kept it could act as that user there.
    [Fact]
    public async Task ThePortalsOwnCookies_AreNotPassedOn_TheControllersAre()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        // In any case: ASP.NET reads cookie names without it, so .wreckfestportal.Auth signs in too.
        client.DefaultRequestHeaders.Add("Cookie", ".WreckfestPortal.Auth=secret; sid=1; .wreckfestportal.AuthC1=more; .WRECKFESTPORTAL.Antiforgery=x; other=2");

        using var response = await client.GetAsync("/tvtp2/api/auth/state", Ct);

        var forwarded = Assert.Single(_controller.Requests);
        Assert.Equal("sid=1; other=2", forwarded.Header("Cookie"));
    }

    [Fact]
    public async Task WithOnlyThePortalsCookies_NoCookieIsPassedOn()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        client.DefaultRequestHeaders.Add("Cookie", ".WreckfestPortal.Auth=secret");

        using var response = await client.GetAsync("/tvtp2/api/auth/state", Ct);

        Assert.Null(Assert.Single(_controller.Requests).Header("Cookie"));
    }

    [Fact]
    public async Task BehindATrustedProxy_TheVisitorItSpeaksFor_IsPassedOn()
    {
        await using var app = await StartAsync(trustedProxy: "10.0.0.1");
        using var client = Client(app, peer: "10.0.0.1");
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "198.51.100.7");
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");

        using var response = await client.GetAsync("/tvtp2/api/auth/state", Ct);

        var forwarded = Assert.Single(_controller.Requests);
        Assert.Equal("198.51.100.7", forwarded.Header("X-Forwarded-For"));
        Assert.Equal("https", forwarded.Header("X-Forwarded-Proto"));
    }

    [Fact]
    public async Task AControllerThatDoesNotAnswer_IsABadGateway()
    {
        _controller.Down = true;
        await using var app = await StartAsync();
        using var client = Client(app);

        using var response = await client.GetAsync("/tvtp2/api/auth/state", Ct);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Theory]
    [InlineData("/tvtp2/")]
    [InlineData("/tvtp2/index.html")]
    [InlineData("/tvtp2/admin/cups")]
    [InlineData("/tvtp2/tracks/fields14/1")]
    public async Task TheAppsRoutes_AreTheIndexPage_WithTheSitesPathWrittenIn(string path)
    {
        await using var app = await StartAsync();
        using var client = Client(app);

        using var response = await client.GetAsync(path, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync(Ct);
        Assert.Contains(IndexMarker, html, StringComparison.Ordinal);
        Assert.Contains("<base href=\"/tvtp2/\" />", html, StringComparison.Ordinal);
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
        Assert.Empty(_controller.Requests);
    }

    [Fact]
    public async Task Assets_AreServed_AndAMissingOneIsNotTheIndexPage()
    {
        await using var app = await StartAsync();
        using var client = Client(app);

        using var asset = await client.GetAsync("/tvtp2/assets/app-1.js", Ct);
        using var missing = await client.GetAsync("/tvtp2/assets/app-2.js", Ct);

        Assert.Equal("console.log('app')", await asset.Content.ReadAsStringAsync(Ct));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Theory]
    [InlineData("/tvtp2", "/tvtp2/")]
    [InlineData("/tvtp2?x=1", "/tvtp2/?x=1")]
    [InlineData("/TVTP2/login?x=1", "/tvtp2/login?x=1")]
    public async Task APageLoad_IsSentToTheSitesOwnSpelling_WithItsSlash(string path, string expected)
    {
        await using var app = await StartAsync();
        using var client = Client(app);

        using var response = await client.GetAsync(path, Ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(expected, response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task APathThatOnlyStartsLikeTheSite_IsNotIt()
    {
        await using var app = await StartAsync();
        using var client = Client(app);

        using var response = await client.GetAsync("/tvtp2x/api/auth/state", Ct);

        // Nothing answers it, so it needs a signed-in caller first, as on the controller.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_controller.Requests);
    }

    /// <summary>What the controller received.</summary>
    private sealed record Received(string Method, string Uri, IReadOnlyDictionary<string, string> Headers, string Body)
    {
        public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
    }

    /// <summary>Stands in for the controller: records each request and answers with a body and a cookie.</summary>
    private sealed class Controller : HttpMessageHandler
    {
        public List<Received> Requests { get; } = [];

        public bool Down { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Down)
            {
                throw new HttpRequestException("No connection could be made.");
            }

            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase);
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new Received(request.Method.Method, request.RequestUri!.ToString(), headers, body));

            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("from the controller") };
            response.Headers.Add("Set-Cookie", "sid=1; path=/tvtp2; httponly");
            return response;
        }
    }

    private sealed class PeerAddressStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                if (context.Request.Headers.TryGetValue(PeerHeader, out var peer))
                {
                    context.Connection.RemoteIpAddress = IPAddress.Parse(peer!);
                    context.Request.Headers.Remove(PeerHeader);
                }

                return nextMiddleware(context);
            });
            next(app);
        };
    }
}
