using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WreckfestController.Data;
using WreckfestController.Models;

namespace WreckfestController.Services.Config;

/// <summary>The version of each settings section an editor loaded, to save it back against.</summary>
public sealed record SettingsVersions(int WreckfestServer, int SteamCmd, int Vote);

/// <summary>
/// What an editor loaded: the settings and their versions, taken at the same moment. It
/// also keeps each section as loaded, so a save can tell what changed even when the caller
/// edits <see cref="Settings"/> in place.
/// </summary>
public sealed class SettingsSnapshot
{
    private readonly Dictionary<Type, string> _loaded;

    public SettingsSnapshot(UserSettings settings, SettingsVersions versions)
    {
        Settings = settings;
        Versions = versions;
        _loaded = new Dictionary<Type, string>
        {
            [typeof(WreckfestServerSettings)] = JsonSerializer.Serialize(settings.WreckfestServer),
            [typeof(SteamCmdSettings)] = JsonSerializer.Serialize(settings.SteamCmd),
            [typeof(VoteSettings)] = JsonSerializer.Serialize(settings.Vote),
        };
    }

    public UserSettings Settings { get; }

    public SettingsVersions Versions { get; }

    /// <summary>The section exactly as it was loaded, whatever has been done to <see cref="Settings"/> since.</summary>
    internal T? Loaded<T>() where T : class => JsonSerializer.Deserialize<T>(_loaded[typeof(T)]);
}

/// <summary>A section the edit changes was changed elsewhere since it was loaded; nothing was saved.</summary>
public sealed class SettingsConflictException(string section)
    : InvalidOperationException($"The {section} settings were changed elsewhere since they were loaded. Nothing was saved.");

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

        _database.Changed += () => DatabaseChanged?.Invoke();
    }

    /// <summary>The startup settings file, read-only for 2.0.</summary>
    public string GetUserSettingsPath() => _userSettingsPath;

    /// <summary>The database file the settings are kept in.</summary>
    public string GetDatabasePath() => _database.DatabasePath;

    /// <summary>The current settings, for reading. Use <see cref="LoadForEdit"/> to save them back.</summary>
    public UserSettings LoadSettings() => LoadForEdit().Settings;

    /// <summary>
    /// Raised when the database becomes available or unavailable, so an editor that loaded
    /// the shipped defaults (recovery mode) can load the stored settings instead.
    /// </summary>
    public event Action? DatabaseChanged;

    /// <summary>The current settings and their versions, taken together, for an editor that will save them.</summary>
    public SettingsSnapshot LoadForEdit()
    {
        var server = _store.GetEntry<WreckfestServerSettings>();
        var steamCmd = _store.GetEntry<SteamCmdSettings>();
        var vote = _store.GetEntry<VoteSettings>();

        return new SettingsSnapshot(
            new UserSettings { WreckfestServer = server.Value, SteamCmd = steamCmd.Value, Vote = vote.Value },
            new SettingsVersions(server.Version, steamCmd.Version, vote.Version));
    }

    /// <summary>
    /// Saves the sections of <paramref name="edited"/> that differ from
    /// <paramref name="loaded"/>, each at the version it was loaded at, in one transaction.
    /// Returns what the editor now holds: the saved values and their new versions, and the
    /// untouched sections as they were loaded.
    /// </summary>
    /// <exception cref="SettingsConflictException">
    /// A section this edit changes was changed elsewhere (the web app, say) since it was
    /// loaded. Nothing is saved.
    /// </exception>
    /// <exception cref="InvalidOperationException">The database is unavailable.</exception>
    public SettingsSnapshot SaveSettings(UserSettings edited, SettingsSnapshot loaded)
    {
        if (!_database.IsReady)
        {
            throw new InvalidOperationException("Settings cannot be saved while the database is unavailable.");
        }

        var changes = new List<SettingsChange>();
        var server = Changed(edited.WreckfestServer, loaded.Loaded<WreckfestServerSettings>(), loaded.Versions.WreckfestServer, changes);
        var steamCmd = Changed(edited.SteamCmd, loaded.Loaded<SteamCmdSettings>(), loaded.Versions.SteamCmd, changes);
        var vote = Changed(edited.Vote, loaded.Loaded<VoteSettings>(), loaded.Versions.Vote, changes);
        if (changes.Count == 0)
        {
            return new SettingsSnapshot(new UserSettings { WreckfestServer = server, SteamCmd = steamCmd, Vote = vote }, loaded.Versions);
        }

        SettingsBatchResult result;
        try
        {
            // The store never resumes on this thread, so blocking the UI thread is safe.
            result = _store.SaveAllAsync(changes).GetAwaiter().GetResult();
        }
        catch (SettingsUnavailableException ex)
        {
            throw new InvalidOperationException("Settings cannot be saved while the database is unavailable.", ex);
        }

        if (result.Status == SettingsSaveStatus.Conflict)
        {
            throw new SettingsConflictException(Label(result.ConflictingSection));
        }

        _logger.LogInformation(
            "Saved the {Sections} settings",
            string.Join(", ", changes.Select(c => Label(c.Section))));

        int VersionOf(Type type, int loadedVersion) => result.Versions.TryGetValue(type, out var saved) ? saved : loadedVersion;
        return new SettingsSnapshot(
            new UserSettings { WreckfestServer = server, SteamCmd = steamCmd, Vote = vote },
            new SettingsVersions(
                VersionOf(typeof(WreckfestServerSettings), loaded.Versions.WreckfestServer),
                VersionOf(typeof(SteamCmdSettings), loaded.Versions.SteamCmd),
                VersionOf(typeof(VoteSettings), loaded.Versions.Vote)));
    }

    /// <summary>
    /// Adds <paramref name="edited"/> to <paramref name="changes"/> when it differs from what
    /// was loaded, once brought into range as the store will store it. Returns the value the
    /// editor holds for the section afterwards.
    /// </summary>
    private static T? Changed<T>(T? edited, T? loaded, int loadedVersion, List<SettingsChange> changes)
        where T : class
    {
        if (edited is null)
        {
            return loaded;
        }

        var stored = (T)SettingsSections.Normalize(typeof(T), Copy(edited));
        if (loaded is not null && Json(stored) == Json(loaded))
        {
            return loaded;
        }

        changes.Add(new SettingsChange(typeof(T), stored, loadedVersion));
        return stored;
    }

    private static string Label(Type? section) =>
        section == typeof(WreckfestServerSettings) ? "server"
        : section == typeof(SteamCmdSettings) ? "SteamCMD"
        : section == typeof(VoteSettings) ? "voting"
        : "these";

    private static string Json<T>(T value) => JsonSerializer.Serialize(value);

    private static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;

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
