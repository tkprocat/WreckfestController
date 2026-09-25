using WreckfestController.Data;

namespace WreckfestController.Services;

/// <summary>
/// Starts <typeparamref name="TService"/> only once the database is ready. In recovery
/// mode it waits, and starts as soon as a Retry succeeds.
/// </summary>
public sealed class DatabaseGatedHostedService<TService> : IHostedService, IDisposable
    where TService : IHostedService
{
    private readonly TService _inner;
    private readonly DatabaseState _state;
    private readonly ILogger<DatabaseGatedHostedService<TService>> _logger;
    private readonly object _lock = new();
    private bool _started;
    private bool _stopped;

    public DatabaseGatedHostedService(
        TService inner,
        DatabaseState state,
        ILogger<DatabaseGatedHostedService<TService>> logger)
    {
        _inner = inner;
        _state = state;
        _logger = logger;
    }

    public bool IsStarted
    {
        get
        {
            lock (_lock)
            {
                return _started;
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (!_state.IsReady)
            {
                _logger.LogWarning(
                    "{Service} not started: the database is unavailable. It will start once the database is ready.",
                    typeof(TService).Name);
                _state.Changed += OnDatabaseStateChanged;
                return Task.CompletedTask;
            }

            _started = true;
        }

        return _inner.StartAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        bool wasStarted;
        lock (_lock)
        {
            _stopped = true;
            _state.Changed -= OnDatabaseStateChanged;
            wasStarted = _started;
        }

        if (wasStarted)
        {
            await _inner.StopAsync(cancellationToken);
        }
    }

    private void OnDatabaseStateChanged()
    {
        lock (_lock)
        {
            if (!_state.IsReady || _started || _stopped)
            {
                return;
            }

            _started = true;
            _state.Changed -= OnDatabaseStateChanged;
        }

        try
        {
            _inner.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            _logger.LogInformation("{Service} started now that the database is ready", typeof(TService).Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Service} failed to start after the database became ready", typeof(TService).Name);
        }
    }

    public void Dispose()
    {
        _state.Changed -= OnDatabaseStateChanged;
    }
}
