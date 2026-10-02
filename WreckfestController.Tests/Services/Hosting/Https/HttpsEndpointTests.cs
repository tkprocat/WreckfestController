using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using WreckfestController.Services.Hosting.Https;

namespace WreckfestController.Tests.Services.Hosting.Https;

/// <summary>
/// Real Kestrel, real TLS: what a browser is handed on a new connection. A TestServer
/// cannot do TLS, so these bind two loopback ports of their own.
/// </summary>
public sealed class HttpsEndpointTests : IAsyncDisposable
{
    private const string Host = "wf.example.com";
    private readonly TestCertificates _ca = new();
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    private WebApplication? _app;
    private CertificateProvider? _provider;
    private int _httpsPort;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }

        _provider?.Dispose();
        _ca.Dispose();
    }

    [Fact]
    public async Task AConnection_GetsTheCertificate_WithItsChain()
    {
        var leaf = _ca.Leaf([Host]);
        await StartAsync(_ca.Pfx(leaf));

        Assert.Equal(leaf.Thumbprint, await HandshakeAsync());
        // The intermediate is handed to TLS with the certificate: without it, clients that do
        // not have it cannot build the chain. Checked here rather than on the wire, because
        // Windows (SChannel) sends intermediates only for a chain that ends in a root this
        // machine trusts, and the test root is not installed as one.
        Assert.Contains(
            _ca.Intermediate.Thumbprint,
            _provider!.Current.Context.IntermediateCertificates.Select(c => c.Thumbprint));
    }

    [Fact]
    public async Task APemWithItsKey_IsServed()
    {
        var leaf = _ca.Leaf([Host]);
        var (certificate, key) = _ca.Pem(leaf);
        await StartAsync(new FileSourceSettings(certificate, key, null));

        Assert.Equal(leaf.Thumbprint, await HandshakeAsync());
    }

    // A renewal replaces the file; the next new connection gets the new certificate, and
    // no restart is needed.
    [Fact]
    public async Task AReplacedCertificate_IsServedOnTheNextConnection()
    {
        var first = _ca.Leaf([Host]);
        var path = _ca.Pfx(first);
        await StartAsync(path);
        Assert.Equal(first.Thumbprint, await HandshakeAsync());

        var renewed = _ca.Leaf([Host], notBefore: DateTimeOffset.UtcNow.AddMinutes(-1));
        _ca.Pfx(renewed);
        _provider!.Refresh();

        Assert.Equal(renewed.Thumbprint, await HandshakeAsync());
    }

    // A broken renewal keeps the working certificate in service, and says why.
    [Fact]
    public async Task ABrokenReplacement_KeepsTheWorkingCertificate()
    {
        var first = _ca.Leaf([Host]);
        var path = _ca.Pfx(first);
        await StartAsync(path);

        File.WriteAllText(path, "not a certificate");
        _provider!.Refresh();

        Assert.Equal(first.Thumbprint, await HandshakeAsync());
        Assert.NotNull(_provider.Status.Error);
    }

    // Past expiry, with nothing better found, handshakes are refused: never plain HTTP instead.
    [Fact]
    public async Task AnExpiredCertificate_IsNotServed()
    {
        await StartAsync(_ca.Pfx(_ca.Leaf([Host], notAfter: DateTimeOffset.UtcNow.AddDays(2))));
        _now = DateTimeOffset.UtcNow.AddDays(3);

        await Assert.ThrowsAsync<HttpRequestException>(() => HandshakeAsync());
    }

    private Task StartAsync(string pfx) => StartAsync(new FileSourceSettings(pfx, null, null));

    private async Task StartAsync(FileSourceSettings settings)
    {
        _provider = new CertificateProvider(new FileCertificateSource(settings, () => _now), () => _now, NullLogger.Instance);
        _provider.Start();
        var httpPort = FreePort();
        _httpsPort = FreePort();
        var endpoints = new ApiEndpoints(IPAddress.Loopback, httpPort, _httpsPort, new HttpsSettings(settings, _httpsPort, false));
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options => HttpsEndpoint.Configure(options, endpoints, _provider));
        _app = builder.Build();
        _app.MapGet("/", () => "ok");
        await _app.StartAsync(Ct);
    }

    /// <summary>A new connection (no pooling): the thumbprint of the certificate presented.</summary>
    private async Task<string> HandshakeAsync()
    {
        string? presented = null;
        using var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.Zero,
            SslOptions =
            {
                TargetHost = Host,
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                {
                    presented = (certificate as X509Certificate2 ?? X509CertificateLoader.LoadCertificate(certificate!.GetRawCertData())).Thumbprint;
                    // The test root is not trusted by this machine: the test checks what is presented.
                    return true;
                },
            },
        };
        using var client = new HttpClient(handler);
        var body = await client.GetStringAsync($"https://127.0.0.1:{_httpsPort}/", Ct);
        Assert.Equal("ok", body);
        return presented!;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
