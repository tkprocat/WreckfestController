using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using WreckfestController.Services;

namespace WreckfestController.Tests.Api;

/// <summary>
/// Api:TrustedProxies and the per-IP login limit. Clients are told apart by the address
/// ForwardedHeadersMiddleware settles on, so the two are tested together.
/// </summary>
public class ProxyAndRateLimitTests
{
    private const string Proxy = "10.0.0.1";
    private const string ClientA = "203.0.113.5";
    private const string ClientB = "203.0.113.6";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Parse_ReadsAnArrayOfAddressesAndRanges()
    {
        var parsed = TrustedProxies.Parse(Config(new()
        {
            ["Api:TrustedProxies:0"] = "192.168.1.1",
            ["Api:TrustedProxies:1"] = "10.0.0.0/24",
            ["Api:TrustedProxies:2"] = "fd00::1",
        }));

        Assert.Equal([IPAddress.Parse("192.168.1.1"), IPAddress.Parse("fd00::1")], parsed.Proxies);
        Assert.Equal([System.Net.IPNetwork.Parse("10.0.0.0/24")], parsed.Networks);
        Assert.Empty(parsed.Invalid);
    }

    [Fact]
    public void Parse_ReadsACommaSeparatedString_AndReportsWhatItCannotUse()
    {
        var parsed = TrustedProxies.Parse(Config(new()
        {
            ["Api:TrustedProxies"] = " 192.168.1.1, proxy.lan ; 10.0.0.0/33,10.0.0.0/8",
        }));

        Assert.Equal([IPAddress.Parse("192.168.1.1")], parsed.Proxies);
        Assert.Equal([System.Net.IPNetwork.Parse("10.0.0.0/8")], parsed.Networks);
        Assert.Equal(["proxy.lan", "10.0.0.0/33"], parsed.Invalid);
    }

    [Fact]
    public void Parse_TrustsNothingWhenUnset()
    {
        var parsed = TrustedProxies.Parse(Config(new()));

        Assert.Empty(parsed.Proxies);
        Assert.Empty(parsed.Networks);
    }

    [Fact]
    public async Task Login_EleventhAttemptInAMinute_Returns429WithRetryAfter()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var browser = Browser(host, peer: ClientA);

        await ExhaustLoginLimitAsync(browser);
        using var response = await browser.LoginAsync("nobody", ApiTestHost.Password);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter?.Delta);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(
            "Too many sign-in attempts. Wait a minute and try again.",
            problem.GetProperty("title").GetString());
    }

    // The limit also stops the right password: a sprayer that finally guesses one
    // should not get in until the window passes.
    [Fact]
    public async Task Login_OverTheLimit_IsRefusedEvenWithTheRightPassword()
    {
        await using var host = await ApiTestHost.StartAsync();
        await host.CreateUserAsync();
        using var browser = Browser(host, peer: ClientA);

        await ExhaustLoginLimitAsync(browser);
        using var response = await browser.LoginAsync("admin", ApiTestHost.Password);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    [Fact]
    public async Task Login_LimitIsPerClientIp()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var first = Browser(host, peer: ClientA);
        using var second = Browser(host, peer: ClientB);

        await ExhaustLoginLimitAsync(first);
        using var response = await second.LoginAsync("nobody", ApiTestHost.Password);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task BehindATrustedProxy_EachForwardedClientHasItsOwnLimit()
    {
        await using var host = await ApiTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Api:TrustedProxies:0"] = "10.0.0.0/24",
        });
        using var first = Browser(host, peer: Proxy, forwardedFor: ClientA);
        using var second = Browser(host, peer: Proxy, forwardedFor: ClientB);

        await ExhaustLoginLimitAsync(first);
        using var response = await second.LoginAsync("nobody", ApiTestHost.Password);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // A client that is not a trusted proxy - loopback included, which ASP.NET Core
    // trusts by default - cannot get a fresh limit by writing its own X-Forwarded-For.
    [Theory]
    [InlineData(ClientA)]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public async Task FromAnUntrustedPeer_ForwardedForIsIgnored(string peer)
    {
        await using var host = await ApiTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Api:TrustedProxies"] = Proxy,
        });
        using var browser = Browser(host, peer);

        for (var attempt = 0; attempt < LoginRateLimit.PermitsPerWindow; attempt++)
        {
            SetForwardedFor(browser, $"198.51.100.{attempt}");
            using var allowed = await browser.LoginAsync("nobody", ApiTestHost.Password);
            Assert.Equal(HttpStatusCode.Unauthorized, allowed.StatusCode);
        }

        SetForwardedFor(browser, "198.51.100.200");
        using var response = await browser.LoginAsync("nobody", ApiTestHost.Password);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    // The proxy terminates TLS, so cookies are marked Secure only when a trusted proxy
    // says the browser used HTTPS. Cookies are passed by hand: a CookieContainer would
    // withhold the Secure ones from the TestServer's http:// address.
    [Theory]
    [InlineData(Proxy, true)]
    [InlineData(ClientA, false)]
    public async Task ForwardedProto_FromATrustedProxy_MakesTheCookiesSecure(string peer, bool secure)
    {
        await using var host = await ApiTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Api:TrustedProxies"] = Proxy,
        });
        await host.CreateUserAsync();
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(ApiTestHost.PeerAddressHeader, peer);
        client.DefaultRequestHeaders.Add("X-Forwarded-For", ClientB);
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");

        using var tokenResponse = await client.GetAsync("/api/auth/antiforgery", Ct);
        var tokenCookies = tokenResponse.Headers.GetValues("Set-Cookie").ToList();
        using var login = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { login = "admin", password = ApiTestHost.Password }),
        };
        login.Headers.Add("Cookie", string.Join("; ", tokenCookies.Select(c => c.Split(';')[0])));
        login.Headers.Add(ApiAuthentication.XsrfHeaderName, Uri.UnescapeDataString(
            tokenCookies.Single(c => c.StartsWith(ApiAuthentication.XsrfCookieName + "=", StringComparison.Ordinal))
                .Split(';')[0][(ApiAuthentication.XsrfCookieName.Length + 1)..]));
        using var response = await client.SendAsync(login, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookies = tokenCookies.Concat(response.Headers.GetValues("Set-Cookie")).ToList();
        Assert.Contains(cookies, c => c.StartsWith(".AspNetCore.Identity.Application=", StringComparison.Ordinal));
        Assert.All(cookies, c => Assert.Equal(secure, c.Contains("; secure", StringComparison.OrdinalIgnoreCase)));
    }

    private static BrowserClient Browser(ApiTestHost host, string peer, string? forwardedFor = null)
    {
        var browser = host.CreateBrowser();
        browser.Http.DefaultRequestHeaders.Add(ApiTestHost.PeerAddressHeader, peer);
        if (forwardedFor is not null)
        {
            SetForwardedFor(browser, forwardedFor);
        }

        return browser;
    }

    private static void SetForwardedFor(BrowserClient browser, string client)
    {
        browser.Http.DefaultRequestHeaders.Remove("X-Forwarded-For");
        browser.Http.DefaultRequestHeaders.Add("X-Forwarded-For", client);
    }

    /// <summary>Uses up the window with an unknown account, so no account locks.</summary>
    private static async Task ExhaustLoginLimitAsync(BrowserClient browser)
    {
        for (var attempt = 0; attempt < LoginRateLimit.PermitsPerWindow; attempt++)
        {
            using var response = await browser.LoginAsync("nobody", ApiTestHost.Password);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
