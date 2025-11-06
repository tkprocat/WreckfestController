using WreckfestController.Models;

namespace WreckfestController.Services;

/// <summary>
/// Service responsible for calculating next instances of recurring events
/// </summary>
public class RecurringEventService
{
    private readonly ILogger<RecurringEventService> _logger;

    public RecurringEventService(ILogger<RecurringEventService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Calculates the next occurrence of a recurring event
    /// </summary>
    /// <param name="event">The event with recurring pattern</param>
    /// <param name="fromTime">Calculate from this time (defaults to now)</param>
    /// <returns>The next occurrence timestamp, or null if event doesn't recur</returns>
    public DateTime? CalculateNextInstance(Event @event, DateTime? fromTime = null)
    {
        if (@event.RecurringPattern == null)
        {
            _logger.LogDebug("Event {EventName} (ID {EventId}) has no recurring pattern", @event.Name, @event.Id);
            return null;
        }

        var pattern = @event.RecurringPattern;

        // Check if occurrences limit is reached
        if (pattern.Occurrences.HasValue && pattern.Occurrences.Value <= 0)
        {
            _logger.LogInformation("Event {EventName} (ID {EventId}) has reached occurrence limit", @event.Name, @event.Id);
            return null;
        }

        var baseTime = fromTime ?? DateTime.UtcNow;

        DateTime? nextOccurrence = pattern.Type switch
        {
            RecurringType.Daily => CalculateDailyNextInstance(baseTime, pattern),
            RecurringType.Weekly => CalculateWeeklyNextInstance(baseTime, pattern),
            _ => null
        };

        if (nextOccurrence.HasValue)
        {
            _logger.LogInformation(
                "Calculated next instance for event {EventName} (ID {EventId}): {NextTime}",
                @event.Name,
                @event.Id,
                nextOccurrence.Value);
        }
        else
        {
            _logger.LogWarning(
                "Could not calculate next instance for event {EventName} (ID {EventId})",
                @event.Name,
                @event.Id);
        }

        return nextOccurrence;
    }

    /// <summary>
    /// Calculates next daily occurrence
    /// </summary>
    private DateTime CalculateDailyNextInstance(DateTime fromTime, RecurringPattern pattern)
    {
        // For daily events, we need to find the next occurrence at the specified time
        var today = fromTime.Date;
        var nextOccurrence = today.Add(pattern.Time);

        // If the time today has already passed, move to tomorrow
        if (nextOccurrence <= fromTime)
        {
            nextOccurrence = nextOccurrence.AddDays(1);
        }

        _logger.LogDebug("Daily event next occurrence: {Time}", nextOccurrence);
        return nextOccurrence;
    }

    /// <summary>
    /// Calculates next weekly occurrence
    /// </summary>
    private DateTime? CalculateWeeklyNextInstance(DateTime fromTime, RecurringPattern pattern)
    {
        if (pattern.Days == null || pattern.Days.Count == 0)
        {
            _logger.LogWarning("Weekly recurring pattern has no days specified");
            return null;
        }

        // Sort days to make it easier to find the next occurrence
        var sortedDays = pattern.Days.OrderBy(d => d).ToList();

        var currentDay = (int)fromTime.DayOfWeek; // 0 = Sunday, 6 = Saturday
        var currentTime = fromTime.TimeOfDay;

        // Find the next valid day
        DateTime? nextOccurrence = null;

        // First, check if there's a valid day later this week
        foreach (var day in sortedDays)
        {
            if (day > currentDay)
            {
                // This day is later in the week
                var daysUntil = day - currentDay;
                var candidate = fromTime.Date.AddDays(daysUntil).Add(pattern.Time);

                if (candidate > fromTime)
                {
                    nextOccurrence = candidate;
                    break;
                }
            }
            else if (day == currentDay)
            {
                // Same day - check if time hasn't passed yet
                var candidate = fromTime.Date.Add(pattern.Time);
                if (candidate > fromTime)
                {
                    nextOccurrence = candidate;
                    break;
                }
            }
        }

        // If no valid day found this week, go to next week
        if (!nextOccurrence.HasValue)
        {
            var firstDay = sortedDays[0];
            var daysUntilNextWeek = (7 - currentDay + firstDay) % 7;
            if (daysUntilNextWeek == 0)
            {
                daysUntilNextWeek = 7; // Full week ahead
            }

            nextOccurrence = fromTime.Date.AddDays(daysUntilNextWeek).Add(pattern.Time);
        }

        _logger.LogDebug(
            "Weekly event (days: {Days}) next occurrence: {Time}",
            string.Join(", ", sortedDays.Select(d => ((DayOfWeek)d).ToString())),
            nextOccurrence);

        return nextOccurrence;
    }

    /// <summary>
    /// Updates an event with its next occurrence and saves to storage
    /// </summary>
    /// <param name="event">The event to reschedule</param>
    /// <param name="storageService">Storage service to save the updated schedule</param>
    /// <param name="schedule">The current schedule</param>
    /// <returns>True if rescheduling was successful</returns>
    public bool RescheduleEvent(Event @event, EventStorageService storageService, EventSchedule schedule)
    {
        if (@event.RecurringPattern == null)
        {
            _logger.LogWarning("Cannot reschedule event {EventName} (ID {EventId}) - no recurring pattern", @event.Name, @event.Id);
            return false;
        }

        var nextInstance = CalculateNextInstance(@event, DateTime.UtcNow);
        if (!nextInstance.HasValue)
        {
            _logger.LogInformation("Event {EventName} (ID {EventId}) will not recur (reached limit or invalid pattern)", @event.Name, @event.Id);
            return false;
        }

        // Update the event in the schedule
        var updated = schedule.UpdateEventStartTime(@event.Id, nextInstance.Value);
        if (!updated)
        {
            _logger.LogError("Failed to update event {EventName} (ID {EventId}) start time in schedule", @event.Name, @event.Id);
            return false;
        }

        // Decrement occurrences if limit is set
        if (@event.RecurringPattern.Occurrences.HasValue)
        {
            @event.RecurringPattern.Occurrences--;
            _logger.LogDebug(
                "Event {EventName} (ID {EventId}) has {Remaining} occurrences remaining",
                @event.Name,
                @event.Id,
                @event.RecurringPattern.Occurrences);
        }

        // Save the updated schedule
        var saved = storageService.SaveSchedule(schedule);
        if (!saved)
        {
            _logger.LogError("Failed to save rescheduled event {EventName} (ID {EventId})", @event.Name, @event.Id);
            return false;
        }

        _logger.LogInformation(
            "Rescheduled event {EventName} (ID {EventId}) to {NextTime}",
            @event.Name,
            @event.Id,
            nextInstance.Value);

        return true;
    }

    /// <summary>
    /// Gets a human-readable description of a recurring pattern
    /// </summary>
    public string GetRecurringDescription(RecurringPattern pattern)
    {
        if (pattern == null)
        {
            return "Does not recur";
        }

        return pattern.Type switch
        {
            RecurringType.Daily => $"Daily at {pattern.Time:hh\\:mm}",
            RecurringType.Weekly => GetWeeklyDescription(pattern),
            _ => "Unknown recurrence type"
        };
    }

    private string GetWeeklyDescription(RecurringPattern pattern)
    {
        if (pattern.Days == null || pattern.Days.Count == 0)
        {
            return "Weekly (no days specified)";
        }

        var dayNames = pattern.Days
            .OrderBy(d => d)
            .Select(d => ((DayOfWeek)d).ToString().Substring(0, 3)) // Mon, Tue, etc.
            .ToList();

        var daysString = string.Join(", ", dayNames);
        return $"Weekly on {daysString} at {pattern.Time:hh\\:mm}";
    }
}
