using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WreckfestController.Data;

namespace WreckfestController.Tests.Data;

/// <summary>
/// Runs against a real database file rather than in-memory SQLite, because file
/// locking, WAL files and table rebuilds only behave for real on disk.
/// </summary>
public sealed class ControllerDbContextTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "wfc-db-tests", Guid.NewGuid().ToString("N"));

    public ControllerDbContextTests()
    {
        // Creating the folder is the bootstrapper's job, not the context's.
        Directory.CreateDirectory(_directory);
    }

    private string DatabaseFile => Path.Combine(_directory, "controller.db");

    [Fact]
    public void Migrations_MatchTheModel()
    {
        using var context = CreateContext();

        // Fails when the model was changed without adding a migration.
        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Migrate_CreatesTheFile()
    {
        await using (var context = CreateContext())
        {
            await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
        }

        Assert.True(File.Exists(DatabaseFile));
    }

    [Fact]
    public async Task User_RoundTripsProfileFields()
    {
        var ct = TestContext.Current.CancellationToken;
        await using (var context = CreateContext())
        {
            await context.Database.MigrateAsync(ct);
            context.Users.Add(new AppUser
            {
                UserName = "admin",
                Email = "admin@example.com",
                DisplayName = "Race Control",
                TimeZone = "Europe/Copenhagen",
            });
            await context.SaveChangesAsync(ct);
        }

        await using (var context = CreateContext())
        {
            var user = await context.Users.SingleAsync(ct);
            Assert.Equal("Race Control", user.DisplayName);
            Assert.Equal("Europe/Copenhagen", user.TimeZone);
        }
    }

    [Fact]
    public async Task MainHost_RegistersFactoryForTheConfiguredPath()
    {
        using var host = Program
            .CreateHostBuilder([$"--Database:Path={DatabaseFile}"])
            .Build();

        var factory = host.Services.GetRequiredService<IDbContextFactory<ControllerDbContext>>();
        await using var context = await factory.CreateDbContextAsync(
            TestContext.Current.CancellationToken);

        var dataSource = new SqliteConnectionStringBuilder(
            context.Database.GetConnectionString()).DataSource;
        Assert.Equal(DatabaseFile, dataSource);
    }

    private ControllerDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ControllerDbContext>();
        ControllerDbContext.Configure(options, DatabaseFile);
        return new ControllerDbContext(options.Options);
    }

    public void Dispose()
    {
        // Pooled connections keep the file open, which would block the delete.
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
