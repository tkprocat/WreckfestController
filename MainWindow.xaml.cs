using System.Timers;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using WreckfestController.Services;
using WreckfestController.Views;
using Timer = System.Timers.Timer;
using materialDesign = MaterialDesignThemes.Wpf;

namespace WreckfestController;

public partial class MainWindow : Window
{
    private readonly ServerManager _serverManager;
    private readonly ILogger<MainWindow> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Timer _statusUpdateTimer;
    private readonly Timer _processListRefreshTimer;

    private ProcessManagerTab? _processManagerTab;
    private ServerControlTab? _serverControlTab;
    private ConfigurationTab? _configurationTab;

    public MainWindow(
        ServerManager serverManager,
        PlayerTracker playerTracker,
        TrackChangeTracker trackChangeTracker,
        SettingsService settingsService,
        ILogger<MainWindow> logger,
        ILoggerFactory loggerFactory)
    {
        InitializeComponent();

        _serverManager = serverManager;
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
            _loggerFactory.CreateLogger<ServerControlTab>());

        _configurationTab = new ConfigurationTab(
            settingsService,
            _loggerFactory.CreateLogger<ConfigurationTab>());

        // Set tab content
        StatusTabContent.Content = _processManagerTab;
        ServerControlTabContent.Content = _serverControlTab;
        ConfigurationTabContent.Content = _configurationTab;

        // Set window icon from Material Design PackIcon
        SetIconFromPackIcon();

        // Setup status update timer
        _statusUpdateTimer = new Timer(1000); // Update every second
        _statusUpdateTimer.Elapsed += OnStatusUpdateTick;
        _statusUpdateTimer.Start();

        // Setup process list refresh timer
        _processListRefreshTimer = new Timer(5000); // Refresh every 5 seconds
        _processListRefreshTimer.Elapsed += OnProcessListRefreshTick;
        _processListRefreshTimer.Start();

        // Initial updates
        _serverControlTab.UpdateServerStatus();
        _processManagerTab.RefreshProcessList();
        UpdateWindowTitle();
    }

    private void OnStatusUpdateTick(object? sender, ElapsedEventArgs e)
    {
        Dispatcher.Invoke(() => _serverControlTab?.UpdateServerStatus());
    }

    private void OnProcessListRefreshTick(object? sender, ElapsedEventArgs e)
    {
        Dispatcher.Invoke(() => _processManagerTab?.RefreshProcessList());
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
                MessageBox.Show(result.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting server");
            _serverControlTab?.AddEventLogItem($"Error starting server: {ex.Message}", "#FF6B6B");
            MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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

    private void SetIconFromPackIcon()
    {
        try
        {
            // Create a PackIcon programmatically
            var packIcon = new materialDesign.PackIcon
            {
                Kind = materialDesign.PackIconKind.Wrench,
                Width = 32,
                Height = 32,
                Foreground = new SolidColorBrush(Colors.White)
            };

            // Render to bitmap
            var drawingVisual = new DrawingVisual();
            using (DrawingContext drawingContext = drawingVisual.RenderOpen())
            {
                var visualBrush = new VisualBrush(packIcon);
                drawingContext.DrawRectangle(visualBrush, null, new Rect(0, 0, 32, 32));
            }

            var renderTargetBitmap = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
            renderTargetBitmap.Render(drawingVisual);

            // Set as window icon directly from BitmapSource
            Icon = renderTargetBitmap;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to set window icon from PackIcon");
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _statusUpdateTimer?.Stop();
        _processListRefreshTimer?.Stop();
    }
}
