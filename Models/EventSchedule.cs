using System.Text.Json.Serialization;

namespace WreckfestController.Models;

/// <summary>
/// Container for the complete event schedule received from Laravel.
/// Provides helper methods for querying events by status and time.
/// </summary>
public class EventSchedule
{
    /// <summary>
    /// List of all scheduled events
    /// </summary>
    [JsonPropertyName("events")]
    public List<Event> Events { get; set; } = new();

    /// <summary>
    /// UTC timestamp when this schedule was last updated from Laravel
    /// </summary>
    [JsonPropertyName("lastUpdated")]
    public DateTime LastUpdated { get; set; }

    /// <summary>
    /// Gets all upcoming events (not active, start time in the future)
    /// </summary>
    /// <returns>List of events ordered by start time ascending</returns>
    public List<Event> GetUpcomingEvents()
    {
        var now = DateTime.UtcNow;
        return Events
            .Where(e => !e.IsActive && e.StartTime > now)
            .OrderBy(e => e.StartTime)
            .ToList();
    }

    /// <summary>
    /// Gets all events that are due to be activated (5 minutes before start time, not yet active).
    /// This allows the smart restart service to warn players and restart at the exact scheduled time.
    /// </summary>
    /// <returns>List of events ordered by start time ascending</returns>
    public List<Event> GetDueEvents()
    {
        var now = DateTime.UtcNow;
        var activationThreshold = now.AddMinutes(5); // Activate 5 minutes early for countdown
        return Events
            .Where(e => !e.IsActive && e.StartTime <= activationThreshold)
            .OrderBy(e => e.StartTime)
            .ToList();
    }

    /// <summary>
    /// Gets the currently active event, if any
    /// </summary>
    /// <returns>The active event or null if none is active</returns>
    public Event? GetActiveEvent()
    {
        return Events.FirstOrDefault(e => e.IsActive);
    }

    /// <summary>
    /// Gets the next event that should be activated
    /// </summary>
    /// <returns>The next event or null if no events are due</returns>
    public Event? GetNextEvent()
    {
        var now = DateTime.UtcNow;
        return Events
            .Where(e => !e.IsActive && e.StartTime <= now)
            .OrderBy(e => e.StartTime)
            .FirstOrDefault();
    }

    /// <summary>
    /// Gets the next upcoming event (soonest event in the future)
    /// </summary>
    /// <returns>The next upcoming event or null if none scheduled</returns>
    public Event? GetNextUpcomingEvent()
    {
        var now = DateTime.UtcNow;
        return Events
            .Where(e => !e.IsActive && e.StartTime > now)
            .OrderBy(e => e.StartTime)
            .FirstOrDefault();
    }

    /// <summary>
    /// Finds an event by ID
    /// </summary>
    /// <param name="id">Event ID to find</param>
    /// <returns>The event or null if not found</returns>
    public Event? GetEventById(int id)
    {
        return Events.FirstOrDefault(e => e.Id == id);
    }

    /// <summary>
    /// Marks an event as active and deactivates all other events
    /// </summary>
    /// <param name="eventId">ID of the event to activate</param>
    /// <returns>True if event was found and activated, false otherwise</returns>
    public bool ActivateEvent(int eventId)
    {
        var eventToActivate = Events.FirstOrDefault(e => e.Id == eventId);
        if (eventToActivate == null)
        {
            return false;
        }

        // Deactivate all other events
        foreach (var evt in Events)
        {
            evt.IsActive = false;
        }

        // Activate the target event
        eventToActivate.IsActive = true;
        return true;
    }

    /// <summary>
    /// Deactivates all events
    /// </summary>
    public void DeactivateAllEvents()
    {
        foreach (var evt in Events)
        {
            evt.IsActive = false;
        }
    }

    /// <summary>
    /// Updates an event's start time (used for recurring events)
    /// </summary>
    /// <param name="eventId">ID of the event to update</param>
    /// <param name="newStartTime">New start time</param>
    /// <returns>True if event was found and updated, false otherwise</returns>
    public bool UpdateEventStartTime(int eventId, DateTime newStartTime)
    {
        var evt = Events.FirstOrDefault(e => e.Id == eventId);
        if (evt == null)
        {
            return false;
        }

        evt.StartTime = newStartTime;
        evt.IsActive = false; // Reset active status for next occurrence
        return true;
    }

    /// <summary>
    /// Adds or updates an event in the schedule
    /// </summary>
    /// <param name="event">Event to add or update</param>
    public void AddOrUpdateEvent(Event @event)
    {
        var existingEvent = Events.FirstOrDefault(e => e.Id == @event.Id);
        if (existingEvent != null)
        {
            Events.Remove(existingEvent);
        }
        Events.Add(@event);
    }

    /// <summary>
    /// Removes an event from the schedule
    /// </summary>
    /// <param name="eventId">ID of the event to remove</param>
    /// <returns>True if event was found and removed, false otherwise</returns>
    public bool RemoveEvent(int eventId)
    {
        var evt = Events.FirstOrDefault(e => e.Id == eventId);
        if (evt == null)
        {
            return false;
        }

        Events.Remove(evt);
        return true;
    }

    /// <summary>
    /// Gets a summary of the schedule status
    /// </summary>
    /// <returns>Tuple containing (total events, active count, upcoming count, due count)</returns>
    public (int Total, int Active, int Upcoming, int Due) GetScheduleSummary()
    {
        var now = DateTime.UtcNow;
        var activationThreshold = now.AddMinutes(5);
        return (
            Total: Events.Count,
            Active: Events.Count(e => e.IsActive),
            Upcoming: Events.Count(e => !e.IsActive && e.StartTime > activationThreshold),
            Due: Events.Count(e => !e.IsActive && e.StartTime <= activationThreshold)
        );
    }
}
