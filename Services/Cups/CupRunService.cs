using Microsoft.Extensions.Hosting;
using WreckfestController.Data.Cups;
using WreckfestController.Services.Config;
using WreckfestController.Services.Hook;
using WreckfestController.Services.Publishing;
using WreckfestController.Services.ServerControl;

namespace WreckfestController.Services.Cups;

/// <summary>
/// Carries the active cup through its run once the restart has applied it (#204):
/// warmup, start, end. The scheduler restarts the server at the warmup; from there on
/// nothing restarts, so players who joined early are not disconnected when the cup begins.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Warmup:</b> a "starts in N minutes" message from <see cref="StartWarning"/>
/// before the start. If a race is still on at the start, "starts after this race".</item>
/// <item><b>Start:</b> at the first lobby at or after the start - so a warmup race finishing
/// late cannot leave points on the board - optionally the rotation sent back to its beginning
/// (<see cref="EventLoopControl.RestartAsync"/>), then <c>/cupreset</c> and "has started". A
/// run activated after its start (a restart that ran long) starts at once.</item>
/// <item><b>End:</b> the cup is no longer active, "is over", and at the next lobby cup points
/// are turned off live (<c>session_mode=normal</c>) and in server_config.cfg, so a later
/// restart does not bring them back. The rotation stays.</item>
/// </list>
/// <para>
/// Every change of phase is a compare-and-set in the database, made <i>before</i> the commands
/// it stands for: a check that finds the run deleted, replaced or already moved on does
/// nothing. The pending points-off is kept on the cup row, so a controller restart does not
/// lose it, and is dropped when another cup has taken over the server.
/// </para>
/// <para>
/// A lobby that never comes, or a session state that cannot be read, is waited for at most
/// <see cref="LobbyWait"/>; then it goes ahead anyway, with a warning in the log. The
/// "starts in" and "after this race" messages are remembered in memory only, so after a
/// controller restart one can repeat.
/// </para>
/// </remarks>
public sealed class CupRunService : IHostedService, IDisposable
{
    public static readonly TimeSpan StartWarning = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan LobbyWait = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(15);

    // Chat text after "/message", kept under the game's 127-character limit.
    private const int ChatLimit = 126;
    private const int NameLimit = 64;

    private readonly CupStore _store;
    private readonly ServerManager _serverManager;
    private readonly EventLoopControl _eventLoop;
    private readonly ConfigService _config;
    private readonly IServerEventPublisher _publisher;
    private readonly TimeProvider _time;
    private readonly Func<bool> _restartInProgress;
    private readonly ILogger<CupRunService> _logger;

    private ITimer? _timer;
    private int _ticking;

    // The run each message was last sent for, so each goes out once per run.
    private (int CupId, DateTime Start)? _warned;
    private (int CupId, DateTime Start)? _toldAfterRace;

    // A started run whose /cupreset could not be sent yet, and since when.
    private (int CupId, DateTime Start, DateTime Since)? _resetPending;

    public CupRunService(
        CupStore store,
        ServerManager serverManager,
        EventLoopControl eventLoop,
        ConfigService config,
        IServerEventPublisher publisher,
        TimeProvider time,
        Func<bool> restartInProgress,
        ILogger<CupRunService> logger)
    {
        _store = store;
        _serverManager = serverManager;
        _eventLoop = eventLoop;
        _config = config;
        _publisher = publisher;
        _time = time;
        _restartInProgress = restartInProgress;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = _time.CreateTimer(_ => _ = TickAsync(), null, CheckInterval, CheckInterval);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        return Task.CompletedTask;
    }

    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    /// <summary>One check. Does nothing while a previous check runs, or a restart does.</summary>
    public async Task TickAsync()
    {
        if (Interlocked.Exchange(ref _ticking, 1) != 0)
        {
            return;
        }

        try
        {
            // The restart is what puts a cup on; mid-restart there is no server to talk to.
            if (_restartInProgress())
            {
                return;
            }

            var run = await _store.ActiveRunAsync();
            await TurnPointsOffAsync(run);

            if (run is not { Occurrence: { } start })
            {
                return;
            }

            if (run.Phase == CupPhase.Warmup)
            {
                await WarmupAsync(run, start);
            }
            else if (run.Phase == CupPhase.Running)
            {
                await RetryResetAsync(run, start);
                if (run.EndsAt is { } end && UtcNow >= end)
                {
                    await EndAsync(run);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cup run check failed");
        }
        finally
        {
            Volatile.Write(ref _ticking, 0);
        }
    }

    private async Task WarmupAsync(ActiveCupRun run, DateTime start)
    {
        var name = Short(run.Name);
        if (UtcNow < start)
        {
            if (UtcNow >= start - StartWarning && _warned != (run.Id, start))
            {
                _warned = (run.Id, start);
                var minutes = Math.Max(1, (int)Math.Ceiling((start - UtcNow).TotalMinutes));
                await ChatAsync($"{name} starts in {minutes} minute{(minutes == 1 ? "" : "s")}.");
            }

            return;
        }

        if (!await WaitForLobbyAsync(start, onRace: async () =>
            {
                if (_toldAfterRace != (run.Id, start))
                {
                    _toldAfterRace = (run.Id, start);
                    await ChatAsync($"{name} starts after this race.");
                }
            }))
        {
            return;
        }

        // Claim the start first. A run deleted, replaced or started by another check since it
        // was read fails here, before anything is sent to the server on its behalf.
        if (_restartInProgress() || !await _store.SetPhaseAsync(run.Id, start, CupPhase.Warmup, CupPhase.Running))
        {
            return;
        }

        _logger.LogInformation("Cup {CupName} (ID {CupId}) has started", run.Name, run.Id);
        _ = _publisher.CupStartedAsync(run.Id, run.Name);

        if (run.RestartRotationAtStart)
        {
            var loop = await _eventLoop.RestartAsync();
            if (loop is not { Enabled: true })
            {
                _logger.LogWarning(
                    "Cup {CupName}: the rotation could not be sent back to its beginning (event loop now {Loop})",
                    run.Name,
                    loop?.ToString() ?? "unreadable");
            }
        }

        _resetPending = (run.Id, start, UtcNow);
        await RetryResetAsync(run, start);
    }

    /// <summary>
    /// <c>/cupreset</c>, then "has started", for a run that has started but not yet reset - no
    /// hook at that moment, say. Tried on every check while the run is still the active one,
    /// for at most <see cref="LobbyWait"/>; then announced without it.
    /// </summary>
    private async Task RetryResetAsync(ActiveCupRun run, DateTime start)
    {
        if (_resetPending is not { } pending)
        {
            return;
        }

        if (pending.CupId != run.Id || pending.Start != start)
        {
            // That run is over or replaced: its reset is no longer wanted.
            _resetPending = null;
            return;
        }

        var reset = await _serverManager.SendCommandAsync("/cupreset");
        if (!reset.Success)
        {
            if (UtcNow - pending.Since < LobbyWait)
            {
                _logger.LogWarning("Cup {CupName}: /cupreset could not be sent ({Message}); retrying", run.Name, reset.Message);
                return;
            }

            _logger.LogWarning(
                "Cup {CupName}: /cupreset could not be sent within {Minutes} minutes ({Message}); the cup runs without it",
                run.Name,
                LobbyWait.TotalMinutes,
                reset.Message);
        }

        _resetPending = null;
        await ChatAsync($"{Short(run.Name)} has started - good luck!");
    }

    private async Task EndAsync(ActiveCupRun run)
    {
        var pointsOff = AwardsCupPoints(run.SessionMode ?? ServerSessionMode()) ? UtcNow : (DateTime?)null;

        // Claim the end first, as for the start; it also records whether cup points are to go off.
        if (!await _store.EndRunAsync(run.Id, run.Occurrence, pointsOff))
        {
            return;
        }

        _resetPending = null;
        _logger.LogInformation("Cup {CupName} (ID {CupId}) has ended", run.Name, run.Id);
        _ = _publisher.CupEndedAsync(run.Id, run.Name);
        await ChatAsync($"{Short(run.Name)} is over - thanks for racing!");

        if (pointsOff is not null)
        {
            await TurnPointsOffAsync(run: null);
        }
    }

    /// <summary>
    /// For ended runs that awarded cup points: <c>session_mode=normal</c> at the next lobby (a
    /// lobby setting; unquoted, as the server takes it), and in server_config.cfg so a later
    /// restart keeps it off. Dropped, not done, while another cup is active: that cup's
    /// settings are what the server runs now.
    /// </summary>
    private async Task TurnPointsOffAsync(ActiveCupRun? run)
    {
        var pending = await _store.PendingPointsOffAsync();
        if (pending.Count == 0)
        {
            return;
        }

        var ids = pending.Select(p => p.Id).ToList();
        if (run is not null)
        {
            _logger.LogInformation(
                "Cup points of {Cups} left as they are: cup {CupName} is active now",
                string.Join(", ", pending.Select(p => p.Name)),
                run.Name);
            await _store.ClearPointsOffAsync(ids);
            return;
        }

        if (!await WaitForLobbyAsync(pending[0].Since, onRace: () => Task.CompletedTask))
        {
            return;
        }

        var sent = await _serverManager.SendCommandAsync("session_mode=normal");
        if (!sent.Success && UtcNow - pending[0].Since < LobbyWait)
        {
            _logger.LogWarning("session_mode=normal could not be sent ({Message}); retrying", sent.Message);
            return;
        }

        try
        {
            _config.WriteSettings(new Dictionary<string, string> { ["session_mode"] = "normal" });
            _logger.LogInformation(
                "Cup points turned off after {Cups} (session_mode=normal)",
                string.Join(", ", pending.Select(p => p.Name)));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "session_mode=normal could not be written to the server config");
        }

        await _store.ClearPointsOffAsync(ids);
    }

    /// <summary>
    /// True when the server is in its lobby, or when waiting longer is pointless: the state
    /// cannot be read, or <see cref="LobbyWait"/> has passed since <paramref name="since"/>.
    /// Otherwise calls <paramref name="onRace"/> and returns false, to be checked again.
    /// </summary>
    private async Task<bool> WaitForLobbyAsync(DateTime since, Func<Task> onRace)
    {
        var session = await _serverManager.ReadHookSessionAsync();
        if (session?.Phase == ServerSessionPhase.Lobby)
        {
            return true;
        }

        if (session is null)
        {
            _logger.LogWarning("The server's session state cannot be read; going ahead without waiting for the lobby");
            return true;
        }

        if (UtcNow - since >= LobbyWait)
        {
            _logger.LogWarning("No lobby within {Minutes} minutes; going ahead mid-session", LobbyWait.TotalMinutes);
            return true;
        }

        await onRace();
        return false;
    }

    /// <summary>
    /// The server's own session mode, for a cup that keeps it: what the cup ran under. Null
    /// when the config cannot be read, which then counts as no cup points.
    /// </summary>
    private string? ServerSessionMode()
    {
        try
        {
            return _config.ReadBasicConfig().SessionMode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The server config could not be read to see whether the cup awarded cup points");
            return null;
        }
    }

    /// <summary>A points system rather than <c>normal</c> or a qualifying mode.</summary>
    private static bool AwardsCupPoints(string? sessionMode) =>
        !string.IsNullOrWhiteSpace(sessionMode)
        && sessionMode != "normal"
        && !sessionMode.StartsWith("qualify", StringComparison.Ordinal);

    private async Task ChatAsync(string text)
    {
        var message = text.Length > ChatLimit ? text[..ChatLimit] : text;
        var sent = await _serverManager.SendCommandAsync($"/message {message}");
        if (!sent.Success)
        {
            _logger.LogWarning("Cup message could not be sent ({Message}): {Text}", sent.Message, message);
        }
    }

    private static string Short(string name) => name.Length > NameLimit ? name[..NameLimit] : name;

    public void Dispose() => _timer?.Dispose();
}
