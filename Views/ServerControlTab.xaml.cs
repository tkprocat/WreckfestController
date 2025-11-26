using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.Logging;
using WreckfestController.Services;

namespace WreckfestController.Views;

public partial class ServerControlTab : UserControl
{
    private readonly ServerManager _serverManager;
    private readonly PlayerTracker _playerTracker;
    private readonly TrackChangeTracker _trackChangeTracker;
    private readonly SettingsService _settingsService;
    private readonly ILogger<ServerControlTab> _logger;
    private readonly StringBuilder _consoleBuffer = new();
    private readonly ObservableCollection<EventLogItem> _eventLogItems = new();
    private const int MaxConsoleLines = 1000;
    private const int MaxEventLogItems = 100;

    public ServerControlTab(
        ServerManager serverManager,
        PlayerTracker playerTracker,
        TrackChangeTracker trackChangeTracker,
        SettingsService settingsService,
        ILogger<ServerControlTab> logger)
    {
        InitializeComponent();

        _serverManager = serverManager;
        _playerTracker = playerTracker;
        _trackChangeTracker = trackChangeTracker;
        _settingsService = settingsService;
        _logger = logger;

        EventLogContainer.ItemsSource = _eventLogItems;

        // Subscribe to server events
        _serverManager.ConsoleOutput += OnConsoleOutput;
        _playerTracker.PlayerEvent += OnPlayerEvent;
        _trackChangeTracker.TrackChanged += OnTrackChangeEvent;
    }

    public void UpdateServerStatus()
    {
        var status = _serverManager.GetStatus();

        // Update server status
        ServerStatusLabel.Text = status.IsRunning ? "Running" : "Stopped";
        ServerStatusLabel.Foreground = status.IsRunning
            ? (SolidColorBrush)FindResource("GreenColor")
            : (SolidColorBrush)FindResource("RedColor");

        // Update player count
        var (onlineCount, maxPlayers) = _playerTracker.GetPlayerCount();
        PlayerCountLabel.Text = $"{onlineCount} / {maxPlayers}";

        // Update current track
        var currentTrack = _trackChangeTracker.GetCurrentTrack();
        CurrentTrackLabel.Text = string.IsNullOrEmpty(currentTrack) ? "None" : currentTrack;

        // Update uptime
        UptimeLabel.Text = status.Uptime?.ToString(@"hh\:mm\:ss") ?? "00:00:00";

        // Update button states
        StartButton.IsEnabled = !status.IsRunning;
        StopButton.IsEnabled = status.IsRunning;
        RestartButton.IsEnabled = status.IsRunning;

        // Enable Update button only if SteamCMD is properly configured
        UpdateButton.IsEnabled = IsSteamCmdConfigured();
    }

    /// <summary>
    /// Checks if SteamCMD is properly configured
    /// </summary>
    private bool IsSteamCmdConfigured()
    {
        try
        {
            var settings = _settingsService.LoadSettings();
            var steamCmdPath = settings.SteamCmd?.SteamCmdPath;
            var appId = settings.SteamCmd?.WreckfestAppId;

            // Both SteamCmdPath and WreckfestAppId must be set
            if (string.IsNullOrWhiteSpace(steamCmdPath) || string.IsNullOrWhiteSpace(appId))
            {
                return false;
            }

            // Check if SteamCMD executable exists
            if (!File.Exists(steamCmdPath))
            {
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking SteamCMD configuration");
            return false;
        }
    }

    private void OnConsoleOutput(string output)
    {
        // Use BeginInvoke (async) instead of Invoke (sync) to prevent deadlocks
        // The timer thread should never block waiting for the UI thread
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                // Append to console buffer
                _consoleBuffer.AppendLine(output);

                // Trim if too long (keep last N lines)
                var lines = _consoleBuffer.ToString().Split('\n');
                if (lines.Length > MaxConsoleLines)
                {
                    var trimmedLines = lines.Skip(lines.Length - MaxConsoleLines);
                    _consoleBuffer.Clear();
                    _consoleBuffer.AppendLine(string.Join('\n', trimmedLines));
                }

                // Update console display
                ConsoleOutput.Text = _consoleBuffer.ToString();

                // Auto-scroll to bottom
                ConsoleScrollView.ScrollToEnd();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating console output");
            }
        });
    }

    private void OnPlayerEvent(PlayerTrackerEvent playerEvent)
    {
        if (playerEvent.EventType == "Join")
        {
            AddEventLogItem($"Player joined: {playerEvent.Player?.Name}", "#51CF66");
        }
        else if (playerEvent.EventType == "Left")
        {
            AddEventLogItem($"Player left: {playerEvent.Player?.Name}", "#FF6B6B");
        }
        else if (playerEvent.EventType == "Kicked")
        {
            AddEventLogItem($"Player kicked: {playerEvent.Player?.Name}", "#FFD43B");
        }
    }

    private void OnTrackChangeEvent(TrackChangeEvent trackEvent)
    {
        AddEventLogItem($"Track changed: {trackEvent.TrackId}", "#74C0FC");
    }

    public void AddEventLogItem(string message, string colorHex)
    {
        // Use BeginInvoke to prevent deadlocks when called from background threads
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                var timestamp = DateTime.Now.ToString("HH:mm:ss");
                var color = (SolidColorBrush)new BrushConverter().ConvertFrom(colorHex)!;

                var eventItem = new EventLogItem
                {
                    Timestamp = timestamp,
                    Message = message,
                    Color = color
                };

                // Add to top of event log
                _eventLogItems.Insert(0, eventItem);

                // Trim if too many items
                while (_eventLogItems.Count > MaxEventLogItems)
                {
                    _eventLogItems.RemoveAt(_eventLogItems.Count - 1);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding event log item");
            }
        });
    }

    private async void OnStartClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            AddEventLogItem("Starting server...", "#FFD43B");
            var result = await _serverManager.StartServerAsync();

            if (result.Success)
            {
                AddEventLogItem("Server started successfully", "#51CF66");
            }
            else
            {
                AddEventLogItem($"Failed to start server: {result.Message}", "#FF6B6B");
                await DialogService.ShowErrorAsync(result.Message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting server");
            AddEventLogItem($"Error starting server: {ex.Message}", "#FF6B6B");
            await DialogService.ShowErrorAsync(ex.Message);
        }
    }

    private async void OnStopClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            AddEventLogItem("Stopping server...", "#FFD43B");
            var result = await _serverManager.StopServerAsync();

            if (result.Success)
            {
                AddEventLogItem("Server stopped successfully", "#51CF66");
                _consoleBuffer.Clear();
                ConsoleOutput.Text = "Server stopped.";
            }
            else
            {
                AddEventLogItem($"Failed to stop server: {result.Message}", "#FF6B6B");
                await DialogService.ShowErrorAsync(result.Message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping server");
            AddEventLogItem($"Error stopping server: {ex.Message}", "#FF6B6B");
            await DialogService.ShowErrorAsync(ex.Message);
        }
    }

    private async void OnRestartClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            AddEventLogItem("Restarting server...", "#FFD43B");
            var result = await _serverManager.RestartServerAsync();

            if (result.Success)
            {
                AddEventLogItem("Server restarted successfully", "#51CF66");
                _consoleBuffer.Clear();
            }
            else
            {
                AddEventLogItem($"Failed to restart server: {result.Message}", "#FF6B6B");
                await DialogService.ShowErrorAsync(result.Message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error restarting server");
            AddEventLogItem($"Error restarting server: {ex.Message}", "#FF6B6B");
            await DialogService.ShowErrorAsync(ex.Message);
        }
    }

    private async void OnUpdateClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            // Check if players are online and confirm action
            if (!await ConfirmActionWithPlayersAsync("server update"))
            {
                AddEventLogItem("Server update cancelled", "#FFD43B");
                return;
            }

            // Disable update button during operation
            UpdateButton.IsEnabled = false;

            AddEventLogItem("Starting server update via SteamCmd...", "#74C0FC");
            var updateResult = await _serverManager.UpdateServerAsync();

            if (updateResult.Success)
            {
                AddEventLogItem("Server updated successfully", "#51CF66");
                _consoleBuffer.Clear();
                await DialogService.ShowSuccessAsync(
                    "Server updated and restarted successfully!",
                    "Update Complete");
            }
            else
            {
                AddEventLogItem($"Server update failed: {updateResult.Message}", "#FF6B6B");
                await DialogService.ShowErrorAsync(updateResult.Message, "Update Failed");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating server");
            AddEventLogItem($"Error updating server: {ex.Message}", "#FF6B6B");
            await DialogService.ShowErrorAsync(ex.Message);
        }
        finally
        {
            // Re-enable update button only if SteamCMD is configured
            UpdateButton.IsEnabled = IsSteamCmdConfigured();
        }
    }

    private void OnCommandKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnCommandSubmitted(sender, e);
        }
    }

    private async void OnCommandSubmitted(object sender, RoutedEventArgs e)
    {
        var command = CommandInput.Text?.Trim();
        if (string.IsNullOrEmpty(command))
            return;

        try
        {
            AddEventLogItem($"Command sent: {command}", "#FFD43B");
            var result = await _serverManager.SendCommandAsync(command);

            if (result.Success)
            {
                CommandInput.Text = string.Empty;
            }
            else
            {
                AddEventLogItem($"Failed to send command: {result.Message}", "#FF6B6B");
                await DialogService.ShowErrorAsync(result.Message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending command");
            AddEventLogItem($"Error sending command: {ex.Message}", "#FF6B6B");
            await DialogService.ShowErrorAsync(ex.Message);
        }
    }

    private void OnClearEventLogClicked(object sender, RoutedEventArgs e)
    {
        _eventLogItems.Clear();
        AddEventLogItem("Event log cleared", "#888888");
    }

    /// <summary>
    /// Checks if players are online and prompts user for confirmation if they are.
    /// </summary>
    /// <param name="actionName">The action being performed (e.g., "update", "stop", "restart")</param>
    /// <returns>True if should proceed (no players or user confirmed), false if user cancelled</returns>
    private async Task<bool> ConfirmActionWithPlayersAsync(string actionName)
    {
        var status = _serverManager.GetStatus();
        if (!status.IsRunning)
        {
            return true; // Server not running, proceed
        }

        if (!_playerTracker.HasPlayersOnline())
        {
            return true; // No players online, proceed
        }

        // Players are online - ask for confirmation
        var (onlinePlayers, bots) = _playerTracker.GetPlayerCount();
        return await DialogService.ShowConfirmationAsync(
            $"{onlinePlayers} player(s) online ({bots} bots).\n\nProceed with {actionName}?",
            "Players Online");
    }
}

public class EventLogItem
{
    public string Timestamp { get; set; } = "";
    public string Message { get; set; } = "";
    public SolidColorBrush Color { get; set; } = Brushes.White;
}
