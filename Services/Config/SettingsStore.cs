using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using WreckfestController.Data;
using WreckfestController.Data.Settings;

namespace WreckfestController.Services.Config;

/// <summary>A section's value and the version it was read at.</summary>
public sealed record SettingsEntry<T>(T Value, int Version);

public enum SettingsSaveStatus
{
    Saved,

    /// <summary>The section is no longer at the version the caller read.</summary>
    Conflict,
}

/// <summary>The outcome of a save, and the section as it now stands.</summary>
public sealed record SettingsSaveResult<T>(SettingsSaveStatus Status, SettingsEntry<T> Current);

public sealed class SettingsChangedEventArgs(Type section) : EventArgs
{
    /// <summary>The section type, such as <see cref="Models.VoteSettings"/>.</summary>
    public Type Section { get; } = section;
}

/// <summary>Saving needs the database, and it is not available (recovery mode).</summary>
public sealed class SettingsUnavailableException(string message) : InvalidOperationException(message);

/// <summary>
/// The settings a person edits, kept in the database one section at a time. See
/// <see cref="SettingsStore"/>.
/// </summary>
public interface ISettingsStore
{
    /// <summary>The section's current value: a copy the caller may change freely.</summary>
    T Get<T>() where T : class;

    /// <summary>The section's current value and version, for an edit that saves it back.</summary>
    SettingsEntry<T> GetEntry<T>() where T : class;

    /// <summary>
    /// Saves <paramref name="value"/> if the section is still at <paramref name="expectedVersion"/>.
    /// Values are brought into range first. Throws <see cref="SettingsUnavailableException"/>
    /// when the database is not available.
    /// </summary>
    Task<SettingsSaveResult<T>> SaveAsync<T>(T value, int expectedVersion, CancellationToken cancellationToken = default)
        where T : class;

    /// <summary>Raised after a section changes: a save, or its values arriving from the database.</summary>
    event EventHandler<SettingsChangedEventArgs>? Changed;
}

/// <summary>
/// Keeps settings sections in the <c>SettingsSections</c> table and serves them from a
/// cache. It is not a configuration provider: nothing is layered over appsettings.json
/// or user-settings.json, so a value always comes from the one place it was edited.
/// </summary>
/// <remarks>
/// <para>
/// A section's row is created from the shipped defaults the first time the store finds
/// it missing, and never again: afterwards the database is the only source.
/// </para>
/// <para>
/// While the database is unavailable (recovery mode) every section reads as its shipped
/// default, version 0, and saving throws. Once the database becomes ready, the store
/// loads it and, if anyone read those defaults, raises <see cref="Changed"/> for every
/// section, so readers pick up the stored values without a restart.
/// </para>
/// <para>
/// The cache holds JSON, and every read deserializes a fresh copy, so no caller can
/// change what another sees.
/// </para>
/// </remarks>
public sealed class SettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IDbContextFactory<ControllerDbContext> _contexts;
    private readonly DatabaseState _database;
    private readonly IConfiguration _shipped;
    private readonly ILogger<SettingsStore> _logger;
    private readonly object _lock = new();

    /// <summary>Section name to its stored JSON and version. Null until loaded.</summary>
    private Dictionary<string, (string Json, int Version)>? _cache;

    /// <summary>Whether a reader has been given the shipped defaults since the last load.</summary>
    private bool _servedDefaults;

    /// <summary>
    /// One save at a time, from its write to publishing the result, so a slower save can
    /// never put an older version back into the cache after a newer one.
    /// </summary>
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    /// <param name="shipped">The shipped appsettings.json only, without user-settings.json.</param>
    public SettingsStore(
        IDbContextFactory<ControllerDbContext> contexts,
        DatabaseState database,
        ShippedSettings shipped,
        ILogger<SettingsStore> logger)
    {
        _contexts = contexts;
        _database = database;
        _shipped = shipped.Configuration;
        _logger = logger;

        // A retry out of recovery mode, or the first bootstrap after this was created.
        _database.Changed += OnDatabaseChanged;
    }

    public event EventHandler<SettingsChangedEventArgs>? Changed;

    public T Get<T>() where T : class => GetEntry<T>().Value;

    public SettingsEntry<T> GetEntry<T>() where T : class
    {
        var name = SettingsSections.NameOf(typeof(T));
        EnsureLoaded();

        lock (_lock)
        {
            if (_cache is not null && _cache.TryGetValue(name, out var stored))
            {
                return new(Deserialize<T>(stored.Json), stored.Version);
            }

            _servedDefaults = true;
        }

        return new((T)SettingsSections.Default(typeof(T), _shipped), 0);
    }

    public async Task<SettingsSaveResult<T>> SaveAsync<T>(T value, int expectedVersion, CancellationToken cancellationToken = default)
        where T : class
    {
        var name = SettingsSections.NameOf(typeof(T));

        // Normalize a copy: the caller's object stays as they passed it.
        var json = JsonSerializer.Serialize(SettingsSections.Normalize(typeof(T), Deserialize<T>(JsonSerializer.Serialize(value))));

        await _saveGate.WaitAsync(cancellationToken);
        int updated;
        SettingsSection current;
        bool changed;
        try
        {
            EnsureLoaded();
            lock (_lock)
            {
                if (_cache is null || !_database.IsReady)
                {
                    throw new SettingsUnavailableException("Settings cannot be saved while the database is unavailable.");
                }
            }

            // From here the write may commit, so the rest runs to the end: a cancellation
            // between the write and the cache update would leave readers on the old value.
            await using var db = await _contexts.CreateDbContextAsync(CancellationToken.None);
            updated = await db.SettingsSections
                .Where(s => s.Section == name && s.Version == expectedVersion)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(r => r.Json, json).SetProperty(r => r.Version, r => r.Version + 1),
                    CancellationToken.None);

            // A conflict reads the row too: another instance's save lands in this cache.
            current = await db.SettingsSections.AsNoTracking().SingleAsync(s => s.Section == name, CancellationToken.None);
            changed = Publish(name, current);
        }
        finally
        {
            _saveGate.Release();
        }

        if (updated > 0)
        {
            _logger.LogInformation("Saved the {Section} settings (version {Version})", name, current.Version);
        }

        if (changed)
        {
            RaiseChanged(typeof(T));
        }

        var entry = new SettingsEntry<T>(Deserialize<T>(current.Json), current.Version);
        return new(updated > 0 ? SettingsSaveStatus.Saved : SettingsSaveStatus.Conflict, entry);
    }

    /// <summary>
    /// Puts <paramref name="row"/> in the cache if it is newer than what is there. True
    /// when that changed the section, so listeners must hear about it.
    /// </summary>
    private bool Publish(string name, SettingsSection row)
    {
        lock (_lock)
        {
            if (_cache is null
                || (_cache.TryGetValue(name, out var cached) && cached.Version >= row.Version))
            {
                return false;
            }

            _cache[name] = (row.Json, row.Version);
            return true;
        }
    }

    private void OnDatabaseChanged()
    {
        if (_database.IsReady)
        {
            lock (_lock)
            {
                // Reload: a retry may have opened a different file than a failed first try.
                _cache = null;
            }

            EnsureLoaded();
            return;
        }

        bool wasLoaded;
        lock (_lock)
        {
            // Back to the shipped defaults, and saving refuses, as in recovery mode from
            // the start. The next load replaces them, so it must be announced.
            wasLoaded = _cache is not null;
            _cache = null;
            _servedDefaults = true;
        }

        if (wasLoaded)
        {
            foreach (var type in SettingsSections.Types)
            {
                RaiseChanged(type);
            }
        }
    }

    /// <summary>
    /// Loads every section, creating the missing ones, if the database is ready. Announces
    /// every section when readers had been given the shipped defaults in the meantime,
    /// since the stored values replace them.
    /// </summary>
    private void EnsureLoaded()
    {
        lock (_lock)
        {
            if (_cache is not null || !_database.IsReady)
            {
                return;
            }

            try
            {
                _cache = Load();
            }
            catch (Exception ex)
            {
                // Readers get the shipped defaults, and saving refuses, until the next try.
                _logger.LogError(ex, "Could not load settings from the database; using the shipped defaults");
                return;
            }

            if (!_servedDefaults)
            {
                return;
            }

            _servedDefaults = false;
        }

        foreach (var type in SettingsSections.Types)
        {
            RaiseChanged(type);
        }
    }

    private Dictionary<string, (string Json, int Version)> Load()
    {
        using var db = _contexts.CreateDbContext();
        var rows = db.SettingsSections.AsNoTracking().ToDictionary(s => s.Section);

        foreach (var type in SettingsSections.Types)
        {
            var name = SettingsSections.NameOf(type);
            if (rows.ContainsKey(name))
            {
                continue;
            }

            // First run for this section. INSERT OR IGNORE, so a row another instance has
            // just created wins over these defaults instead of failing the load.
            var json = JsonSerializer.Serialize(SettingsSections.Default(type, _shipped), type);
            db.Database.ExecuteSql(
                $"INSERT OR IGNORE INTO SettingsSections (Section, Json, Version) VALUES ({name}, {json}, 1)");
            rows[name] = db.SettingsSections.AsNoTracking().Single(s => s.Section == name);
            _logger.LogInformation("Created the {Section} settings from the shipped defaults", name);
        }

        return rows.ToDictionary(r => r.Key, r => (r.Value.Json, r.Value.Version));
    }

    private void RaiseChanged(Type section)
    {
        if (Changed is not { } changed)
        {
            return;
        }

        var args = new SettingsChangedEventArgs(section);
        foreach (EventHandler<SettingsChangedEventArgs> listener in changed.GetInvocationList())
        {
            try
            {
                listener(this, args);
            }
            catch (Exception ex)
            {
                // One failing listener must neither undo a save that has happened nor
                // keep the others from hearing about it.
                _logger.LogError(ex, "A settings listener failed for {Section}", section.Name);
            }
        }
    }

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, JsonOptions)
        ?? throw new InvalidOperationException($"The stored {typeof(T).Name} settings are empty.");
}

/// <summary>
/// The shipped appsettings.json (and its environment file), without user-settings.json:
/// where first-run settings come from.
/// </summary>
public sealed class ShippedSettings(IConfiguration configuration)
{
    public IConfiguration Configuration { get; } = configuration;
}
