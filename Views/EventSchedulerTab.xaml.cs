using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.Logging;
using WreckfestController.Models;
using WreckfestController.Services;

namespace WreckfestController.Views;

public partial class EventSchedulerTab : UserControl
{
    private readonly EventStorageService _eventStorage;
    private readonly SmartRestartService _smartRestartService;
    private readonly ConfigService _configService;
    private readonly WreckfestWebWebhookService _webhookService;
    private readonly ILogger<EventSchedulerTab> _logger;
    private readonly ObservableCollection<EventViewModel> _events = new();
    private Event? _selectedEvent;

    public EventSchedulerTab(
        EventStorageService eventStorage,
        SmartRestartService smartRestartService,
        ConfigService configService,
        WreckfestWebWebhookService webhookService,
        ILogger<EventSchedulerTab> logger)
    {
        InitializeComponent();

        _eventStorage = eventStorage;
        _smartRestartService = smartRestartService;
        _configService = configService;
        _webhookService = webhookService;
        _logger = logger;

        EventsDataGrid.ItemsSource = _events;

        // Initial load
        LoadUpcomingEvents();
    }

    private void LoadUpcomingEvents()
    {
        try
        {
            // Save currently selected event ID
            var selectedEventId = _selectedEvent?.Id;

            var schedule = _eventStorage.LoadSchedule();
            var allEvents = schedule.Events;

            _events.Clear();
            foreach (var evt in allEvents.OrderBy(e => e.StartTime))
            {
                _events.Add(new EventViewModel
                {
                    Id = evt.Id,
                    Name = evt.Name,
                    StartTime = evt.StartTime,
                    TrackCount = evt.Tracks?.Count ?? 0,
                    RepeatSchedule = GetRepeatDisplay(evt),
                    Event = evt
                });
            }

            _logger.LogDebug("Loaded {Count} events", _events.Count);

            // Restore selection if the event still exists
            if (selectedEventId.HasValue)
            {
                var eventToSelect = _events.FirstOrDefault(e => e.Id == selectedEventId.Value);
                if (eventToSelect != null)
                {
                    EventsDataGrid.SelectedItem = eventToSelect;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading events");
        }
    }

    private string GetRepeatDisplay(Event evt)
    {
        if (evt.Repeat == null)
            return "One-time";

        if (evt.Repeat.IsDaily)
            return $"Daily at {evt.Repeat.Time}";

        if (evt.Repeat.IsWeekly && evt.Repeat.Days != null && evt.Repeat.Days.Count > 0)
        {
            var dayNames = new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
            var days = string.Join(", ", evt.Repeat.Days.Select(d => dayNames[d]));
            return $"Weekly ({days}) at {evt.Repeat.Time}";
        }

        return "Repeating";
    }

    private void OnEventSelected(object sender, SelectionChangedEventArgs e)
    {
        if (EventsDataGrid.SelectedItem is EventViewModel selectedViewModel)
        {
            _selectedEvent = selectedViewModel.Event;
            ShowEventDetails(_selectedEvent);
            ActivateButton.IsEnabled = true;
        }
        else
        {
            _selectedEvent = null;
            HideEventDetails();
            ActivateButton.IsEnabled = false;
        }
    }

    private void ShowEventDetails(Event evt)
    {
        NoSelectionText.Visibility = Visibility.Collapsed;
        DetailsContent.Visibility = Visibility.Visible;

        EventNameText.Text = evt.Name;
        StartTimeText.Text = evt.StartTime.ToString("yyyy-MM-dd HH:mm:ss");
        ServerNameText.Text = evt.ServerConfig?.ServerName ?? "N/A";

        if (evt.Tracks != null && evt.Tracks.Count > 0)
        {
            TracksText.Text = string.Join("\n", evt.Tracks.Select((t, i) => $"{i + 1}. {t.Track}"));
        }
        else
        {
            TracksText.Text = "No tracks defined";
        }

        RecurringText.Text = GetRepeatDisplay(evt);
    }

    private void HideEventDetails()
    {
        NoSelectionText.Visibility = Visibility.Visible;
        DetailsContent.Visibility = Visibility.Collapsed;
    }

    private void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        LoadUpcomingEvents();
    }

    private async void OnActivateClicked(object sender, RoutedEventArgs e)
    {
        if (_selectedEvent == null)
            return;

        try
        {
            // Check if smart restart is already in progress
            var state = _smartRestartService.GetState();
            if (state != SmartRestartState.Idle)
            {
                await DialogService.ShowWarningAsync(
                    $"Smart restart is already in progress (State: {state}). Please wait for it to complete.",
                    "Cannot Activate");
                return;
            }

            // Confirm with user
            var result = await DialogService.ShowConfirmationAsync(
                $"This will initiate a smart restart to activate event:\n\n" +
                $"'{_selectedEvent.Name}'\n\n" +
                $"The server will send countdown warnings to players and restart at the next lobby.\n\n" +
                $"Continue?",
                "Confirm Event Activation");

            if (!result)
                return;

            _logger.LogInformation("Manually activating event: {EventName} (ID: {EventId})", _selectedEvent.Name, _selectedEvent.Id);

            // Disable button during activation
            ActivateButton.IsEnabled = false;

            // Apply event configuration BEFORE initiating restart
            var configApplied = ApplyEventConfiguration(_selectedEvent);
            if (!configApplied)
            {
                await DialogService.ShowErrorAsync("Failed to apply event configuration. Check logs for details.");
                ActivateButton.IsEnabled = true;
                return;
            }

            // Initiate smart restart for this event
            _smartRestartService.InitiateRestart(_selectedEvent, OnEventActivated);

            await DialogService.ShowSuccessAsync(
                $"Event activation initiated!\n\n" +
                $"The server will begin the countdown process and restart at the next opportunity.",
                "Activation Started");

            // Refresh the event list
            LoadUpcomingEvents();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error activating event");
            await DialogService.ShowErrorAsync($"Failed to activate event: {ex.Message}");
            ActivateButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Applies the event's server configuration (tracks and server settings)
    /// </summary>
    private bool ApplyEventConfiguration(Event @event)
    {
        try
        {
            _logger.LogInformation("Applying configuration for event: {EventName}", @event.Name);

            // Read current config
            var currentConfig = _configService.ReadBasicConfig();

            // Apply server config overrides if present
            if (@event.ServerConfig != null)
            {
                var eventConfig = @event.ServerConfig;

                if (!string.IsNullOrWhiteSpace(eventConfig.ServerName))
                    currentConfig.ServerName = eventConfig.ServerName;

                if (!string.IsNullOrWhiteSpace(eventConfig.WelcomeMessage))
                    currentConfig.WelcomeMessage = eventConfig.WelcomeMessage;

                if (eventConfig.Password != null)
                    currentConfig.Password = eventConfig.Password;

                if (eventConfig.MaxPlayers.HasValue)
                    currentConfig.MaxPlayers = eventConfig.MaxPlayers.Value;

                if (eventConfig.Bots.HasValue)
                    currentConfig.Bots = eventConfig.Bots.Value;

                if (!string.IsNullOrWhiteSpace(eventConfig.AiDifficulty))
                    currentConfig.AiDifficulty = eventConfig.AiDifficulty;

                if (eventConfig.Laps.HasValue)
                    currentConfig.Laps = eventConfig.Laps.Value;

                if (!string.IsNullOrWhiteSpace(eventConfig.VehicleDamage))
                    currentConfig.VehicleDamage = eventConfig.VehicleDamage;

                if (eventConfig.LobbyCountdown.HasValue)
                    currentConfig.LobbyCountdown = eventConfig.LobbyCountdown.Value;

                // Write updated config
                _configService.WriteBasicConfig(currentConfig);
                _logger.LogInformation("Server configuration updated");
            }

            // Apply track rotation if present
            if (@event.Tracks != null && @event.Tracks.Count > 0)
            {
                var collectionName = string.IsNullOrWhiteSpace(@event.CollectionName)
                    ? $"Event: {@event.Name}"
                    : @event.CollectionName;

                _configService.WriteEventLoopTracks(collectionName, @event.Tracks);
                _logger.LogInformation("Track rotation updated with {Count} tracks", @event.Tracks.Count);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error applying event configuration");
            return false;
        }
    }

    /// <summary>
    /// Callback invoked when the event has been activated (restart completed)
    /// </summary>
    private void OnEventActivated(Event @event)
    {
        try
        {
            _logger.LogInformation("Event {EventName} (ID {EventId}) activated successfully", @event.Name, @event.Id);

            // Mark event as active in storage
            var schedule = _eventStorage.LoadSchedule();
            var activated = schedule.ActivateEvent(@event.Id);
            if (activated)
            {
                _eventStorage.SaveSchedule(schedule);
                _logger.LogInformation("Marked event {EventName} as active in schedule", @event.Name);
            }

            // Send webhook to Laravel
            _ = _webhookService.SendEventActivatedAsync(@event.Id, @event.Name);

            // Re-enable button on UI thread
            Dispatcher.Invoke(() =>
            {
                ActivateButton.IsEnabled = true;
                LoadUpcomingEvents();
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in event activation callback");
            Dispatcher.Invoke(() => ActivateButton.IsEnabled = true);
        }
    }
}

public class EventViewModel
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public int TrackCount { get; set; }
    public string RepeatSchedule { get; set; } = string.Empty;
    public Event Event { get; set; } = null!;
}
