using Microsoft.Extensions.Hosting;
using WreckfestController.Data.Events;

namespace WreckfestController.Services;

/// <summary>
/// Starts scheduled events. Every half minute it asks the database for the earliest
/// occurrence due within the <see cref="EventActivator.LeadIn"/> and hands it to the
/// <see cref="EventActivator"/>, one at a time.
/// </summary>
/// <remarks>
/// An occurrence more than <see cref="MissedGrace"/> overdue is recorded as
/// <see cref="OccurrenceOutcome.Missed"/>, and the event moves on to its next occurrence.
/// Starting a two-hour-old race night by surprise helps nobody: the miss is logged and
/// sent to signed-in clients, and an admin activates the event by hand if it should
/// still run. The grace covers waiting out another restart, which can take 15 minutes.
/// </remarks>
public class EventSchedulerService : IHostedService, IDisposable
{
    public static readonly TimeSpan MissedGrace = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

    private readonly EventStore _store;
    private readonly EventActivator _activator;
    private readonly TimeProvider _time;
    private readonly ILogger<EventSchedulerService> _logger;
    private readonly object _lock = new();

    private ITimer? _timer;
    private bool _checking;

    /// <summary>True from starting an occurrence until its restart has ended.</summary>
    private bool _activating;

    public EventSchedulerService(
        EventStore store,
        EventActivator activator,
        TimeProvider time,
        ILogger<EventSchedulerService> logger)
    {
        _store = store;
        _activator = activator;
        _time = time;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = _time.CreateTimer(_ => _ = CheckAsync(), null, TimeSpan.Zero, CheckInterval);
        _logger.LogInformation("Event scheduler started; checking every {Seconds} seconds", CheckInterval.TotalSeconds);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Deals with whatever is due: skips missed occurrences and starts the earliest due
    /// one. Does nothing while an earlier check or activation is still running.
    /// </summary>
    public async Task CheckAsync()
    {
        lock (_lock)
        {
            if (_checking || _activating)
            {
                return;
            }

            _checking = true;
        }

        try
        {
            // Bounded, in case a write keeps failing to move an event on.
            for (var i = 0; i < 100; i++)
            {
                if (!await HandleNextDueAsync())
                {
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Event scheduler check failed");
        }
        finally
        {
            lock (_lock)
            {
                _checking = false;
            }
        }
    }

    /// <summary>True when it dealt with an occurrence without starting a restart, so there may be another.</summary>
    private async Task<bool> HandleNextDueAsync()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var due = await _store.NextDueAsync(now + EventActivator.LeadIn);
        if (due is null)
        {
            return false;
        }

        // Someone else - a manual activation - owns this event until its outcome is
        // recorded. Counting it missed, or starting it again, would be wrong either way.
        // Stop the scan: while its restart runs, nothing else could start anyway.
        if (!_activator.TryClaim(due.Id))
        {
            return false;
        }

        var claimHeld = true;
        try
        {
            // Re-read under the claim: what was due a moment ago may have been dealt with.
            var evt = await _store.GetAsync(due.Id);
            if (evt?.NextOccurrence is not { } occurrence || occurrence != due.NextOccurrence)
            {
                return true;
            }

            return await HandleClaimedAsync(evt, occurrence, now, () => claimHeld = false);
        }
        finally
        {
            if (claimHeld)
            {
                _activator.Release(due.Id);
            }
        }
    }

    /// <param name="handOff">Called when the claim passes to a started activation.</param>
    private async Task<bool> HandleClaimedAsync(ScheduledEvent evt, DateTime occurrence, DateTime now, Action handOff)
    {
        if (occurrence < now - MissedGrace)
        {
            await _activator.EndOccurrenceAsync(evt, occurrence, OccurrenceOutcome.Missed);
            return true;
        }

        if (evt.IsActive)
        {
            _logger.LogInformation(
                "Event {EventName} (ID {EventId}) is already active; its {Occurrence:u} occurrence needs no restart",
                evt.Name,
                evt.Id,
                occurrence);
            await _activator.EndOccurrenceAsync(evt, occurrence, OccurrenceOutcome.Activated);
            return true;
        }

        lock (_lock)
        {
            _activating = true;
        }

        ActivationResult result;
        try
        {
            // The activation takes the claim whatever happens: it releases it itself.
            handOff();
            result = _activator.StartOccurrence(evt, occurrence, outcome => Release(evt.Id, outcome));
        }
        catch (Exception ex)
        {
            // The settings could not be written, so nothing restarted. One attempt per
            // occurrence: retrying would fail the same way every half minute.
            _logger.LogError(ex, "Could not apply event {EventName} (ID {EventId})", evt.Name, evt.Id);
            ReleaseActivation();

            // Claimed again, so the failure is recorded against the row as it is now.
            if (_activator.TryClaim(evt.Id))
            {
                try
                {
                    await _activator.EndOccurrenceAsync(evt, occurrence, OccurrenceOutcome.Failed);
                }
                finally
                {
                    _activator.Release(evt.Id);
                }
            }

            return true;
        }

        if (result == ActivationResult.Busy)
        {
            // Someone else's restart is running. Try again next check, until MissedGrace.
            _logger.LogInformation(
                "Event {EventName} (ID {EventId}) is due, but a restart is in progress; will retry",
                evt.Name,
                evt.Id);
            ReleaseActivation();
        }

        return false;
    }

    private void Release(int eventId, RestartOutcome outcome)
    {
        ReleaseActivation();
        _logger.LogInformation("Restart for event {EventId} finished with {Outcome}; scheduler released", eventId, outcome);
    }

    private void ReleaseActivation()
    {
        lock (_lock)
        {
            _activating = false;
        }
    }

    public void Dispose() => _timer?.Dispose();
}
