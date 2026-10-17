using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using WreckfestController.Services.Auth;

namespace WreckfestController.Tests.Api;

/// <summary>
/// The controller's only sign-in for people: the portal's token, signed with this server's
/// secret and addressed to its id. Anything else is no one.
/// </summary>
public sealed class PortalTokenTests
{
    private const string AdminOnlyPath = "/api/server/status";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ATokenFromThePortal_ForThisServer_SignsIn()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreatePortalClient();

        using var response = await client.GetAsync(AdminOnlyPath, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    public static TheoryData<string, string> NotForThisServer => new()
    {
        { "another server's", ApiTestHost.PortalToken(audience: "vanilla") },
        { "a forged", ApiTestHost.PortalToken(secret: "a-different-secret-of-at-least-32-chars") },
        { "an expired", ApiTestHost.PortalToken(expiresIn: TimeSpan.FromMinutes(-2)) },
        { "another issuer's", ApiTestHost.PortalToken(issuer: "SomeoneElse") },
        { "a malformed", "not.a.token" },
    };

    [Theory]
    [MemberData(nameof(NotForThisServer))]
    public async Task AnyOtherToken_IsNoOne(string kind, string token)
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.GetAsync(AdminOnlyPath, Ct);

        Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{kind} token got {response.StatusCode}");
    }

    // An unsigned token ("alg": "none") must not get past the algorithm check.
    [Fact]
    public async Task AnUnsignedToken_IsNoOne()
    {
        await using var host = await ApiTestHost.StartAsync();
        var signed = ApiTestHost.PortalToken();
        var unsigned = Base64Url("""{"alg":"none","typ":"JWT"}""") + "." + signed.Split('.')[1] + ".";
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", unsigned);

        using var response = await client.GetAsync(AdminOnlyPath, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // Without the portal's settings no token is anyone, though scripts keep working.
    [Fact]
    public async Task WithoutThePortalsSettings_OnlyTheApiKeySignsIn()
    {
        await using var host = await ApiTestHost.StartAsync(new Dictionary<string, string?>
        {
            [ApiAuthentication.PortalSecretKey] = null,
            [ApiAuthentication.PortalServerIdKey] = null,
        });
        using var portal = host.CreatePortalClient();
        using var script = host.CreateAuthenticatedClient();

        using var refused = await portal.GetAsync(AdminOnlyPath, Ct);
        using var allowed = await script.GetAsync(AdminOnlyPath, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Theory]
    [InlineData(ApiTestHost.PortalSecret, null, "Set both")]
    [InlineData(null, "tvtp2", "Set both")]
    [InlineData("too-short", "tvtp2", "at least 32")]
    public void UnusablePortalSettings_SayWhy(string? secret, string? serverId, string expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ApiAuthentication.PortalSecretKey] = secret,
                [ApiAuthentication.PortalServerIdKey] = serverId,
            })
            .Build();

        var error = Assert.Throws<PortalTrustException>(() => ApiAuthentication.ResolvePortal(configuration));

        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    private static string Base64Url(string json) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
