using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace WreckfestController.Services.Hosting.Https;

/// <summary>A server certificate with its private key, and the chain to send with it (intermediates).</summary>
public sealed record LoadedCertificate(X509Certificate2 Certificate, X509Certificate2Collection Chain);

/// <summary>Where the certificate is read from. Loads are done outside TLS handshakes.</summary>
public interface ICertificateSource
{
    /// <summary>The certificate to use now, or an <see cref="HttpsConfigurationException"/> saying why there is none.</summary>
    LoadedCertificate Load();

    /// <summary>Files to watch for a sooner reload; none for the store, which the timer covers.</summary>
    IReadOnlyList<string> WatchedFiles { get; }
}

/// <summary>
/// The rules a certificate must meet to be served, whatever its source: valid now, a
/// Server Authentication EKU if it has any EKU, and a private key this account can use.
/// </summary>
public static class CertificateRules
{
    private const string ServerAuthentication = "1.3.6.1.5.5.7.3.1";

    /// <summary>Null when <paramref name="certificate"/> can be served, else why not.</summary>
    public static string? Unusable(X509Certificate2 certificate, DateTimeOffset now)
    {
        if (now < certificate.NotBefore.ToUniversalTime())
        {
            return $"it is not valid until {certificate.NotBefore.ToUniversalTime():u}";
        }

        if (now > certificate.NotAfter.ToUniversalTime())
        {
            return $"it expired on {certificate.NotAfter.ToUniversalTime():u}";
        }

        var ekus = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().SelectMany(e => e.EnhancedKeyUsages.Cast<Oid>()).ToList();
        if (ekus.Count > 0 && !ekus.Any(o => o.Value == ServerAuthentication))
        {
            return "it is not for server authentication (no Server Authentication usage)";
        }

        if (!HasUsableKey(certificate))
        {
            return "its private key is missing, or this account may not use it";
        }

        return null;
    }

    /// <summary>The certificate's DNS names (its Subject Alternative Names).</summary>
    public static IReadOnlyList<string> DnsNames(X509Certificate2 certificate) =>
        certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().SelectMany(e => e.EnumerateDnsNames()).ToList();

    private static bool HasUsableKey(X509Certificate2 certificate)
    {
        if (!certificate.HasPrivateKey)
        {
            return false;
        }

        try
        {
            // Opening the key is the check: a store certificate can list a key the account
            // has no permission to read.
            using var rsa = certificate.GetRSAPrivateKey();
            using var ecdsa = rsa is null ? certificate.GetECDsaPrivateKey() : null;
            return rsa is not null || ecdsa is not null;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}

/// <summary>
/// The Windows certificate store. Of the certificates for <see cref="StoreSourceSettings.Host"/>
/// (an exact DNS name) that can be served now, the one issued last (latest NotBefore) wins,
/// ties broken by thumbprint, so a renewal is picked up as soon as it lands.
/// </summary>
public sealed class StoreCertificateSource(StoreSourceSettings settings, Func<DateTimeOffset> now) : ICertificateSource
{
    public IReadOnlyList<string> WatchedFiles => [];

    public LoadedCertificate Load()
    {
        using var store = new X509Store(settings.StoreName, settings.Location);
        try
        {
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        }
        catch (CryptographicException ex)
        {
            throw new HttpsConfigurationException($"The {settings.Location} {settings.StoreName} certificate store could not be opened.", ex);
        }

        var chosen = Choose(store.Certificates.Cast<X509Certificate2>(), settings.Host, now(), out var rejected);
        if (chosen is null)
        {
            throw new HttpsConfigurationException(
                $"No usable certificate for {settings.Host} in the {settings.Location} {settings.StoreName} store." +
                (rejected is null ? string.Empty : $" The newest one for that name cannot be used: {rejected}."));
        }

        return new LoadedCertificate(chosen, ChainOf(chosen));
    }

    /// <summary>
    /// The certificate to serve for <paramref name="host"/> of <paramref name="candidates"/>,
    /// or null; <paramref name="rejected"/> then says why the newest one for that name was not.
    /// </summary>
    public static X509Certificate2? Choose(IEnumerable<X509Certificate2> candidates, string host, DateTimeOffset now, out string? rejected)
    {
        var forHost = candidates
            .Where(c => CertificateRules.DnsNames(c).Any(n => string.Equals(n, host, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(c => c.NotBefore)
            .ThenBy(c => c.Thumbprint, StringComparer.Ordinal)
            .ToList();

        rejected = forHost.Count > 0 ? CertificateRules.Unusable(forHost[0], now) : null;
        return forHost.FirstOrDefault(c => CertificateRules.Unusable(c, now) is null);
    }

    /// <summary>The intermediates the system can find for <paramref name="certificate"/>, to send with it.</summary>
    private static X509Certificate2Collection ChainOf(X509Certificate2 certificate)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllFlags;
        chain.Build(certificate);
        var collection = new X509Certificate2Collection();
        foreach (var element in chain.ChainElements.Cast<X509ChainElement>().Skip(1))
        {
            // Not the root: clients have their own, and sending it only costs bytes.
            if (element.Certificate.Subject != element.Certificate.Issuer)
            {
                collection.Add(element.Certificate);
            }
        }

        return collection;
    }
}

/// <summary>
/// A certificate file: .pfx/.p12 (the certificate with its key, and usually its chain), or
/// .pem/.crt holding the certificate and its chain, with the key in <see cref="FileSourceSettings.KeyPath"/>.
/// </summary>
public sealed class FileCertificateSource(FileSourceSettings settings, Func<DateTimeOffset> now) : ICertificateSource
{
    public IReadOnlyList<string> WatchedFiles => settings.KeyPath is null ? [settings.Path] : [settings.Path, settings.KeyPath];

    public LoadedCertificate Load()
    {
        if (!File.Exists(settings.Path))
        {
            throw new HttpsConfigurationException($"The certificate file {Path.GetFileName(settings.Path)} was not found (Api:Https:Path).");
        }

        LoadedCertificate loaded;
        try
        {
            loaded = settings.KeyPath is null ? LoadPkcs12() : LoadPem(settings.KeyPath);
        }
        catch (CryptographicException ex)
        {
            // Wrong password, a damaged file, or a key that does not match: the exception
            // text is Windows' own and says little, so the answer says what to check.
            throw new HttpsConfigurationException(
                $"The certificate file {Path.GetFileName(settings.Path)} could not be read: check Api:Https:Password and that the key matches the certificate.",
                ex);
        }

        if (CertificateRules.Unusable(loaded.Certificate, now()) is { } why)
        {
            throw new HttpsConfigurationException($"The certificate in {Path.GetFileName(settings.Path)} cannot be used: {why}.");
        }

        return loaded;
    }

    private LoadedCertificate LoadPkcs12()
    {
        var all = X509CertificateLoader.LoadPkcs12CollectionFromFile(
            settings.Path,
            settings.Password,
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
        var leaf = all.Cast<X509Certificate2>().FirstOrDefault(c => c.HasPrivateKey)
            ?? throw new HttpsConfigurationException($"The certificate file {Path.GetFileName(settings.Path)} holds no private key.");
        return new LoadedCertificate(leaf, Others(all, leaf));
    }

    private LoadedCertificate LoadPem(string keyPath)
    {
        if (!File.Exists(keyPath))
        {
            throw new HttpsConfigurationException($"The key file {Path.GetFileName(keyPath)} was not found (Api:Https:KeyPath).");
        }

        using var pem = settings.Password is null
            ? X509Certificate2.CreateFromPemFile(settings.Path, keyPath)
            : X509Certificate2.CreateFromEncryptedPemFile(settings.Path, settings.Password, keyPath);

        // Windows' TLS needs a key it can find again, which a key read from PEM is not:
        // round-trip it through PKCS#12 so it is.
        var leaf = X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.UserKeySet);
        var all = new X509Certificate2Collection();
        all.ImportFromPemFile(settings.Path);
        return new LoadedCertificate(leaf, Others(all, leaf));
    }

    /// <summary>The rest of the file's certificates, less any root: the chain to send.</summary>
    private static X509Certificate2Collection Others(X509Certificate2Collection all, X509Certificate2 leaf)
    {
        var chain = new X509Certificate2Collection();
        foreach (var c in all.Cast<X509Certificate2>())
        {
            if (c.Thumbprint != leaf.Thumbprint && c.Subject != c.Issuer)
            {
                chain.Add(c);
            }
        }

        return chain;
    }
}
