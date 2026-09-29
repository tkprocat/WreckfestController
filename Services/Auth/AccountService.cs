using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WreckfestController.Data;
using WreckfestController.Services.Hosting;

namespace WreckfestController.Services.Auth;

/// <summary>
/// Web UI accounts, as the desktop app sees them. The first admin can only be created
/// here: nothing reachable over HTTP creates a user without a signed-in caller, so
/// sitting at the game machine is the proof of ownership.
/// </summary>
public sealed class AccountService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly DatabaseState _database;
    private readonly IConfiguration _configuration;

    public AccountService(
        IServiceScopeFactory scopes,
        DatabaseState database,
        IConfiguration configuration)
    {
        _scopes = scopes;
        _database = database;
        _configuration = configuration;
    }

    /// <summary>
    /// Registers what <see cref="AccountService"/> needs in the desktop app's host: a
    /// UserManager with the same rules as the API's.
    /// </summary>
    public static void AddAccounts(IServiceCollection services)
    {
        services.AddScoped(sp =>
            sp.GetRequiredService<IDbContextFactory<ControllerDbContext>>().CreateDbContext());
        services.AddIdentityCore<AppUser>(ApiAuthentication.ConfigureIdentity)
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<ControllerDbContext>();
        services.AddSingleton<AccountService>();
    }

    public bool IsDatabaseReady => _database.IsReady;

    /// <summary>
    /// True when the API is on but nobody can sign in to it yet. Desktop-only users,
    /// with the API off, are never asked to create an account.
    /// </summary>
    public async Task<bool> NeedsFirstAdminAsync(CancellationToken cancellationToken = default) =>
        ApiServer.IsEnabled(_configuration)
        && _database.IsReady
        && await CountUsersAsync(cancellationToken) == 0;

    public async Task<int> CountUsersAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopes.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        return await users.Users.CountAsync(cancellationToken);
    }

    /// <summary>
    /// Creates a web UI account. Every account is an admin in v1. Validation (password
    /// length, unique username and email) is Identity's, and its messages are returned.
    /// </summary>
    public async Task<IdentityResult> CreateAccountAsync(string userName, string email, string password)
    {
        using var scope = _scopes.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = new AppUser { UserName = userName.Trim(), Email = email.Trim() };
        return await users.CreateAsync(user, password);
    }

#if DEBUG
    /// <summary>
    /// Debug builds only: creates the admin named by <c>DevSeed:Email</c> and
    /// <c>DevSeed:Password</c> (user name <c>DevSeed:UserName</c>, else the email) when
    /// the database has no accounts, so a fresh development database can be signed in
    /// to without the first-admin dialog. The values come from configuration (an
    /// environment variable such as <c>DevSeed__Password</c>, the command line, or
    /// user-settings.json), never from the repository. Release builds do not contain it.
    /// </summary>
    /// <returns>The seeded user name, or null when nothing was seeded.</returns>
    public async Task<string?> SeedDevAdminAsync(CancellationToken cancellationToken = default)
    {
        var email = _configuration["DevSeed:Email"];
        var password = _configuration["DevSeed:Password"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password) || !_database.IsReady)
        {
            return null;
        }

        var userName = _configuration["DevSeed:UserName"];
        userName = (string.IsNullOrWhiteSpace(userName) ? email : userName).Trim();

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControllerDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();

        // SQLite's BEGIN IMMEDIATE (Microsoft.Data.Sqlite's default) takes the write lock
        // before the count, so no other writer - another instance's first-admin dialog -
        // can add an account between "there are none" and the insert.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await users.Users.AnyAsync(cancellationToken))
        {
            return null;
        }

        var result = await users.CreateAsync(new AppUser { UserName = userName, Email = email.Trim() }, password);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                "DevSeed account rejected: " + string.Join("; ", result.Errors.Select(e => e.Description)));
        }

        await transaction.CommitAsync(cancellationToken);
        return userName;
    }
#endif
}
