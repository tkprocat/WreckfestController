using Microsoft.Data.Sqlite;
using WreckfestController.Data;

namespace WreckfestController.Tests.Data;

/// <summary>
/// Real database files in a per-test temp folder: the point of VACUUM INTO is what it
/// does with a WAL file on disk while another connection holds the database open.
/// </summary>
public sealed class DatabaseBackupTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "wfc-backup-tests", Guid.NewGuid().ToString("N"));

    private string DatabaseFile => Path.Combine(_directory, "controller.db");

    public DatabaseBackupTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public void Create_WhileTheDatabaseIsOpen_CopiesRowsStillInTheWal()
    {
        using var open = Open(DatabaseFile);
        Execute(open, "PRAGMA journal_mode=WAL;");
        // No checkpoint: the row lives only in the -wal file, which a plain file copy misses.
        Execute(open, "PRAGMA wal_autocheckpoint=0;");
        Execute(open, "CREATE TABLE Keep (Value TEXT); INSERT INTO Keep VALUES ('in the wal');");

        var backupPath = DatabaseBackup.Create(DatabaseFile, "manual", new DateTimeOffset(2026, 10, 8, 12, 30, 45, 123, TimeSpan.Zero));

        Assert.Equal(
            Path.Combine(DatabaseBackup.FolderFor(DatabaseFile), "controller-20261008-123045-123-manual.db"),
            backupPath);
        using var copy = Open(backupPath);
        using var command = copy.CreateCommand();
        command.CommandText = "SELECT Value FROM Keep;";
        Assert.Equal("in the wal", command.ExecuteScalar());
    }

    [Fact]
    public void FolderFor_IsTheBackupsFolderBesideTheDatabase()
    {
        Assert.Equal(Path.Combine(_directory, DatabaseBackup.FolderName), DatabaseBackup.FolderFor(DatabaseFile));
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqlitePools.ReleaseFolder(_directory);
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
