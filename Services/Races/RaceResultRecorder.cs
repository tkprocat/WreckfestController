using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WreckfestController.Services.Cups;
using WreckfestController.Services.Hook;
using WreckfestController.Services.ServerControl;
using WreckfestController.Services.Tracking;

namespace WreckfestController.Services.Races;

/// <summary>
/// Saves every race the hook reports. <see cref="ServerManager.RaceFinished"/> is raised
/// on the thread draining the hook pipe, so records are queued here and written one at a
/// time on a worker of their own.
/// </summary>
/// <remarks>
/// Started through <c>DatabaseGatedHostedService</c>: while the database is unavailable
/// nothing subscribes, and races finished in that time are not recorded.
/// </remarks>
public sealed class RaceResultRecorder : IHostedService, IDisposable
{
    // A race ends at most every few minutes; anything near this is a stuck database.
    private const int QueueCapacity = 64;

    private readonly ServerManager _serverManager;
    private readonly RaceResultStore _store;
    private readonly CupStore _cups;
    private readonly TrackChangeTracker _tracks;
    private readonly ILogger<RaceResultRecorder> _logger;
    // Wait, not a Drop mode: only Wait makes TryWrite return false when the queue is
    // full. The Drop modes report success while discarding a race.
    private readonly Channel<(HookRaceRecord Record, ActiveCupSnapshot? Cup, string? FallbackTrackId)> _queue =
        Channel.CreateBounded<(HookRaceRecord, ActiveCupSnapshot?, string?)>(new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

    private readonly CancellationTokenSource _stopping = new();
    private Task? _worker;
    private volatile bool _closed;

    /// <summary>
    /// Waits between attempts to save one race. A locked or briefly unavailable
    /// database should cost a delay, not the race.
    /// </summary>
    internal IReadOnlyList<TimeSpan> RetryDelays { get; init; } =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15)];

    public RaceResultRecorder(
        ServerManager serverManager,
        RaceResultStore store,
        CupStore cups,
        TrackChangeTracker tracks,
        ILogger<RaceResultRecorder> logger)
    {
        _serverManager = serverManager;
        _store = store;
        _cups = cups;
        _tracks = tracks;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Races note the active cup from this cache. The database decides at save time
        // whenever it can, so a cache that failed to load costs only the rare case the
        // note exists for, and must not keep the recorder from subscribing.
        try
        {
            await _cups.LoadActiveCupAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not load the active cup; races will be linked from the database alone");
        }

        _worker = Task.Run(() => DrainAsync(_stopping.Token));
        _serverManager.RaceFinished += OnRaceFinished;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _serverManager.RaceFinished -= OnRaceFinished;

        // Let queued races finish writing; a shutdown that times out abandons the rest.
        // A race reported while this runs can still arrive, and is logged as lost.
        _closed = true;
        _queue.Writer.TryComplete();
        if (_worker != null)
        {
            await Task.WhenAny(_worker, Task.Delay(Timeout.Infinite, cancellationToken));
        }

        await _stopping.CancelAsync();
    }

    /// <summary>Waits until every race queued so far is saved. For tests.</summary>
    internal async Task FlushAsync()
    {
        _closed = true;
        _queue.Writer.TryComplete();
        if (_worker != null)
        {
            await _worker;
        }
    }

    private void OnRaceFinished(HookRaceRecord record)
    {
        // The text tracker is only a fallback for a track the hook could not read.
        var fallback = string.IsNullOrEmpty(record.TrackId) ? _tracks.GetCurrentTrack() : null;

        // Noted now, not when the worker saves, for the case where an admin switches cups
        // while the race waits in the queue. RaceResultStore.CupAtEnd decides how far to
        // trust it.
        var cup = _cups.CachedActiveCup;

        if (!_queue.Writer.TryWrite((record, cup, fallback)))
        {
            _logger.LogError(
                "Race on {Track} ending {EndedAt} was not recorded: {Reason}",
                record.TrackId,
                record.EndedAt,
                _closed ? "the recorder is shutting down" : "the results queue is full");
        }
    }

    private async Task DrainAsync(CancellationToken cancellationToken)
    {
        await foreach (var (record, cup, fallback) in _queue.Reader.ReadAllAsync(cancellationToken))
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    var race = await _store.SaveAsync(record, cup, fallback, cancellationToken);
                    _logger.LogInformation(
                        "Recorded race {RaceId} on {Track} with {Cars} cars{Cup}",
                        race.Id,
                        race.TrackId,
                        race.Entries.Count,
                        race.CupId == null ? string.Empty : $" for cup {race.CupName}");
                    break;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex) when (attempt < RetryDelays.Count)
                {
                    _logger.LogWarning(ex, "Saving the race on {Track} failed; retrying", record.TrackId);
                    try
                    {
                        await Task.Delay(RetryDelays[attempt], cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Race on {Track} ending {EndedAt} could not be saved", record.TrackId, record.EndedAt);
                    break;
                }
            }
        }
    }

    public void Dispose()
    {
        _serverManager.RaceFinished -= OnRaceFinished;
        _stopping.Dispose();
    }
}
