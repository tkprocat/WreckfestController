using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WreckfestController.Data;
using WreckfestController.Services;

namespace WreckfestController.Tests.Services;

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

    /// <summary>The desktop host's real registration, on a migrated temp database.</summary>
    private async Task<AccountService> CreateAsync(bool apiEnabled, bool databaseReady = true)
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
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Api:Enabled"] = apiEnabled.ToString() })
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

        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
