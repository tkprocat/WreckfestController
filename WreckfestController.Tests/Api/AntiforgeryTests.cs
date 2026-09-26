using System.Net;
using System.Net.Http.Json;
using WreckfestController.Services;

namespace WreckfestController.Tests.Api;

/// <summary>
/// CSRF protection applies to browser requests and never to API-key requests, which
/// carry no antiforgery cookie.
/// </summary>
public class AntiforgeryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly object NewUser = new
    {
        userName = "second",
        email = "second@example.com",
        password = ApiTestHost.Password,
    };

    [Fact]
    public async Task CookiePost_WithoutToken_Returns400()
    {
        await using var host = await ApiTestHost.StartAsync();
        await host.CreateUserAsync();
        using var browser = host.CreateBrowser();
        await browser.SignInAsync("admin");
        browser.SendXsrfHeader = false;

        using var response = await browser.Http.PostAsJsonAsync("/api/users", NewUser, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CookiePost_WithToken_Succeeds()
    {
        await using var host = await ApiTestHost.StartAsync();
        await host.CreateUserAsync();
        using var browser = host.CreateBrowser();
        await browser.SignInAsync("admin");

        using var response = await browser.Http.PostAsJsonAsync("/api/users", NewUser, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // The token is tied to the identity it was issued to, which is why the SPA must
    // fetch a fresh one after signing in.
    [Fact]
    public async Task TokenIssuedBeforeLogin_IsRejectedAfterIt_AndAFreshOneWorks()
    {
        await using var host = await ApiTestHost.StartAsync();
        await host.CreateUserAsync();
        using var browser = host.CreateBrowser();

        await browser.FetchAntiforgeryAsync();
        using (var login = await browser.Http.PostAsJsonAsync(
                   "/api/auth/login", new { login = "admin", password = ApiTestHost.Password }, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        }

        using (var stale = await browser.Http.PostAsJsonAsync("/api/users", NewUser, Ct))
        {
            Assert.Equal(HttpStatusCode.BadRequest, stale.StatusCode);
        }

        await browser.FetchAntiforgeryAsync();
        using var fresh = await browser.Http.PostAsJsonAsync("/api/users", NewUser, Ct);
        Assert.Equal(HttpStatusCode.Created, fresh.StatusCode);
    }

    // A script's request shape: key, no cookie, no token.
    [Fact]
    public async Task ApiKeyPost_WithoutToken_Succeeds()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateAuthenticatedClient();

        using var response = await client.PostAsJsonAsync("/api/users", NewUser, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // Login is anonymous, so without this a hostile page could sign the browser in to
    // an account of the attacker's choosing.
    [Fact]
    public async Task AnonymousLogin_WithoutToken_Returns400()
    {
        await using var host = await ApiTestHost.StartAsync();
        await host.CreateUserAsync();
        using var browser = host.CreateBrowser();

        using var response = await browser.Http.PostAsJsonAsync(
            "/api/auth/login", new { login = "admin", password = ApiTestHost.Password }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TokenCookie_IsReadableByScript_AndStrict()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateClient();

        using var response = await client.GetAsync("/api/auth/antiforgery", Ct);

        var xsrf = Assert.Single(
            response.Headers.GetValues("Set-Cookie"),
            c => c.StartsWith(ApiAuthentication.XsrfCookieName + "=", StringComparison.Ordinal));
        var attributes = xsrf.Split(';').Select(a => a.Trim().ToLowerInvariant()).ToList();
        Assert.DoesNotContain("httponly", attributes);
        Assert.Contains("samesite=strict", attributes);
    }
}
