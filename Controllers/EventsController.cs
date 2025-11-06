using Microsoft.AspNetCore.Mvc;
using WreckfestController.Models;
using WreckfestController.Services;

namespace WreckfestController.Controllers;

/// <summary>
/// API controller for managing scheduled server events
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class EventsController : ControllerBase
{
    private readonly EventStorageService _storageService;
    private readonly SmartRestartService _smartRestartService;
    private readonly LaravelWebhookService _webhookService;
    private readonly ILogger<EventsController> _logger;

    public EventsController(
        EventStorageService storageService,
        SmartRestartService smartRestartService,
        LaravelWebhookService webhookService,
        ILogger<EventsController> logger)
    {
        _storageService = storageService;
        _smartRestartService = smartRestartService;
        _webhookService = webhookService;
        _logger = logger;
    }

    /// <summary>
    /// Receives the complete event schedule from Laravel and replaces the existing schedule
    /// </summary>
    /// <param name="request">Request containing list of events</param>
    /// <returns>Success or error message</returns>
    [HttpPost("schedule")]
    public IActionResult UpdateSchedule([FromBody] EventScheduleRequest request)
    {
        if (request?.Events == null)
        {
            _logger.LogWarning("Received null or invalid schedule update request");
            return BadRequest(new { message = "Invalid request: events list is required" });
        }

        _logger.LogInformation("Received schedule update with {Count} events from Laravel", request.Events.Count);

        // Validate events
        var validationErrors = ValidateEvents(request.Events);
        if (validationErrors.Count > 0)
        {
            _logger.LogWarning("Schedule validation failed with {Count} errors", validationErrors.Count);
            return BadRequest(new
            {
                message = "Schedule validation failed",
                errors = validationErrors
            });
        }

        // Save schedule
        var success = _storageService.ReplaceSchedule(request.Events);

        if (success)
        {
            _logger.LogInformation("Successfully saved schedule with {Count} events", request.Events.Count);
            return Ok(new
            {
                message = "Event schedule updated successfully",
                eventsReceived = request.Events.Count
            });
        }

        _logger.LogError("Failed to save event schedule");
        return StatusCode(500, new { message = "Failed to save event schedule" });
    }

    /// <summary>
    /// Gets the currently active event
    /// </summary>
    /// <returns>The active event or null if none is active</returns>
    [HttpGet("current")]
    public IActionResult GetCurrentEvent()
    {
        try
        {
            var schedule = _storageService.LoadSchedule();
            var activeEvent = schedule.GetActiveEvent();

            if (activeEvent == null)
            {
                _logger.LogDebug("No active event found");
                return Ok(new { activeEvent = (Event?)null });
            }

            _logger.LogDebug("Active event: {EventName} (ID: {EventId})", activeEvent.Name, activeEvent.Id);
            return Ok(activeEvent);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving current event");
            return StatusCode(500, new { message = "Error retrieving current event" });
        }
    }

    /// <summary>
    /// Gets all upcoming events (not active, scheduled for the future)
    /// </summary>
    /// <returns>List of upcoming events ordered by start time</returns>
    [HttpGet("upcoming")]
    public IActionResult GetUpcomingEvents()
    {
        try
        {
            var schedule = _storageService.LoadSchedule();
            var upcomingEvents = schedule.GetUpcomingEvents();

            _logger.LogDebug("Found {Count} upcoming events", upcomingEvents.Count);

            var now = DateTime.UtcNow;
            var eventsWithStartsIn = upcomingEvents.Select(e => new
            {
                e.Id,
                e.Name,
                e.Description,
                e.StartTime,
                e.IsActive,
                e.ServerConfig,
                e.Tracks,
                e.CollectionName,
                e.RecurringPattern,
                StartsIn = FormatTimeUntil(e.StartTime, now),
                StartsInMinutes = (e.StartTime - now).TotalMinutes
            }).ToList();

            return Ok(new
            {
                count = eventsWithStartsIn.Count,
                events = eventsWithStartsIn
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving upcoming events");
            return StatusCode(500, new { message = "Error retrieving upcoming events" });
        }
    }

    /// <summary>
    /// Gets events that are due to be activated (start time has passed but not yet active)
    /// </summary>
    /// <returns>List of due events</returns>
    [HttpGet("due")]
    public IActionResult GetDueEvents()
    {
        try
        {
            var schedule = _storageService.LoadSchedule();
            var dueEvents = schedule.GetDueEvents();

            _logger.LogDebug("Found {Count} due events", dueEvents.Count);

            return Ok(new
            {
                count = dueEvents.Count,
                events = dueEvents
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving due events");
            return StatusCode(500, new { message = "Error retrieving due events" });
        }
    }

    /// <summary>
    /// Gets a summary of the event schedule status
    /// </summary>
    /// <returns>Schedule summary with counts</returns>
    [HttpGet("summary")]
    public IActionResult GetScheduleSummary()
    {
        try
        {
            var schedule = _storageService.LoadSchedule();
            var summary = schedule.GetScheduleSummary();

            return Ok(new
            {
                totalEvents = summary.Total,
                activeEvents = summary.Active,
                upcomingEvents = summary.Upcoming,
                dueEvents = summary.Due,
                lastUpdated = schedule.LastUpdated
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving schedule summary");
            return StatusCode(500, new { message = "Error retrieving schedule summary" });
        }
    }

    /// <summary>
    /// Manually activates a specific event by ID.
    /// Finds the event, applies its configuration (server name, welcome message, track rotation),
    /// marks it as active, and calls back to Laravel webhook.
    /// </summary>
    /// <param name="id">Event ID to activate</param>
    /// <returns>Success or error message</returns>
    [HttpPost("{id}/activate")]
    public async Task<IActionResult> ActivateEvent(int id)
    {
        _logger.LogInformation("Received manual activation request for event ID {EventId}", id);

        try
        {
            // 1. Find the event by ID from the schedule
            var schedule = _storageService.LoadSchedule();
            var eventToActivate = schedule.GetEventById(id);

            if (eventToActivate == null)
            {
                _logger.LogWarning("Event ID {EventId} not found in schedule", id);
                return NotFound(new { message = $"Event with ID {id} not found in schedule" });
            }

            if (eventToActivate.IsActive)
            {
                _logger.LogWarning("Event ID {EventId} is already active", id);
                return BadRequest(new { message = $"Event '{eventToActivate.Name}' is already active" });
            }

            _logger.LogInformation(
                "Activating event: {EventName} (ID: {EventId})",
                eventToActivate.Name,
                eventToActivate.Id);

            // 2. Initiate smart restart to apply the event's configuration
            var restartInitiated = _smartRestartService.InitiateRestart(
                eventToActivate,
                OnManualEventActivated);

            if (!restartInitiated)
            {
                _logger.LogError(
                    "Failed to initiate restart for event {EventName} (ID {EventId}) - restart already in progress",
                    eventToActivate.Name,
                    eventToActivate.Id);

                return Conflict(new
                {
                    message = "A server restart is already in progress. Please wait for it to complete.",
                    eventId = id,
                    eventName = eventToActivate.Name
                });
            }

            _logger.LogInformation(
                "Smart restart initiated for event {EventName} (ID {EventId}). Configuration will be applied and server will restart.",
                eventToActivate.Name,
                eventToActivate.Id);

            return Ok(new
            {
                message = "Event activation initiated. Server will restart with new configuration.",
                eventId = id,
                eventName = eventToActivate.Name,
                note = "The server will warn players and restart gracefully. Configuration will be applied automatically."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error activating event ID {EventId}", id);
            return StatusCode(500, new { message = "Error activating event", error = ex.Message });
        }
    }

    /// <summary>
    /// Callback invoked when a manually activated event completes activation.
    /// Marks the event as active in the schedule and sends webhook to Laravel.
    /// </summary>
    private void OnManualEventActivated(Event @event)
    {
        try
        {
            _logger.LogInformation(
                "Manual event activation completed for {EventName} (ID: {EventId})",
                @event.Name,
                @event.Id);

            // 3. Mark the event as active in the schedule
            var schedule = _storageService.LoadSchedule();
            var activated = schedule.ActivateEvent(@event.Id);

            if (!activated)
            {
                _logger.LogWarning(
                    "Event {EventName} (ID {EventId}) not found in schedule when marking as active",
                    @event.Name,
                    @event.Id);
            }
            else
            {
                // Save updated schedule with active flag
                var saved = _storageService.SaveSchedule(schedule);
                if (saved)
                {
                    _logger.LogInformation(
                        "Marked event {EventName} (ID {EventId}) as active in schedule",
                        @event.Name,
                        @event.Id);
                }
                else
                {
                    _logger.LogError(
                        "Failed to save schedule after marking event {EventName} (ID {EventId}) as active",
                        @event.Name,
                        @event.Id);
                }
            }

            // 4. Call back to Laravel webhook
            _ = _webhookService.SendEventActivatedAsync(@event.Id, @event.Name);

            _logger.LogInformation(
                "Manual event activation workflow completed for {EventName} (ID {EventId})",
                @event.Name,
                @event.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error in manual event activation callback for {EventName} (ID {EventId})",
                @event.Name,
                @event.Id);
        }
    }

    /// <summary>
    /// Gets a specific event by ID
    /// </summary>
    /// <param name="id">Event ID</param>
    /// <returns>The event or 404 if not found</returns>
    [HttpGet("{id}")]
    public IActionResult GetEventById(int id)
    {
        try
        {
            var schedule = _storageService.LoadSchedule();
            var evt = schedule.GetEventById(id);

            if (evt == null)
            {
                return NotFound(new { message = $"Event with ID {id} not found" });
            }

            return Ok(evt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving event ID {EventId}", id);
            return StatusCode(500, new { message = "Error retrieving event" });
        }
    }

    /// <summary>
    /// Validates a list of events for common errors
    /// </summary>
    private List<string> ValidateEvents(List<Event> events)
    {
        var errors = new List<string>();

        for (int i = 0; i < events.Count; i++)
        {
            var evt = events[i];

            if (evt.Id <= 0)
            {
                errors.Add($"Event {i}: ID must be greater than 0");
            }

            if (string.IsNullOrWhiteSpace(evt.Name))
            {
                errors.Add($"Event {i} (ID {evt.Id}): Name is required");
            }

            if (evt.StartTime == default)
            {
                errors.Add($"Event {i} (ID {evt.Id}): StartTime is required");
            }

            // Validate tracks if present
            if (evt.Tracks != null && evt.Tracks.Count > 0)
            {
                for (int j = 0; j < evt.Tracks.Count; j++)
                {
                    var track = evt.Tracks[j];
                    if (string.IsNullOrWhiteSpace(track.Track))
                    {
                        errors.Add($"Event {i} (ID {evt.Id}), Track {j}: Track path is required");
                    }
                }
            }

            // Validate recurring pattern if present
            if (evt.RecurringPattern != null)
            {
                if (evt.RecurringPattern.Type == RecurringType.Weekly &&
                    (evt.RecurringPattern.Days == null || evt.RecurringPattern.Days.Count == 0))
                {
                    errors.Add($"Event {i} (ID {evt.Id}): Weekly recurring events must specify at least one day");
                }
            }
        }

        return errors;
    }

    /// <summary>
    /// Formats the time until an event starts into a human-readable string
    /// </summary>
    private static string FormatTimeUntil(DateTime eventTime, DateTime now)
    {
        var timeSpan = eventTime - now;

        if (timeSpan.TotalSeconds < 0)
            return "overdue";

        if (timeSpan.TotalDays >= 1)
        {
            var days = (int)timeSpan.TotalDays;
            var hours = timeSpan.Hours;
            return hours > 0 ? $"{days}d {hours}h" : $"{days}d";
        }

        if (timeSpan.TotalHours >= 1)
        {
            var hours = (int)timeSpan.TotalHours;
            var minutes = timeSpan.Minutes;
            return minutes > 0 ? $"{hours}h {minutes}m" : $"{hours}h";
        }

        if (timeSpan.TotalMinutes >= 1)
        {
            var minutes = (int)timeSpan.TotalMinutes;
            var seconds = timeSpan.Seconds;
            return seconds > 0 ? $"{minutes}m {seconds}s" : $"{minutes}m";
        }

        return $"{(int)timeSpan.TotalSeconds}s";
    }
}

/// <summary>
/// Request model for updating the event schedule
/// </summary>
public class EventScheduleRequest
{
    public List<Event> Events { get; set; } = new();
}
