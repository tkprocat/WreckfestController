using WreckfestController.Services.Hosting.Https;

namespace WreckfestController.Tests.Services.Hosting.Https;

/// <summary>Where a remote browser is redirected, and what the desktop app says about the API.</summary>
public sealed class HttpsPolicyTests
{
    private static readonly StoreSourceSettings Store = new("store.example.com", "My", System.Security.Cryptography.X509Certificates.StoreLocation.CurrentUser);
    private static readonly FileSourceSettings File = new(@"C:\certs\server.pfx", null, null);

    private static HttpsStatus Serving(params string[] names) =>
        new(true, "CN=" + names.FirstOrDefault(), names, "CN=Issuer", DateTimeOffset.UtcNow.AddDays(60), false, DateTimeOffset.UtcNow, null);

    [Fact]
    public void PublicHost_WinsOverTheStoreHostAndTheCertificate()
    {
        var policy = new HttpsPolicy(new HttpsSettings(Store, 443, false, "public.example.com"), HttpsStatusSource.None);

        Assert.Equal("https://public.example.com", policy.PublicOrigin);
    }

    [Fact]
    public void WithoutPublicHost_TheStoresHostIsUsed()
    {
        var policy = new HttpsPolicy(new HttpsSettings(Store, 8443, false), HttpsStatusSource.None);

        Assert.Equal("https://store.example.com:8443", policy.PublicOrigin);
    }

    // A file has no Host setting: the certificate's own name, not a wildcard.
    [Fact]
    public void ForAFile_TheCertificatesFirstExactNameIsUsed()
    {
        var status = new HttpsStatusSource(() => Serving("*.example.com", "wf.example.com"));
        var policy = new HttpsPolicy(new HttpsSettings(File, 443, false), status);

        Assert.Equal("https://wf.example.com", policy.PublicOrigin);
    }

    // No name known: no redirect (the request is refused instead), never one built from the request.
    [Fact]
    public void WithNoNameKnown_ThereIsNoOrigin()
    {
        var policy = new HttpsPolicy(new HttpsSettings(File, 443, false), HttpsStatusSource.None);

        Assert.Null(policy.PublicOrigin);
    }

    [Fact]
    public void TheDesktopStatus_SaysWhyTheApiDidNotStart()
    {
        var text = ApiStatusDescription.Describe(false, "http://127.0.0.1:5100", "The certificate file server.pfx was not found (Api:Https:Path).", null, DateTimeOffset.UtcNow);

        Assert.StartsWith("The web API did not start: The certificate file server.pfx", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDesktopStatus_ShowsTheCertificate_AndWarnsBeforeExpiry()
    {
        var now = DateTimeOffset.UtcNow;
        var soon = new HttpsStatus(true, "CN=wf.example.com", ["wf.example.com"], "CN=R11", now.AddDays(5), true, now, null);

        var text = ApiStatusDescription.Describe(true, "http://127.0.0.1:5100", null, soon, now);

        Assert.Contains("wf.example.com, from CN=R11", text, StringComparison.Ordinal);
        Assert.Contains("renew it", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDesktopStatus_SaysWhenHttpsIsOff()
    {
        var text = ApiStatusDescription.Describe(true, "http://127.0.0.1:5100", null, HttpsStatus.Off, DateTimeOffset.UtcNow);

        Assert.Contains("HTTPS is off", text, StringComparison.Ordinal);
    }

    // An IPv6 address needs brackets in a URL, or a browser cannot follow the redirect.
    [Theory]
    [InlineData("2001:db8::1", 8443, "https://[2001:db8::1]:8443")]
    [InlineData("[2001:db8::1]", 8443, "https://[2001:db8::1]:8443")]
    [InlineData("2001:db8::1", 443, "https://[2001:db8::1]")]
    [InlineData("192.0.2.10", 8443, "https://192.0.2.10:8443")]
    public void AnAddressAsPublicHost_MakesAValidOrigin(string publicHost, int port, string expected)
    {
        var policy = new HttpsPolicy(new HttpsSettings(File, port, false, publicHost), HttpsStatusSource.None);

        Assert.Equal(expected, policy.PublicOrigin);
    }
}
