using Microsoft.AspNetCore.SignalR;
using WreckfestController.Hubs;
using WreckfestController.Models;

namespace WreckfestController.Services;

/// <summary>
/// Sends server events to <see cref="ServerHub"/> clients.
/// <para>
/// This is a WPF-app singleton, but the hub lives in the API host, which starts later,
/// can be stopped and started again, and does not exist at all when the API is
/// disabled. The API host therefore <see cref="Attach"/>es its hub context when it
/// starts and <see cref="Detach"/>es it when it stops; while nothing is attached,
/// every event is dropped.
/// </para>
/// <para>
/// Console lines are buffered and sent to the admin group as one
/// <see cref="ConsoleLogMessage"/> per <see cref="FlushInterval"/>, or sooner once
/// <see cref="MaxBatchSize"/> lines are waiting, instead of one message per line.
/// </para>
/// </summary>
public sealed class HubServerEventPublisher : IServerEventPublisher, IDisposable
{
    public static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);
    public const int MaxBatchSize = 1000;

    /// <summary>
    /// Lines held while a flush is stalled, e.g. by a slow client. Beyond this the
    /// newest lines are dropped, and the next flush logs how many.
    /// </summary>
    public const int MaxBufferedLines = 20_000;

    private readonly ILogger<HubServerEventPublisher> _logger;
    private readonly ITimer _flushTimer;
    private readonly object _bufferLock = new();
    private readonly List<string> _buffer = [];
    // One flush at a time, so batches reach clients in the order the lines arrived.
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private IHubContext<ServerHub, IServerHubClient>? _hub;
    private int _droppedLines;
    private bool _disposed;

    public HubServerEventPublisher(ILogger<HubServerEventPublisher> logger)
        : this(logger, TimeProvider.System)
    {
    }

    public HubServerEventPublisher(ILogger<HubServerEventPublisher> logger, TimeProvider timeProvider)
    {
        _logger = logger;
        _flushTimer = timeProvider.CreateTimer(_ => _ = FlushConsoleLogAsync(), null, FlushInterval, FlushInterval);
    }

    public bool IsAttached => Volatile.Read(ref _hub) is not null;

    /// <summary>Starts sending to <paramref name="hub"/>. Called when the API host starts.</summary>
    public void Attach(IHubContext<ServerHub, IServerHubClient> hub)
    {
        Volatile.Write(ref _hub, hub);
        _logger.LogDebug("Server events are now sent to the SignalR hub");
    }

    /// <summary>
    /// Stops sending to <paramref name="hub"/> and discards unsent console lines, since
    /// the clients they were for are disconnecting. Does nothing if another hub has been
    /// attached since, so a late stop cannot detach a newer API host.
    /// </summary>
    public void Detach(IHubContext<ServerHub, IServerHubClient> hub)
    {
        if (Interlocked.CompareExchange(ref _hub, null, hub) != hub)
        {
            return;
        }

        lock (_bufferLock)
        {
            _buffer.Clear();
            _droppedLines = 0;
        }
    }

    public Task PlayersUpdatedAsync(IReadOnlyList<Player> players)
    {
        var message = new PlayersUpdatedMessage(players
            .Select(p => new PlayerSummary(p.Name, p.PlayerId, p.Score, p.Vehicle, p.Slot, p.IsBot, p.JoinedAt))
            .ToList());
        return PublishAsync(nameof(IServerHubClient.PlayersUpdated), c => c.PlayersUpdated(message));
    }

    public Task PlayerJoinedAsync(string playerName, bool isBot) =>
        PublishAsync(nameof(IServerHubClient.PlayerJoined), c => c.PlayerJoined(new PlayerJoinedMessage(playerName, isBot)));

    public Task PlayerLeftAsync(string playerName) =>
        PublishAsync(nameof(IServerHubClient.PlayerLeft), c => c.PlayerLeft(new PlayerLeftMessage(playerName)));

    public Task TrackChangedAsync(string trackId) =>
        PublishAsync(nameof(IServerHubClient.TrackChanged), c => c.TrackChanged(new TrackChangedMessage(trackId)));

    public Task EventActivatedAsync(int eventId, string eventName) =>
        PublishAsync(
            nameof(IServerHubClient.EventActivated),
            c => c.EventActivated(new EventActivatedMessage(eventId, eventName, DateTime.UtcNow)));

    public Task ServerStartedAsync(ServerStartedEvent e) =>
        PublishAsync(
            nameof(IServerHubClient.ServerStarted),
            c => c.ServerStarted(new ServerStartedMessage(e.ProcessId, e.ProcessName, e.StartTime, DateTime.UtcNow)));

    public Task ServerStoppedAsync(ServerStoppedEvent e) =>
        PublishAsync(
            nameof(IServerHubClient.ServerStopped),
            c => c.ServerStopped(new ServerStoppedMessage(e.ProcessId, e.StopMethod, DateTime.UtcNow)));

    public Task ServerRestartedAsync(ServerRestartedEvent e) =>
        PublishAsync(
            nameof(IServerHubClient.ServerRestarted),
            c => c.ServerRestarted(
                new ServerRestartedMessage(e.OldProcessId, e.NewProcessId, e.RestartMethod, DateTime.UtcNow)));

    public Task ServerAttachedAsync(ServerAttachedEvent e) =>
        PublishAsync(
            nameof(IServerHubClient.ServerAttached),
            c => c.ServerAttached(new ServerAttachedMessage(e.ProcessId, e.ProcessName, e.StartTime, DateTime.UtcNow)));

    public Task ServerRestartPendingAsync(ServerRestartPendingEvent e) =>
        PublishAsync(
            nameof(IServerHubClient.ServerRestartPending),
            c => c.ServerRestartPending(new ServerRestartPendingMessage(
                e.MinutesRemaining, e.EventName, e.EventId, e.ScheduledRestartTime, DateTime.UtcNow)));

    public void AddConsoleLog(string line)
    {
        if (_disposed || !IsAttached)
        {
            return;
        }

        bool flushNow;
        lock (_bufferLock)
        {
            if (_buffer.Count >= MaxBufferedLines)
            {
                _droppedLines++;
                return;
            }

            _buffer.Add(line);
            flushNow = _buffer.Count == MaxBatchSize;
        }

        if (flushNow)
        {
            _ = Task.Run(FlushConsoleLogAsync);
        }
    }

    /// <summary>Sends every buffered console line, in batches of at most <see cref="MaxBatchSize"/>.</summary>
    public async Task FlushConsoleLogAsync()
    {
        await _flushGate.WaitAsync();
        try
        {
            while (true)
            {
                List<string> batch;
                int dropped;
                lock (_bufferLock)
                {
                    if (_buffer.Count == 0)
                    {
                        return;
                    }

                    var count = Math.Min(_buffer.Count, MaxBatchSize);
                    batch = _buffer.GetRange(0, count);
                    _buffer.RemoveRange(0, count);
                    dropped = _droppedLines;
                    _droppedLines = 0;
                }

                if (dropped > 0)
                {
                    _logger.LogWarning(
                        "Dropped {Count} console lines for the web UI because sending fell behind",
                        dropped);
                }

                await PublishAsync(
                    nameof(IServerHubClient.ConsoleLog),
                    c => c.ConsoleLog(new ConsoleLogMessage(batch)),
                    ServerHub.AdminGroup);
            }
        }
        finally
        {
            _flushGate.Release();
        }
    }

    private async Task PublishAsync(
        string messageName,
        Func<IServerHubClient, Task> send,
        string group = ServerHub.PublicGroup)
    {
        var hub = Volatile.Read(ref _hub);
        if (hub is null)
        {
            return;
        }

        try
        {
            await send(hub.Clients.Group(group));
            _logger.LogDebug("Sent {Message} to the {Group} group", messageName, group);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send {Message} to the {Group} group", messageName, group);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _flushTimer.Dispose();
        // Best effort: the last lines are only useful if a client is still listening.
        FlushConsoleLogAsync().Wait(TimeSpan.FromSeconds(5));
    }
}
