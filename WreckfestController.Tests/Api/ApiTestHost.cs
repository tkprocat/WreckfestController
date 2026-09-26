using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using WreckfestController.Data;
using WreckfestController.Services;

namespace WreckfestController.Tests.Api;

/// <summary>
/// The real API host - the services and pipeline ApiServer builds - running on a
/// TestServer instead of a bound port. The singletons the API borrows from the WPF
/// app are real instances, except that the hook's pipe I/O is mocked so nothing
/// touches a game process. The database is a real, migrated SQLite file in a temp
/// folder, which also holds the Data Protection keys.
/// </summary>
public sealed class ApiTestHost : IAsyncDisposable
{
    public const string ApiKey = "test-api-key";
    public const string Password = "correct horse battery";

    private readonly ServiceProvider _main;
    private readonly WebApplication _app;
    private readonly string? _ownedDirectory;

    private ApiTestHost(ServiceProvider main, WebApplication app, string? ownedDirectory)
    {
        _main = main;
        _app = app;
        _ownedDirectory = ownedDirectory;
    }

    /// <summary>The API host's own services (Identity, authentication).</summary>
    public IServiceProvider Services => _app.Services;

    /// <summary>The WPF app's service provider, which the API copies singletons from.</summary>
    public IServiceProvider MainServices => _main;

    /// <param name="databaseState">
    /// Defaults to a ready database. Pass one that is not ready to run in recovery mode.
    /// </param>
    /// <param name="dataDirectory">
    /// Folder for the database and keys. Pass the same folder to a second host to
    /// simulate a restart; the caller then owns deleting it. Defaults to a fresh temp
    /// folder that is deleted on dispose.
    /// </param>
    /// <param name="configureDatabase">
    /// Extra options for every context the hosts create, such as interceptors that
    /// make a race happen on cue.
    /// </param>
    public static async Task<ApiTestHost> StartAsync(
        IDictionary<string, string?>? settings = null,
        DatabaseState? databaseState = null,
        string? dataDirectory = null,
        Action<DbContextOptionsBuilder>? configureDatabase = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["Api:Enabled"] = "true",
            ["Api:Key"] = ApiKey,
            ["Webhooks:Enabled"] = "false",
        };
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            values[key] = value;
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        var ownedDirectory = dataDirectory is null ? NewDataDirectory() : null;
        dataDirectory ??= ownedDirectory!;
        Directory.CreateDirectory(dataDirectory);

        if (databaseState is null)
        {
            databaseState = new DatabaseState(Path.Combine(dataDirectory, "controller.db"));
            databaseState.MarkReady(backupPath: null);
        }

        var main = BuildMainServices(configuration, databaseState, configureDatabase);
        if (databaseState.IsReady)
        {
            var factory = main.GetRequiredService<IDbContextFactory<ControllerDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            await db.Database.MigrateAsync();
        }

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        ApiServer.ConfigureServices(builder, main, configuration);

        var app = builder.Build();
        ApiServer.ConfigurePipeline(app);
        await app.StartAsync();

        return new ApiTestHost(main, app, ownedDirectory);
    }

    public static string NewDataDirectory() =>
        Path.Combine(Path.GetTempPath(), "wfc-api-tests", Guid.NewGuid().ToString("N"));

    /// <summary>Deletes a folder passed as <c>dataDirectory</c>, once every host using it is disposed.</summary>
    public static void DeleteDataDirectory(string directory)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    public async Task<AppUser> CreateUserAsync(string userName = "admin", string password = Password)
    {
        using var scope = _app.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = new AppUser { UserName = userName, Email = $"{userName}@example.com" };
        var result = await users.CreateAsync(user, password);
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Description)));
        return user;
    }

    /// <summary>
    /// Signs <paramref name="user"/> in through the real SignInManager and cookie handler
    /// and returns the Set-Cookie header they produced. Stands in for the login endpoint
    /// until one exists.
    /// </summary>
    public async Task<string> SignInAsync(AppUser user, bool persistent = true)
    {
        using var scope = _app.Services.CreateScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("localhost");
        var signIn = scope.ServiceProvider.GetRequiredService<SignInManager<AppUser>>();
        signIn.Context = context;

        var fresh = await signIn.UserManager.FindByIdAsync(user.Id);
        await signIn.SignInAsync(fresh!, persistent);

        return context.Response.Headers.SetCookie.Single()!;
    }

    /// <summary>A client that sends the cookie from <paramref name="setCookie"/>, and no API key.</summary>
    public HttpClient CreateCookieClient(string setCookie)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", setCookie.Split(';')[0]);
        return client;
    }

    /// <summary>A client with no API key header.</summary>
    public HttpClient CreateClient() => _app.GetTestClient();

    /// <summary>A cookie-keeping client that sends the antiforgery header like the SPA.</summary>
    public BrowserClient CreateBrowser()
    {
        var server = _app.GetTestServer();
        return new BrowserClient(server.CreateHandler(), server.BaseAddress);
    }

    /// <summary>A client that sends the configured API key.</summary>
    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        await _main.DisposeAsync();
        if (_ownedDirectory is not null)
        {
            DeleteDataDirectory(_ownedDirectory);
        }
    }

    private static ServiceProvider BuildMainServices(
        IConfiguration configuration,
        DatabaseState databaseState,
        Action<DbContextOptionsBuilder>? configureDatabase)
    {
        var services = new ServiceCollection();
        services.AddSingleton(databaseState);
        services.AddDbContextFactory<ControllerDbContext>(options =>
        {
            ControllerDbContext.Configure(options, databaseState.DatabasePath);
            configureDatabase?.Invoke(options);
        });
        services.AddLogging();
        services.AddSingleton(configuration);
        services.AddSingleton(new HttpClient());
        services.AddSingleton<WreckfestWebWebhookService>();
        services.AddSingleton<ConsoleLogWebhookSender>();
        services.AddSingleton<PlayerTracker>();
        services.AddSingleton<TrackChangeTracker>();
        services.AddSingleton<ServerInfoTracker>();
        services.AddSingleton(sp => new ServerManager(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<ILogger<ServerManager>>(),
            sp.GetRequiredService<PlayerTracker>(),
            sp.GetRequiredService<TrackChangeTracker>(),
            sp.GetRequiredService<ServerInfoTracker>(),
            sp.GetRequiredService<WreckfestWebWebhookService>(),
            sp.GetRequiredService<ConsoleLogWebhookSender>(),
            Mock.Of<IServerInputWriter>(),
            Mock.Of<IInjectedHookOutputReader>()));
        services.AddSingleton<ConfigService>();
        services.AddSingleton<EventStorageService>();
        services.AddSingleton<RecurringEventService>();
        services.AddSingleton<SmartRestartService>();
        return services.BuildServiceProvider();
    }
}
