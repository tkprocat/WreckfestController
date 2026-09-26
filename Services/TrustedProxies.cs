using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;

namespace WreckfestController.Services;

/// <summary>
/// Honours <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c> only when they come from
/// the reverse proxies listed in <c>Api:TrustedProxies</c> (for example the OPNsense
/// HAProxy address). That gives lockout and rate limiting the real client IP, and the
/// cookie the HTTPS scheme the browser actually used. With nothing listed, the headers
/// are ignored - including from loopback, which ASP.NET Core would otherwise trust.
/// </summary>
public static class TrustedProxies
{
    public const string ConfigKey = "Api:TrustedProxies";

    /// <summary>What <see cref="Parse"/> made of the configured entries.</summary>
    public sealed record Parsed(
        IReadOnlyList<IPAddress> Proxies,
        IReadOnlyList<System.Net.IPNetwork> Networks,
        IReadOnlyList<string> Invalid);

    /// <summary>
    /// Reads <c>Api:TrustedProxies</c> as a JSON array or as one comma-separated string.
    /// Each entry is an address (<c>192.168.1.1</c>) or a CIDR range (<c>10.0.0.0/24</c>).
    /// </summary>
    public static Parsed Parse(IConfiguration configuration)
    {
        var section = configuration.GetSection(ConfigKey);
        var entries = section.GetChildren().Select(child => child.Value)
            .Append(section.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .SelectMany(value => value!.Split(
                [',', ';'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        var proxies = new List<IPAddress>();
        var networks = new List<System.Net.IPNetwork>();
        var invalid = new List<string>();
        foreach (var entry in entries)
        {
            if (entry.Contains('/') && System.Net.IPNetwork.TryParse(entry, out var network))
            {
                networks.Add(network);
            }
            else if (!entry.Contains('/') && IPAddress.TryParse(entry, out var address))
            {
                proxies.Add(address);
            }
            else
            {
                invalid.Add(entry);
            }
        }

        return new Parsed(proxies, networks, invalid);
    }

    public static void AddTrustedProxies(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ForwardedHeadersOptions>().Configure<ILoggerFactory>((options, loggers) =>
        {
            var parsed = Parse(configuration);
            var logger = loggers.CreateLogger(typeof(TrustedProxies));
            foreach (var entry in parsed.Invalid)
            {
                logger.LogWarning(
                    "{Key} entry '{Entry}' is not an IP address or CIDR range and is ignored",
                    ConfigKey,
                    entry);
            }

            // Host is deliberately not forwarded: nothing here builds absolute URLs.
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

            // Replace the loopback defaults, so only the configured proxies are trusted.
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            foreach (var proxy in parsed.Proxies)
            {
                options.KnownProxies.Add(proxy);
            }

            foreach (var network in parsed.Networks)
            {
                options.KnownIPNetworks.Add(network);
            }

            // Walk back through every trusted hop and stop at the first untrusted one, so
            // an X-Forwarded-For entry the client wrote itself is never taken as its IP.
            options.ForwardLimit = null;

            if (parsed.Proxies.Count + parsed.Networks.Count > 0)
            {
                logger.LogInformation(
                    "Trusting forwarded headers from {Proxies}",
                    string.Join(", ", parsed.Proxies.Select(p => p.ToString())
                        .Concat(parsed.Networks.Select(n => n.ToString()))));
            }
        });
    }
}
