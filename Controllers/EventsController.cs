using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WreckfestController.Data.Events;
using WreckfestController.Services;

namespace WreckfestController.Controllers;

/// <summary>
/// Scheduled events. Edits and deletes need If-Match with the version the caller read,
/// so two admins, or an admin and the WPF window, cannot silently undo each other.
/// </summary>
[ApiController]
[Authorize(Policy = ApiAuthentication.AdminPolicy)]
[Route("api/events")]
public class EventsController : ControllerBase
{
    private readonly EventStore _store;
    private readonly EventActivator _activator;
    private readonly TimeProvider _time;
    private readonly ILogger<EventsController> _logger;

    public EventsController(
        EventStore store,
        EventActivator activator,
        TimeProvider time,
        ILogger<EventsController> logger)
    {
        _store = store;
        _activator = activator;
        _time = time;
        _logger = logger;
    }

    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    /// <summary>Every event, past ones included: upcoming soonest first, then finished ones.</summary>
    [HttpGet]
    public async Task<EventListResponse> List() => ToList(await _store.ListAsync());

    [HttpGet("current")]
    public async Task<ActionResult<EventResponse>> Current()
    {
        var active = (await _store.ListAsync()).FirstOrDefault(e => e.IsActive);
        return active is null ? NoContent() : EventResponse.From(active);
    }

    /// <summary>Events whose next occurrence is past the lead-in window, soonest first.</summary>
    [HttpGet("upcoming")]
    public async Task<EventListResponse> Upcoming()
    {
        var dueBy = UtcNow + EventActivator.LeadIn;
        return ToList((await _store.ListAsync()).Where(e => e.NextOccurrence > dueBy));
    }

    /// <summary>Events the scheduler will start at its next check.</summary>
    [HttpGet("due")]
    public async Task<EventListResponse> Due()
    {
        var dueBy = UtcNow + EventActivator.LeadIn;
        return ToList((await _store.ListAsync()).Where(e => e.NextOccurrence <= dueBy));
    }

    [HttpGet("summary")]
    public async Task<EventSummaryResponse> Summary()
    {
        var events = await _store.ListAsync();
        var dueBy = UtcNow + EventActivator.LeadIn;
        return new EventSummaryResponse(
            events.Count,
            events.Count(e => e.IsActive),
            events.Count(e => e.NextOccurrence > dueBy),
            events.Count(e => e.NextOccurrence <= dueBy),
            events.Count == 0 ? null : events.Max(e => e.UpdatedAt));
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<EventResponse>> Get(int id)
    {
        var evt = await _store.GetAsync(id);
        if (evt is null)
        {
            return NotFound();
        }

        this.SetETag(evt.Version);
        return EventResponse.From(evt);
    }

    [HttpPost]
    public async Task<ActionResult<EventResponse>> Create(EventRequest request)
    {
        if (EventRules.Validate(request, out var definition) is { } error)
        {
            return this.Invalid(error.Field, error.Message);
        }

        // A signed-in user; API-key callers have no user id.
        var createdById = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var (status, evt) = await _store.CreateAsync(definition!, createdById);
        if (status == EventWriteStatus.UnknownCollection)
        {
            return UnknownCollection();
        }

        _logger.LogInformation("{Caller} created event {Name} (ID {Id})", this.Caller(), evt!.Name, evt.Id);
        this.SetETag(evt.Version);
        return CreatedAtAction(nameof(Get), new { id = evt.Id }, EventResponse.From(evt));
    }

    /// <summary>Replaces everything an admin sets. Needs If-Match; a stale one gets 409 with the current event.</summary>
    [HttpPut("{id:int}")]
    public async Task<ActionResult<EventResponse>> Update(int id, EventRequest request)
    {
        if (this.ReadIfMatch(out var expected) is { } badPrecondition)
        {
            return badPrecondition;
        }

        if (EventRules.Validate(request, out var definition) is { } error)
        {
            return this.Invalid(error.Field, error.Message);
        }

        var (status, evt) = await _store.UpdateAsync(id, definition!, expected);
        switch (status)
        {
            case EventWriteStatus.NotFound:
                return NotFound();
            case EventWriteStatus.UnknownCollection:
                return UnknownCollection();
            case EventWriteStatus.Conflict:
                return this.VersionConflict(EventResponse.From(evt!), evt!.Version);
        }

        _logger.LogInformation("{Caller} saved event {Name} (ID {Id})", this.Caller(), evt!.Name, id);
        this.SetETag(evt.Version);
        return EventResponse.From(evt);
    }

    /// <summary>Needs If-Match, like PUT: deleting an event someone just changed would lose their change.</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        if (this.ReadIfMatch(out var expected) is { } badPrecondition)
        {
            return badPrecondition;
        }

        var (status, current) = await _store.DeleteAsync(id, expected);
        switch (status)
        {
            case EventWriteStatus.NotFound:
                return NotFound();
            case EventWriteStatus.Conflict:
                return this.VersionConflict(EventResponse.From(current!), current!.Version);
        }

        _logger.LogInformation("{Caller} deleted event {Id}", this.Caller(), id);
        return NoContent();
    }

    /// <summary>
    /// Applies the event's settings and starts a smart restart; the event becomes active
    /// when the restart succeeds. 202, because that can take minutes.
    /// </summary>
    [HttpPost("{id:int}/activate")]
    public async Task<IActionResult> Activate(int id)
    {
        ActivationResult result;
        try
        {
            result = await _activator.ActivateAsync(id);
        }
        catch (Exception ex) when (ConfigWriteFailure.From(ex) is { } failure)
        {
            _logger.LogWarning(ex, "Could not activate event {Id}: {Reason}", id, failure.Reason);
            return this.Refused(failure.Message, ("reason", failure.Reason));
        }

        switch (result)
        {
            case ActivationResult.NotFound:
                return NotFound();
            case ActivationResult.AlreadyActive:
                return this.Refused("The event is already active.");
            case ActivationResult.Busy:
                return this.Refused("A server restart is already in progress. Try again when it has finished.");
        }

        _logger.LogInformation("{Caller} activated event {Id}", this.Caller(), id);
        return Accepted(new
        {
            message = "Activation started. Players are warned, and the server restarts with the event's settings.",
            eventId = id,
        });
    }

    private ActionResult UnknownCollection() =>
        this.Invalid("collectionId", "There is no collection with that id.");

    private static EventListResponse ToList(IEnumerable<ScheduledEvent> events)
    {
        var list = events.Select(EventResponse.From).ToList();
        return new EventListResponse(list.Count, list);
    }
}
