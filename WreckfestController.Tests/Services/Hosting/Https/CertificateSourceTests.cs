using System.IO;
using System.Security.Cryptography.X509Certificates;
using WreckfestController.Services.Hosting.Https;

namespace WreckfestController.Tests.Services.Hosting.Https;

/// <summary>Which certificate is served, from the store's candidates or from a file.</summary>
public sealed class CertificateSourceTests : IDisposable
{
    private const string Host = "wf.example.com";
    private readonly TestCertificates _ca = new();
    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

    public void Dispose() => _ca.Dispose();

    // A renewal lands next to the old certificate: the one issued last wins.
    [Fact]
    public void TheStore_ChoosesTheNewestUsableCertificateForTheHost()
    {
        var older = _ca.Leaf([Host], notBefore: Now.AddDays(-30));
        var newer = _ca.Leaf([Host], notBefore: Now.AddDays(-1));

        var chosen = StoreCertificateSource.Choose([older, newer], Host, Now, out _);

        Assert.Equal(newer.Thumbprint, chosen!.Thumbprint);
    }

    [Fact]
    public void TheStore_SkipsCertificatesThatCannotBeServed()
    {
        var usable = _ca.Leaf([Host], notBefore: Now.AddDays(-20));
        var expired = _ca.Leaf([Host], notBefore: Now.AddDays(-10), notAfter: Now.AddDays(-1));
        var notYet = _ca.Leaf([Host], notBefore: Now.AddDays(1), notAfter: Now.AddDays(90));
        var clientOnly = _ca.ClientOnly(Host);
        var noKey = X509CertificateLoader.LoadCertificate(_ca.Leaf([Host], notBefore: Now.AddHours(-1)).RawData);

        var chosen = StoreCertificateSource.Choose([usable, expired, notYet, clientOnly, noKey], Host, Now, out _);

        Assert.Equal(usable.Thumbprint, chosen!.Thumbprint);
    }

    // Exact names: not a wildcard, not a longer name that ends the same way.
    [Theory]
    [InlineData("*.example.com")]
    [InlineData("other.wf.example.com")]
    [InlineData("wf.example.com.evil.test")]
    public void TheStore_MatchesTheHostExactly(string otherName)
    {
        var other = _ca.Leaf([otherName]);

        Assert.Null(StoreCertificateSource.Choose([other], Host, Now, out _));
    }

    [Fact]
    public void TheStore_MatchesTheHostIgnoringCase_AndAnyOfItsNames()
    {
        var multi = _ca.Leaf(["other.test", "WF.Example.COM"]);

        Assert.NotNull(StoreCertificateSource.Choose([multi], Host, Now, out _));
    }

    [Fact]
    public void TheStore_BreaksATieByThumbprint_SoTheChoiceIsStable()
    {
        var from = Now.AddDays(-2);
        var a = _ca.Leaf([Host], notBefore: from);
        var b = _ca.Leaf([Host], notBefore: from);
        var expected = string.CompareOrdinal(a.Thumbprint, b.Thumbprint) < 0 ? a : b;

        Assert.Equal(expected.Thumbprint, StoreCertificateSource.Choose([a, b], Host, Now, out _)!.Thumbprint);
        Assert.Equal(expected.Thumbprint, StoreCertificateSource.Choose([b, a], Host, Now, out _)!.Thumbprint);
    }

    [Fact]
    public void TheStore_SaysWhyTheNewestCertificateCannotBeUsed()
    {
        var expired = _ca.Leaf([Host], notBefore: Now.AddDays(-10), notAfter: Now.AddDays(-1));

        Assert.Null(StoreCertificateSource.Choose([expired], Host, Now, out var why));
        Assert.Contains("expired", why, StringComparison.Ordinal);
    }

    // As win-acme writes it: the leaf with its key, and the intermediate to send with it.
    [Theory]
    [InlineData(null)]
    [InlineData("pfx-password")]
    public void APfx_GivesTheCertificateAndItsChain_WithoutTheRoot(string? password)
    {
        var leaf = _ca.Leaf([Host]);
        var loaded = Source(new FileSourceSettings(_ca.Pfx(leaf, password), null, password)).Load();

        Assert.Equal(leaf.Thumbprint, loaded.Certificate.Thumbprint);
        Assert.True(loaded.Certificate.HasPrivateKey);
        Assert.Equal([_ca.Intermediate.Thumbprint], loaded.Chain.Select(c => c.Thumbprint));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("key-password")]
    public void APemWithItsKey_GivesTheCertificateAndItsChain(string? password)
    {
        var leaf = _ca.Leaf([Host]);
        var (certificate, key) = _ca.Pem(leaf, password);

        var loaded = Source(new FileSourceSettings(certificate, key, password)).Load();

        Assert.Equal(leaf.Thumbprint, loaded.Certificate.Thumbprint);
        Assert.True(loaded.Certificate.HasPrivateKey);
        Assert.Equal([_ca.Intermediate.Thumbprint], loaded.Chain.Select(c => c.Thumbprint));
    }

    // The message names the file and what to check, and never holds the password tried.
    [Fact]
    public void AWrongPassword_SaysWhatToCheck_WithoutThePassword()
    {
        var path = _ca.Pfx(_ca.Leaf([Host]), "right-password");

        var error = Assert.Throws<HttpsConfigurationException>(() => Source(new FileSourceSettings(path, null, "wrong-password")).Load());

        Assert.Contains("Password", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("wrong-password", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_ca.Folder, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AMissingFile_IsAnError_NamingOnlyTheFileName()
    {
        var error = Assert.Throws<HttpsConfigurationException>(() => Source(new FileSourceSettings(Path.Combine(_ca.Folder, "absent.pfx"), null, null)).Load());

        Assert.Contains("absent.pfx", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_ca.Folder, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnExpiredFile_IsAnError()
    {
        var path = _ca.Pfx(_ca.Leaf([Host], notBefore: Now.AddDays(-30), notAfter: Now.AddDays(-1)));

        var error = Assert.Throws<HttpsConfigurationException>(() => Source(new FileSourceSettings(path, null, null)).Load());

        Assert.Contains("expired", error.Message, StringComparison.Ordinal);
    }

    private static FileCertificateSource Source(FileSourceSettings settings) => new(settings, () => DateTimeOffset.UtcNow);

    // A damaged extension throws when read: the store passes over that certificate, a file
    // holding one is refused with a reason, and neither throws anything else.
    [Fact]
    public void TheStore_PassesOverACertificateWithADamagedExtension()
    {
        var usable = _ca.Leaf([Host], notBefore: Now.AddDays(-5));
        var badEku = _ca.Malformed(Host, badSan: false);
        var badSan = _ca.Malformed(Host, badSan: true);

        var chosen = StoreCertificateSource.Choose([badSan, badEku, usable], Host, Now, out _);

        Assert.Equal(usable.Thumbprint, chosen!.Thumbprint);
    }

    [Fact]
    public void AFileWithADamagedExtension_IsRefused_WithAReason()
    {
        var path = _ca.Pfx(_ca.Malformed(Host, badSan: false));

        var error = Assert.Throws<HttpsConfigurationException>(() => Source(new FileSourceSettings(path, null, null)).Load());

        Assert.Contains("cannot be read", error.Message, StringComparison.Ordinal);
    }

    // A renewal still writing the file holds it: the message names the file, not the folder.
    [Fact]
    public void ALockedFile_SaysSo_WithoutItsFolder()
    {
        var path = _ca.Pfx(_ca.Leaf([Host]));
        using var hold = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var error = Assert.Throws<HttpsConfigurationException>(() => Source(new FileSourceSettings(path, null, null)).Load());

        Assert.Contains("server.pfx", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_ca.Folder, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // PEM files are read as text, which reports a locked file differently: still no folder.
    [Fact]
    public void ALockedPemKey_SaysSo_WithoutItsFolder()
    {
        var (certificate, key) = _ca.Pem(_ca.Leaf([Host]));
        using var hold = new FileStream(key, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var error = Assert.Throws<HttpsConfigurationException>(() => Source(new FileSourceSettings(certificate, key, null)).Load());

        Assert.DoesNotContain(_ca.Folder, error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
