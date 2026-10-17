using System.Net;
using WreckfestController.Services.Auth;

namespace WreckfestController.Tests.Api;

/// <summary>
/// Behind the portal each controller's site has a prefix (<c>/tvtp2</c>) that the portal
/// strips and sends as <c>X-Forwarded-Prefix</c>. The cookies belong to that prefix, so two
/// controllers behind one portal never see each other's.
/// </summary>
public sealed class ForwardedPrefixTests
{
    private const string Portal = "10.0.0.1";
    private const string Prefix = "/tvtp2";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Dictionary<string, string?> TrustingThePortal => new() { [TrustedProxies.ConfigKey] = Portal };

    [Fact]
    public async Task FromTheTrustedPortal_TheCookiesBelongToThePrefix()
    {
        await using var host = await ApiTestHost.StartAsync(TrustingThePortal);
        await host.CreateUserAsync("admin");
        using var browser = Browser(host, peer: Portal);

        await browser.SignInAsync("admin");
        using var me = await browser.Http.GetAsync(Prefix + "/api/auth/me", Ct);

        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var cookies = browser.Cookies.GetAllCookies();
        Assert.Contains(cookies, c => c.Name == ApiAuthentication.XsrfCookieName);
        Assert.All(cookies, c => Assert.Equal(Prefix, c.Path));
    }

    [Fact]
    public async Task AnotherPrefixOnTheSamePortal_GetsNoneOfTheCookies()
    {
        await using var host = await ApiTestHost.StartAsync(TrustingThePortal);
        await host.CreateUserAsync("admin");
        using var browser = Browser(host, peer: Portal);

        await browser.SignInAsync("admin");

        Assert.NotEmpty(browser.Cookies.GetCookies(new Uri(browser.Http.BaseAddress!, Prefix + "/api/auth/me")));
        Assert.Empty(browser.Cookies.GetCookies(new Uri(browser.Http.BaseAddress!, "/vanilla/api/auth/me")));
    }

    // Anyone else's X-Forwarded-Prefix is ignored, like their X-Forwarded-For.
    [Theory]
    [InlineData(null, Portal)]
    [InlineData(Portal, "203.0.113.5")]
    public async Task FromAnUntrustedPeer_ThePrefixIsIgnored(string? trusted, string peer)
    {
        await using var host = await ApiTestHost.StartAsync(new Dictionary<string, string?> { [TrustedProxies.ConfigKey] = trusted });
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(ApiTestHost.PeerAddressHeader, peer);
        client.DefaultRequestHeaders.Add("X-Forwarded-Prefix", Prefix);

        using var response = await client.GetAsync("/api/auth/antiforgery", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.All(
            response.Headers.GetValues("Set-Cookie"),
            cookie => Assert.Contains("path=/;", cookie + ";", StringComparison.OrdinalIgnoreCase));
    }

    private static BrowserClient Browser(ApiTestHost host, string peer)
    {
        var browser = host.CreateBrowser(Prefix);
        browser.Http.DefaultRequestHeaders.Add(ApiTestHost.PeerAddressHeader, peer);
        return browser;
    }
}
