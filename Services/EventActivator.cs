using WreckfestController.Data.Events;
using WreckfestController.Models;
using WreckfestController.Services.Publishing;
using WreckfestController.Services.ServerControl;

namespace WreckfestController.Services;

public enum ActivationResult
{
    /// <summary>The smart restart has begun; the event becomes active when it succeeds.</summary>
    Started,
    NotFound,
    AlreadyActive,

    /// <summary>Another restart is in progress.</summary>
    Busy,
}

/// <summary>
/// Activates events: applies an event's settings through a smart restart and, when the
/// restart succeeds, makes it the active event. The scheduler, the API and the WPF
/// window all activate through here, so they record the outcome the same way.
/// </summary>
/// <remarks>
/// An activation stands for one occurrence at most. The scheduler passes the occurrence
/// it is starting. A manual activation takes the event's next occurrence if that is
/// within the <see cref="LeadIn"/>, because the scheduler would otherwise start the same
/// event again minutes later. Once the restart ends - succeeded, failed or cancelled -
/// the event moves past that occurrence, so a cancelled or failed occurrence is not
/// retried every half minute until someone deletes the event.
/// </remarks>
public sealed class EventActivator
{
    /// <summary>How far ahead of its start an occurrence is started, for the players' countdown.</summary>
    public static readonly TimeSpan LeadIn = TimeSpan.FromMinutes(5);

    private readonly EventStore _store;
    private readonly SmartRestartService _restart;
    private readonly IServerEventPublisher _publisher;
    private readonly TimeProvider _time;
    private readonly ILogger<EventActivator> _logger;

    /// <summary>
    /// Claimed events. Whoever acts on an event - an activation, or the scheduler
    /// recording a miss - claims it first and re-reads it afterwards. A running
    /// activation holds its claim until its outcome has been recorded, and records the
    /// outcome before releasing. So a claimer either fails to claim or sees the recorded
    /// row, never a stale one. SmartRestartService's own state is no substitute: it
    /// reports Idle before its finish callback runs.
    /// </summary>
    private readonly HashSet<int> _claimed = new();

    public EventActivator(
        EventStore store,
        SmartRestartService restart,
        IServerEventPublisher publisher,
        TimeProvider time,
        ILogger<EventActivator> logger)
    {
        _store = store;
        _restart = restart;
        _publisher = publisher;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Activates <paramref name="id"/> now, at an admin's request. Throws when the event's
    /// settings cannot be written to the server config, before any restart starts.
    /// </summary>
    /// <param name="onActivated">Called after the event has been marked active.</param>
    public async Task<ActivationResult> ActivateAsync(int id, Action<ScheduledEvent>? onActivated = null)
    {
        if (!TryClaim(id))
        {
            // This event is already being activated, or the scheduler is dealing with it.
            return ActivationResult.Busy;
        }

        ScheduledEvent? evt;
        try
        {
            // Read after claiming, so an outcome recorded just before is seen.
            evt = await _store.GetAsync(id);
        }
        catch
        {
            Release(id);
            throw;
        }

        if (evt is null)
        {
            Release(id);
            return ActivationResult.NotFound;
        }

        if (evt.IsActive)
        {
            Release(id);
            return ActivationResult.AlreadyActive;
        }

        var dueBy = _time.GetUtcNow().UtcDateTime + LeadIn;
        var occurrence = evt.NextOccurrence <= dueBy ? evt.NextOccurrence : null;
        return StartClaimed(evt, occurrence, onActivated, onFinished: null);
    }

    /// <summary>
    /// Starts the scheduled <paramref name="occurrence"/> of <paramref name="evt"/>. The
    /// caller must hold the claim (<see cref="TryClaim"/>) and have read
    /// <paramref name="evt"/> after taking it; the claim passes to the activation, which
    /// releases it once the outcome is recorded, or at once if nothing started. Throws,
    /// as <see cref="ActivateAsync"/> does, when the settings cannot be written.
    /// </summary>
    /// <param name="onFinished">Called once the restart has ended, after the event has moved on.</param>
    public ActivationResult StartOccurrence(ScheduledEvent evt, DateTime occurrence, Action<RestartOutcome> onFinished) =>
        StartClaimed(evt, occurrence, onActivated: null, onFinished);

    /// <summary>
    /// Claims <paramref name="eventId"/> for the caller. False when someone else holds it:
    /// an activation that has not finished, or the scheduler.
    /// </summary>
    public bool TryClaim(int eventId)
    {
        lock (_claimed)
        {
            return _claimed.Add(eventId);
        }
    }

    public void Release(int eventId)
    {
        lock (_claimed)
        {
            _claimed.Remove(eventId);
        }
    }

    private ActivationResult StartClaimed(
        ScheduledEvent evt,
        DateTime? occurrence,
        Action<ScheduledEvent>? onActivated,
        Action<RestartOutcome>? onFinished)
    {
        bool started;
        try
        {
            started = _restart.InitiateRestart(
                EventStore.ToRestartEvent(evt),
                _ => MarkActive(evt, onActivated),
                (_, outcome) => Finish(evt, occurrence, outcome, onFinished));
        }
        catch
        {
            Release(evt.Id);
            throw;
        }

        if (!started)
        {
            Release(evt.Id);
        }
        else
        {
            _logger.LogInformation(
                "Activating event {EventName} (ID {EventId}){Occurrence}",
                evt.Name,
                evt.Id,
                occurrence is { } at ? $" for its {at:u} occurrence" : string.Empty);
        }

        return started ? ActivationResult.Started : ActivationResult.Busy;
    }

    // SmartRestartService calls back on a pool thread and expects the work done when
    // the callback returns: the scheduler must not look for the next due event before
    // this one has been marked. Hence the blocking waits.
    private void MarkActive(ScheduledEvent evt, Action<ScheduledEvent>? onActivated)
    {
        try
        {
            if (_store.SetActiveAsync(evt.Id).GetAwaiter().GetResult())
            {
                _logger.LogInformation("Event {EventName} (ID {EventId}) is now the active event", evt.Name, evt.Id);
            }
            else
            {
                _logger.LogWarning(
                    "Event {EventName} (ID {EventId}) was deleted during its restart; its settings are applied, but no event is marked active",
                    evt.Name,
                    evt.Id);
            }

            _ = _publisher.EventActivatedAsync(evt.Id, evt.Name);
            onActivated?.Invoke(evt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not mark event {EventName} (ID {EventId}) active", evt.Name, evt.Id);
        }
    }

    /// <summary>
    /// Records how <paramref name="occurrence"/> ended and moves the event past it. Anything
    /// but <see cref="OccurrenceOutcome.Activated"/> is logged as a warning and sent to
    /// signed-in clients: the occurrence is not retried, so an admin decides whether to
    /// activate it by hand.
    /// </summary>
    public async Task EndOccurrenceAsync(ScheduledEvent evt, DateTime occurrence, OccurrenceOutcome outcome)
    {
        if (!await _store.AdvanceAsync(evt.Id, occurrence, outcome))
        {
            // Rescheduled or deleted meanwhile: the admin's change stands.
            return;
        }

        if (outcome != OccurrenceOutcome.Activated)
        {
            _logger.LogWarning(
                "The {Occurrence:u} occurrence of event {EventName} (ID {EventId}) was {Outcome} and will not be retried; activate it by hand if it should still run",
                occurrence,
                evt.Name,
                evt.Id,
                outcome);
        }

        _ = _publisher.EventOccurrenceEndedAsync(evt.Id, evt.Name, occurrence, outcome);
    }

    private void Finish(ScheduledEvent evt, DateTime? occurrence, RestartOutcome outcome, Action<RestartOutcome>? onFinished)
    {
        try
        {
            if (occurrence is { } at)
            {
                var ended = outcome switch
                {
                    RestartOutcome.Succeeded => OccurrenceOutcome.Activated,
                    RestartOutcome.Cancelled => OccurrenceOutcome.Cancelled,
                    _ => OccurrenceOutcome.Failed,
                };
                EndOccurrenceAsync(evt, at, ended).GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not move event {EventName} (ID {EventId}) past its occurrence", evt.Name, evt.Id);
        }
        finally
        {
            // After the outcome is recorded, so the next claimer reads the recorded row.
            Release(evt.Id);
            onFinished?.Invoke(outcome);
        }
    }
}
