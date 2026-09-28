namespace WreckfestController.Data.Settings;

/// <summary>
/// One section of the settings a person edits (the server paths, SteamCMD, voting),
/// stored as JSON. Only <c>SettingsStore</c> reads and writes these rows.
/// </summary>
/// <remarks>
/// Startup settings (the API's binding and key, the database path) are not here: they
/// stay in a file, so a lockout can be fixed by editing it.
/// </remarks>
public class SettingsSection
{
    public const int NameMaxLength = 64;

    /// <summary>The section's name, such as <c>Vote</c>.</summary>
    public string Section { get; set; } = string.Empty;

    public string Json { get; set; } = string.Empty;

    /// <summary>Bumped on every save; a save from a stale copy gets a conflict.</summary>
    public int Version { get; set; } = 1;
}
