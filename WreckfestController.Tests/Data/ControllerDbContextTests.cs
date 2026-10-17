using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
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

    // The accounts moved to the portal; each cup keeps its creator's name, as they had it.
    [Fact]
    public async Task MovingTheAccountsToThePortal_KeepsEachCupsCreatorsName()
    {
        var ct = TestContext.Current.CancellationToken;
        await using (var context = CreateContext())
        {
            var migrator = context.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
            await migrator.MigrateAsync("20261016002809_RaceEntryBestLapSetAt", ct);
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "AspNetUsers" ("Id", "UserName", "DisplayName", "EmailConfirmed", "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount")
                VALUES ('u1', 'allan', 'Race Control', 0, 0, 0, 0, 0), ('u2', 'bob', NULL, 0, 0, 0, 0, 0), ('u3', 'carol', '  ', 0, 0, 0, 0, 0);
                """,
                ct);
            // The cups as that migration's table had them: every required column filled with a
            // value of its type, so the test does not depend on the columns cups had then.
            var connection = context.Database.GetDbConnection();
            await connection.OpenAsync(ct);
            var required = new List<(string Name, string Type)>();
            await using (var columns = connection.CreateCommand())
            {
                columns.CommandText = "SELECT name, type FROM pragma_table_info('Cups') WHERE \"notnull\" = 1 AND dflt_value IS NULL AND pk = 0";
                await using var reader = await columns.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    required.Add((reader.GetString(0), reader.GetString(1)));
                }
            }

            foreach (var (name, creator) in new[] { ("One", "u1"), ("Two", "u2"), ("Three", "u3"), ("Four", null) })
            {
                await using var insert = connection.CreateCommand();
                var filled = required.Where(c => c.Name is not "Name" and not "CreatedById").ToList();
                insert.CommandText =
                    $"INSERT INTO \"Cups\" (\"Name\", \"CreatedById\"{string.Concat(filled.Select(c => $", \"{c.Name}\""))}) " +
                    $"VALUES ($name, $creator{string.Concat(filled.Select(c => c.Type == "INTEGER" ? ", 0" : c.Type == "REAL" ? ", 0" : c.Name.EndsWith("Tracks", StringComparison.Ordinal) ? ", '[]'" : ", ''"))})";
                insert.Parameters.Add(new SqliteParameter("$name", name));
                insert.Parameters.Add(new SqliteParameter("$creator", (object?)creator ?? DBNull.Value));
                await insert.ExecuteNonQueryAsync(ct);
            }

            await connection.CloseAsync();
            await context.Database.MigrateAsync(ct);
        }

        await using (var context = CreateContext())
        {
            var creators = await context.Cups.OrderBy(c => c.Id).Select(c => c.CreatedBy).ToListAsync(ct);
            Assert.Equal(["Race Control", "bob", "carol", null], creators);
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
        SqlitePools.ReleaseFolder(_directory);
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
