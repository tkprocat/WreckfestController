using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace WreckfestController.Tests.Services.Hosting.Https;

/// <summary>
/// A throwaway CA hierarchy for the HTTPS tests: a root, an intermediate, and leaf
/// certificates issued by the intermediate, as a real CA would. Nothing is installed in a
/// store; files go to a temporary folder the test owns.
/// </summary>
internal sealed class TestCertificates : IDisposable
{
    private const string ServerAuthentication = "1.3.6.1.5.5.7.3.1";
    private const string ClientAuthentication = "1.3.6.1.5.5.7.3.2";

    public TestCertificates()
    {
        Directory.CreateDirectory(Folder);
        Root = Authority("CN=WFC Test Root", issuer: null, DateTimeOffset.UtcNow.AddYears(-1), DateTimeOffset.UtcNow.AddYears(5));
        Intermediate = Authority("CN=WFC Test Intermediate", Root, DateTimeOffset.UtcNow.AddYears(-1), DateTimeOffset.UtcNow.AddYears(4));
    }

    public string Folder { get; } = Path.Combine(Path.GetTempPath(), $"wfc-https-{Guid.NewGuid():N}");

    public X509Certificate2 Root { get; }

    public X509Certificate2 Intermediate { get; }

    /// <summary>A server certificate for <paramref name="dnsNames"/>, issued by the intermediate, with its key.</summary>
    public X509Certificate2 Leaf(
        string[] dnsNames,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? notAfter = null,
        string? eku = ServerAuthentication)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={dnsNames[0]}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in dnsNames)
        {
            san.AddDnsName(name);
        }

        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        if (eku is not null)
        {
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(eku)], false));
        }

        var from = notBefore ?? DateTimeOffset.UtcNow.AddDays(-1);
        var until = notAfter ?? DateTimeOffset.UtcNow.AddDays(60);
        using var signed = request.Create(Intermediate, from, until, RandomNumberGenerator.GetBytes(16));
        return signed.CopyWithPrivateKey(key);
    }

    /// <summary>
    /// A server certificate whose Subject Alternative Name or Enhanced Key Usage bytes are
    /// not valid DER: it loads, but reading that extension throws.
    /// </summary>
    public X509Certificate2 Malformed(string dnsName, bool badSan)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={dnsName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var garbage = new byte[] { 0x30, 0x05, 0xFF, 0xFF };
        if (badSan)
        {
            request.CertificateExtensions.Add(new X509Extension("2.5.29.17", garbage, false));
        }
        else
        {
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName(dnsName);
            request.CertificateExtensions.Add(san.Build());
            request.CertificateExtensions.Add(new X509Extension("2.5.29.37", garbage, false));
        }

        using var signed = request.Create(Intermediate, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(60), RandomNumberGenerator.GetBytes(16));
        return signed.CopyWithPrivateKey(key);
    }

    /// <summary>A client-only certificate: valid, keyed, but not for servers.</summary>
    public X509Certificate2 ClientOnly(string dnsName) => Leaf([dnsName], eku: ClientAuthentication);

    /// <summary>The leaf and the intermediate (not the root) in a .pfx, as win-acme writes it.</summary>
    public string Pfx(X509Certificate2 leaf, string? password = null, string name = "server.pfx")
    {
        var collection = new X509Certificate2Collection { leaf, X509CertificateLoader.LoadCertificate(Intermediate.RawData) };
        var path = Path.Combine(Folder, name);
        File.WriteAllBytes(path, collection.Export(X509ContentType.Pkcs12, password)!);
        return path;
    }

    /// <summary>The leaf then the intermediate in one PEM, and the key in another (encrypted when a password is given).</summary>
    public (string Certificate, string Key) Pem(X509Certificate2 leaf, string? password = null)
    {
        var certificate = Path.Combine(Folder, "server.pem");
        var key = Path.Combine(Folder, "server.key");
        File.WriteAllText(certificate, leaf.ExportCertificatePem() + "\n" + Intermediate.ExportCertificatePem());
        using var rsa = leaf.GetRSAPrivateKey()!;
        File.WriteAllText(
            key,
            password is null
                ? rsa.ExportPkcs8PrivateKeyPem()
                : rsa.ExportEncryptedPkcs8PrivateKeyPem(password, new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 100_000)));
        return (certificate, key);
    }

    private static X509Certificate2 Authority(string subject, X509Certificate2? issuer, DateTimeOffset from, DateTimeOffset until)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        if (issuer is null)
        {
            return request.CreateSelfSigned(from, until);
        }

        using var signed = request.Create(issuer, from, until, RandomNumberGenerator.GetBytes(16));
        return signed.CopyWithPrivateKey(key);
    }

    public void Dispose()
    {
        Root.Dispose();
        Intermediate.Dispose();
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (IOException)
        {
            // A watcher may still hold the folder for a moment; it is a temp folder.
        }
    }
}
