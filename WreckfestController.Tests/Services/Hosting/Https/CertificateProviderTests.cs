using System.IO;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using WreckfestController.Services.Hosting.Https;

namespace WreckfestController.Tests.Services.Hosting.Https;

/// <summary>What the provider keeps, lets go of, and reports, as sources change under it.</summary>
public sealed class CertificateProviderTests : IDisposable
{
    private const string Host = "wf.example.com";
    private readonly TestCertificates _ca = new();

    public void Dispose() => _ca.Dispose();

    /// <summary>A source that hands out whatever the test says, and remembers what it handed out.</summary>
    private sealed class ScriptedSource(Func<LoadedCertificate> next) : ICertificateSource
    {
        public List<LoadedCertificate> Given { get; } = [];

        public IReadOnlyList<string> WatchedFiles => [];

        public LoadedCertificate Load()
        {
            var loaded = next();
            Given.Add(loaded);
            return loaded;
        }
    }

    private LoadedCertificate Copy(X509Certificate2 certificate) =>
        new(X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), null), []);

    // The same certificate found again: that copy, and its key container, are let go at once.
    [Fact]
    public void AnUnchangedReload_DisposesItsCopy_AndKeepsServing()
    {
        var leaf = _ca.Leaf([Host]);
        var source = new ScriptedSource(() => Copy(leaf));
        using var provider = new CertificateProvider(source, () => DateTimeOffset.UtcNow, NullLogger.Instance);
        provider.Start();

        provider.Refresh();
        provider.Refresh();

        Assert.Equal(IntPtr.Zero, source.Given[1].Certificate.Handle);
        Assert.Equal(IntPtr.Zero, source.Given[2].Certificate.Handle);
        Assert.NotEqual(IntPtr.Zero, provider.Current.Loaded.Certificate.Handle);
        Assert.Same(source.Given[0], provider.Current.Loaded);
    }

    // Whatever a reload throws stays in the provider: the working certificate stays in
    // service, and the status says what went wrong without naming a path.
    [Fact]
    public void AReloadThatThrows_KeepsTheWorkingCertificate_AndReportsSafely()
    {
        var leaf = _ca.Leaf([Host]);
        var calls = 0;
        var source = new ScriptedSource(() => ++calls == 1
            ? Copy(leaf)
            : throw new IOException(@"The process cannot access the file 'C:\secret\folder\server.pfx'."));
        using var provider = new CertificateProvider(source, () => DateTimeOffset.UtcNow, NullLogger.Instance);
        provider.Start();

        provider.Refresh();

        Assert.Equal(leaf.Thumbprint, provider.Current.Loaded.Certificate.Thumbprint);
        Assert.NotNull(provider.Status.Error);
        Assert.DoesNotContain("secret", provider.Status.Error, StringComparison.Ordinal);
    }

    // A replacement whose extensions cannot be read: refused on reload, the old one serves on.
    [Fact]
    public void AMalformedReplacement_KeepsTheWorkingCertificate()
    {
        var first = _ca.Leaf([Host]);
        var path = _ca.Pfx(first);
        using var provider = new CertificateProvider(
            new FileCertificateSource(new FileSourceSettings(path, null, null), () => DateTimeOffset.UtcNow),
            () => DateTimeOffset.UtcNow,
            NullLogger.Instance);
        provider.Start();

        _ca.Pfx(_ca.Malformed(Host, badSan: false));
        provider.Refresh();

        Assert.Equal(first.Thumbprint, provider.Current.Loaded.Certificate.Thumbprint);
        Assert.Contains("cannot be read", provider.Status.Error, StringComparison.Ordinal);
    }
}
