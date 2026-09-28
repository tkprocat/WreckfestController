using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WreckfestController.Data;
using WreckfestController.Data.Events;
using WreckfestController.Models;
using WreckfestController.Services;

namespace WreckfestController.Tests.Services;

/// <summary>A clock the test sets. Only <see cref="GetUtcNow"/> is faked; timers stay real.</summary>
public sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    public DateTime UtcNow => Now.UtcDateTime;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>
/// A migrated SQLite file in a temp folder, with an <see cref="EventStore"/> over it.
/// A real file, because the concurrency tests need real locking.
/// </summary>
public sealed class EventTestDatabase : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "wfc-event-tests", Guid.NewGuid().ToString("N"));

    public EventTestDatabase()
    {
        Directory.CreateDirectory(_directory);
        var options = new DbContextOptionsBuilder<ControllerDbContext>();
        ControllerDbContext.Configure(options, Path.Combine(_directory, "controller.db"));
        Contexts = new Factory(options.Options);

        using var db = Contexts.CreateDbContext();
        db.Database.Migrate();

        Store = new EventStore(Contexts, Clock);
    }

    public TestClock Clock { get; } = new();

    public IDbContextFactory<ControllerDbContext> Contexts { get; }

    public EventStore Store { get; }

    public static EventDefinition Definition(
        string name,
        DateTime startTime,
        RepeatSchedule? repeat = null,
        string timeZone = "UTC",
        int? collectionId = null,
        IReadOnlyList<EventLoopTrack>? tracks = null,
        EventServerConfig? serverConfig = null) =>
        new(name, string.Empty, startTime, timeZone, repeat, serverConfig, collectionId, tracks ?? [], string.Empty);

    public async Task<ScheduledEvent> CreateAsync(EventDefinition definition)
    {
        var (status, evt) = await Store.CreateAsync(definition, createdById: null);
        Assert.Equal(EventWriteStatus.Saved, status);
        return evt!;
    }

    public async Task<ScheduledEvent> ReloadAsync(int id) => (await Store.GetAsync(id))!;

    /// <summary>
    /// Retries for a while: a restart's finish callback can still be writing, because
    /// SmartRestartService reports Idle before it runs the callback.
    /// </summary>
    public void Dispose()
    {
        for (var attempt = 0; ; attempt++)
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(_directory, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 40)
            {
                Thread.Sleep(50);
            }
        }
    }

    private sealed class Factory(DbContextOptions<ControllerDbContext> options) : IDbContextFactory<ControllerDbContext>
    {
        public ControllerDbContext CreateDbContext() => new(options);
    }
}
