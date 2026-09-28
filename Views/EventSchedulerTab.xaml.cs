using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.Logging;
using WreckfestController.Data.Events;
using WreckfestController.Services;

namespace WreckfestController.Views;

/// <summary>
/// Lists scheduled events and activates one on demand. Events are created and edited in
/// the web UI; activation goes through the same <see cref="EventActivator"/> as the API
/// and the scheduler.
/// </summary>
public partial class EventSchedulerTab : UserControl
{
    private readonly EventStore _store;
    private readonly EventActivator _activator;
    private readonly ILogger<EventSchedulerTab> _logger;
    private readonly ObservableCollection<EventViewModel> _events = new();
    private ScheduledEvent? _selectedEvent;

    public EventSchedulerTab(
        EventStore store,
        EventActivator activator,
        ILogger<EventSchedulerTab> logger)
    {
        InitializeComponent();

        _store = store;
        _activator = activator;
        _logger = logger;

        EventsDataGrid.ItemsSource = _events;

        _ = LoadEventsAsync();
    }

    private async Task LoadEventsAsync()
    {
        try
        {
            var selectedEventId = _selectedEvent?.Id;
            var events = await _store.ListAsync();

            _events.Clear();
            foreach (var evt in events)
            {
                _events.Add(new EventViewModel(evt));
            }

            _logger.LogDebug("Loaded {Count} events", _events.Count);

            if (selectedEventId is { } id && _events.FirstOrDefault(e => e.Event.Id == id) is { } reselect)
            {
                EventsDataGrid.SelectedItem = reselect;
            }
        }
        catch (Exception ex)
        {
            // Recovery mode: the database banner already says why.
            _logger.LogError(ex, "Could not load events");
        }
    }

    private void OnEventSelected(object sender, SelectionChangedEventArgs e)
    {
        if (EventsDataGrid.SelectedItem is EventViewModel selected)
        {
            _selectedEvent = selected.Event;
            ShowEventDetails(selected);
            ActivateButton.IsEnabled = true;
        }
        else
        {
            _selectedEvent = null;
            HideEventDetails();
            ActivateButton.IsEnabled = false;
        }
    }

    private void ShowEventDetails(EventViewModel selected)
    {
        NoSelectionText.Visibility = Visibility.Collapsed;
        DetailsContent.Visibility = Visibility.Visible;

        var evt = selected.Event;
        EventNameText.Text = evt.IsActive ? $"{evt.Name} (active)" : evt.Name;
        StartTimeText.Text = selected.Next;
        ServerNameText.Text = evt.ServerConfig?.ServerName ?? "N/A";

        var deployed = EventStore.ToRestartEvent(evt);
        TracksText.Text = deployed.Tracks.Count > 0
            ? string.Join("\n", deployed.Tracks.Select((t, i) => $"{i + 1}. {t.Track}"))
            : "No tracks defined";

        RecurringText.Text = selected.RepeatSchedule;
    }

    private void HideEventDetails()
    {
        NoSelectionText.Visibility = Visibility.Visible;
        DetailsContent.Visibility = Visibility.Collapsed;
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e) => await LoadEventsAsync();

    private async void OnActivateClicked(object sender, RoutedEventArgs e)
    {
        var eventToActivate = _selectedEvent;
        if (eventToActivate == null)
            return;

        try
        {
            var confirmed = await DialogService.ShowConfirmationAsync(
                $"This will initiate a smart restart to activate event:\n\n" +
                $"'{eventToActivate.Name}'\n\n" +
                $"The server will send countdown warnings to players and restart at the next lobby.\n\n" +
                $"Continue?",
                "Confirm Event Activation");

            if (!confirmed)
                return;

            ActivateButton.IsEnabled = false;
            _logger.LogInformation("Manually activating event: {EventName} (ID: {EventId})", eventToActivate.Name, eventToActivate.Id);

            var result = await _activator.ActivateAsync(
                eventToActivate.Id,
                _ => Dispatcher.InvokeAsync(LoadEventsAsync));

            switch (result)
            {
                case ActivationResult.Started:
                    await DialogService.ShowSuccessAsync(
                        "Event activation initiated!\n\n" +
                        "The server will begin the countdown process and restart at the next opportunity.",
                        "Activation Started");
                    break;
                case ActivationResult.Busy:
                    await DialogService.ShowWarningAsync(
                        "A server restart is already in progress. Please wait for it to complete.",
                        "Cannot Activate");
                    break;
                case ActivationResult.AlreadyActive:
                    await DialogService.ShowWarningAsync($"'{eventToActivate.Name}' is already active.", "Cannot Activate");
                    break;
                case ActivationResult.NotFound:
                    await DialogService.ShowWarningAsync($"'{eventToActivate.Name}' has been deleted.", "Cannot Activate");
                    break;
            }

            await LoadEventsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error activating event");
            await DialogService.ShowErrorAsync($"Failed to activate event: {ex.Message}");
        }
        finally
        {
            ActivateButton.IsEnabled = _selectedEvent != null;
        }
    }
}

public class EventViewModel
{
    public EventViewModel(ScheduledEvent evt)
    {
        Event = evt;
        Name = evt.IsActive ? $"{evt.Name} (active)" : evt.Name;
        Next = evt.NextOccurrence is { } next ? next.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "Finished";
        Last = evt.LastOccurrence is { } last
            ? $"{evt.LastOutcome} {last.ToLocalTime():yyyy-MM-dd HH:mm}"
            : "-";
        TrackCount = EventStore.ToRestartEvent(evt).Tracks.Count;
        RepeatSchedule = evt.Repeat is null
            ? "One-time"
            : $"{EventRecurrence.Describe(evt.Repeat)} ({evt.TimeZone})";
    }

    public ScheduledEvent Event { get; }
    public string Name { get; }

    /// <summary>The next occurrence in this machine's local time.</summary>
    public string Next { get; }

    /// <summary>
    /// How the last occurrence ended, such as "Missed 2026-10-02 20:00". A missed, failed
    /// or cancelled occurrence is not retried; activate the event to run it anyway.
    /// </summary>
    public string Last { get; }

    public int TrackCount { get; }
    public string RepeatSchedule { get; }
}
