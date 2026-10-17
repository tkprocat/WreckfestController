using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WreckfestPortal.Accounts;

namespace WreckfestPortal.Tests;

/// <summary>A portal on a TestServer with its own database, two servers, and nothing behind them.</summary>
internal sealed class AccountTestHost : IAsyncDisposable
{
    public const string Password = "correct horse battery";
    public const string PeerHeader = "X-Test-Peer-Address";

    private readonly string _root;

    private AccountTestHost(WebApplication app, string root)
    {
        App = app;
        _root = root;
    }

    public WebApplication App { get; }

    public static async Task<AccountTestHost> StartAsync(Action<IServiceCollection>? configureServices = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "wfc-portal-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "wwwroot"));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { WebRootPath = Path.Combine(root, "wwwroot") });
        builder.WebHost.UseTestServer();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Portal:Servers:0:Id"] = "vanilla",
            ["Portal:Servers:0:Name"] = "Vanilla",
            ["Portal:Servers:0:ApiUrl"] = "http://10.9.8.7:5100",
            ["Portal:Servers:1:Id"] = "tvtp2",
            ["Portal:Servers:1:Name"] = "TVTP2",
            ["Portal:Servers:1:ApiUrl"] = "http://10.9.8.7:5102",
            ["Portal:AppRoot"] = Path.Combine(root, "app"),
            ["Portal:DatabasePath"] = Path.Combine(root, "data", "portal.db"),
        });
        PortalApp.ConfigureServices(builder.Services, builder.Configuration);
        builder.Services.AddHttpClient(ServerPoller.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new FakeController());
        builder.Services.AddSingleton<IStartupFilter, PeerAddressStartupFilter>();
        configureServices?.Invoke(builder.Services);

        var app = builder.Build();
        PortalApp.ConfigurePipeline(app);
        await app.StartAsync(TestContext.Current.CancellationToken);
        return new AccountTestHost(app, root);
    }

    /// <summary>A global admin, as <c>create-admin</c> makes one.</summary>
    public async Task CreateAdminAsync(string userName = "admin")
    {
        var exit = await CreateAdminCommand.CreateAsync(App.Services, userName, userName + "@example.com", Password, TextWriter.Null);
        Assert.Equal(0, exit);
    }

    public HttpClient CreateClient() => App.GetTestClient();

    public Browser CreateBrowser(string peer = "203.0.113.5")
    {
        var server = App.GetTestServer();
        var browser = new Browser(server.CreateHandler(), server.BaseAddress);
        browser.Http.DefaultRequestHeaders.Add(PeerHeader, peer);
        return browser;
    }

    /// <summary>A browser signed in as <paramref name="userName"/>.</summary>
    public async Task<Browser> SignInAsync(string userName, string password = Password)
    {
        var browser = CreateBrowser();
        using var response = await browser.LoginAsync(userName, password);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return browser;
    }

    public async ValueTask DisposeAsync()
    {
        await App.DisposeAsync();
        TestDatabase.Delete(Path.Combine(_root, "data", "portal.db"));
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>
    /// Behaves like the web app's fetch wrapper: keeps cookies between requests and copies the
    /// <c>XSRF-TOKEN</c> cookie into the <c>X-XSRF-TOKEN</c> header on unsafe methods.
    /// </summary>
    public sealed class Browser : IDisposable
    {
        private readonly Handler _handler;

        public Browser(HttpMessageHandler server, Uri baseAddress)
        {
            _handler = new Handler { InnerHandler = server };
            Http = new HttpClient(_handler) { BaseAddress = baseAddress };
        }

        public HttpClient Http { get; }

        public CookieContainer Cookies => _handler.Cookies;

        public bool SendXsrfHeader
        {
            get => _handler.SendXsrfHeader;
            set => _handler.SendXsrfHeader = value;
        }

        public async Task FetchAntiforgeryAsync()
        {
            using var response = await Http.GetAsync("/api/auth/antiforgery", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        /// <summary>The web app's sequence: token, sign in, fresh token for the new identity.</summary>
        public async Task<HttpResponseMessage> LoginAsync(string login, string password, bool remember = false)
        {
            await FetchAntiforgeryAsync();
            var response = await Http.PostAsJsonAsync("/api/auth/login", new { login, password, remember }, TestContext.Current.CancellationToken);
            if (response.IsSuccessStatusCode)
            {
                await FetchAntiforgeryAsync();
            }

            return response;
        }

        public void Dispose() => Http.Dispose();

        private sealed class Handler : DelegatingHandler
        {
            public CookieContainer Cookies { get; } = new();

            public bool SendXsrfHeader { get; set; } = true;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var uri = request.RequestUri!;
                var cookieHeader = Cookies.GetCookieHeader(uri);
                if (cookieHeader.Length > 0)
                {
                    request.Headers.Add("Cookie", cookieHeader);
                }

                if (SendXsrfHeader && request.Method != HttpMethod.Get && request.Method != HttpMethod.Head
                    && Cookies.GetCookies(uri)[PortalAccounts.XsrfCookieName] is { } xsrf)
                {
                    request.Headers.Add(PortalAccounts.XsrfHeaderName, xsrf.Value);
                }

                var response = await base.SendAsync(request, cancellationToken);
                if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
                {
                    foreach (var setCookie in setCookies)
                    {
                        Cookies.SetCookies(uri, setCookie);
                    }
                }

                return response;
            }
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
