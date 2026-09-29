using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WreckfestController.Data;
using WreckfestController.Services.Auth;

namespace WreckfestController.Tests.Services.Auth;

/// <summary>
/// The desktop side of accounts: when the first-admin dialog is offered, and that it
/// applies the same rules as the API.
/// </summary>
public sealed class AccountServiceTests : IDisposable
{
    private const string Password = "correct horse battery";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "wfc-account-tests", Guid.NewGuid().ToString("N"));

    private readonly List<ServiceProvider> _providers = [];

    [Fact]
    public async Task FirstAdmin_IsOffered_WhenTheApiIsOnAndThereAreNoAccounts()
    {
        var accounts = await CreateAsync(apiEnabled: true);

        Assert.True(await accounts.NeedsFirstAdminAsync(TestContext.Current.CancellationToken));
    }

    // Desktop-only users never use the web UI and are never asked.
    [Fact]
    public async Task FirstAdmin_IsNotOffered_WhenTheApiIsOff()
    {
        var accounts = await CreateAsync(apiEnabled: false);

        Assert.False(await accounts.NeedsFirstAdminAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FirstAdmin_IsNotOffered_OnceAnAccountExists()
    {
        var accounts = await CreateAsync(apiEnabled: true);

        var result = await accounts.CreateAccountAsync("admin", "admin@example.com", Password);

        Assert.True(result.Succeeded);
        Assert.False(await accounts.NeedsFirstAdminAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FirstAdmin_IsNotOffered_InRecoveryMode()
    {
        var accounts = await CreateAsync(apiEnabled: true, databaseReady: false);

        Assert.False(await accounts.NeedsFirstAdminAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateAccount_AppliesTheApisPasswordRule()
    {
        var accounts = await CreateAsync(apiEnabled: true);

        var result = await accounts.CreateAccountAsync("admin", "admin@example.com", "short");

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Code == "PasswordTooShort");
    }

    [Fact]
    public async Task CreateAccount_TrimsTheNames()
    {
        var accounts = await CreateAsync(apiEnabled: true);

        await accounts.CreateAccountAsync("  admin ", " admin@example.com ", Password);
        var duplicate = await accounts.CreateAccountAsync("admin", "other@example.com", Password);

        Assert.Contains(duplicate.Errors, e => e.Code == "DuplicateUserName");
    }

#if DEBUG
    [Fact]
    public async Task DevSeed_CreatesTheConfiguredAdmin_OnAnEmptyDatabase()
    {
        var accounts = await CreateAsync(apiEnabled: true, extra: DevSeed("dev@example.com", Password));

        var seeded = await accounts.SeedDevAdminAsync(TestContext.Current.CancellationToken);

        Assert.Equal("dev@example.com", seeded);
        Assert.Equal(1, await accounts.CountUsersAsync(TestContext.Current.CancellationToken));
        Assert.False(await accounts.NeedsFirstAdminAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DevSeed_UsesTheConfiguredUserName()
    {
        var extra = DevSeed("dev@example.com", Password);
        extra["DevSeed:UserName"] = "dev";
        var accounts = await CreateAsync(apiEnabled: true, extra: extra);

        Assert.Equal("dev", await accounts.SeedDevAdminAsync(TestContext.Current.CancellationToken));
    }

    // An existing database keeps its accounts; the seed never adds a second admin.
    [Fact]
    public async Task DevSeed_DoesNothing_OnceAnAccountExists()
    {
        var accounts = await CreateAsync(apiEnabled: true, extra: DevSeed("dev@example.com", Password));
        await accounts.CreateAccountAsync("admin", "admin@example.com", Password);

        Assert.Null(await accounts.SeedDevAdminAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await accounts.CountUsersAsync(TestContext.Current.CancellationToken));
    }

    // Another writer - say a second instance's first-admin dialog - is adding an account
    // right now. The seed must wait for it and then see it, not count zero first and
    // add a second admin after.
    [Fact]
    public async Task DevSeed_WaitsForAnotherWriter_AndThenDoesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var accounts = await CreateAsync(apiEnabled: true, extra: DevSeed("dev@example.com", Password));
        using var scope = _providers[^1].CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControllerDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        Assert.True((await users.CreateAsync(new AppUser { UserName = "admin", Email = "admin@example.com" }, Password)).Succeeded);

        // Its own thread: SQLite's async calls run synchronously, so a waiting seed would
        // otherwise block this one before it could commit.
        var seed = Task.Run(() => accounts.SeedDevAdminAsync(ct), ct);
        await Task.Delay(500, ct);
        Assert.False(seed.IsCompleted);
        await transaction.CommitAsync(ct);

        Assert.Null(await seed);
        Assert.Equal(1, await accounts.CountUsersAsync(ct));
    }

    [Fact]
    public async Task DevSeed_DoesNothing_WhenNotConfigured()
    {
        var accounts = await CreateAsync(apiEnabled: true);

        Assert.Null(await accounts.SeedDevAdminAsync(TestContext.Current.CancellationToken));
        Assert.True(await accounts.NeedsFirstAdminAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DevSeed_DoesNothing_InRecoveryMode()
    {
        var accounts = await CreateAsync(
            apiEnabled: true, databaseReady: false, extra: DevSeed("dev@example.com", Password));

        Assert.Null(await accounts.SeedDevAdminAsync(TestContext.Current.CancellationToken));
    }

    // A bad seed fails loudly instead of leaving a database no one can sign in to.
    [Fact]
    public async Task DevSeed_Throws_WhenIdentityRejectsIt()
    {
        var accounts = await CreateAsync(apiEnabled: true, extra: DevSeed("dev@example.com", "short"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => accounts.SeedDevAdminAsync(TestContext.Current.CancellationToken));
        Assert.Contains("DevSeed", error.Message);
    }

    private static Dictionary<string, string?> DevSeed(string email, string password) => new()
    {
        ["DevSeed:Email"] = email,
        ["DevSeed:Password"] = password,
    };
#endif

    /// <summary>The desktop host's real registration, on a migrated temp database.</summary>
    private async Task<AccountService> CreateAsync(
        bool apiEnabled, bool databaseReady = true, Dictionary<string, string?>? extra = null)
    {
        Directory.CreateDirectory(_directory);
        var databasePath = Path.Combine(_directory, $"{Guid.NewGuid():N}.db");
        var state = new DatabaseState(databasePath);
        if (databaseReady)
        {
            state.MarkReady(backupPath: null);
        }

        var services = new ServiceCollection();
        services.AddLogging();
        var settings = new Dictionary<string, string?>(extra ?? []) { ["Api:Enabled"] = apiEnabled.ToString() };
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build());
        services.AddSingleton(state);
        services.AddDbContextFactory<ControllerDbContext>(
            options => ControllerDbContext.Configure(options, databasePath));
        AccountService.AddAccounts(services);
        var provider = services.BuildServiceProvider();
        _providers.Add(provider);

        if (databaseReady)
        {
            var factory = provider.GetRequiredService<IDbContextFactory<ControllerDbContext>>();
            await using var db = await factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
            await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        }

        return provider.GetRequiredService<AccountService>();
    }

    public void Dispose()
    {
        foreach (var provider in _providers)
        {
            provider.Dispose();
        }

        SqlitePools.ReleaseFolder(_directory);
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
