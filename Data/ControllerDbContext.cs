using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WreckfestController.Data.Catalogue;
using WreckfestController.Data.Collections;

namespace WreckfestController.Data;

/// <summary>
/// The controller's own SQLite database: web UI users, the track catalogue and track
/// collections today, and events and settings as later phases move them in.
/// </summary>
public class ControllerDbContext : IdentityDbContext<AppUser>
{
    public ControllerDbContext(DbContextOptions<ControllerDbContext> options)
        : base(options)
    {
    }

    public DbSet<Track> Tracks => Set<Track>();

    public DbSet<TrackVariant> TrackVariants => Set<TrackVariant>();

    public DbSet<Tag> Tags => Set<Tag>();

    public DbSet<WeatherCondition> WeatherConditions => Set<WeatherCondition>();

    public DbSet<Mod> Mods => Set<Mod>();

    public DbSet<TrackCollection> TrackCollections => Set<TrackCollection>();

    public DbSet<TrackCollectionEntry> TrackCollectionEntries => Set<TrackCollectionEntry>();

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

        ConfigureCatalogue(builder);
        ConfigureCollections(builder);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        BumpVersions();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        BumpVersions();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// Moves every changed <see cref="IVersioned"/> row to the version after the one it
    /// was read at. The UPDATE still checks the original version, so a caller that sets
    /// the original to the client's If-Match version gets a concurrency failure when
    /// someone saved in between.
    /// </summary>
    private void BumpVersions()
    {
        ChangeTracker.DetectChanges();
        foreach (var entry in ChangeTracker.Entries<IVersioned>())
        {
            if (entry.State == EntityState.Modified)
            {
                var version = entry.Property(e => e.Version);
                version.CurrentValue = version.OriginalValue + 1;
            }
        }
    }

    private static void ConfigureCatalogue(ModelBuilder builder)
    {
        // Ids are compared the way the game and VotingService compare them: ignoring case.
        const string NoCase = "NOCASE";

        builder.Entity<Track>(track =>
        {
            track.Property(t => t.Key).HasMaxLength(Track.KeyMaxLength).UseCollation(NoCase);
            track.HasIndex(t => t.Key).IsUnique();
            track.Property(t => t.Name).HasMaxLength(Track.NameMaxLength);
            track.Property(t => t.Origin).HasConversion<string>().HasMaxLength(16);
            track.Property(t => t.DlcName).HasMaxLength(Track.NameMaxLength);
            track.Property(t => t.Version).IsConcurrencyToken();

            // A mod cannot go while tracks still come from it.
            track.HasOne(t => t.Mod)
                .WithMany(m => m.Tracks)
                .HasForeignKey(t => t.ModId)
                .OnDelete(DeleteBehavior.Restrict);

            track.HasMany(t => t.WeatherConditions)
                .WithMany()
                .UsingEntity(
                    "TrackWeatherConditions",
                    r => r.HasOne(typeof(WeatherCondition)).WithMany().HasForeignKey("WeatherConditionId"),
                    l => l.HasOne(typeof(Track)).WithMany().HasForeignKey("TrackId"),
                    j => j.HasKey("TrackId", "WeatherConditionId"));
        });

        builder.Entity<TrackVariant>(variant =>
        {
            variant.Property(v => v.VariantId).HasMaxLength(TrackVariant.VariantIdMaxLength).UseCollation(NoCase);
            variant.HasIndex(v => v.VariantId).IsUnique();
            variant.Property(v => v.Name).HasMaxLength(TrackVariant.NameMaxLength);
            variant.Property(v => v.GameMode).HasConversion<string>().HasMaxLength(16);
            variant.Property(v => v.Version).IsConcurrencyToken();

            variant.HasOne(v => v.Track)
                .WithMany(t => t.Variants)
                .HasForeignKey(v => v.TrackId)
                .OnDelete(DeleteBehavior.Cascade);

            variant.HasMany(v => v.Tags)
                .WithMany()
                .UsingEntity(
                    "TrackVariantTags",
                    r => r.HasOne(typeof(Tag)).WithMany().HasForeignKey("TagId"),
                    l => l.HasOne(typeof(TrackVariant)).WithMany().HasForeignKey("TrackVariantId"),
                    j => j.HasKey("TrackVariantId", "TagId"));
        });

        builder.Entity<Tag>(tag =>
        {
            tag.Property(t => t.Name).HasMaxLength(Tag.NameMaxLength);
            tag.Property(t => t.Slug).HasMaxLength(Tag.SlugMaxLength).UseCollation(NoCase);
            tag.HasIndex(t => t.Slug).IsUnique();
            tag.Property(t => t.Color).HasMaxLength(7);
        });

        builder.Entity<WeatherCondition>(weather =>
        {
            weather.Property(w => w.Name).HasMaxLength(WeatherCondition.NameMaxLength).UseCollation(NoCase);
            weather.HasIndex(w => w.Name).IsUnique();
        });

        builder.Entity<Mod>(mod =>
        {
            mod.Property(m => m.Name).HasMaxLength(Mod.NameMaxLength);
            mod.Property(m => m.FolderName).HasMaxLength(Mod.FolderNameMaxLength).UseCollation(NoCase);
            mod.HasIndex(m => m.FolderName).IsUnique();
            mod.Property(m => m.WorkshopId).HasMaxLength(Mod.WorkshopIdMaxLength);
        });
    }

    private static void ConfigureCollections(ModelBuilder builder)
    {
        builder.Entity<TrackCollection>(collection =>
        {
            collection.Property(c => c.Name).HasMaxLength(TrackCollection.NameMaxLength).UseCollation("NOCASE");
            collection.HasIndex(c => c.Name).IsUnique();
            collection.Property(c => c.Version).IsConcurrencyToken();

            collection.HasMany(c => c.Entries)
                .WithOne(e => e.Collection)
                .HasForeignKey(e => e.CollectionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<TrackCollectionEntry>(entry =>
        {
            entry.HasIndex(e => new { e.CollectionId, e.Position }).IsUnique();

            // Matched against TrackVariant.VariantId when linking, which ignores case.
            entry.Property(e => e.TrackId).HasMaxLength(TrackVariant.VariantIdMaxLength).UseCollation("NOCASE");
            entry.Property(e => e.Gamemode).HasMaxLength(CollectionLimits.TextMaxLength);
            entry.Property(e => e.CarClassRestriction).HasMaxLength(CollectionLimits.TextMaxLength);
            entry.Property(e => e.CarRestriction).HasMaxLength(CollectionLimits.TextMaxLength);
            entry.Property(e => e.Weather).HasMaxLength(CollectionLimits.TextMaxLength);
            entry.Ignore(e => e.EffectiveTrackId);

            // A variant a collection uses cannot be deleted; hide it instead.
            entry.HasOne(e => e.TrackVariant)
                .WithMany()
                .HasForeignKey(e => e.TrackVariantId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
