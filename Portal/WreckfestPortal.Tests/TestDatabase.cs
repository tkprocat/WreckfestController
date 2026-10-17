using Microsoft.Data.Sqlite;

namespace WreckfestPortal.Tests;

internal static class TestDatabase
{
    /// <summary>Deletes a test's database folder once SQLite's pooled connections let go of it.</summary>
    public static void Delete(string databasePath)
    {
        SqliteConnection.ClearAllPools();
        var folder = Path.GetDirectoryName(databasePath)!;
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
