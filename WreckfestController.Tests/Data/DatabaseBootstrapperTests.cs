using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WreckfestController.Data;

namespace WreckfestController.Tests.Data;

/// <summary>
/// Real database files in a per-test temp folder, because file locking, WAL files and
/// table rebuilds only behave for real on disk.
/// </summary>
public sealed class DatabaseBootstrapperTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "wfc-bootstrap-tests", Guid.NewGuid().ToString("N"));

    private string DatabaseFile => Path.Combine(_directory, "controller.db");

    private string BackupFolder => Path.Combine(_directory, DatabaseBackup.FolderName);

    [Fact]
    public void Run_OnFreshInstall_CreatesMigratedDatabaseWithoutBackup()
    {
        var (bootstrapper, state) = Create();

        Assert.True(bootstrapper.Run());

        Assert.True(state.IsReady);
        Assert.Null(state.Error);
        Assert.Null(state.BackupPath);
        Assert.False(Directory.Exists(BackupFolder));
        using var context = CreateContext();
        Assert.Empty(context.Database.GetPendingMigrations());
    }

    [Fact]
    public void Run_SwitchesTheFileToWal()
    {
        var (bootstrapper, _) = Create();

        bootstrapper.Run();

        Assert.Equal("wal", Scalar(DatabaseFile, "PRAGMA journal_mode;"));
    }

    [Fact]
    public void Run_WithNothingPending_MakesNoBackup()
    {
        var (bootstrapper, state) = Create();
        bootstrapper.Run();

        Assert.True(bootstrapper.Run());

        Assert.Null(state.BackupPath);
        Assert.False(Directory.Exists(BackupFolder));
    }

    [Fact]
    public void Run_WithPendingMigrationsOnExistingFile_BacksUpFirst()
    {
        // A database with data but no migration history: InitialCreate is pending.
        Directory.CreateDirectory(_directory);
        Execute(DatabaseFile, "CREATE TABLE Keep (Value TEXT); INSERT INTO Keep VALUES ('original');");
        var (bootstrapper, state) = Create();

        Assert.True(bootstrapper.Run());

        Assert.NotNull(state.BackupPath);
        Assert.StartsWith(BackupFolder, state.BackupPath);
        Assert.EndsWith("-before-migration.db", state.BackupPath);
        Assert.Equal("original", Scalar(state.BackupPath, "SELECT Value FROM Keep;"));
        // The backup is the state before migrating.
        Assert.Equal(0L, Scalar(state.BackupPath,
            "SELECT COUNT(*) FROM sqlite_master WHERE name = '__EFMigrationsHistory';"));
    }

    [Fact]
    public void Run_OnCorruptFile_EntersRecoveryModeAndLeavesTheFileAlone()
    {
        Directory.CreateDirectory(_directory);
        var garbage = Encoding.ASCII.GetBytes(new string('x', 4096));
        File.WriteAllBytes(DatabaseFile, garbage);
        var (bootstrapper, state) = Create();

        Assert.False(bootstrapper.Run());

        Assert.False(state.IsReady);
        Assert.False(string.IsNullOrWhiteSpace(state.Error));
        Assert.Equal(garbage, File.ReadAllBytes(DatabaseFile));
    }

    [Fact]
    public void Run_AfterTheFileIsFixed_RecoversAndRaisesChanged()
    {
        Directory.CreateDirectory(_directory);
        var corruptFile = Path.Combine(_directory, "corrupt.db");
        File.WriteAllBytes(DatabaseFile, Encoding.ASCII.GetBytes(new string('x', 4096)));
        var (bootstrapper, state) = Create();
        Assert.False(bootstrapper.Run());

        // What the user does from "Open data folder": move the bad file aside.
        File.Move(DatabaseFile, corruptFile);
        var changes = 0;
        state.Changed += () => changes++;

        Assert.True(bootstrapper.Run());

        Assert.True(state.IsReady);
        Assert.Null(state.Error);
        Assert.Equal(1, changes);
    }

    // Each fixture is a database as an earlier release left it. Migrating every one of
    // them forward catches an upgrade path that only works from an empty database.
    public static TheoryData<string> Fixtures()
    {
        var data = new TheoryData<string>();
        var folder = Path.Combine(AppContext.BaseDirectory, "Data", "Fixtures");
        foreach (var file in Directory.GetFiles(folder, "*.db").OrderBy(f => f))
        {
            data.Add(Path.GetFileName(file));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task Run_MigratesFixtureForwardAndKeepsItsData(string fixture)
    {
        Directory.CreateDirectory(_directory);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Data", "Fixtures", fixture), DatabaseFile);
        var (bootstrapper, state) = Create();

        Assert.True(bootstrapper.Run(), state.Error);

        await using var context = CreateContext();
        Assert.Empty(await context.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken));
        var user = await context.Users.SingleAsync(
            u => u.UserName == "fixture",
            TestContext.Current.CancellationToken);
        Assert.Equal("Fixture User", user.DisplayName);
        Assert.Equal("Europe/Copenhagen", user.TimeZone);
    }

    private (DatabaseBootstrapper Bootstrapper, DatabaseState State) Create()
    {
        var state = new DatabaseState(DatabaseFile);
        var bootstrapper = new DatabaseBootstrapper(
            new TestContextFactory(DatabaseFile),
            state,
            NullLogger<DatabaseBootstrapper>.Instance);
        return (bootstrapper, state);
    }

    private ControllerDbContext CreateContext() => new TestContextFactory(DatabaseFile).CreateDbContext();

    private static void Execute(string path, string sql)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static object? Scalar(string path, string sql)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
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

    private sealed class TestContextFactory(string path) : IDbContextFactory<ControllerDbContext>
    {
        public ControllerDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<ControllerDbContext>();
            ControllerDbContext.Configure(options, path);
            return new ControllerDbContext(options.Options);
        }
    }
}
