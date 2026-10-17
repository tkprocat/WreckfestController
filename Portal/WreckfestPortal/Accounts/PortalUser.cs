using Microsoft.AspNetCore.Identity;

namespace WreckfestPortal.Accounts;

/// <summary>
/// A person who can sign in to the web site. A global admin manages every server and the
/// accounts; anyone else manages only the servers in <see cref="Servers"/>.
/// </summary>
public class PortalUser : IdentityUser
{
    public const int DisplayNameMaxLength = 100;
    public const int TimeZoneMaxLength = 64;

    /// <summary>Shown in the UI instead of the username when set.</summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// IANA time zone id (for example "Europe/Copenhagen") that event times are shown
    /// in. Null means the browser's own zone.
    /// </summary>
    public string? TimeZone { get; set; }

    /// <summary>Every server, and the accounts. Its <see cref="Servers"/> are not used.</summary>
    public bool IsAdmin { get; set; }

    /// <summary>The servers a user who is not an admin may manage.</summary>
    public List<ServerGrant> Servers { get; set; } = [];

    /// <summary>
    /// Whether this account may manage the server with <paramref name="serverId"/>. A grant
    /// for a server the portal no longer lists is kept but grants nothing.
    /// </summary>
    public bool CanManage(string serverId) =>
        IsAdmin || Servers.Any(g => string.Equals(g.ServerId, serverId, StringComparison.OrdinalIgnoreCase));
}

/// <summary>One server a user may manage, by its <see cref="PortalServer.Id"/>.</summary>
public class ServerGrant
{
    public const int ServerIdMaxLength = 64;

    public string UserId { get; set; } = "";

    public string ServerId { get; set; } = "";
}
