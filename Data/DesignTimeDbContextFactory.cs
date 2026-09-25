using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace WreckfestController.Data;

/// <summary>
/// Lets <c>dotnet ef migrations add</c> build the context without starting the WPF
/// host. Adding a migration never opens the database. Pass
/// <c>-- --Database:Path=...</c> to point other ef commands somewhere other than the
/// default database.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ControllerDbContext>
{
    public ControllerDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder().AddCommandLine(args).Build();
        var databasePath = DatabasePath.Resolve(configuration, AppContext.BaseDirectory);

        // For `dotnet ef database update`; adding a migration never opens the file.
        ControllerDbContext.EnsureFolder(databasePath);

        var options = new DbContextOptionsBuilder<ControllerDbContext>();
        ControllerDbContext.Configure(options, databasePath);
        return new ControllerDbContext(options.Options);
    }
}
