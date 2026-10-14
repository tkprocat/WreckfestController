namespace WreckfestPortal;

/// <summary>The <c>Portal</c> section of appsettings.json: which controllers to list.</summary>
public sealed class PortalOptions
{
    public const string Section = "Portal";

    /// <summary>The page's heading and title, such as "Monday Night Wrecking".</summary>
    public string Title { get; set; } = "Wreckfest servers";

    /// <summary>A line under the heading. Optional.</summary>
    public string? Intro { get; set; }

    /// <summary>Seconds between checks of every server. At least 5.</summary>
    public int PollSeconds { get; set; } = 15;

    /// <summary>Seconds a controller has to answer before it counts as unreachable. 1 to 30.</summary>
    public int TimeoutSeconds { get; set; } = 3;

    /// <summary>The servers, in the order the page lists them.</summary>
    public List<PortalServer> Servers { get; set; } = [];

    /// <summary>What is wrong with the settings; empty when they are usable.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(Title))
        {
            problems.Add($"{Section}:Title is empty.");
        }

        if (PollSeconds < 5)
        {
            problems.Add($"{Section}:PollSeconds must be at least 5.");
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
                problems.Add($"{where}:ApiUrl must be an http or https address, such as http://127.0.0.1:5100/tvtp2.");
            }

            // A path on this domain, or a full address on another.
            if (!(server.Link.StartsWith('/') && !server.Link.StartsWith("//", StringComparison.Ordinal)) && !IsHttpUrl(server.Link))
            {
                problems.Add($"{where}:Link must be a path such as /tvtp2/, or an http or https address.");
            }
        }

        return problems;
    }

    private static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}

/// <summary>One controller the page lists.</summary>
public sealed class PortalServer
{
    /// <summary>Unique, for the page to tell the cards apart.</summary>
    public string Id { get; set; } = "";

    /// <summary>The card's heading, such as "TVTP2".</summary>
    public string Name { get; set; } = "";

    /// <summary>A line under the name, such as "Tracks from the TVTP2 mod". Optional.</summary>
    public string? Description { get; set; }

    /// <summary>Where the card links: the controller's site, such as <c>/tvtp2/</c>.</summary>
    public string Link { get; set; } = "";

    /// <summary>
    /// Where the portal reaches the controller, including its Web:PathBase, such as
    /// <c>http://127.0.0.1:5101/tvtp2</c>. Never shown on the page.
    /// </summary>
    public string ApiUrl { get; set; } = "";
}
