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
        if (@event.Repeat == null)
        {
            _logger.LogDebug("Event {EventName} (ID {EventId}) has no repeat schedule (single occurrence)", @event.Name, @event.Id);
            return null;
        }

        var repeat = @event.Repeat;
        var baseTime = fromTime ?? DateTime.UtcNow;

        DateTime? nextOccurrence = repeat.IsDaily
            ? CalculateDailyNextInstance(baseTime, repeat)
            : repeat.IsWeekly
                ? CalculateWeeklyNextInstance(baseTime, repeat)
                : null;

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
    private DateTime CalculateDailyNextInstance(DateTime fromTime, RepeatSchedule repeat)
    {
        // For daily events, we need to find the next occurrence at the specified time
        var today = fromTime.Date;
        var nextOccurrence = today.Add(repeat.TimeAsTimeSpan);

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
    private DateTime? CalculateWeeklyNextInstance(DateTime fromTime, RepeatSchedule repeat)
    {
        if (repeat.Days == null || repeat.Days.Count == 0)
        {
            _logger.LogWarning("Weekly recurring pattern has no days specified");
            return null;
        }

        // Sort days to make it easier to find the next occurrence
        var sortedDays = repeat.Days.OrderBy(d => d).ToList();

        var currentDay = (int)fromTime.DayOfWeek; // 0 = Sunday, 6 = Saturday
        var timeOfDay = repeat.TimeAsTimeSpan;

        // Find the next valid day
        DateTime? nextOccurrence = null;

        // First, check if there's a valid day later this week
        foreach (var day in sortedDays)
        {
            if (day > currentDay)
            {
                // This day is later in the week
                var daysUntil = day - currentDay;
                var candidate = fromTime.Date.AddDays(daysUntil).Add(timeOfDay);

                if (candidate > fromTime)
                {
                    nextOccurrence = candidate;
                    break;
                }
            }
            else if (day == currentDay)
            {
                // Same day - check if time hasn't passed yet
                var candidate = fromTime.Date.Add(timeOfDay);
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

            nextOccurrence = fromTime.Date.AddDays(daysUntilNextWeek).Add(timeOfDay);
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
        if (@event.Repeat == null)
        {
            _logger.LogWarning("Cannot reschedule event {EventName} (ID {EventId}) - no repeat schedule (single occurrence)", @event.Name, @event.Id);
            return false;
        }

        // An empty server may activate during the five-minute lead-in. Advance
        // beyond the occurrence just completed, even if its start is still future.
        var now = DateTime.UtcNow;
        var nextInstance = CalculateNextInstance(@event, @event.StartTime > now ? @event.StartTime : now);
        if (!nextInstance.HasValue)
        {
            _logger.LogInformation("Event {EventName} (ID {EventId}) will not recur (invalid pattern)", @event.Name, @event.Id);
            return false;
        }

        // Update the event in the schedule
        var updated = schedule.UpdateEventStartTime(@event.Id, nextInstance.Value);
        if (!updated)
        {
            _logger.LogError("Failed to update event {EventName} (ID {EventId}) start time in schedule", @event.Name, @event.Id);
            return false;
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
    /// Gets a human-readable description of a repeat schedule
    /// </summary>
    public string GetRepeatDescription(RepeatSchedule? repeat)
    {
        if (repeat == null)
        {
            return "Does not repeat";
        }

        if (repeat.IsDaily)
        {
            return $"Daily at {repeat.Time}";
        }

        if (repeat.IsWeekly)
        {
            return GetWeeklyDescription(repeat);
        }

        return "Unknown repeat frequency";
    }

    private string GetWeeklyDescription(RepeatSchedule repeat)
    {
        if (repeat.Days == null || repeat.Days.Count == 0)
        {
            return "Weekly (no days specified)";
        }

        var dayNames = repeat.Days
            .OrderBy(d => d)
            .Select(d => ((DayOfWeek)d).ToString().Substring(0, 3)) // Mon, Tue, etc.
            .ToList();

        var daysString = string.Join(", ", dayNames);
        return $"Weekly on {daysString} at {repeat.Time}";
    }
}
