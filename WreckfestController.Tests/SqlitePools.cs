using Microsoft.Data.Sqlite;
using WreckfestController.Data;

namespace WreckfestController.Tests;

/// <summary>
/// Releases a test's own pooled SQLite connections before it deletes its temp folder.
/// </summary>
/// <remarks>
/// Never <c>SqliteConnection.ClearAllPools()</c>: tests run in parallel, and clearing every
/// pool in the process can dispose a connection another test is just taking from its pool
/// ("Cannot access a disposed object. Object name: 'SQLitePCL.sqlite3'", issue #128).
/// </remarks>
public static class SqlitePools
{
    /// <summary>
    /// Clears the pools of the database files in <paramref name="folder"/>, as the app opens
    /// them (<see cref="ControllerDbContext.ConnectionString"/>). A file with no pool is a no-op.
    /// </summary>
    public static void ReleaseFolder(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            using var connection = new SqliteConnection(ControllerDbContext.ConnectionString(file));
            SqliteConnection.ClearPool(connection);
        }
    }
}
