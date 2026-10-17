using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using WreckfestController.Services.Auth;

namespace WreckfestController.Tests.Api;

/// <summary>
/// Api:TrustedProxies and the per-IP limits. Clients are told apart by the address
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
    public async Task OverTheLimit_Returns429WithRetryAfter()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = Client(host, peer: ClientA);

        await ExhaustLimitAsync(client);
        using var response = await client.GetAsync(LimitedPath, Ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter?.Delta);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("Too many requests. Wait a minute and try again.", problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task TheLimitIsPerClientIp()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var first = Client(host, peer: ClientA);
        using var second = Client(host, peer: ClientB);

        await ExhaustLimitAsync(first);
        using var response = await second.GetAsync(LimitedPath, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task BehindATrustedProxy_EachForwardedClientHasItsOwnLimit()
    {
        await using var host = await ApiTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Api:TrustedProxies:0"] = "10.0.0.0/24",
        });
        using var first = Client(host, peer: Proxy, forwardedFor: ClientA);
        using var second = Client(host, peer: Proxy, forwardedFor: ClientB);

        await ExhaustLimitAsync(first);
        using var response = await second.GetAsync(LimitedPath, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
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

        await AssertOneLimitForEveryForwardedForAsync(host, peer);
    }

    // With no usable proxy listed, nothing is trusted. ASP.NET Core reads EMPTY known-proxy
    // lists as "trust every peer", so clearing its loopback defaults and adding nothing
    // let any client pick its own IP - and a fresh limit - per request.
    [Theory]
    [InlineData(null, ClientA)]
    [InlineData("", ClientA)]
    [InlineData("proxy.lan, 10.0.0.0/33", ClientA)]
    [InlineData(null, "127.0.0.1")]
    public async Task WithNoTrustedProxy_ForwardedForIsIgnored(string? trusted, string peer)
    {
        await using var host = await ApiTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Api:TrustedProxies"] = trusted,
        });

        await AssertOneLimitForEveryForwardedForAsync(host, peer);
    }

    /// <summary>The anonymous public overview: limited per client, and answered in a test host.</summary>
    private const string LimitedPath = "/api/public/overview";

    private static async Task AssertOneLimitForEveryForwardedForAsync(ApiTestHost host, string peer)
    {
        using var client = Client(host, peer);
        for (var attempt = 0; attempt < RateLimits.PublicPermitsPerWindow; attempt++)
        {
            SetForwardedFor(client, $"198.51.100.{attempt % 250}");
            using var allowed = await client.GetAsync(LimitedPath, Ct);
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        SetForwardedFor(client, "198.51.100.251");
        using var response = await client.GetAsync(LimitedPath, Ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    private static HttpClient Client(ApiTestHost host, string peer, string? forwardedFor = null)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(ApiTestHost.PeerAddressHeader, peer);
        if (forwardedFor is not null)
        {
            SetForwardedFor(client, forwardedFor);
        }

        return client;
    }

    private static void SetForwardedFor(HttpClient client, string forwardedFor)
    {
        client.DefaultRequestHeaders.Remove("X-Forwarded-For");
        client.DefaultRequestHeaders.Add("X-Forwarded-For", forwardedFor);
    }

    private static async Task ExhaustLimitAsync(HttpClient client)
    {
        for (var attempt = 0; attempt < RateLimits.PublicPermitsPerWindow; attempt++)
        {
            using var response = await client.GetAsync(LimitedPath, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
