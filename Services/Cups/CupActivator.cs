using WreckfestController.Data.Cups;
using WreckfestController.Models;
using WreckfestController.Services.Publishing;
using WreckfestController.Services.ServerControl;

namespace WreckfestController.Services.Cups;

public enum ActivationResult
{
    /// <summary>The smart restart has begun; the cup becomes active when it succeeds.</summary>
    Started,
    NotFound,
    AlreadyActive,

    /// <summary>Another restart is in progress.</summary>
    Busy,
}

/// <summary>
/// Activates cups: applies a cup's settings through a smart restart and, when the
/// restart succeeds, makes it the active cup. The scheduler, the API and the WPF
/// window all activate through here, so they record the outcome the same way.
/// </summary>
/// <remarks>
/// An activation stands for one occurrence at most. The scheduler passes the occurrence
/// it is starting. A manual activation takes the cup's next occurrence if that is
/// within the <see cref="LeadIn"/>, because the scheduler would otherwise start the same
/// cup again minutes later. Once the restart ends - succeeded, failed or cancelled -
/// the cup moves past that occurrence, so a cancelled or failed occurrence is not
/// retried every half minute until someone deletes the cup.
/// </remarks>
public sealed class CupActivator
{
    /// <summary>How far ahead of its start an occurrence is started, for the players' countdown.</summary>
    public static readonly TimeSpan LeadIn = TimeSpan.FromMinutes(5);

    private readonly CupStore _store;
    private readonly SmartRestartService _restart;
    private readonly IServerEventPublisher _publisher;
    private readonly TimeProvider _time;
    private readonly ILogger<CupActivator> _logger;

    /// <summary>
    /// Claimed cups. Whoever acts on a cup - an activation, or the scheduler
    /// recording a miss - claims it first and re-reads it afterwards. A running
    /// activation holds its claim until its outcome has been recorded, and records the
    /// outcome before releasing. So a claimer either fails to claim or sees the recorded
    /// row, never a stale one. SmartRestartService's own state is no substitute: it
    /// reports Idle before its finish callback runs.
    /// </summary>
    private readonly HashSet<int> _claimed = new();

    public CupActivator(
        CupStore store,
        SmartRestartService restart,
        IServerEventPublisher publisher,
        TimeProvider time,
        ILogger<CupActivator> logger)
    {
        _store = store;
        _restart = restart;
        _publisher = publisher;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Activates <paramref name="id"/> now, at an admin's request. Throws when the cup's
    /// settings cannot be written to the server config, before any restart starts.
    /// </summary>
    /// <param name="onActivated">Called after the cup has been marked active.</param>
    public async Task<ActivationResult> ActivateAsync(int id, Action<Cup>? onActivated = null)
    {
        if (!TryClaim(id))
        {
            // This cup is already being activated, or the scheduler is dealing with it.
            return ActivationResult.Busy;
        }

        Cup? cup;
        try
        {
            // Read after claiming, so an outcome recorded just before is seen.
            cup = await _store.GetAsync(id);
        }
        catch
        {
            Release(id);
            throw;
        }

        if (cup is null)
        {
            Release(id);
            return ActivationResult.NotFound;
        }

        if (cup.IsActive)
        {
            Release(id);
            return ActivationResult.AlreadyActive;
        }

        var dueBy = _time.GetUtcNow().UtcDateTime + LeadIn;
        var occurrence = cup.NextOccurrence <= dueBy ? cup.NextOccurrence : null;
        return StartClaimed(cup, occurrence, onActivated, onFinished: null);
    }

    /// <summary>
    /// Starts the scheduled <paramref name="occurrence"/> of <paramref name="cup"/>. The
    /// caller must hold the claim (<see cref="TryClaim"/>) and have read
    /// <paramref name="cup"/> after taking it; the claim passes to the activation, which
    /// releases it once the outcome is recorded, or at once if nothing started. Throws,
    /// as <see cref="ActivateAsync"/> does, when the settings cannot be written.
    /// </summary>
    /// <param name="onFinished">Called once the restart has ended, after the cup has moved on.</param>
    public ActivationResult StartOccurrence(Cup cup, DateTime occurrence, Action<RestartOutcome> onFinished) =>
        StartClaimed(cup, occurrence, onActivated: null, onFinished);

    /// <summary>
    /// Claims <paramref name="cupId"/> for the caller. False when someone else holds it:
    /// an activation that has not finished, or the scheduler.
    /// </summary>
    public bool TryClaim(int cupId)
    {
        lock (_claimed)
        {
            return _claimed.Add(cupId);
        }
    }

    public void Release(int cupId)
    {
        lock (_claimed)
        {
            _claimed.Remove(cupId);
        }
    }

    private ActivationResult StartClaimed(
        Cup cup,
        DateTime? occurrence,
        Action<Cup>? onActivated,
        Action<RestartOutcome>? onFinished)
    {
        bool started;
        try
        {
            started = _restart.InitiateRestart(
                CupStore.ToRestartEvent(cup),
                _ => MarkActive(cup, onActivated),
                (_, outcome) => Finish(cup, occurrence, outcome, onFinished));
        }
        catch
        {
            Release(cup.Id);
            throw;
        }

        if (!started)
        {
            Release(cup.Id);
        }
        else
        {
            _logger.LogInformation(
                "Activating cup {CupName} (ID {CupId}){Occurrence}",
                cup.Name,
                cup.Id,
                occurrence is { } at ? $" for its {at:u} occurrence" : string.Empty);
        }

        return started ? ActivationResult.Started : ActivationResult.Busy;
    }

    // SmartRestartService calls back on a pool thread and expects the work done when
    // the callback returns: the scheduler must not look for the next due cup before
    // this one has been marked. Hence the blocking waits.
    private void MarkActive(Cup cup, Action<Cup>? onActivated)
    {
        try
        {
            if (_store.SetActiveAsync(cup.Id).GetAwaiter().GetResult())
            {
                _logger.LogInformation("Cup {CupName} (ID {CupId}) is now the active cup", cup.Name, cup.Id);
            }
            else
            {
                _logger.LogWarning(
                    "Cup {CupName} (ID {CupId}) was deleted during its restart; its settings are applied, but no cup is marked active",
                    cup.Name,
                    cup.Id);
            }

            _ = _publisher.CupActivatedAsync(cup.Id, cup.Name);
            onActivated?.Invoke(cup);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not mark cup {CupName} (ID {CupId}) active", cup.Name, cup.Id);
        }
    }

    /// <summary>
    /// Records how <paramref name="occurrence"/> ended and moves the cup past it. Anything
    /// but <see cref="OccurrenceOutcome.Activated"/> is logged as a warning and sent to
    /// signed-in clients: the occurrence is not retried, so an admin decides whether to
    /// activate it by hand.
    /// </summary>
    public async Task EndOccurrenceAsync(Cup cup, DateTime occurrence, OccurrenceOutcome outcome)
    {
        if (!await _store.AdvanceAsync(cup.Id, occurrence, outcome))
        {
            // Rescheduled or deleted meanwhile: the admin's change stands.
            return;
        }

        if (outcome != OccurrenceOutcome.Activated)
        {
            _logger.LogWarning(
                "The {Occurrence:u} occurrence of cup {CupName} (ID {CupId}) was {Outcome} and will not be retried; activate it by hand if it should still run",
                occurrence,
                cup.Name,
                cup.Id,
                outcome);
        }

        _ = _publisher.CupOccurrenceEndedAsync(cup.Id, cup.Name, occurrence, outcome);
    }

    private void Finish(Cup cup, DateTime? occurrence, RestartOutcome outcome, Action<RestartOutcome>? onFinished)
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
                EndOccurrenceAsync(cup, at, ended).GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not move cup {CupName} (ID {CupId}) past its occurrence", cup.Name, cup.Id);
        }
        finally
        {
            // After the outcome is recorded, so the next claimer reads the recorded row.
            Release(cup.Id);
            onFinished?.Invoke(outcome);
        }
    }
}
