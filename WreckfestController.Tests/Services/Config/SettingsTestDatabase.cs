using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using WreckfestController.Data;
using WreckfestController.Services.Config;

namespace WreckfestController.Tests.Services.Config;

/// <summary>A migrated SQLite file in a temp folder, ready, with a <see cref="SettingsStore"/> over it.</summary>
public sealed class SettingsTestDatabase : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "wfc-settings-db", Guid.NewGuid().ToString("N"));

    public SettingsTestDatabase()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "controller.db");
        var options = new DbContextOptionsBuilder<ControllerDbContext>();
        ControllerDbContext.Configure(options, path);
        var contexts = new Factory(options.Options);
        using (var db = contexts.CreateDbContext())
        {
            db.Database.Migrate();
        }

        var database = new DatabaseState(path);
        database.MarkReady(null);
        Store = new SettingsStore(
            contexts,
            database,
            new ShippedSettings(new ConfigurationBuilder().Build()),
            NullLogger<SettingsStore>.Instance);
    }

    public SettingsStore Store { get; }

    public void Dispose()
    {
        SqlitePools.ReleaseFolder(_directory);
        Directory.Delete(_directory, recursive: true);
    }

    private sealed class Factory(DbContextOptions<ControllerDbContext> options) : IDbContextFactory<ControllerDbContext>
    {
        public ControllerDbContext CreateDbContext() => new(options);
    }
}
