using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
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
/// touches a game process.
/// </summary>
public sealed class ApiTestHost : IAsyncDisposable
{
    public const string ApiKey = "test-api-key";

    private readonly ServiceProvider _main;
    private readonly WebApplication _app;

    private ApiTestHost(ServiceProvider main, WebApplication app)
    {
        _main = main;
        _app = app;
    }

    /// <summary>The WPF app's service provider, which the API copies singletons from.</summary>
    public IServiceProvider MainServices => _main;

    /// <param name="databaseState">
    /// Defaults to a ready database. Pass one that is not ready to run in recovery mode.
    /// </param>
    public static async Task<ApiTestHost> StartAsync(
        IDictionary<string, string?>? settings = null,
        DatabaseState? databaseState = null)
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

        if (databaseState is null)
        {
            databaseState = new DatabaseState("test.db");
            databaseState.MarkReady(backupPath: null);
        }

        var main = BuildMainServices(configuration, databaseState);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        ApiServer.ConfigureServices(builder, main, configuration);

        var app = builder.Build();
        ApiServer.ConfigurePipeline(app);
        await app.StartAsync();

        return new ApiTestHost(main, app);
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

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        await _main.DisposeAsync();
    }

    private static ServiceProvider BuildMainServices(
        IConfiguration configuration,
        DatabaseState databaseState)
    {
        var services = new ServiceCollection();
        services.AddSingleton(databaseState);
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
