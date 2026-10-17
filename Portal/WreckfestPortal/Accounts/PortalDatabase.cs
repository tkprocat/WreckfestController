using Microsoft.EntityFrameworkCore;

namespace WreckfestPortal.Accounts;

/// <summary>
/// Where the portal's database is: <see cref="PortalOptions.DatabasePath"/>, or
/// <c>data\portal.db</c> next to the exe. The sign-in keys live in a <c>keys</c> folder beside it.
/// </summary>
public sealed class PortalDatabase(string path)
{
    public const string DefaultFolder = "data";
    public const string DefaultFile = "portal.db";

    /// <summary>The database file, full path.</summary>
    public string Path { get; } = System.IO.Path.GetFullPath(path);

    /// <summary>The keys that protect the sign-in cookies. Deleting them signs everyone out.</summary>
    public string KeysFolder => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path) ?? ".", "keys");

    /// <summary>
    /// A relative <see cref="PortalOptions.DatabasePath"/> is relative to the exe's folder, not
    /// the working folder: the service starts in System32, and <c>create-admin</c> wherever it
    /// is run from, and both must open the same database.
    /// </summary>
    public static PortalDatabase Resolve(PortalOptions options, string baseDirectory) =>
        new(string.IsNullOrWhiteSpace(options.DatabasePath)
            ? System.IO.Path.Combine(baseDirectory, DefaultFolder, DefaultFile)
            // GetFullPath with a base, not Combine: \data\portal.db would otherwise take the
            // working folder's drive.
            : System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(options.DatabasePath), baseDirectory));

    /// <summary>Creates the database, or brings it up to date, before anything uses it.</summary>
    public static async Task MigrateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var database = services.GetRequiredService<PortalDatabase>();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(database.Path)!);
        using var scope = services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<PortalDbContext>().Database.MigrateAsync(cancellationToken);
    }
}
