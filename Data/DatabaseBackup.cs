using Microsoft.Data.Sqlite;

namespace WreckfestController.Data;

/// <summary>
/// Consistent copies of the database, made with SQLite's <c>VACUUM INTO</c>.
/// </summary>
/// <remarks>
/// Copying the .db file while the app has it open can miss whatever is still in the
/// -wal file; VACUUM INTO reads through SQLite, so the copy is complete.
/// </remarks>
public static class DatabaseBackup
{
    public const string FolderName = "backups";

    /// <summary>
    /// Writes a copy to <c>backups\&lt;name&gt;-&lt;timestamp&gt;-&lt;reason&gt;.db</c>
    /// beside the database and returns its path.
    /// </summary>
    public static string Create(string databasePath, string reason, DateTimeOffset now)
    {
        var directory = Path.Combine(
            Path.GetDirectoryName(databasePath) ?? string.Empty,
            FolderName);
        Directory.CreateDirectory(directory);

        var backupPath = Path.Combine(
            directory,
            $"{Path.GetFileNameWithoutExtension(databasePath)}-{now:yyyyMMdd-HHmmss-fff}-{reason}.db");

        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "VACUUM INTO $path;";
        command.Parameters.AddWithValue("$path", backupPath);
        command.ExecuteNonQuery();

        return backupPath;
    }
}
