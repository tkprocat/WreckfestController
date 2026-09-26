using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WreckfestController.Data;

namespace WreckfestController.Services;

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
}
