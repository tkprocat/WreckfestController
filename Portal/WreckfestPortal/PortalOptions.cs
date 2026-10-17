using System.Net;
using System.Text.RegularExpressions;

namespace WreckfestPortal;

/// <summary>The <c>Portal</c> section of appsettings.json: which controllers to list and serve.</summary>
public sealed partial class PortalOptions
{
    public const string Section = "Portal";

    /// <summary>
    /// Top-level paths the portal itself answers, which no server's <see cref="PortalServer.Id"/>
    /// may take: its own API and its page's assets.
    /// </summary>
    public static readonly IReadOnlySet<string> ReservedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "api", "assets" };

    /// <summary>The page's heading and title, such as "Monday Night Wrecking".</summary>
    public string Title { get; set; } = "Wreckfest servers";

    /// <summary>A line under the heading. Optional.</summary>
    public string? Intro { get; set; }

    /// <summary>Seconds between checks of every server. 5 to 3600.</summary>
    public int PollSeconds { get; set; } = 15;

    /// <summary>Seconds a controller has to answer before it counts as unreachable. 1 to 30.</summary>
    public int TimeoutSeconds { get; set; } = 3;

    /// <summary>The servers, in the order the page lists them.</summary>
    public List<PortalServer> Servers { get; set; } = [];

    /// <summary>
    /// Reverse proxies in front of the portal (such as HAProxy), as addresses or CIDR ranges,
    /// whose <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c> are believed and passed on
    /// to the controllers. Empty: the portal faces the visitors itself.
    /// </summary>
    public List<string> TrustedProxies { get; set; } = [];

    /// <summary>
    /// The portal's database (the web accounts), if not <c>data\portal.db</c> next to the exe.
    /// Environment variables such as <c>%ProgramData%</c> are expanded.
    /// </summary>
    public string? DatabasePath { get; set; }

    /// <summary>
    /// Where the controllers' web app is, if not the <c>app</c> folder next to the exe,
    /// where the build puts it.
    /// </summary>
    public string? AppRoot { get; set; }

    /// <summary>What is wrong with the settings; empty when they are usable.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(Title))
        {
            problems.Add($"{Section}:Title is empty.");
        }

        if (PollSeconds is < 5 or > 3600)
        {
            problems.Add($"{Section}:PollSeconds must be 5 to 3600.");
        }

        if (TimeoutSeconds is < 1 or > 30)
        {
            problems.Add($"{Section}:TimeoutSeconds must be 1 to 30.");
        }

        if (Servers.Count == 0)
        {
            problems.Add($"{Section}:Servers lists no servers.");
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < Servers.Count; i++)
        {
            var server = Servers[i];
            var where = $"{Section}:Servers:{i}";
            if (string.IsNullOrWhiteSpace(server.Id))
            {
                problems.Add($"{where}:Id is empty.");
            }
            else if (!IdPattern().IsMatch(server.Id))
            {
                problems.Add($"{where}:Id \"{server.Id}\" is not usable as a path: use letters, digits, - and _ only, such as tvtp2.");
            }
            else if (ReservedIds.Contains(server.Id))
            {
                problems.Add($"{where}:Id \"{server.Id}\" is a path the portal uses itself: pick another.");
            }
            else if (!ids.Add(server.Id))
            {
                problems.Add($"{where}:Id \"{server.Id}\" is used twice.");
            }

            if (string.IsNullOrWhiteSpace(server.Name))
            {
                problems.Add($"{where}:Name is empty.");
            }

            if (!IsHttpUrl(server.ApiUrl))
            {
                problems.Add($"{where}:ApiUrl must be an http or https address, such as http://127.0.0.1:5100.");
            }

            if (string.IsNullOrEmpty(server.Secret) || server.Secret.Length < Accounts.ControllerTokens.MinimumSecretLength)
            {
                problems.Add($"{where}:Secret must be at least {Accounts.ControllerTokens.MinimumSecretLength} characters, the same as that controller's Api:Portal:Secret.");
            }
        }

        // One secret each: a controller that leaked its own must not be able to sign for another.
        foreach (var shared in Servers.Where(s => !string.IsNullOrEmpty(s.Secret)).GroupBy(s => s.Secret).Where(g => g.Count() > 1))
        {
            problems.Add($"{Section}:Servers {string.Join(", ", shared.Select(s => s.Id))} share a Secret: give each server its own.");
        }

        for (var i = 0; i < TrustedProxies.Count; i++)
        {
            if (!TryParseProxy(TrustedProxies[i], out _, out _))
            {
                problems.Add($"{Section}:TrustedProxies:{i} \"{TrustedProxies[i]}\" is not an IP address or CIDR range.");
            }
        }

        return problems;
    }

    /// <summary>An address (<c>192.168.1.1</c>) or a CIDR range (<c>10.0.0.0/24</c>).</summary>
    public static bool TryParseProxy(string? value, out IPAddress? address, out IPNetwork? network)
    {
        address = null;
        network = null;
        value = value?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        if (value.Contains('/'))
        {
            if (IPNetwork.TryParse(value, out var parsed))
            {
                network = parsed;
                return true;
            }

            return false;
        }

        return IPAddress.TryParse(value, out address);
    }

    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex IdPattern();

    private static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}

/// <summary>One controller the page lists, and whose site the portal serves under <c>/{Id}/</c>.</summary>
public sealed class PortalServer
{
    /// <summary>Unique, and the path its site lives under: <c>tvtp2</c> is <c>/tvtp2/</c>.</summary>
    public string Id { get; set; } = "";

    /// <summary>The card's heading, such as "TVTP2".</summary>
    public string Name { get; set; } = "";

    /// <summary>A line under the name, such as "Tracks from the TVTP2 mod". Optional.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// Where the portal reaches the controller's API, such as <c>http://127.0.0.1:5100</c>.
    /// Never shown on the page: the browser asks the portal, which forwards.
    /// </summary>
    public string ApiUrl { get; set; } = "";

    /// <summary>
    /// Shared with this server's controller (its <c>Api:Portal:Secret</c>): signs the token the
    /// portal sends it to say who is calling. At least 32 characters, and different for every
    /// server. Never shown on the page.
    /// </summary>
    public string Secret { get; set; } = "";

    /// <summary>Where the card links, and the path the portal serves the site under.</summary>
    public string Path => "/" + Id + "/";
}
