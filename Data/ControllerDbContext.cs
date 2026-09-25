using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace WreckfestController.Data;

/// <summary>
/// The controller's own SQLite database: web UI users today, and the track catalogue,
/// collections, events and settings as later phases move them in.
/// </summary>
public class ControllerDbContext : IdentityDbContext<AppUser>
{
    public ControllerDbContext(DbContextOptions<ControllerDbContext> options)
        : base(options)
    {
    }

    /// <summary>
    /// Points <paramref name="options"/> at the SQLite file at <paramref name="databasePath"/>.
    /// Shared by the app's registration and the design-time factory so both open the
    /// database the same way.
    /// </summary>
    /// <remarks>
    /// Must not touch the file system: DI runs this while resolving the context factory,
    /// before <see cref="DatabaseBootstrapper.Run"/> can turn a failure into recovery
    /// mode. SQLite does not create the folder, so <see cref="EnsureFolder"/> does, from
    /// inside the bootstrapper.
    /// </remarks>
    public static void Configure(DbContextOptionsBuilder options, string databasePath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
        }.ToString();

        options.UseSqlite(connectionString);
    }

    /// <summary>Creates the folder that will hold the database file. SQLite creates only the file.</summary>
    public static void EnsureFolder(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<AppUser>(user =>
        {
            user.Property(u => u.DisplayName).HasMaxLength(AppUser.DisplayNameMaxLength);
            user.Property(u => u.TimeZone).HasMaxLength(AppUser.TimeZoneMaxLength);
        });
    }
}
