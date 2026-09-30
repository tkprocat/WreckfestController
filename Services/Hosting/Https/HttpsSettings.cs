using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace WreckfestController.Services.Hosting.Https;

/// <summary>
/// Where the API listens: one bind address, the HTTP port, and the HTTPS port and its
/// certificate source when <c>Api:Https</c> is configured. <c>Api</c> is the only say:
/// Kestrel:Endpoints and URL settings are not honoured.
/// </summary>
public sealed record ApiEndpoints(IPAddress Address, int HttpPort, int HttpsPort, HttpsSettings? Https)
{
    public const int DefaultHttpPort = 5100;
    public const int DefaultHttpsPort = 5101;

    /// <summary>
    /// Reads and checks <c>Api:AllowRemote</c>, the ports and <c>Api:Https</c>. A setting that
    /// is wrong is an error naming it, never quietly replaced by a default: a typo in a
    /// port or a certificate path must not leave the API somewhere unexpected, or on HTTP.
    /// </summary>
    public static ApiEndpoints Resolve(IConfiguration configuration, string baseDirectory)
    {
        var address = configuration.GetValue<bool>("Api:AllowRemote") ? IPAddress.Any : IPAddress.Loopback;
        var httpPort = Port(configuration, "Api:HttpPort", DefaultHttpPort);
        var httpsPort = Port(configuration, "Api:HttpsPort", DefaultHttpsPort);
        // "Https": {} is not "no Https": the configuration system reports an empty section as
        // absent, which would quietly mean HTTP only. Its key is still there; look for it.
        var section = configuration.GetSection("Api:Https");
        var present = section.Exists()
            || configuration.AsEnumerable().Any(kv => string.Equals(kv.Key, "Api:Https", StringComparison.OrdinalIgnoreCase));
        var https = present ? HttpsSettings.Read(section, httpsPort, baseDirectory) : null;
        if (https is not null && httpPort == httpsPort)
        {
            throw new HttpsConfigurationException(
                $"Api:HttpPort and Api:HttpsPort are both {httpPort}. Give HTTPS its own port.");
        }

        return new ApiEndpoints(address, httpPort, httpsPort, https);
    }

    private static int Port(IConfiguration configuration, string key, int fallback)
    {
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
        {
            throw new HttpsConfigurationException($"{key} is '{raw}', which is not a TCP port (1 to 65535).");
        }

        return port;
    }
}

/// <summary>Where the HTTPS certificate comes from: exactly one of the two.</summary>
public abstract record CertificateSourceSettings;

/// <summary>
/// The Windows certificate store: the newest usable certificate for <see cref="Host"/>.
/// <see cref="StoreName"/> is the store's own name (My, WebHosting, ...): not every store has
/// a <see cref="System.Security.Cryptography.X509Certificates.StoreName"/> member.
/// </summary>
public sealed record StoreSourceSettings(string Host, string StoreName, StoreLocation Location) : CertificateSourceSettings;

/// <summary>A file: .pfx (with <see cref="Password"/> if it has one), or .pem/.crt with its chain plus <see cref="KeyPath"/>.</summary>
public sealed record FileSourceSettings(string Path, string? KeyPath, string? Password) : CertificateSourceSettings;

/// <summary>
/// <c>Api:Https</c>. Absent: HTTP only, as before. Present: one certificate source, the
/// port browsers use (<see cref="PublicPort"/>, when NAT maps 443 to the HTTPS port), and
/// HSTS, which is opt-in.
/// </summary>
public sealed record HttpsSettings(CertificateSourceSettings Source, int PublicPort, bool Hsts)
{
    /// <summary>
    /// The settings, or an error that names what is wrong: both sources, neither, or one only
    /// partly given. The caller has established the section is there (empty counts).
    /// </summary>
    public static HttpsSettings Read(IConfigurationSection section, int httpsPort, string baseDirectory)
    {
        var host = Text(section, "Host");
        var store = Text(section, "Store");
        var location = Text(section, "Location");
        var path = Text(section, "Path");
        var keyPath = Text(section, "KeyPath");
        var password = section["Password"];

        var storeGiven = host is not null || store is not null || location is not null;
        var fileGiven = path is not null || keyPath is not null || password is not null;
        if (storeGiven && fileGiven)
        {
            throw new HttpsConfigurationException(
                "Api:Https has both a store (Host, Store, Location) and a file (Path, KeyPath, Password). Give one.");
        }

        CertificateSourceSettings source;
        if (storeGiven)
        {
            if (host is null || store is null)
            {
                throw new HttpsConfigurationException(
                    "Api:Https from the certificate store needs Host (the name browsers use) and Store (such as My or WebHosting).");
            }

            // Any name Windows has, WebHosting included; a store that does not exist is an
            // error when it is opened, which names it.
            if (store.IndexOfAny(['\\', '/']) >= 0)
            {
                throw new HttpsConfigurationException($"Api:Https:Store is '{store}'. Use a Windows store name such as My or WebHosting.");
            }

            var storeLocation = StoreLocation.CurrentUser;
            if (location is not null
                && (!Enum.TryParse(location, ignoreCase: true, out storeLocation) || !Enum.IsDefined(storeLocation)))
            {
                throw new HttpsConfigurationException($"Api:Https:Location is '{location}'. Use CurrentUser or LocalMachine.");
            }

            source = new StoreSourceSettings(host, store, storeLocation);
        }
        else if (fileGiven)
        {
            if (path is null)
            {
                throw new HttpsConfigurationException("Api:Https has KeyPath or Password but no Path to the certificate file.");
            }

            // Relative to the exe's folder, not the working directory, which depends on how
            // the app was started.
            source = new FileSourceSettings(
                Resolve(path, baseDirectory),
                keyPath is null ? null : Resolve(keyPath, baseDirectory),
                password);
        }
        else
        {
            throw new HttpsConfigurationException(
                "Api:Https is there but names no certificate. Give Host and Store (the Windows store) or Path (a file), or remove the section for HTTP only.");
        }

        var publicPort = httpsPort;
        var rawPublic = Text(section, "PublicPort");
        if (rawPublic is not null
            && (!int.TryParse(rawPublic, NumberStyles.None, CultureInfo.InvariantCulture, out publicPort) || publicPort is < 1 or > 65535))
        {
            throw new HttpsConfigurationException($"Api:Https:PublicPort is '{rawPublic}', which is not a TCP port (1 to 65535).");
        }

        var rawHsts = Text(section, "Hsts");
        var hsts = false;
        if (rawHsts is not null && !bool.TryParse(rawHsts, out hsts))
        {
            throw new HttpsConfigurationException($"Api:Https:Hsts is '{rawHsts}'. Use true or false.");
        }

        return new HttpsSettings(source, publicPort, hsts);
    }

    private static string? Text(IConfigurationSection section, string key) =>
        string.IsNullOrWhiteSpace(section[key]) ? null : section[key]!.Trim();

    private static string Resolve(string path, string baseDirectory) =>
        System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(path) ? path : System.IO.Path.Combine(baseDirectory, path));
}

/// <summary>HTTPS is configured but cannot be used as it is. The message says what to fix, and holds no secret.</summary>
public sealed class HttpsConfigurationException(string message, Exception? inner = null) : Exception(message, inner);
