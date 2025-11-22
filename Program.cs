using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WreckfestController.Services;

namespace WreckfestController;

public class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var host = CreateHostBuilder(args).Build();

        // Start the embedded API server in background
        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1000); // Wait for app initialization
                var apiServer = host.Services.GetRequiredService<IApiServer>();
                await apiServer.StartAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to start API server: {ex}");
            }
        });

        // Create and run WPF application
        var app = new App();
        app.InitializeComponent();

        // Create MainWindow with dependency injection
        var mainWindow = host.Services.GetRequiredService<MainWindow>();
        app.MainWindow = mainWindow;
        mainWindow.Show();

        app.Run();
    }

    public static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((context, config) =>
            {
                // For single-file apps, use the directory where the exe is located
                // Environment.ProcessPath gives us the actual exe path even in single-file mode
                var exeDirectory = Path.GetDirectoryName(Environment.ProcessPath) ?? AppDomain.CurrentDomain.BaseDirectory;
                config.SetBasePath(exeDirectory);

                // appsettings.json is optional - app works with defaults if not present
                config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
                config.AddJsonFile($"appsettings.{context.HostingEnvironment.EnvironmentName}.json", optional: true);

                // Build temporary configuration to get UserSettingsPath
                var tempConfig = config.Build();
                var userSettingsPath = ResolveUserSettingsPath(tempConfig);

                // Add user settings with override priority
                config.AddJsonFile(userSettingsPath, optional: true, reloadOnChange: true);
            })
            .ConfigureServices((context, services) =>
            {
                // Register GUI logger provider
                var guiLoggerProvider = new GuiLoggerProvider();
                services.AddSingleton(guiLoggerProvider);
                services.AddLogging(builder =>
                {
#if DEBUG
                    builder.AddDebug();
#endif
                    builder.AddProvider(guiLoggerProvider);
                });

                // Register core services
                services.AddHttpClient<WreckfestWebWebhookService>();
                services.AddSingleton<PlayerTracker>();
                services.AddSingleton<TrackChangeTracker>();
                services.AddSingleton<ServerInfoTracker>();
                services.AddSingleton<WreckfestWebWebhookService>();
                services.AddSingleton<ConfigService>();
                services.AddSingleton<EventStorageService>();
                services.AddSingleton<RecurringEventService>();
                services.AddSingleton<SmartRestartService>();
                services.AddSingleton<ConsoleMonitor>();
                services.AddSingleton<ServerManager>();
                services.AddSingleton<SettingsService>();

                // Register API server
                services.AddSingleton<IApiServer, ApiServer>();

                // Register UI
                services.AddSingleton<MainWindow>();

                // Register hosted services (background services)
                services.AddHostedService<EventSchedulerService>();
            });

    /// <summary>
    /// Resolves the user settings file path based on configuration
    /// </summary>
    private static string ResolveUserSettingsPath(IConfiguration configuration)
    {
        var configuredPath = configuration["UserSettingsPath"];

        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            // Default to %LocalAppData%\WreckfestController\user-settings.json
            var appDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WreckfestController"
            );
            Directory.CreateDirectory(appDataPath);
            return Path.Combine(appDataPath, "user-settings.json");
        }
        else
        {
            // Use configured path (expand environment variables)
            var expandedPath = Environment.ExpandEnvironmentVariables(configuredPath);

            // Ensure directory exists
            var directory = Path.GetDirectoryName(expandedPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            return expandedPath;
        }
    }
}
