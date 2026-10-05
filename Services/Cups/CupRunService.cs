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
/// late cannot leave points on the board - <c>/cupreset</c>, optionally the rotation sent back
/// to its beginning (<see cref="EventLoopControl.RestartAsync"/>), and "has started".</item>
/// <item><b>End:</b> "is over", the cup is no longer active, and at the next lobby cup points
/// are turned off live (<c>session_mode=normal</c>) and in server_config.cfg, so a later
/// restart does not bring them back. The rotation stays.</item>
/// </list>
/// A lobby that never comes, or a session state that cannot be read, is waited for at most
/// <see cref="LobbyWait"/>; then it goes ahead anyway, with a warning in the log. What is
/// already sent is kept in memory only, so after a controller restart a message can repeat.
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

    // Set when a cup with cup points ends: turn them off at the next lobby.
    private (string CupName, DateTime Since)? _pendingPointsOff;

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

            var now = _time.GetUtcNow().UtcDateTime;
            if (_pendingPointsOff is { } pending)
            {
                await TurnPointsOffAsync(pending.CupName, pending.Since, now);
            }

            var run = await _store.ActiveRunAsync();
            if (run is not { Occurrence: { } start })
            {
                return;
            }

            if (run.Phase == CupPhase.Warmup)
            {
                await WarmupAsync(run, start, now);
            }
            else if (run.Phase == CupPhase.Running && run.EndsAt is { } end && now >= end)
            {
                await EndAsync(run);
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

    private async Task WarmupAsync(ActiveCupRun run, DateTime start, DateTime now)
    {
        var name = Short(run.Name);
        if (now < start)
        {
            if (now >= start - StartWarning && _warned != (run.Id, start))
            {
                _warned = (run.Id, start);
                var minutes = Math.Max(1, (int)Math.Ceiling((start - now).TotalMinutes));
                await ChatAsync($"{name} starts in {minutes} minute{(minutes == 1 ? "" : "s")}.");
            }

            return;
        }

        if (!await WaitForLobbyAsync(start, now, onRace: async () =>
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

        if (run.RestartRotationAtStart)
        {
            var loop = await _eventLoop.RestartAsync();
            if (loop is not { Enabled: true, Index: 0 })
            {
                _logger.LogWarning(
                    "Cup {CupName}: the rotation could not be sent back to its beginning (event loop now {Loop})",
                    run.Name,
                    loop?.ToString() ?? "unreadable");
            }
        }

        var reset = await _serverManager.SendCommandAsync("/cupreset");
        if (!reset.Success)
        {
            if (now - start < LobbyWait)
            {
                // No hook yet, say: try again on the next check.
                _logger.LogWarning("Cup {CupName}: /cupreset could not be sent ({Message}); retrying", run.Name, reset.Message);
                return;
            }

            _logger.LogWarning(
                "Cup {CupName}: /cupreset could not be sent within {Minutes} minutes of the start ({Message}); the cup starts without it",
                run.Name,
                LobbyWait.TotalMinutes,
                reset.Message);
        }

        await ChatAsync($"{name} has started - good luck!");
        if (await _store.SetPhaseAsync(run.Id, start, CupPhase.Running))
        {
            _logger.LogInformation("Cup {CupName} (ID {CupId}) has started", run.Name, run.Id);
            _ = _publisher.CupStartedAsync(run.Id, run.Name);
        }
    }

    private async Task EndAsync(ActiveCupRun run)
    {
        await ChatAsync($"{Short(run.Name)} is over - thanks for racing!");
        if (!await _store.EndRunAsync(run.Id, run.Occurrence))
        {
            return;
        }

        _logger.LogInformation("Cup {CupName} (ID {CupId}) has ended", run.Name, run.Id);
        _ = _publisher.CupEndedAsync(run.Id, run.Name);

        if (AwardsCupPoints(run.SessionMode))
        {
            _pendingPointsOff = (run.Name, _time.GetUtcNow().UtcDateTime);
            await TurnPointsOffAsync(run.Name, _pendingPointsOff.Value.Since, _time.GetUtcNow().UtcDateTime);
        }
    }

    /// <summary>
    /// <c>session_mode=normal</c> at the next lobby (a lobby setting; unquoted, as the server
    /// takes it), and in server_config.cfg so a later restart keeps it off.
    /// </summary>
    private async Task TurnPointsOffAsync(string cupName, DateTime since, DateTime now)
    {
        if (!await WaitForLobbyAsync(since, now, onRace: () => Task.CompletedTask))
        {
            return;
        }

        var sent = await _serverManager.SendCommandAsync("session_mode=normal");
        if (!sent.Success && now - since < LobbyWait)
        {
            _logger.LogWarning("Cup {CupName} ended: session_mode=normal could not be sent ({Message}); retrying", cupName, sent.Message);
            return;
        }

        _pendingPointsOff = null;
        try
        {
            _config.WriteSettings(new Dictionary<string, string> { ["session_mode"] = "normal" });
            _logger.LogInformation("Cup {CupName} ended: cup points turned off (session_mode=normal)", cupName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cup {CupName} ended: session_mode=normal could not be written to the server config", cupName);
        }
    }

    /// <summary>
    /// True when the server is in its lobby, or when waiting longer is pointless: the state
    /// cannot be read, or <see cref="LobbyWait"/> has passed since <paramref name="since"/>.
    /// Otherwise calls <paramref name="onRace"/> and returns false, to be checked again.
    /// </summary>
    private async Task<bool> WaitForLobbyAsync(DateTime since, DateTime now, Func<Task> onRace)
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

        if (now - since >= LobbyWait)
        {
            _logger.LogWarning("No lobby within {Minutes} minutes; going ahead mid-session", LobbyWait.TotalMinutes);
            return true;
        }

        await onRace();
        return false;
    }

    /// <summary>A points system rather than <c>normal</c>, a qualifying mode, or the server's own.</summary>
    private static bool AwardsCupPoints(string? sessionMode) =>
        sessionMode is not null and not "normal" && !sessionMode.StartsWith("qualify", StringComparison.Ordinal);

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
