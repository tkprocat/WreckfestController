using Microsoft.AspNetCore.Identity;

namespace WreckfestController.Data;

/// <summary>
/// A person who can sign in to the web UI.
/// </summary>
public class AppUser : IdentityUser
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
}
