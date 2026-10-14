using System.Text.Json.Serialization;

namespace WreckfestPortal;

/// <summary>How a server looked at the last check.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ServerState>))]
public enum ServerState
{
    /// <summary>Not checked yet: the portal has just started.</summary>
    [JsonStringEnumMemberName("checking")]
    Checking,

    /// <summary>The controller answered and its game server is running.</summary>
    [JsonStringEnumMemberName("online")]
    Online,

    /// <summary>The controller answered, but its game server is not running.</summary>
    [JsonStringEnumMemberName("server-stopped")]
    ServerStopped,

    /// <summary>The controller did not answer, or not with an overview.</summary>
    [JsonStringEnumMemberName("unreachable")]
    Unreachable,
}

/// <summary>What <c>GET /api/servers</c> answers: the page's heading and a card for each server.</summary>
public sealed record PortalPage(string Title, string? Intro, IReadOnlyList<ServerCard> Servers);

/// <summary>
/// One card on the page: the configured server and what its controller last said. Built
/// field by field, so <see cref="PortalServer.ApiUrl"/> never reaches the browser.
/// </summary>
/// <param name="CheckedAt">When the state was last established; null before the first check.</param>
/// <param name="LastSeenAt">When the controller last answered; its details are from then.</param>
public sealed record ServerCard(
    string Id,
    string Name,
    string? Description,
    string Link,
    ServerState State,
    string? ServerName,
    int? Humans,
    int? Bots,
    int? MaxPlayers,
    string? CurrentTrack,
    DateTimeOffset? CheckedAt,
    DateTimeOffset? LastSeenAt);

/// <summary>
/// The fields of a controller's <c>GET /api/public/overview</c> the page shows. Its own
/// copy, so the portal does not depend on the controller; anything else is ignored.
/// </summary>
internal sealed record Overview(string? ServerName, int? MaxPlayers, OverviewStatus? Status, OverviewTrack? CurrentTrack, OverviewPlayers? Players);

internal sealed record OverviewStatus(bool IsRunning);

internal sealed record OverviewTrack(string? Name);

internal sealed record OverviewPlayers(int Humans, int Bots);
