using System.IO;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;
using WreckfestController.Services.Hosting.Https;

namespace WreckfestController.Tests.Services.Hosting.Https;

/// <summary>
/// Api's endpoints and Api:Https: a setting that is wrong is an error naming it, never
/// replaced by a default, and never a quiet fallback to HTTP.
/// </summary>
public sealed class HttpsSettingsTests
{
    private const string BaseDirectory = @"C:\apps\wfc";

    private static ApiEndpoints Resolve(params (string Key, string? Value)[] settings) =>
        ApiEndpoints.Resolve(
            new ConfigurationBuilder().AddInMemoryCollection(settings.Select(s => KeyValuePair.Create(s.Key, s.Value))).Build(),
            BaseDirectory);

    [Fact]
    public void WithoutApiHttps_ItIsHttpOnly_OnTheDefaultPorts_OnLoopback()
    {
        var endpoints = Resolve();

        Assert.Null(endpoints.Https);
        Assert.Equal((IPAddress.Loopback, 5100), (endpoints.Address, endpoints.HttpPort));
    }

    [Fact]
    public void AllowRemote_ListensOnEveryAddress()
    {
        Assert.Equal(IPAddress.Any, Resolve(("Api:AllowRemote", "true")).Address);
    }

    [Theory]
    [InlineData("Api:HttpPort", "fifty")]
    [InlineData("Api:HttpPort", "0")]
    [InlineData("Api:HttpsPort", "70000")]
    [InlineData("Api:HttpsPort", "-1")]
    public void APortThatIsNotAPort_IsAnError_NamingIt(string key, string value)
    {
        var error = Assert.Throws<HttpsConfigurationException>(() => Resolve((key, value)));

        Assert.Contains(key, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSamePortForBoth_IsAnError_WhenHttpsIsOn()
    {
        var error = Assert.Throws<HttpsConfigurationException>(() => Resolve(
            ("Api:HttpPort", "5100"), ("Api:HttpsPort", "5100"), ("Api:Https:Path", "server.pfx")));

        Assert.Contains("5100", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStore_IsReadWithItsHostStoreAndLocation()
    {
        var https = Resolve(("Api:Https:Host", "wf.example.com"), ("Api:Https:Store", "My"), ("Api:Https:Location", "LocalMachine")).Https!;

        Assert.Equal(new StoreSourceSettings("wf.example.com", StoreName.My, StoreLocation.LocalMachine), https.Source);
        Assert.Equal(5101, https.PublicPort);
        Assert.False(https.Hsts);
    }

    [Fact]
    public void TheStoreLocation_DefaultsToCurrentUser()
    {
        var source = Assert.IsType<StoreSourceSettings>(Resolve(("Api:Https:Host", "wf.example.com"), ("Api:Https:Store", "My")).Https!.Source);

        Assert.Equal(StoreLocation.CurrentUser, source.Location);
    }

    // Relative to the exe's folder, not wherever the app happened to be started from.
    [Fact]
    public void ARelativeFile_IsResolvedAgainstTheExesFolder()
    {
        var source = Assert.IsType<FileSourceSettings>(Resolve(("Api:Https:Path", @"certs\server.pem"), ("Api:Https:KeyPath", "server.key")).Https!.Source);

        Assert.Equal(Path.Combine(BaseDirectory, "certs", "server.pem"), source.Path);
        Assert.Equal(Path.Combine(BaseDirectory, "server.key"), source.KeyPath);
    }

    [Theory]
    // Both sources.
    [InlineData("Api:Https:Host", "wf.example.com", "Api:Https:Path", "server.pfx", "both")]
    // A store without its store name.
    [InlineData("Api:Https:Host", "wf.example.com", "Api:Https:PublicPort", "443", "Store")]
    // A store name without the host.
    [InlineData("Api:Https:Store", "My", "Api:Https:PublicPort", "443", "Host")]
    // A key without the certificate.
    [InlineData("Api:Https:KeyPath", "server.key", "Api:Https:PublicPort", "443", "Path")]
    // Nothing that names a certificate.
    [InlineData("Api:Https:Hsts", "false", "Api:Https:PublicPort", "443", "names no certificate")]
    // Values that are not what they claim.
    [InlineData("Api:Https:Host", "wf.example.com", "Api:Https:Store", "Nowhere", "Nowhere")]
    [InlineData("Api:Https:Path", "server.pfx", "Api:Https:PublicPort", "port", "PublicPort")]
    [InlineData("Api:Https:Path", "server.pfx", "Api:Https:Hsts", "sometimes", "Hsts")]
    public void AnIncompleteOrConflictingSection_IsAnError_SayingWhat(string key1, string value1, string key2, string value2, string named)
    {
        var error = Assert.Throws<HttpsConfigurationException>(() => Resolve((key1, value1), (key2, value2)));

        Assert.Contains(named, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ABadLocation_IsAnError()
    {
        var error = Assert.Throws<HttpsConfigurationException>(() => Resolve(
            ("Api:Https:Host", "wf.example.com"), ("Api:Https:Store", "My"), ("Api:Https:Location", "Attic")));

        Assert.Contains("Attic", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PublicPortAndHsts_AreRead()
    {
        var https = Resolve(("Api:Https:Path", "server.pfx"), ("Api:Https:PublicPort", "443"), ("Api:Https:Hsts", "true")).Https!;

        Assert.Equal((443, true), (https.PublicPort, https.Hsts));
    }
}
