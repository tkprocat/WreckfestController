using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;

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

            // Configure Kestrel URLs
            var configuration = _serviceProvider.GetRequiredService<IConfiguration>();
            var urls = configuration["Kestrel:Urls"] ?? "http://localhost:5100";

            // Filter out HTTPS URLs if no valid certificate is available
            // This prevents startup errors when running as a WPF app
            var filteredUrls = string.Join(";", urls.Split(';')
                .Where(url => !url.Trim().StartsWith("https://", StringComparison.OrdinalIgnoreCase)));

            if (string.IsNullOrWhiteSpace(filteredUrls))
            {
                filteredUrls = "http://localhost:5100"; // Fallback to HTTP
            }

            builder.WebHost.UseUrls(filteredUrls);
            BaseUrl = filteredUrls.Split(';')[0];

            _logger.LogInformation("API server will listen on: {Urls}", filteredUrls);

            // Add controllers
            builder.Services.AddControllers();
            builder.Services.AddEndpointsApiExplorer();
            // Swagger disabled - causes build issues with MAUI

            // Register services from main service provider
            // Note: We're creating a new service collection, but we'll use the existing singletons
            builder.Services.AddSingleton(_serviceProvider.GetRequiredService<PlayerTracker>());
            builder.Services.AddSingleton(_serviceProvider.GetRequiredService<TrackChangeTracker>());
            builder.Services.AddSingleton(_serviceProvider.GetRequiredService<WreckfestWebWebhookService>());
            builder.Services.AddSingleton(_serviceProvider.GetRequiredService<ConsoleLogWebhookSender>());
            builder.Services.AddSingleton(_serviceProvider.GetRequiredService<ServerManager>());
            builder.Services.AddSingleton(_serviceProvider.GetRequiredService<ConfigService>());
            builder.Services.AddSingleton(_serviceProvider.GetRequiredService<EventStorageService>());
            builder.Services.AddSingleton(_serviceProvider.GetRequiredService<RecurringEventService>());
            builder.Services.AddSingleton(_serviceProvider.GetRequiredService<SmartRestartService>());

            _app = builder.Build();

            // Configure middleware
            // Swagger disabled - causes build issues with MAUI

            _app.UseAuthorization();
            _app.MapControllers();

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
