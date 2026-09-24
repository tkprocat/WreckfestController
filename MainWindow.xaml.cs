using System.Timers;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using WreckfestController.Data;
using WreckfestController.Services;
using WreckfestController.Views;
using Timer = System.Timers.Timer;
using materialDesign = MaterialDesignThemes.Wpf;

namespace WreckfestController;

public partial class MainWindow : Window
{
    private readonly ServerManager _serverManager;
    private readonly DatabaseState _databaseState;
    private readonly DatabaseBootstrapper _databaseBootstrapper;
    private readonly ILogger<MainWindow> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Timer _statusUpdateTimer;
    private readonly Timer _processListRefreshTimer;

    private volatile bool _closed;

    private ProcessManagerTab? _processManagerTab;
    private ServerControlTab? _serverControlTab;
    private ConfigurationTab? _configurationTab;
    private EventSchedulerTab? _eventSchedulerTab;
    private ControllerLogTab? _controllerLogTab;
    private PlayersTab? _playersTab;

    public MainWindow(
        ServerManager serverManager,
        PlayerTracker playerTracker,
        TrackChangeTracker trackChangeTracker,
        SettingsService settingsService,
        EventStorageService eventStorageService,
        SmartRestartService smartRestartService,
        ConfigService configService,
        WreckfestWebWebhookService webhookService,
        GuiLoggerProvider guiLoggerProvider,
        DatabaseState databaseState,
        DatabaseBootstrapper databaseBootstrapper,
        ILogger<MainWindow> logger,
        ILoggerFactory loggerFactory)
    {
        InitializeComponent();

        _serverManager = serverManager;
        _databaseState = databaseState;
        _databaseBootstrapper = databaseBootstrapper;
        _logger = logger;
        _loggerFactory = loggerFactory;

        // Initialize tabs
        _processManagerTab = new ProcessManagerTab(
            serverManager,
            _loggerFactory.CreateLogger<ProcessManagerTab>(),
            UpdateWindowTitle);

        _serverControlTab = new ServerControlTab(
            serverManager,
            playerTracker,
            trackChangeTracker,
            settingsService,
            _loggerFactory.CreateLogger<ServerControlTab>());

        _configurationTab = new ConfigurationTab(
            settingsService,
            _loggerFactory.CreateLogger<ConfigurationTab>());

        _eventSchedulerTab = new EventSchedulerTab(
            eventStorageService,
            smartRestartService,
            webhookService,
            _loggerFactory.CreateLogger<EventSchedulerTab>());

        _controllerLogTab = new ControllerLogTab();

        _playersTab = new PlayersTab(
            playerTracker,
            serverManager,
            _loggerFactory.CreateLogger<PlayersTab>());

        // Connect GUI logger to Controller Log tab
        guiLoggerProvider.SetLogTab(_controllerLogTab);

        // Subscribe to PID changes to update window title
        _serverManager.ProcessIdChanged += OnProcessIdChanged;

        // Set tab content
        StatusTabContent.Content = _processManagerTab;
        ServerControlTabContent.Content = _serverControlTab;
        PlayersTabContent.Content = _playersTab;
        ConfigurationTabContent.Content = _configurationTab;
        EventSchedulerTabContent.Content = _eventSchedulerTab;
        ControllerLogTabContent.Content = _controllerLogTab;

        // Setup status update timer
        _statusUpdateTimer = new Timer(1000); // Update every second
        _statusUpdateTimer.Elapsed += OnStatusUpdateTick;
        _statusUpdateTimer.Start();

        // Setup process list refresh timer
        _processListRefreshTimer = new Timer(5000); // Refresh every 5 seconds
        _processListRefreshTimer.Elapsed += OnProcessListRefreshTick;
        _processListRefreshTimer.Start();

        _databaseState.Changed += OnDatabaseStateChanged;
        UpdateDatabaseBanner();

        // Initial updates
        _serverControlTab.UpdateServerStatus();
        _processManagerTab.RefreshProcessList();
        UpdateWindowTitle();
    }

    private void OnStatusUpdateTick(object? sender, ElapsedEventArgs e)
    {
        QueueUiUpdate(() => _serverControlTab?.UpdateServerStatus());
    }

    private void OnProcessListRefreshTick(object? sender, ElapsedEventArgs e)
    {
        QueueUiUpdate(() => _processManagerTab?.RefreshProcessList());
    }

    private void OnProcessIdChanged(int? newPid)
    {
        QueueUiUpdate(UpdateWindowTitle);
    }

    private void OnDatabaseStateChanged()
    {
        QueueUiUpdate(UpdateDatabaseBanner);
    }

    private void UpdateDatabaseBanner()
    {
        if (_databaseState.IsReady)
        {
            DatabaseBanner.Visibility = Visibility.Collapsed;
            return;
        }

        DatabaseBannerError.Text = _databaseState.Error ?? "The database has not been prepared yet.";
        DatabaseBannerBackup.Text = _databaseState.BackupPath is { } backup
            ? $"A backup was made before the failed migration: {backup}"
            : $"Database: {_databaseState.DatabasePath}";
        DatabaseBanner.Visibility = Visibility.Visible;
    }

    private void OpenDataFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var folder = Path.GetDirectoryName(_databaseState.DatabasePath);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            return;
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = folder,
            UseShellExecute = true,
        });
    }

    private async void RetryDatabaseButton_Click(object sender, RoutedEventArgs e)
    {
        RetryDatabaseButton.IsEnabled = false;
        try
        {
            // The banner follows DatabaseState.Changed, so the result needs no handling here.
            await Task.Run(_databaseBootstrapper.Run);
        }
        finally
        {
            RetryDatabaseButton.IsEnabled = true;
        }
    }

    private void QueueUiUpdate(Action update)
    {
        if (_closed || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            return;

        try
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_closed || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                    return;

                try
                {
                    update();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to update window status");
                }
            }));
        }
        catch (InvalidOperationException) when (
            _closed || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            // Shutdown can begin between the check and scheduling the callback.
        }
    }
    public void UpdateWindowTitle()
    {
        var status = _serverManager.GetStatus();
        string titleText;

        if (status.IsRunning && status.ProcessId.HasValue)
        {
            var configFile = _serverManager.GetCurrentConfigFileName();
            if (!string.IsNullOrEmpty(configFile))
            {
                titleText = $"Wreckfest Server Console - PID: {status.ProcessId} - Config: {configFile}";
            }
            else
            {
                titleText = $"Wreckfest Server Console - PID: {status.ProcessId}";
            }
        }
        else
        {
            titleText = "Wreckfest Server Console";
        }

        Title = titleText;
        TitleBarText.Text = titleText; // Update custom titlebar too
    }

    // Method called by ProcessManagerTab when starting a new server
    public async void StartServerFromProcessManagerTab()
    {
        try
        {
            _serverControlTab?.AddEventLogItem("Starting server...", "#FFD43B");
            var result = await _serverManager.StartServerAsync();

            if (result.Success)
            {
                _serverControlTab?.AddEventLogItem("Server started successfully", "#51CF66");
                // Refresh process list after a short delay
                await Task.Delay(2000);
                _processManagerTab?.RefreshProcessList();
            }
            else
            {
                _serverControlTab?.AddEventLogItem($"Failed to start server: {result.Message}", "#FF6B6B");
                await DialogService.ShowErrorAsync(result.Message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting server");
            _serverControlTab?.AddEventLogItem($"Error starting server: {ex.Message}", "#FF6B6B");
            await DialogService.ShowErrorAsync(ex.Message);
        }
    }

    // Custom titlebar window control handlers
    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            // Double-click to maximize/restore
            MaximizeButton_Click(sender, new RoutedEventArgs());
        }
        else
        {
            // Single click to drag
            DragMove();
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

        // Update button text
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _serverManager.ProcessIdChanged -= OnProcessIdChanged;
        _databaseState.Changed -= OnDatabaseStateChanged;
        _statusUpdateTimer.Elapsed -= OnStatusUpdateTick;
        _statusUpdateTimer.Dispose();
        _processListRefreshTimer.Elapsed -= OnProcessListRefreshTick;
        _processListRefreshTimer.Dispose();
        _controllerLogTab?.Dispose();
        base.OnClosed(e);
    }
}
