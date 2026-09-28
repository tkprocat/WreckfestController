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

/// <summary>One section to save with <see cref="ISettingsStore.SaveAllAsync"/>: its type, value and the version it was read at.</summary>
public sealed record SettingsChange(Type Section, object Value, int ExpectedVersion);

/// <summary>
/// The outcome of saving several sections together: saved, or the first section that
/// conflicted (nothing saved). <see cref="Versions"/> is every changed section's version now.
/// </summary>
public sealed record SettingsBatchResult(SettingsSaveStatus Status, Type? ConflictingSection, IReadOnlyDictionary<Type, int> Versions);

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

    /// <summary>
    /// Saves several sections in one transaction, each at its expected version, or none of
    /// them. Throws <see cref="SettingsUnavailableException"/> when the database is not available.
    /// </summary>
    Task<SettingsBatchResult> SaveAllAsync(IReadOnlyList<SettingsChange> changes, CancellationToken cancellationToken = default);

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
/// default, version 0, and saving throws. When the database becomes ready the store
/// loads it, and when it fails again the store drops back to the defaults.
/// </para>
/// <para>
/// <see cref="Changed"/> follows one rule: the store remembers, per section, the value
/// its listeners last heard of (or the first value anyone read). Every read, save and
/// database change updates the cache and compares it with those in one step under the
/// lock, then announces each section whose value now differs. Announcements are made
/// outside every lock, so a listener may read or save settings itself.
/// </para>
/// <para>
/// A database change (recovery mode, or a retry out of it) waits for a save in progress,
/// so a save never runs against a database state it did not check. Saves never resume
/// on the caller's thread, so a UI thread that retries the database while its own save
/// is running cannot deadlock.
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

    /// <summary>
    /// Bumped whenever the cache is dropped, so a save that began against an earlier
    /// cache cannot publish into the current one.
    /// </summary>
    private int _generation;

    /// <summary>Per section, the JSON listeners last heard of, or that a reader first saw.</summary>
    private readonly Dictionary<string, string> _heard = new();

    /// <summary>Per section, the shipped default's JSON.</summary>
    private readonly Dictionary<string, string> _defaults = new();

    /// <summary>
    /// One save at a time, from its write to publishing the result, so a slower save can
    /// never put an older version back into the cache after a newer one. Database changes
    /// take it too.
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
        string json;
        int version;
        List<Type> changed;
        lock (_lock)
        {
            // Load, read and compare in one step: what this reader gets is what listeners
            // are told about, even if the cache was being reloaded a moment ago.
            EnsureLoadedLocked();
            (json, version) = _cache is not null && _cache.TryGetValue(name, out var stored)
                ? stored
                : (DefaultJson(typeof(T)), 0);
            changed = ReconcileLocked();
        }

        Announce(changed);
        return new(Deserialize<T>(json), version);
    }

    public async Task<SettingsSaveResult<T>> SaveAsync<T>(T value, int expectedVersion, CancellationToken cancellationToken = default)
        where T : class
    {
        var (status, _, rows) = await SaveCoreAsync([new SettingsChange(typeof(T), value, expectedVersion)], cancellationToken)
            .ConfigureAwait(false);
        var current = rows[typeof(T)];
        return new(status, new SettingsEntry<T>(Deserialize<T>(current.Json), current.Version));
    }

    public async Task<SettingsBatchResult> SaveAllAsync(IReadOnlyList<SettingsChange> changes, CancellationToken cancellationToken = default)
    {
        var (status, conflict, rows) = await SaveCoreAsync(changes, cancellationToken).ConfigureAwait(false);
        return new(status, conflict, rows.ToDictionary(r => r.Key, r => r.Value.Version));
    }

    /// <summary>
    /// Saves every change in one transaction, each at its expected version, or none: the
    /// first section found at another version rolls the lot back. Reads every changed
    /// section back afterwards, so a conflict brings another instance's saves into the cache.
    /// </summary>
    private async Task<(SettingsSaveStatus Status, Type? Conflict, Dictionary<Type, SettingsSection> Rows)> SaveCoreAsync(
        IReadOnlyList<SettingsChange> changes,
        CancellationToken cancellationToken)
    {
        if (changes.Count == 0 || changes.Select(c => c.Section).Distinct().Count() != changes.Count)
        {
            throw new ArgumentException("Give each section to save once.", nameof(changes));
        }

        // Normalize copies: the callers' objects stay as they passed them.
        var writes = changes
            .Select(c => (
                c.Section,
                Name: SettingsSections.NameOf(c.Section),
                Json: JsonSerializer.Serialize(SettingsSections.Normalize(c.Section, Copy(c.Value, c.Section)), c.Section),
                c.ExpectedVersion))
            .ToList();

        Type? conflict = null;
        var rows = new Dictionary<Type, SettingsSection>();
        try
        {
            // ConfigureAwait(false) throughout: see the class remarks on deadlocks.
            await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                int generation;
                lock (_lock)
                {
                    EnsureLoadedLocked();
                    if (_cache is null || !_database.IsReady)
                    {
                        throw new SettingsUnavailableException("Settings cannot be saved while the database is unavailable.");
                    }

                    generation = _generation;
                }

                // From here the writes may commit, so the rest runs to the end: a cancellation
                // between the commit and the cache update would leave readers on the old values.
                var db = await _contexts.CreateDbContextAsync(CancellationToken.None).ConfigureAwait(false);
                await using (db.ConfigureAwait(false))
                {
                    var transaction = await db.Database.BeginTransactionAsync(CancellationToken.None).ConfigureAwait(false);
                    await using (transaction.ConfigureAwait(false))
                    {
                        foreach (var write in writes)
                        {
                            var updated = await db.SettingsSections
                                .Where(s => s.Section == write.Name && s.Version == write.ExpectedVersion)
                                .ExecuteUpdateAsync(
                                    s => s.SetProperty(r => r.Json, write.Json).SetProperty(r => r.Version, r => r.Version + 1),
                                    CancellationToken.None)
                                .ConfigureAwait(false);
                            if (updated == 0)
                            {
                                conflict = write.Section;
                                break;
                            }
                        }

                        if (conflict is null)
                        {
                            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                        }
                        else
                        {
                            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                        }
                    }

                    foreach (var write in writes)
                    {
                        rows[write.Section] = await db.SettingsSections
                            .AsNoTracking()
                            .SingleAsync(s => s.Section == write.Name, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                }

                foreach (var write in writes)
                {
                    Publish(write.Name, rows[write.Section], generation);
                }
            }
            finally
            {
                _saveGate.Release();
            }
        }
        finally
        {
            // Even when the save failed: it may have loaded the cache, which listeners
            // must hear about.
            List<Type> changed;
            lock (_lock)
            {
                EnsureLoadedLocked();
                changed = ReconcileLocked();
            }

            Announce(changed);
        }

        if (conflict is null)
        {
            foreach (var write in writes)
            {
                _logger.LogInformation("Saved the {Section} settings (version {Version})", write.Name, rows[write.Section].Version);
            }
        }

        return (conflict is null ? SettingsSaveStatus.Saved : SettingsSaveStatus.Conflict, conflict, rows);
    }

    private static object Copy(object value, Type type) =>
        JsonSerializer.Deserialize(JsonSerializer.Serialize(value, type), type, JsonOptions)
        ?? throw new ArgumentException($"The {type.Name} settings are empty.", nameof(value));

    /// <summary>
    /// Puts <paramref name="row"/> in the cache if it is newer than what is there. A
    /// database change waits for saves, so the generation should always match; if it does
    /// not, the row is not trusted, and the cache is dropped for the next read to reload.
    /// </summary>
    private void Publish(string name, SettingsSection row, int generation)
    {
        lock (_lock)
        {
            if (generation != _generation)
            {
                _cache = null;
                _generation++;
                return;
            }

            if (_cache is not null && (!_cache.TryGetValue(name, out var cached) || cached.Version < row.Version))
            {
                _cache[name] = (row.Json, row.Version);
            }
        }
    }

    private void OnDatabaseChanged()
    {
        // Wait for a save in progress: it checked the database state and must finish
        // against it. Saves never need this thread to finish.
        List<Type> changed;
        _saveGate.Wait();
        try
        {
            lock (_lock)
            {
                // Failed: back to the shipped defaults, and saving refuses. Ready: reload.
                _cache = null;
                _generation++;
                EnsureLoadedLocked();
                changed = ReconcileLocked();
            }
        }
        finally
        {
            _saveGate.Release();
        }

        Announce(changed);
    }

    /// <summary>
    /// Loads every section, creating the missing ones, if the database is ready and the
    /// cache is empty. Call under <see cref="_lock"/>. Announces nothing: callers do,
    /// outside locks, after <see cref="ReconcileLocked"/>.
    /// </summary>
    private void EnsureLoadedLocked()
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
        }
    }

    /// <summary>
    /// The sections whose current value differs from what listeners last heard, marking
    /// them heard. A section nobody has heard of or read is only recorded. Call under
    /// <see cref="_lock"/>.
    /// </summary>
    private List<Type> ReconcileLocked()
    {
        var changed = new List<Type>();
        foreach (var type in SettingsSections.Types)
        {
            var name = SettingsSections.NameOf(type);
            var current = _cache is not null && _cache.TryGetValue(name, out var stored) ? stored.Json : DefaultJson(type);
            if (_heard.TryGetValue(name, out var heard) && heard == current)
            {
                continue;
            }

            if (heard is not null)
            {
                changed.Add(type);
            }

            _heard[name] = current;
        }

        return changed;
    }

    /// <summary>The shipped default's JSON. Call under <see cref="_lock"/>.</summary>
    private string DefaultJson(Type type)
    {
        var name = SettingsSections.NameOf(type);
        if (!_defaults.TryGetValue(name, out var json))
        {
            json = JsonSerializer.Serialize(SettingsSections.Default(type, _shipped), type);
            _defaults[name] = json;
        }

        return json;
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
            var json = DefaultJson(type);
            db.Database.ExecuteSql(
                $"INSERT OR IGNORE INTO SettingsSections (Section, Json, Version) VALUES ({name}, {json}, 1)");
            rows[name] = db.SettingsSections.AsNoTracking().Single(s => s.Section == name);
            _logger.LogInformation("Created the {Section} settings from the shipped defaults", name);
        }

        return rows.ToDictionary(r => r.Key, r => (r.Value.Json, r.Value.Version));
    }

    private void Announce(List<Type> sections)
    {
        foreach (var section in sections)
        {
            RaiseChanged(section);
        }
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
