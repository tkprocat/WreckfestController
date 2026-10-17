using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace WreckfestPortal.Accounts;

/// <summary>The portal's own database: the web accounts and which servers each may manage.</summary>
public class PortalDbContext(DbContextOptions<PortalDbContext> options) : IdentityDbContext<PortalUser>(options)
{
    public DbSet<ServerGrant> ServerGrants => Set<ServerGrant>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<PortalUser>(user =>
        {
            user.Property(u => u.DisplayName).HasMaxLength(PortalUser.DisplayNameMaxLength);
            user.Property(u => u.TimeZone).HasMaxLength(PortalUser.TimeZoneMaxLength);
            user.HasMany(u => u.Servers).WithOne().HasForeignKey(g => g.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ServerGrant>(grant =>
        {
            grant.HasKey(g => new { g.UserId, g.ServerId });
            // Server ids compare without case, like the portal's settings.
            grant.Property(g => g.ServerId).HasMaxLength(ServerGrant.ServerIdMaxLength).UseCollation("NOCASE");
        });
    }
}

/// <summary>For <c>dotnet ef migrations</c>: a context on a throwaway path, never opened.</summary>
internal sealed class DesignTimePortalDbContextFactory : IDesignTimeDbContextFactory<PortalDbContext>
{
    public PortalDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<PortalDbContext>().UseSqlite("Data Source=design-time.db").Options);
}
