using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WreckfestController.Data;
using WreckfestController.Services;

namespace WreckfestController;

public class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        IHost host;
        try
        {
            host = CreateHostBuilder(args).Build();
        }
        catch (Exception ex)
        {
            // Nothing is registered yet, so there is no window to carry on in. This is
            // almost always a malformed settings file.
            ReportFatalStartupError(ex);
            return;
        }

        try
        {
            // Never throws: a failure puts the app into recovery mode instead, which the
            // window, the scheduler and the API all read from DatabaseState.
            host.Services.GetRequiredService<DatabaseBootstrapper>().Run();

            // Start hosted services, including EventSchedulerService, before entering
            // the WPF message loop.
            //
            // Guarded because EventSchedulerService.StartAsync does real work up front -
            // it loads the schedule file and scans for missed events synchronously - and
            // this runs before MainWindow is shown. An unreadable or corrupt schedule
            // would otherwise kill the app with no UI to report it. Losing the scheduler
            // is bad; losing the whole controller because of it is worse.
            try
            {
                host.Start();
            }
            catch (Exception ex)
            {
                ReportHostStartFailure(host, ex);
            }

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

            // Force instantiation so it subscribes to ServerManager.ChatCommandReceived.
            host.Services.GetRequiredService<VotingService>();

            // Create and run WPF application
            var app = new App();
            app.InitializeComponent();

            // Create MainWindow with dependency injection
            var mainWindow = host.Services.GetRequiredService<MainWindow>();
            app.MainWindow = mainWindow;
            mainWindow.Show();

            app.Run();
        }
        finally
        {
            try
            {
                host.StopAsync().GetAwaiter().GetResult();
            }
            finally
            {
                host.Dispose();
            }
        }
    }

    private static void ReportFatalStartupError(Exception ex)
    {
        Console.WriteLine($"Failed to start: {ex}");
        MessageBox.Show(
            $"Wreckfest Controller could not start.\n\n{ex.Message}\n\n" +
            "Check appsettings.json and user-settings.json for errors.",
            "Wreckfest Controller",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    // The host failed to start, so hosted services - the event scheduler above all -
    // are not running: scheduled events will not activate until the app is restarted.
    // Everything reached through the DI container still works, so the app carries on.
    private static void ReportHostStartFailure(IHost host, Exception ex)
    {
        Console.WriteLine($"Failed to start hosted services: {ex}");

        try
        {
            host.Services.GetService<ILoggerFactory>()
                ?.CreateLogger(typeof(Program))
                .LogError(
                    ex,
                    "Hosted services failed to start; continuing without them. Scheduled events will not activate.");
        }
        catch
        {
            // The container itself is unusable, so the console line above is all we get.
        }
    }

    // For single-file apps, use the directory where the exe is located.
    // Environment.ProcessPath gives the actual exe path even in single-file mode.
    private static string ExeDirectory =>
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppDomain.CurrentDomain.BaseDirectory;

    public static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((context, config) =>
            {
                config.SetBasePath(ExeDirectory);

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

                    // EF Core logs every SQL statement at Information, which buries the
                    // Controller Log. A more specific Logging:LogLevel entry still wins.
                    builder.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
                });

                // Register core services
                services.AddSingleton<PlayerTracker>();
                services.AddSingleton<TrackChangeTracker>();
                services.AddSingleton<ServerInfoTracker>();
                services.AddSingleton<HubServerEventPublisher>();
                services.AddSingleton<IServerEventPublisher>(sp => sp.GetRequiredService<HubServerEventPublisher>());
                services.AddSingleton<ConfigService>();
                services.AddSingleton<EventStorageService>();
                services.AddSingleton<RecurringEventService>();
                services.AddSingleton<SmartRestartService>();
                services.AddSingleton<InjectedHookInputWriter>();
                services.AddSingleton<IServerInputWriter>(sp => sp.GetRequiredService<InjectedHookInputWriter>());
                services.AddSingleton<IInjectedHookOutputReader, InjectedHookOutputReader>();
                services.AddSingleton<ServerManager>();
                services.AddSingleton<SettingsService>();
                services.AddSingleton<VotingService>();

                // The controller's own database. The path is read once; changing it needs a restart.
                var databasePath = DatabasePath.Resolve(context.Configuration, ExeDirectory);
                services.AddDbContextFactory<ControllerDbContext>(
                    options => ControllerDbContext.Configure(options, databasePath));
                services.AddSingleton(new DatabaseState(databasePath));
                services.AddSingleton<DatabaseBootstrapper>();
                AccountService.AddAccounts(services);

                // Register API server
                services.AddSingleton<IApiServer, ApiServer>();

                // Register UI
                services.AddSingleton<MainWindow>();

                // Register hosted services (background services)
                // The scheduler waits while the database is unavailable (recovery mode).
                services.AddSingleton<EventSchedulerService>();
                services.AddHostedService<DatabaseGatedHostedService<EventSchedulerService>>();
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
