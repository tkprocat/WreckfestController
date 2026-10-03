using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
    private readonly TrackChangeTracker _tracks;
    private readonly ILogger<RaceResultRecorder> _logger;
    private readonly Channel<(HookRaceRecord Record, string? FallbackTrackId)> _queue =
        Channel.CreateBounded<(HookRaceRecord, string?)>(new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropWrite,
        });

    private readonly CancellationTokenSource _stopping = new();
    private Task? _worker;

    public RaceResultRecorder(
        ServerManager serverManager,
        RaceResultStore store,
        TrackChangeTracker tracks,
        ILogger<RaceResultRecorder> logger)
    {
        _serverManager = serverManager;
        _store = store;
        _tracks = tracks;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _worker = Task.Run(() => DrainAsync(_stopping.Token));
        _serverManager.RaceFinished += OnRaceFinished;
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _serverManager.RaceFinished -= OnRaceFinished;

        // Let queued races finish writing; a shutdown that times out abandons the rest.
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
        if (!_queue.Writer.TryWrite((record, fallback)))
        {
            _logger.LogError(
                "Race on {Track} ending {EndedAt} was not recorded: the results queue is full",
                record.TrackId,
                record.EndedAt);
        }
    }

    private async Task DrainAsync(CancellationToken cancellationToken)
    {
        await foreach (var (record, fallback) in _queue.Reader.ReadAllAsync(cancellationToken))
        {
            try
            {
                var race = await _store.SaveAsync(record, fallback, cancellationToken);
                _logger.LogInformation(
                    "Recorded race {RaceId} on {Track} with {Cars} cars{Cup}",
                    race.Id,
                    race.TrackId,
                    race.Entries.Count,
                    race.CupId == null ? string.Empty : $" for cup {race.CupName}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Race on {Track} ending {EndedAt} could not be saved", record.TrackId, record.EndedAt);
            }
        }
    }

    public void Dispose()
    {
        _serverManager.RaceFinished -= OnRaceFinished;
        _stopping.Dispose();
    }
}
