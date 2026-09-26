using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;
using Microsoft.Extensions.Hosting;
using WreckfestController.Data;

namespace WreckfestController.Services;

/// <summary>
/// Interface for the embedded API server.
/// </summary>
public interface IApiServer
{
    Task StartAsync();
    Task StopAsync();
    bool IsRunning { get; }
    string BaseUrl { get; }
}

/// <summary>
/// Embedded ASP.NET Core API server that runs within the MAUI application.
/// Provides the REST API and WebSocket endpoints.
/// </summary>
public class ApiServer : IApiServer, IDisposable
{
    public const int DefaultHttpPort = 5100;
    public const int DefaultHttpsPort = 5101;
    private const string LoopbackHost = "127.0.0.1";
    private const string RemoteHost = "0.0.0.0";
    private readonly ILogger<ApiServer> _logger;
    private readonly IServiceProvider _serviceProvider;
    private WebApplication? _app;
    private bool _isRunning;

    public ApiServer(ILogger<ApiServer> logger, IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    public bool IsRunning => _isRunning;
    public string BaseUrl { get; private set; } = "http://localhost:5100";

    /// <summary>
    /// The HTTP API is opt-in. When disabled no port is bound at all, which also
    /// keeps several controller instances on one host from contending for ports.
    /// The key is optional: browsers sign in with a cookie, and only scripts need it.
    /// </summary>
    public static bool IsEnabled(IConfiguration configuration) =>
        bool.TryParse(configuration["Api:Enabled"], out var enabled) && enabled;

    /// <summary>
    /// Builds the listen URLs. Ports are configurable so several controller
    /// instances can manage separate servers on one Windows host.
    /// </summary>
    public static string GetListenUrls(
        bool allowRemote,
        int httpPort = DefaultHttpPort,
        int httpsPort = DefaultHttpsPort)
    {
        var host = allowRemote ? RemoteHost : LoopbackHost;
        return $"http://{host}:{httpPort};https://{host}:{httpsPort}";
    }

    /// <summary>
    /// Reads a port from configuration, falling back to the default when the value
    /// is absent or outside the valid TCP range.
    /// </summary>
    private int ResolvePort(IConfiguration configuration, string key, int fallback)
    {
        var configured = configuration.GetValue<int?>(key);
        if (configured is null)
        {
            return fallback;
        }

        if (configured is <= 0 or > 65535)
        {
            _logger.LogWarning(
                "{Key} is {Value}, which is not a valid TCP port. Falling back to {Fallback}.",
                key,
                configured,
                fallback);
            return fallback;
        }

        return configured.Value;
    }

    public async Task StartAsync()
    {
        if (_isRunning)
        {
            _logger.LogWarning("API server is already running");
            return;
        }

        try
        {
            _logger.LogInformation("Starting embedded API server...");

            var builder = WebApplication.CreateBuilder();

            var configuration = _serviceProvider.GetRequiredService<IConfiguration>();

            if (!IsEnabled(configuration))
            {
                _logger.LogInformation("HTTP API is disabled (Api:Enabled is false). No port will be bound.");
                return;
            }

            var allowRemote = configuration.GetValue<bool>("Api:AllowRemote");
            var httpPort = ResolvePort(configuration, "Api:HttpPort", DefaultHttpPort);
            var httpsPort = ResolvePort(configuration, "Api:HttpsPort", DefaultHttpsPort);
            var urls = GetListenUrls(allowRemote, httpPort, httpsPort);

            // Filter out HTTPS URLs if no valid certificate is available
            // This prevents startup errors when running as a WPF app
            var filteredUrls = string.Join(";", urls.Split(';')
                .Where(url => !url.Trim().StartsWith("https://", StringComparison.OrdinalIgnoreCase)));

            if (string.IsNullOrWhiteSpace(filteredUrls))
            {
                filteredUrls = $"http://{(allowRemote ? RemoteHost : LoopbackHost)}:{httpPort}"; // Fallback to HTTP
            }

            builder.WebHost.UseUrls(filteredUrls);
            BaseUrl = filteredUrls.Split(';')[0];

            _logger.LogInformation("API server will listen on: {Urls}", filteredUrls);

            ConfigureServices(builder, _serviceProvider, configuration);

            _app = builder.Build();

            ConfigurePipeline(_app);

            await _app.StartAsync();

            _isRunning = true;
            _logger.LogInformation("API server started at {BaseUrl}", BaseUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start API server");
            throw;
        }
    }

    /// <summary>
    /// Registers the API's services. The controllers share the WPF app's singletons,
    /// so they are copied across from <paramref name="main"/> rather than created anew.
    /// Split from <see cref="StartAsync"/> so tests can build the same host on a
    /// TestServer without binding a port.
    /// </summary>
    public static void ConfigureServices(
        WebApplicationBuilder builder,
        IServiceProvider main,
        IConfiguration configuration)
    {
        builder.Services.AddControllers(options =>
            {
                options.Filters.Add<CookieAntiforgeryFilter>();
                // Name validation errors after the JSON property ("email"), not the C#
                // one ("Email"), to match the Identity errors and the request body.
                options.ModelMetadataDetailsProviders.Add(new SystemTextJsonValidationMetadataProvider());
            })
            // AddControllers discovers controllers from the entry assembly, which is
            // the test runner rather than this app when a test builds the host.
            .AddApplicationPart(typeof(ApiServer).Assembly);
        builder.Services.AddEndpointsApiExplorer();
        // Swagger disabled - causes build issues with MAUI

        // Register services from main service provider
        // Note: We're creating a new service collection, but we'll use the existing singletons
        builder.Services.AddSingleton(main.GetRequiredService<PlayerTracker>());
        builder.Services.AddSingleton(main.GetRequiredService<TrackChangeTracker>());
        builder.Services.AddSingleton(main.GetRequiredService<WreckfestWebWebhookService>());
        builder.Services.AddSingleton(main.GetRequiredService<ConsoleLogWebhookSender>());
        builder.Services.AddSingleton(main.GetRequiredService<ServerManager>());
        builder.Services.AddSingleton(main.GetRequiredService<ConfigService>());
        builder.Services.AddSingleton(main.GetRequiredService<EventStorageService>());
        builder.Services.AddSingleton(main.GetRequiredService<RecurringEventService>());
        builder.Services.AddSingleton(main.GetRequiredService<SmartRestartService>());
        builder.Services.AddSingleton(main.GetRequiredService<DatabaseState>());

        builder.Services.AddApiAuthentication(main, configuration);
    }

    /// <summary>
    /// Configures the request pipeline. Expects the services from
    /// <see cref="ConfigureServices"/>.
    /// </summary>
    public static void ConfigurePipeline(WebApplication app)
    {
        // First, so recovery mode answers before anything touches the user store.
        app.UseMiddleware<DatabaseUnavailableMiddleware>();

        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
    }

    public async Task StopAsync()
    {
        if (!_isRunning || _app == null)
        {
            _logger.LogWarning("API server is not running");
            return;
        }

        try
        {
            _logger.LogInformation("Stopping API server...");
            await _app.StopAsync();
            await _app.DisposeAsync();
            _app = null;
            _isRunning = false;
            _logger.LogInformation("API server stopped");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping API server");
            throw;
        }
    }

    public void Dispose()
    {
        Task.Run(() => StopAsync()).GetAwaiter().GetResult();
    }
}
