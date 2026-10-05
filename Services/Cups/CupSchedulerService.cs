using Microsoft.Extensions.Hosting;
using WreckfestController.Data.Cups;
using WreckfestController.Services.ServerControl;

namespace WreckfestController.Services.Cups;

/// <summary>
/// Starts scheduled cups. Every half minute it asks the database for the earliest
/// occurrence due within the <see cref="CupActivator.LeadIn"/> and hands it to the
/// <see cref="CupActivator"/>, one at a time.
/// </summary>
/// <remarks>
/// An occurrence more than <see cref="MissedGrace"/> overdue is recorded as
/// <see cref="OccurrenceOutcome.Missed"/>, and the cup moves on to its next occurrence.
/// Starting a two-hour-old race night by surprise helps nobody: the miss is logged and
/// sent to signed-in clients, and an admin activates the cup by hand if it should
/// still run. The grace covers waiting out another restart, which can take 15 minutes.
/// </remarks>
public class CupSchedulerService : IHostedService, IDisposable
{
    public static readonly TimeSpan MissedGrace = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

    private readonly CupStore _store;
    private readonly CupActivator _activator;
    private readonly TimeProvider _time;
    private readonly ILogger<CupSchedulerService> _logger;
    private readonly object _lock = new();

    private ITimer? _timer;
    private bool _checking;

    /// <summary>True from starting an occurrence until its restart has ended.</summary>
    private bool _activating;

    public CupSchedulerService(
        CupStore store,
        CupActivator activator,
        TimeProvider time,
        ILogger<CupSchedulerService> logger)
    {
        _store = store;
        _activator = activator;
        _time = time;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = _time.CreateTimer(_ => _ = CheckAsync(), null, TimeSpan.Zero, CheckInterval);
        _logger.LogInformation("Cup scheduler started; checking every {Seconds} seconds", CheckInterval.TotalSeconds);
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
            // Bounded, in case a write keeps failing to move a cup on.
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
            _logger.LogError(ex, "Cup scheduler check failed");
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
        // Due from the lead-in before its warmup, or before its start without one.
        var due = await _store.NextDueAsync(now + CupActivator.LeadIn);
        if (due is null)
        {
            return false;
        }

        // Someone else - a manual activation - owns this cup until its outcome is
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
            var cup = await _store.GetAsync(due.Id);
            if (cup?.NextOccurrence is not { } occurrence || occurrence != due.NextOccurrence)
            {
                return true;
            }

            return await HandleClaimedAsync(cup, occurrence, now, () => claimHeld = false);
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
    private async Task<bool> HandleClaimedAsync(Cup cup, DateTime occurrence, DateTime now, Action handOff)
    {
        if (occurrence < now - MissedGrace)
        {
            await _activator.EndOccurrenceAsync(cup, occurrence, OccurrenceOutcome.Missed);
            return true;
        }

        if (cup.IsActive)
        {
            // Still active from an earlier run (no end time): no restart, but this occurrence
            // is the run now, so its start resets the cup points as a restart would have.
            _logger.LogInformation(
                "Cup {CupName} (ID {CupId}) is already active; its {Occurrence:u} occurrence needs no restart",
                cup.Name,
                cup.Id,
                occurrence);
            // Replaces the run: not in the middle of a run step for the old one.
            await _store.RunGate.WaitAsync();
            try
            {
                await _store.SetActiveAsync(
                    cup.Id, occurrence, CupActivator.PhaseFor(occurrence), CupStore.WindowOf(cup, occurrence)?.End);
            }
            finally
            {
                _store.RunGate.Release();
            }
            await _activator.EndOccurrenceAsync(cup, occurrence, OccurrenceOutcome.Activated);
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
            result = _activator.StartOccurrence(cup, occurrence, outcome => Release(cup.Id, outcome));
        }
        catch (Exception ex)
        {
            // The settings could not be written, so nothing restarted. One attempt per
            // occurrence: retrying would fail the same way every half minute.
            _logger.LogError(ex, "Could not apply cup {CupName} (ID {CupId})", cup.Name, cup.Id);
            ReleaseActivation();

            // Claimed again, so the failure is recorded against the row as it is now.
            if (_activator.TryClaim(cup.Id))
            {
                try
                {
                    await _activator.EndOccurrenceAsync(cup, occurrence, OccurrenceOutcome.Failed);
                }
                finally
                {
                    _activator.Release(cup.Id);
                }
            }

            return true;
        }

        if (result == ActivationResult.Busy)
        {
            // Someone else's restart is running. Try again next check, until MissedGrace.
            _logger.LogInformation(
                "Cup {CupName} (ID {CupId}) is due, but a restart is in progress; will retry",
                cup.Name,
                cup.Id);
            ReleaseActivation();
        }

        return false;
    }

    private void Release(int cupId, RestartOutcome outcome)
    {
        ReleaseActivation();
        _logger.LogInformation("Restart for cup {CupId} finished with {Outcome}; scheduler released", cupId, outcome);
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
