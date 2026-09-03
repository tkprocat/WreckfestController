using Microsoft.Extensions.Hosting;
using WreckfestController.Models;

namespace WreckfestController.Services;

/// <summary>
/// Background service that periodically checks for events that need to be activated.
/// Runs independently of WreckfestWeb and activates events at their scheduled time.
/// </summary>
public class EventSchedulerService : IHostedService, IDisposable
{
    private readonly EventStorageService _storageService;
    private readonly SmartRestartService _smartRestartService;
    private readonly RecurringEventService _recurringEventService;
    private readonly ConfigService _configService;
    private readonly WreckfestWebWebhookService _webhookService;
    private readonly ILogger<EventSchedulerService> _logger;

    private System.Threading.Timer? _timer;
    private EventSchedule? _schedule;
    private readonly object _lock = new();
    private bool _isProcessingEvent = false;

    // Configuration
    private const int CheckIntervalSeconds = 30;

    public EventSchedulerService(
        EventStorageService storageService,
        SmartRestartService smartRestartService,
        RecurringEventService recurringEventService,
        ConfigService configService,
        WreckfestWebWebhookService webhookService,
        ILogger<EventSchedulerService> logger)
    {
        _storageService = storageService;
        _smartRestartService = smartRestartService;
        _recurringEventService = recurringEventService;
        _configService = configService;
        _webhookService = webhookService;
        _logger = logger;
    }

    /// <summary>
    /// Starts the background service
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Event Scheduler Service starting");

        // Load schedule on startup
        _schedule = _storageService.LoadSchedule();
        _logger.LogInformation("Loaded schedule with {Count} events", _schedule.Events.Count);

        // Log any events that were missed while service was down
        CheckForMissedEvents();

        // Start timer (check every 30 seconds)
        _timer = new System.Threading.Timer(
            CheckForDueEvents,
            null,
            TimeSpan.Zero, // Start immediately
            TimeSpan.FromSeconds(CheckIntervalSeconds));

        _logger.LogInformation("Event Scheduler Service started. Checking every {Seconds} seconds.", CheckIntervalSeconds);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops the background service
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Event Scheduler Service stopping");

        _timer?.Change(Timeout.Infinite, 0);
        _timer?.Dispose();

        return Task.CompletedTask;
    }

    /// <summary>
    /// Checks for events that were due while the service was offline
    /// </summary>
    private void CheckForMissedEvents()
    {
        if (_schedule == null) return;

        var now = DateTime.UtcNow;
        var missedEvents = _schedule.Events
            .Where(e => !e.IsActive && e.StartTime < now.AddMinutes(-5))
            .OrderBy(e => e.StartTime)
            .ToList();

        if (missedEvents.Count > 0)
        {
            _logger.LogWarning(
                "Found {Count} events that were scheduled while service was offline (will not activate automatically):",
                missedEvents.Count);

            foreach (var evt in missedEvents)
            {
                _logger.LogWarning(
                    "  - Event {EventName} (ID {EventId}) was scheduled for {StartTime}",
                    evt.Name,
                    evt.Id,
                    evt.StartTime.ToLocalTime());
            }
        }
    }

    /// <summary>
    /// Timer callback - checks for events that need to be activated
    /// </summary>
    private void CheckForDueEvents(object? state)
    {
        lock (_lock)
        {
            // Skip if already processing an event
            if (_isProcessingEvent)
            {
                _logger.LogDebug("Skipping event check - already processing an event");
                return;
            }

            // Reload schedule from disk (in case Laravel updated it)
            _schedule = _storageService.LoadSchedule();

            if (_schedule == null || _schedule.Events.Count == 0)
            {
                return;
            }

            // Get due events
            var dueEvents = _schedule.GetDueEvents();

            if (dueEvents.Count == 0)
            {
                // Log next upcoming event if any
                var nextEvent = _schedule.GetNextUpcomingEvent();
                if (nextEvent != null)
                {
                    var timeUntil = nextEvent.StartTime - DateTime.UtcNow;
                    _logger.LogDebug(
                        "No events due. Next event: {EventName} in {Minutes:F1} minutes ({StartTime})",
                        nextEvent.Name,
                        timeUntil.TotalMinutes,
                        nextEvent.StartTime.ToLocalTime());
                }
                return;
            }

            // Process the first due event
            var eventToActivate = dueEvents.First();
            _logger.LogInformation(
                "Event {EventName} (ID {EventId}) is due for activation (scheduled: {StartTime})",
                eventToActivate.Name,
                eventToActivate.Id,
                eventToActivate.StartTime.ToLocalTime());

            _isProcessingEvent = true;

            // Activate event asynchronously
            _ = Task.Run(() => ActivateEventAsync(eventToActivate));
        }
    }

    /// <summary>
    /// Activates an event by applying configuration and initiating smart restart
    /// </summary>
    private async Task ActivateEventAsync(Event @event)
    {
        try
        {
            _logger.LogInformation("Beginning activation for event: {EventName} (ID {EventId})", @event.Name, @event.Id);

            // Apply event configuration before initiating restart
            var configApplied = await ApplyEventConfigurationAsync(@event);
            if (!configApplied)
            {
                _logger.LogError("Failed to apply event configuration for {EventName} (ID {EventId}) - aborting activation", @event.Name, @event.Id);

                lock (_lock)
                {
                    _isProcessingEvent = false;
                }

                return;
            }

            _logger.LogInformation("Configuration applied successfully for event {EventName}", @event.Name);

            // Initiate smart restart
            var restartInitiated = _smartRestartService.InitiateRestart(@event, OnEventActivated);

            if (!restartInitiated)
            {
                _logger.LogError(
                    "Failed to initiate restart for event {EventName} (ID {EventId}) - restart already in progress",
                    @event.Name,
                    @event.Id);

                lock (_lock)
                {
                    _isProcessingEvent = false;
                }

                return;
            }

            _logger.LogInformation(
                "Smart restart initiated for event {EventName} (ID {EventId})",
                @event.Name,
                @event.Id);

            // Smart restart service will handle the rest and call OnEventActivated when complete
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error activating event {EventName} (ID {EventId})", @event.Name, @event.Id);

            lock (_lock)
            {
                _isProcessingEvent = false;
            }
        }
    }

    /// <summary>
    /// Callback invoked when an event has been successfully activated
    /// </summary>
    private void OnEventActivated(Event @event)
    {
        try
        {
            _logger.LogInformation("Event {EventName} (ID {EventId}) activated successfully", @event.Name, @event.Id);

            // Reload schedule
            _schedule = _storageService.LoadSchedule();
            if (_schedule == null)
            {
                _logger.LogError("Failed to reload schedule after event activation");
                return;
            }

            // Mark event as active
            var activated = _schedule.ActivateEvent(@event.Id);
            if (!activated)
            {
                _logger.LogWarning("Event {EventName} (ID {EventId}) not found in schedule", @event.Name, @event.Id);
            }
            else
            {
                // Save updated schedule
                var saved = _storageService.SaveSchedule(_schedule);
                if (saved)
                {
                    _logger.LogInformation("Marked event {EventName} (ID {EventId}) as active in schedule", @event.Name, @event.Id);
                }
            }

            // Send webhook to Laravel
            _ = _webhookService.SendEventActivatedAsync(@event.Id, @event.Name);

            // Handle recurring events
            if (@event.Repeat != null)
            {
                _logger.LogInformation(
                    "Event {EventName} (ID {EventId}) is recurring - calculating next instance",
                    @event.Name,
                    @event.Id);

                // Reload to ensure we have latest
                _schedule = _storageService.LoadSchedule();
                if (_schedule != null)
                {
                    var eventInSchedule = _schedule.GetEventById(@event.Id);
                    if (eventInSchedule != null)
                    {
                        var rescheduled = _recurringEventService.RescheduleEvent(
                            eventInSchedule,
                            _storageService,
                            _schedule);

                        if (rescheduled)
                        {
                            _logger.LogInformation(
                                "Event {EventName} (ID {EventId}) rescheduled successfully",
                                @event.Name,
                                @event.Id);
                        }
                        else
                        {
                            _logger.LogWarning(
                                "Failed to reschedule recurring event {EventName} (ID {EventId})",
                                @event.Name,
                                @event.Id);
                        }
                    }
                }
            }

            _logger.LogInformation("Event activation complete for {EventName} (ID {EventId})", @event.Name, @event.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in OnEventActivated callback for event {EventName} (ID {EventId})", @event.Name, @event.Id);
        }
        finally
        {
            // Reset processing flag
            lock (_lock)
            {
                _isProcessingEvent = false;
            }
        }
    }

    /// <summary>
    /// Gets the current schedule summary (for monitoring/debugging)
    /// </summary>
    public (int Total, int Active, int Upcoming, int Due) GetScheduleSummary()
    {
        if (_schedule == null)
        {
            return (0, 0, 0, 0);
        }

        return _schedule.GetScheduleSummary();
    }

    /// <summary>
    /// Forces a reload of the schedule from disk (useful after Laravel pushes new schedule)
    /// </summary>
    public void ReloadSchedule()
    {
        lock (_lock)
        {
            _logger.LogInformation("Manually reloading schedule from disk");
            _schedule = _storageService.LoadSchedule();
            _logger.LogInformation("Reloaded schedule with {Count} events", _schedule?.Events.Count ?? 0);
        }
    }

    /// <summary>
    /// Applies the event's server configuration (tracks and server settings)
    /// </summary>
    private async Task<bool> ApplyEventConfigurationAsync(Event @event)
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

            return await Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error applying event configuration");
            return false;
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }
}
