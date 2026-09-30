using System.Net;
using System.Net.Http;
using WreckfestController.Services.Hosting.Https;

namespace WreckfestController.Tests.Api;

/// <summary>
/// With HTTPS on, plain HTTP is for this PC only: remote page loads are sent to the public
/// HTTPS origin, everything else remote over HTTP is refused. Through the whole pipeline,
/// with the TCP peer set per request.
/// </summary>
public sealed class HttpsEnforcementTests
{
    private const string Remote = "203.0.113.5";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Dictionary<string, string?> HttpsOn(string publicPort = "443", string hsts = "false", string? trustedProxy = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Api:Https:Path"] = "server.pfx",
            ["Api:Https:PublicHost"] = "wf.example.com",
            ["Api:Https:PublicPort"] = publicPort,
            ["Api:Https:Hsts"] = hsts,
        };
        if (trustedProxy is not null)
        {
            settings["Api:TrustedProxies:0"] = trustedProxy;
        }

        return settings;
    }

    private static async Task<HttpResponseMessage> SendAsync(
        ApiTestHost host,
        HttpMethod method,
        string path,
        string? peer = Remote,
        bool https = false,
        IDictionary<string, string>? headers = null,
        string? hostHeader = null)
    {
        var client = host.CreateClient();
        client.BaseAddress = new Uri(https ? "https://localhost" : "http://localhost");
        using var request = new HttpRequestMessage(method, path);
        if (peer is not null)
        {
            request.Headers.Add(ApiTestHost.PeerAddressHeader, peer);
        }

        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
        {
            request.Headers.Add(name, value);
        }

        if (hostHeader is not null)
        {
            request.Headers.Host = hostHeader;
        }

        if (method == HttpMethod.Post)
        {
            request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        }

        return await client.SendAsync(request, Ct);
    }

    // Without Api:Https nothing changes: a proxy that ends TLS keeps working as before.
    [Fact]
    public async Task WithHttpsOff_RemotePlainHttp_IsServedAsBefore()
    {
        await using var host = await ApiTestHost.StartAsync();

        using var response = await SendAsync(host, HttpMethod.Get, "/api/auth/state");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ARemotePageLoad_IsSentToThePublicHttpsOrigin()
    {
        await using var host = await ApiTestHost.StartAsync(HttpsOn());

        using var response = await SendAsync(host, HttpMethod.Get, "/admin/cups?tab=next");

        Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
        Assert.Equal("https://wf.example.com/admin/cups?tab=next", response.Headers.Location!.ToString());
    }

    // The client picks the Host header; the redirect never goes where it says.
    [Fact]
    public async Task TheRedirect_IgnoresTheRequestsHostHeader_AndKeepsAPublicPort()
    {
        await using var host = await ApiTestHost.StartAsync(HttpsOn(publicPort: "8443"));

        using var response = await SendAsync(host, HttpMethod.Get, "/", hostHeader: "evil.test");

        Assert.Equal("https://wf.example.com:8443/", response.Headers.Location!.ToString());
    }

    // A password already sent over HTTP cannot be taken back by a redirect: refused.
    [Theory]
    [InlineData("GET", "/api/auth/state")]
    [InlineData("POST", "/api/auth/login")]
    [InlineData("POST", "/admin")]
    [InlineData("GET", "/hubs/server/negotiate")]
    public async Task RemotePlainHttp_ToTheApi_OrUnsafe_IsRefused(string method, string path)
    {
        await using var host = await ApiTestHost.StartAsync(HttpsOn());

        using var response = await SendAsync(host, new HttpMethod(method), path);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Use HTTPS", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    // Tools on this PC keep plain HTTP: the peer is loopback and no proxy spoke for anyone.
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    public async Task ALoopbackPeer_KeepsPlainHttp(string peer)
    {
        await using var host = await ApiTestHost.StartAsync(HttpsOn());

        using var response = await SendAsync(host, HttpMethod.Get, "/api/auth/state", peer);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // A proxy on this PC carries remote traffic: its plain-HTTP requests are remote ones,
    // with or without the client's address. (The middleware also treats X-Original-* as
    // "came through a proxy", a second guard these requests cannot reach on their own.)
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ATrustedProxyOnLoopback_IsNotExempt(bool forwardsTheClient)
    {
        await using var host = await ApiTestHost.StartAsync(HttpsOn(trustedProxy: "127.0.0.1"));
        var headers = new Dictionary<string, string> { ["X-Forwarded-Proto"] = "http" };
        if (forwardsTheClient)
        {
            headers["X-Forwarded-For"] = Remote;
        }

        using var response = await SendAsync(host, HttpMethod.Get, "/api/auth/state", "127.0.0.1", headers: headers);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // A proxy that ends TLS and says so is HTTPS: served, no local certificate needed for it.
    [Fact]
    public async Task HttpsForwardedByATrustedProxy_IsServed()
    {
        await using var host = await ApiTestHost.StartAsync(HttpsOn(trustedProxy: "192.168.1.1"));

        using var response = await SendAsync(host, HttpMethod.Get, "/api/auth/state", "192.168.1.1", headers: new Dictionary<string, string>
        {
            ["X-Forwarded-For"] = Remote,
            ["X-Forwarded-Proto"] = "https",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // Anyone can write X-Forwarded-Proto: from a peer that is not a trusted proxy it counts for nothing.
    [Fact]
    public async Task ForwardedHttpsFromAnUntrustedPeer_IsStillPlainHttp()
    {
        await using var host = await ApiTestHost.StartAsync(HttpsOn(trustedProxy: "192.168.1.1"));

        using var response = await SendAsync(host, HttpMethod.Get, "/api/auth/state", Remote, headers: new Dictionary<string, string>
        {
            ["X-Forwarded-Proto"] = "https",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Hsts_IsSentOverHttps_OnlyWhenOn_AndNeverForLocalNames()
    {
        await using var on = await ApiTestHost.StartAsync(HttpsOn(hsts: "true"));
        await using var off = await ApiTestHost.StartAsync(HttpsOn(hsts: "false"));

        using var sent = await SendAsync(on, HttpMethod.Get, "/api/auth/state", https: true, hostHeader: "wf.example.com");
        using var local = await SendAsync(on, HttpMethod.Get, "/api/auth/state", https: true, hostHeader: "localhost");
        using var notOn = await SendAsync(off, HttpMethod.Get, "/api/auth/state", https: true, hostHeader: "wf.example.com");
        using var plain = await SendAsync(on, HttpMethod.Get, "/api/auth/state", "127.0.0.1", hostHeader: "wf.example.com");

        Assert.Equal(HttpsEnforcementMiddleware.HstsValue, sent.Headers.GetValues("Strict-Transport-Security").Single());
        Assert.False(local.Headers.Contains("Strict-Transport-Security"));
        Assert.False(notOn.Headers.Contains("Strict-Transport-Security"));
        Assert.False(plain.Headers.Contains("Strict-Transport-Security"));
    }
}
