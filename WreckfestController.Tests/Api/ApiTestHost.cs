using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using WreckfestController.Data;
using WreckfestController.Hubs;
using WreckfestController.Models;
using WreckfestController.Services.Cups;
using WreckfestController.Services.Config;
using WreckfestController.Services.Hook;
using WreckfestController.Services.Auth;
using WreckfestController.Services.Hosting;
using WreckfestController.Services.Publishing;
using WreckfestController.Services.ServerControl;
using WreckfestController.Services.Tracking;

namespace WreckfestController.Tests.Api;

/// <summary>
/// The real API host - the services and pipeline ApiServer builds - running on a
/// TestServer instead of a bound port. The singletons the API borrows from the WPF
/// app are real instances, except that the hook's pipe I/O is mocked so nothing
/// touches a game process. The database is a real, migrated SQLite file in a temp
/// folder. Callers sign in as the portal would, with <see cref="PortalToken"/>.
/// </summary>
public sealed class ApiTestHost : IAsyncDisposable
{
    public const string ApiKey = "test-api-key";

    /// <summary>The portal's settings for this server, as in its Api:Portal section.</summary>
    public const string PortalSecret = "test-portal-secret-of-at-least-32-chars";
    public const string PortalServerId = "tvtp2";

    /// <summary>
    /// Sets the request's TCP peer address, which a TestServer otherwise leaves unset.
    /// Applied before the app's own pipeline, so it stands in for the real connection.
    /// </summary>
    public const string PeerAddressHeader = "X-Test-Peer-Address";

    private readonly ServiceProvider _main;
    private readonly WebApplication _app;
    private readonly string? _ownedDirectory;

    private ApiTestHost(ServiceProvider main, WebApplication app, string? ownedDirectory)
    {
        _main = main;
        _app = app;
        _ownedDirectory = ownedDirectory;
    }

    /// <summary>The API host's own services (authentication, the hub).</summary>
    public IServiceProvider Services => _app.Services;

    /// <summary>The WPF app's service provider, which the API copies singletons from.</summary>
    public IServiceProvider MainServices => _main;

    /// <param name="databaseState">
    /// Defaults to a ready database. Pass one that is not ready to run in recovery mode.
    /// </param>
    /// <param name="dataDirectory">
    /// Folder for the database. Pass the same folder to a second host to
    /// simulate a restart; the caller then owns deleting it. Defaults to a fresh temp
    /// folder that is deleted on dispose.
    /// </param>
    /// <param name="configureDatabase">
    /// Extra options for every context the hosts create, such as interceptors that
    /// make a race happen on cue.
    /// </param>
    /// <param name="configureServices">
    /// Changes to the API host's services after ApiServer has registered its own, such as
    /// a fake handler for an outbound HTTP client.
    /// </param>
    public static async Task<ApiTestHost> StartAsync(
        IDictionary<string, string?>? settings = null,
        DatabaseState? databaseState = null,
        string? dataDirectory = null,
        Action<DbContextOptionsBuilder>? configureDatabase = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["Api:Enabled"] = "true",
            ["Api:Key"] = ApiKey,
            [ApiAuthentication.PortalSecretKey] = PortalSecret,
            [ApiAuthentication.PortalServerIdKey] = PortalServerId,
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
        builder.Services.AddSingleton<IStartupFilter, PeerAddressStartupFilter>();
        ApiServer.ConfigureServices(builder, main, configuration);
        configureServices?.Invoke(builder.Services);

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
        SqlitePools.ReleaseFolder(directory);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// A token as the portal mints one (WreckfestPortal's ControllerTokens): signed with this
    /// host's portal secret and addressed to its server id, unless a test says otherwise.
    /// </summary>
    public static string PortalToken(
        string userName = "admin",
        string? displayName = null,
        string audience = PortalServerId,
        string secret = PortalSecret,
        string issuer = ApiAuthentication.PortalIssuer,
        TimeSpan? expiresIn = null)
    {
        var now = DateTime.UtcNow;
        var expires = now + (expiresIn ?? TimeSpan.FromMinutes(5));
        var claims = new List<System.Security.Claims.Claim>
        {
            new("sub", "user-" + userName),
            new("name", userName),
        };
        if (displayName is not null)
        {
            claims.Add(new System.Security.Claims.Claim(ApiAuthentication.DisplayNameClaim, displayName));
        }

        return new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler().CreateToken(new Microsoft.IdentityModel.Tokens.SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Subject = new System.Security.Claims.ClaimsIdentity(claims),
            IssuedAt = expires < now ? expires.AddMinutes(-5) : now,
            NotBefore = expires < now ? expires.AddMinutes(-5) : now,
            Expires = expires,
            SigningCredentials = new Microsoft.IdentityModel.Tokens.SigningCredentials(
                new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(secret)),
                Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256),
        });
    }

    /// <summary>A client the portal vouches for, as it does for someone who may manage this server.</summary>
    public HttpClient CreatePortalClient(string userName = "admin", string? displayName = null)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", PortalToken(userName, displayName));
        return client;
    }

    /// <summary>A client with no API key header.</summary>
    public HttpClient CreateClient() => _app.GetTestClient();

    /// <summary>A client that sends the configured API key.</summary>
    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
        return client;
    }

    /// <summary>
    /// A connection to <see cref="ServerHub"/> over the TestServer, not yet started.
    /// Long polling, because a TestServer handler speaks plain HTTP. Add a header in
    /// <paramref name="configure"/> to sign in.
    /// </summary>
    public HubConnection CreateHubConnection(Action<HttpConnectionOptions>? configure = null)
    {
        var server = _app.GetTestServer();
        return new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, ServerHub.Route.TrimStart('/')), options =>
            {
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                configure?.Invoke(options);
            })
            .Build();
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
        services.AddSingleton(new ShippedSettings(new ConfigurationBuilder().Build()));
        services.AddSingleton<ISettingsStore, SettingsStore>();
        services.AddSingleton<HubServerEventPublisher>();
        services.AddSingleton<IServerEventPublisher>(sp => sp.GetRequiredService<HubServerEventPublisher>());
        services.AddSingleton<PlayerTracker>();
        services.AddSingleton<TrackChangeTracker>();
        services.AddSingleton<ServerInfoTracker>();
        // A test that points the server at a folder does it with the keys 1.x used.
        IOptionsMonitor<WreckfestServerSettings> server = TestSettings.Server(
            workingDirectory: configuration["WreckfestServer:WorkingDirectory"] ?? string.Empty,
            serverArguments: configuration["WreckfestServer:ServerArguments"] ?? SettingsSections.DefaultServerArguments);
        services.AddSingleton(server);
        services.AddSingleton(sp => new ServerManager(
            sp.GetRequiredService<IConfiguration>(), server, TestSettings.SteamCmd(),
            sp.GetRequiredService<ILogger<ServerManager>>(),
            sp.GetRequiredService<PlayerTracker>(),
            sp.GetRequiredService<TrackChangeTracker>(),
            sp.GetRequiredService<ServerInfoTracker>(),
            sp.GetRequiredService<IServerEventPublisher>(),
            Mock.Of<IServerInputWriter>(),
            Mock.Of<IInjectedHookOutputReader>()));
        services.AddSingleton<ConfigService>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<SmartRestartService>();
        services.AddSingleton<CupStore>();
        services.AddSingleton<CupActivator>();
        return services.BuildServiceProvider();
    }

    private sealed class PeerAddressStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                if (context.Request.Headers.TryGetValue(PeerAddressHeader, out var peer))
                {
                    context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(peer!);
                }

                return nextMiddleware(context);
            });
            next(app);
        };
    }
}
