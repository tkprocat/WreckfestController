using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using WreckfestController.Data;
using WreckfestController.Hubs;
using WreckfestController.Services.Auth;
using WreckfestController.Services.Config;
using WreckfestController.Services.Cups;
using WreckfestController.Services.Hosting.Https;
using WreckfestController.Services.Publishing;
using WreckfestController.Services.ServerControl;
using WreckfestController.Services.Tracking;

namespace WreckfestController.Services.Hosting;

/// <summary>
/// Interface for the embedded API server.
/// </summary>
public interface IApiServer
{
    Task StartAsync();
    Task StopAsync();
    bool IsRunning { get; }
    string BaseUrl { get; }

    /// <summary>The HTTPS certificate's status, "off", or null before the API has started.</summary>
    HttpsStatus? HttpsStatus { get; }

    /// <summary>Why the API did not start, when it did not.</summary>
    string? StartError { get; }
}

/// <summary>
/// Embedded ASP.NET Core API server that runs within the MAUI application.
/// Provides the REST API and the SignalR hub.
/// </summary>
public class ApiServer : IApiServer, IDisposable
{
    public const int DefaultHttpPort = ApiEndpoints.DefaultHttpPort;
    public const int DefaultHttpsPort = ApiEndpoints.DefaultHttpsPort;
    private readonly ILogger<ApiServer> _logger;
    private readonly IServiceProvider _serviceProvider;
    private WebApplication? _app;
    private CertificateProvider? _certificates;
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
    /// HTTPS as it stands: the certificate being served and any error, or "off". Null
    /// before the API has started; the desktop app shows it when set.
    /// </summary>
    public HttpsStatus? HttpsStatus => _certificates?.Status ?? (_isRunning ? Https.HttpsStatus.Off : null);

    /// <summary>Why the API did not start, when it did not; for the desktop app.</summary>
    public string? StartError { get; private set; }

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

            StartError = null;
            var endpoints = ApiEndpoints.Resolve(configuration, AppContext.BaseDirectory);

            // HTTPS configured but unusable stops here, with the reason: the API never
            // falls back to HTTP for a remote browser that expected HTTPS.
            if (endpoints.Https is { } https)
            {
                ICertificateSource source = https.Source switch
                {
                    StoreSourceSettings store => new StoreCertificateSource(store, () => DateTimeOffset.UtcNow),
                    FileSourceSettings file => new FileCertificateSource(file, () => DateTimeOffset.UtcNow),
                    _ => throw new HttpsConfigurationException("Api:Https names no certificate source."),
                };
                _certificates = new CertificateProvider(source, () => DateTimeOffset.UtcNow, _logger);
                _certificates.Start();
                var status = _certificates.Status;
                _logger.LogInformation(
                    "HTTPS on port {Port} with {Subject} ({DnsNames}), valid until {NotAfter:u}",
                    endpoints.HttpsPort, status.Subject, string.Join(", ", status.DnsNames), status.NotAfter);
                if (status.ExpiresSoon)
                {
                    _logger.LogWarning("The HTTPS certificate expires on {NotAfter:u}: renew it", status.NotAfter);
                }
            }
            else
            {
                _logger.LogInformation("HTTPS is off (no Api:Https): serving HTTP only");
            }

            builder.WebHost.ConfigureKestrel(options => HttpsEndpoint.Configure(options, endpoints, _certificates));
            builder.Services.AddSingleton(new HttpsStatusSource(_certificates));
            var host = endpoints.Address.Equals(System.Net.IPAddress.Any) ? "0.0.0.0" : "127.0.0.1";
            BaseUrl = $"http://{host}:{endpoints.HttpPort}";

            _logger.LogInformation(
                "API server will listen on {BaseUrl}{Https}",
                BaseUrl,
                _certificates is null ? string.Empty : $" and https://{host}:{endpoints.HttpsPort}");

            ConfigureServices(builder, _serviceProvider, configuration);

            _app = builder.Build();

            ConfigurePipeline(_app);

            await _app.StartAsync();

            _isRunning = true;
            _logger.LogInformation("API server started at {BaseUrl}", BaseUrl);
        }
        catch (Exception ex)
        {
            // A half-built host still holds ports and services: let it go, so a corrected
            // configuration can start cleanly.
            StartError = ex is HttpsConfigurationException ? ex.Message : "The API did not start. The log has the details.";
            if (_app is not null)
            {
                await _app.DisposeAsync();
                _app = null;
            }

            _certificates?.Dispose();
            _certificates = null;
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
            .AddApplicationPart(typeof(ApiServer).Assembly)
            // Numbers are numbers. The web default also accepts "5", which makes every
            // integer in the OpenAPI contract, and so in the web app's types, number | string.
            .AddJsonOptions(options => options.JsonSerializerOptions.NumberHandling = JsonNumberHandling.Strict);

        // The OpenAPI generator reads these options, not MVC's, when it describes numbers.
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict);
        builder.Services.AddEndpointsApiExplorer();

        // /openapi/v1.json: the contract the web app's TypeScript types are generated
        // from. Signed-in only, like the rest of the API; the committed copy in
        // web/src/api/openapi.json is what the build uses.
        builder.Services.AddOpenApi(options => options.AddDocumentTransformer((document, _, _) =>
        {
            // Otherwise named after the entry assembly, which is the test runner in tests.
            document.Info.Title = "WreckfestController API";
            document.Info.Version = "2.0";
            return Task.CompletedTask;
        }));

        // Register services from main service provider
        // Note: We're creating a new service collection, but we'll use the existing singletons
        builder.Services.AddSingleton(main.GetRequiredService<PlayerTracker>());
        builder.Services.AddSingleton(main.GetRequiredService<TrackChangeTracker>());
        // One instance under both types: controllers take the interface, and
        // ConfigurePipeline attaches this host's hub to the concrete publisher.
        var publisher = main.GetRequiredService<HubServerEventPublisher>();
        builder.Services.AddSingleton(publisher);
        builder.Services.AddSingleton<IServerEventPublisher>(publisher);
        builder.Services.AddSingleton(main.GetRequiredService<ServerManager>());
        builder.Services.AddSingleton(main.GetRequiredService<ConfigService>());
        builder.Services.AddSingleton(main.GetRequiredService<CupStore>());
        builder.Services.AddSingleton(main.GetRequiredService<CupActivator>());
        builder.Services.AddSingleton(main.GetRequiredService<SmartRestartService>());
        builder.Services.AddSingleton(main.GetRequiredService<DatabaseState>());
        builder.Services.AddSingleton(main.GetRequiredService<ISettingsStore>());

        builder.Services.AddApiAuthentication(main, configuration);
        builder.Services.AddTrustedProxies(configuration);
        builder.Services.AddRateLimits();
        builder.Services.AddSignalR();
        builder.Services.AddSingleton(WebApp.Resolve(configuration));
        // HTTPS status: StartAsync registers the real one first; a test host has none.
        builder.Services.TryAddSingleton(HttpsStatusSource.None);
        var https = ApiEndpoints.Resolve(configuration, AppContext.BaseDirectory).Https;
        builder.Services.AddSingleton(sp => new HttpsPolicy(https, sp.GetRequiredService<HttpsStatusSource>()));
    }

    /// <summary>
    /// Configures the request pipeline. Expects the services from
    /// <see cref="ConfigureServices"/>.
    /// </summary>
    public static void ConfigurePipeline(WebApplication app)
    {
        // What arrived, before the forwarded headers rewrite it: HTTPS enforcement decides
        // "this PC or not" from that.
        app.Use(HttpsEnforcementMiddleware.CaptureOriginal);

        // Before anything reads the client IP or the scheme.
        app.UseForwardedHeaders();

        // With HTTPS on: remote plain HTTP is redirected (page loads) or refused (the rest)
        // before any file, recovery page, sign-in or API call is served over it.
        app.UseMiddleware<HttpsEnforcementMiddleware>();

        // The web app's files, which need neither the database nor a signed-in user.
        var web = app.Services.GetRequiredService<WebApp>();
        web.UseFiles(app);

        // Next, so recovery mode answers before anything touches the user store.
        app.UseMiddleware<DatabaseUnavailableMiddleware>();

        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();

        // Anonymous so the public page gets live updates. What a connection receives
        // is decided by its group, and only ServerHub puts a signed-in caller in admin.
        app.MapHub<ServerHub>(ServerHub.Route).AllowAnonymous();
        app.MapOpenApi().RequireAuthorization(ApiAuthentication.AdminPolicy);

        // After the API and the hub: only what neither of them answers.
        web.MapFallback(app);
        app.Logger.LogInformation(
            web.Files is null
                ? "No web app at {WebRoot}; serving the API only"
                : "Serving the web app from {WebRoot}",
            web.Root);

        // The publisher belongs to the WPF app and outlives this host, so it is
        // pointed at this host's hub only while the host is running.
        var publisher = app.Services.GetRequiredService<HubServerEventPublisher>();
        var hub = app.Services.GetRequiredService<IHubContext<ServerHub, IServerHubClient>>();
        app.Lifetime.ApplicationStarted.Register(() => publisher.Attach(hub));
        app.Lifetime.ApplicationStopping.Register(() => publisher.Detach(hub));
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
            _certificates?.Dispose();
            _certificates = null;
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
