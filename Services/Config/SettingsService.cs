using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WreckfestController.Data;
using WreckfestController.Models;

namespace WreckfestController.Services.Config;

/// <summary>The version of each settings section an editor loaded, to save it back against.</summary>
public sealed record SettingsVersions(int WreckfestServer, int SteamCmd, int Vote);

/// <summary>
/// The WPF Configuration tab's view of the settings: the database sections as one
/// <see cref="UserSettings"/>, and where things are kept.
/// </summary>
/// <remarks>
/// user-settings.json is 2.0's startup file (the API binding and key, the database path).
/// It is read by the host's configuration and <b>never written</b>, so rolling back to 1.x
/// finds it as it was. 1.x keys still in it are no longer read; each is logged once.
/// </remarks>
public class SettingsService
{
    /// <summary>1.x sections whose keys now live in the database, or nowhere.</summary>
    private static readonly string[] RetiredSections = ["WreckfestServer", "SteamCmd", "Vote", "Webhooks", "WreckfestWeb"];

    /// <summary>Keys under a retired section that are still read from the file.</summary>
    private static readonly string[] StillReadKeys = ["WreckfestServer:SupportedBuild"];

    private readonly string _userSettingsPath;
    private readonly ISettingsStore _store;
    private readonly DatabaseState _database;
    private readonly ILogger<SettingsService> _logger;

    public SettingsService(
        IConfiguration configuration,
        ISettingsStore store,
        DatabaseState database,
        ILogger<SettingsService> logger)
    {
        _logger = logger;
        _store = store;
        _database = database;
        _userSettingsPath = ResolveUserSettingsPath(configuration);

        _logger.LogInformation("Startup settings file: {Path}", _userSettingsPath);
        ReportRetiredKeys();
    }

    /// <summary>The startup settings file, read-only for 2.0.</summary>
    public string GetUserSettingsPath() => _userSettingsPath;

    /// <summary>The database file the settings are kept in.</summary>
    public string GetDatabasePath() => _database.DatabasePath;

    /// <summary>The current settings, for reading. Use <see cref="LoadForEdit"/> to save them back.</summary>
    public UserSettings LoadSettings() => LoadForEdit().Settings;

    /// <summary>The current settings and their versions, for an editor that will save them.</summary>
    public (UserSettings Settings, SettingsVersions Versions) LoadForEdit()
    {
        var server = _store.GetEntry<WreckfestServerSettings>();
        var steamCmd = _store.GetEntry<SteamCmdSettings>();
        var vote = _store.GetEntry<VoteSettings>();

        return (
            new UserSettings { WreckfestServer = server.Value, SteamCmd = steamCmd.Value, Vote = vote.Value },
            new SettingsVersions(server.Version, steamCmd.Version, vote.Version));
    }

    /// <summary>
    /// Saves each section of <paramref name="settings"/> that differs from what is stored,
    /// against the version <paramref name="loaded"/> says the editor started from. Returns
    /// the versions to save against next time.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A section was changed elsewhere (the web app, say) since it was loaded, or the
    /// database is unavailable. Every section is checked before any is saved, so a
    /// stale edit saves nothing.
    /// </exception>
    public SettingsVersions SaveSettings(UserSettings settings, SettingsVersions loaded)
    {
        if (!_database.IsReady)
        {
            throw new InvalidOperationException("Settings cannot be saved while the database is unavailable.");
        }

        Check(loaded.WreckfestServer, _store.GetEntry<WreckfestServerSettings>().Version, "server");
        Check(loaded.SteamCmd, _store.GetEntry<SteamCmdSettings>().Version, "SteamCMD");
        Check(loaded.Vote, _store.GetEntry<VoteSettings>().Version, "voting");

        return new SettingsVersions(
            Save(settings.WreckfestServer, loaded.WreckfestServer, "server"),
            Save(settings.SteamCmd, loaded.SteamCmd, "SteamCMD"),
            Save(settings.Vote, loaded.Vote, "voting"));
    }

    private static void Check(int loadedVersion, int currentVersion, string label)
    {
        if (loadedVersion != currentVersion)
        {
            Conflict(label);
        }
    }

    private int Save<T>(T? value, int loadedVersion, string label)
        where T : class
    {
        var current = _store.GetEntry<T>();
        if (value is null || SameJson(value, current.Value))
        {
            // Unchanged: saving would only bump the version under someone else's editor.
            return current.Version == loadedVersion ? loadedVersion : Conflict(label);
        }

        SettingsSaveResult<T> result;
        try
        {
            // The store never resumes on this thread, so blocking the UI thread is safe.
            result = _store.SaveAsync(value, loadedVersion).GetAwaiter().GetResult();
        }
        catch (SettingsUnavailableException ex)
        {
            throw new InvalidOperationException("Settings cannot be saved while the database is unavailable.", ex);
        }

        if (result.Status == SettingsSaveStatus.Conflict)
        {
            return Conflict(label);
        }

        _logger.LogInformation("Saved the {Section} settings", label);
        return result.Current.Version;
    }

    private static int Conflict(string label) =>
        throw new InvalidOperationException(
            $"The {label} settings were changed elsewhere since this tab loaded them. Reload them and try again.");

    private static bool SameJson<T>(T a, T b) => JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);

    /// <summary>
    /// Logs, once, each 1.x key in user-settings.json that 2.0 no longer reads, so an
    /// operator who edits the file sees why a change there does nothing.
    /// </summary>
    private void ReportRetiredKeys()
    {
        if (!File.Exists(_userSettingsPath))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(
                File.ReadAllText(_userSettingsPath),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

            foreach (var section in document.RootElement.EnumerateObject())
            {
                if (!RetiredSections.Contains(section.Name, StringComparer.OrdinalIgnoreCase)
                    || section.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var key in section.Value.EnumerateObject())
                {
                    var path = $"{section.Name}:{key.Name}";
                    if (!StillReadKeys.Contains(path, StringComparer.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning("user-settings.json: '{Key}' is no longer read; set it in Settings", path);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // The host already read this file; a failure here only loses the hint.
            _logger.LogDebug(ex, "Could not scan {Path} for keys that are no longer read", _userSettingsPath);
        }
    }

    /// <summary>
    /// Resolves the user settings file path based on configuration
    /// </summary>
    private static string ResolveUserSettingsPath(IConfiguration configuration)
    {
        var configuredPath = configuration["UserSettingsPath"];

        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            // Default to %LocalAppData%\WreckfestController\user-settings.json
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WreckfestController",
                "user-settings.json");
        }

        return Environment.ExpandEnvironmentVariables(configuredPath);
    }
}
