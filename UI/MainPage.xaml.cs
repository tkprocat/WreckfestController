using System.Collections.ObjectModel;
using System.Text;
using System.Timers;
using WreckfestController.Models;
using WreckfestController.Services;
using Timer = System.Timers.Timer;

namespace WreckfestController.UI;

public partial class MainPage : ContentPage
{
    private readonly ServerManager _serverManager;
    private readonly PlayerTracker _playerTracker;
    private readonly TrackChangeTracker _trackChangeTracker;
    private readonly ILogger<MainPage> _logger;
    private readonly Timer _statusUpdateTimer;
    private readonly StringBuilder _consoleBuffer = new();
    private const int MaxConsoleLines = 1000;
    private const int MaxEventLogItems = 100;

    public MainPage(
        ServerManager serverManager,
        PlayerTracker playerTracker,
        TrackChangeTracker trackChangeTracker,
        ILogger<MainPage> logger)
    {
        InitializeComponent();

        _serverManager = serverManager;
        _playerTracker = playerTracker;
        _trackChangeTracker = trackChangeTracker;
        _logger = logger;

        // Subscribe to server events
        _serverManager.SubscribeToConsoleOutput(OnConsoleOutput);
        _playerTracker.SubscribeToPlayerTracker(OnPlayerEvent);
        _trackChangeTracker.SubscribeToTrackChange(OnTrackChangeEvent);

        // Setup status update timer
        _statusUpdateTimer = new Timer(1000); // Update every second
        _statusUpdateTimer.Elapsed += OnStatusUpdateTick;
        _statusUpdateTimer.Start();

        // Initial status update
        UpdateServerStatus();
    }

    private void OnStatusUpdateTick(object? sender, ElapsedEventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(() => UpdateServerStatus());
    }

    private void UpdateServerStatus()
    {
        var status = _serverManager.GetStatus();

        // Update server status
        ServerStatusLabel.Text = status.IsRunning ? "Running" : "Stopped";
        ServerStatusLabel.TextColor = status.IsRunning ? Color.FromArgb("#51CF66") : Color.FromArgb("#FF6B6B");

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
    }

    private void OnConsoleOutput(string output)
    {
        MainThread.BeginInvokeOnMainThread(() =>
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
                ConsoleScrollView.ScrollToAsync(ConsoleOutput, ScrollToPosition.End, false);
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
            AddEventLogItem($"Player joined: {playerEvent.Player.Name}", "#51CF66");
        }
        else if (playerEvent.EventType == "Left")
        {
            AddEventLogItem($"Player left: {playerEvent.Player.Name}", "#FF6B6B");
        }
        else if (playerEvent.EventType == "Kicked")
        {
            AddEventLogItem($"Player kicked: {playerEvent.Player.Name}", "#FFD43B");
        }
    }

    private void OnTrackChangeEvent(TrackChangeEvent trackEvent)
    {
        AddEventLogItem($"Track changed: {trackEvent.TrackId}", "#74C0FC");
    }

    private void AddEventLogItem(string message, string color)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                var timestamp = DateTime.Now.ToString("HH:mm:ss");

                // Create event item
                var eventItem = new Border
                {
                    Padding = new Thickness(8, 5),
                    Margin = new Thickness(0, 2),
                    BackgroundColor = Color.FromArgb("#0D1117"),
                    StrokeThickness = 0,
                    Content = new VerticalStackLayout
                    {
                        Spacing = 2,
                        Children =
                        {
                            new Label
                            {
                                Text = timestamp,
                                FontSize = 10,
                                TextColor = Color.FromArgb("#888888")
                            },
                            new Label
                            {
                                Text = message,
                                FontSize = 12,
                                TextColor = Color.FromArgb(color)
                            }
                        }
                    }
                };

                // Add to top of event log
                EventLogContainer.Children.Insert(0, eventItem);

                // Trim if too many items
                while (EventLogContainer.Children.Count > MaxEventLogItems)
                {
                    EventLogContainer.Children.RemoveAt(EventLogContainer.Children.Count - 1);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding event log item");
            }
        });
    }

    private async void OnStartClicked(object sender, EventArgs e)
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
                await DisplayAlert("Error", result.Message, "OK");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting server");
            AddEventLogItem($"Error starting server: {ex.Message}", "#FF6B6B");
            await DisplayAlert("Error", ex.Message, "OK");
        }
    }

    private async void OnStopClicked(object sender, EventArgs e)
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
                await DisplayAlert("Error", result.Message, "OK");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping server");
            AddEventLogItem($"Error stopping server: {ex.Message}", "#FF6B6B");
            await DisplayAlert("Error", ex.Message, "OK");
        }
    }

    private async void OnRestartClicked(object sender, EventArgs e)
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
                await DisplayAlert("Error", result.Message, "OK");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error restarting server");
            AddEventLogItem($"Error restarting server: {ex.Message}", "#FF6B6B");
            await DisplayAlert("Error", ex.Message, "OK");
        }
    }

    private async void OnCommandSubmitted(object sender, EventArgs e)
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
                await DisplayAlert("Error", result.Message, "OK");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending command");
            AddEventLogItem($"Error sending command: {ex.Message}", "#FF6B6B");
            await DisplayAlert("Error", ex.Message, "OK");
        }
    }

    private void OnClearEventLogClicked(object sender, EventArgs e)
    {
        EventLogContainer.Children.Clear();
        AddEventLogItem("Event log cleared", "#888888");
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _statusUpdateTimer?.Stop();
    }
}
