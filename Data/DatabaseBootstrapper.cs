using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace WreckfestController.Data;

/// <summary>
/// The one place the database is prepared at startup. Nothing else migrates, and
/// configuration loading never touches the database.
/// </summary>
/// <remarks>
/// The steps, in order: create the folder, back up the existing file when a migration
/// is about to run, apply migrations, and switch the file to WAL. Phase 3b adds seeding
/// the first-run settings and warming the settings store's cache here.
/// A failure never escapes: it is recorded in <see cref="DatabaseState"/>, which puts
/// the app into recovery mode, and <see cref="Run"/> can be called again to retry.
/// </remarks>
public sealed class DatabaseBootstrapper
{
    private readonly IDbContextFactory<ControllerDbContext> _contextFactory;
    private readonly DatabaseState _state;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DatabaseBootstrapper> _logger;
    private readonly object _runLock = new();

    public DatabaseBootstrapper(
        IDbContextFactory<ControllerDbContext> contextFactory,
        DatabaseState state,
        ILogger<DatabaseBootstrapper> logger)
        : this(contextFactory, state, logger, TimeProvider.System)
    {
    }

    public DatabaseBootstrapper(
        IDbContextFactory<ControllerDbContext> contextFactory,
        DatabaseState state,
        ILogger<DatabaseBootstrapper> logger,
        TimeProvider timeProvider)
    {
        _contextFactory = contextFactory;
        _state = state;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    /// <summary>Prepares the database. Returns false, and enters recovery mode, on failure.</summary>
    public bool Run()
    {
        lock (_runLock)
        {
            string? backupPath = null;
            try
            {
                ControllerDbContext.EnsureFolder(_state.DatabasePath);

                using var context = _contextFactory.CreateDbContext();

                var pending = context.Database.GetPendingMigrations().ToList();
                if (pending.Count > 0 && HasContent(_state.DatabasePath))
                {
                    // Migrations rebuild tables in place. Without a copy there is no way
                    // back from one that goes wrong half way.
                    backupPath = DatabaseBackup.Create(
                        _state.DatabasePath,
                        "before-migration",
                        _timeProvider.GetLocalNow());
                    _logger.LogInformation(
                        "Backed up the database to {BackupPath} before applying {Count} migration(s)",
                        backupPath,
                        pending.Count);
                }

                context.Database.Migrate();

                // WAL lets the API and the UI read while the other writes. The mode is
                // stored in the file, so this only changes anything the first time.
                context.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");

                _logger.LogInformation(
                    "Database ready at {DatabasePath} ({Count} migration(s) applied)",
                    _state.DatabasePath,
                    pending.Count);
                _state.MarkReady(backupPath);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Database at {DatabasePath} could not be prepared. Running in recovery mode: sign-in is unavailable and scheduled events will not activate.",
                    _state.DatabasePath);
                _state.MarkFailed(ex.Message, backupPath);
                return false;
            }
            finally
            {
                // Pooled connections would keep the file open, which blocks the user from
                // moving or replacing it while the app sits in recovery mode.
                SqliteConnection.ClearAllPools();
            }
        }
    }

    private static bool HasContent(string path) =>
        File.Exists(path) && new FileInfo(path).Length > 0;
}
