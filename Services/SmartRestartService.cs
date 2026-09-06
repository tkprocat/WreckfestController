using WreckfestController.Models;

namespace WreckfestController.Services;

/// <summary>
/// Service responsible for gracefully restarting the server with player warnings and lobby detection.
/// Implements a 5-minute countdown with smart waiting for lobby between races.
/// </summary>
public class SmartRestartService
{
    private readonly ServerManager _serverManager;
    private readonly PlayerTracker _playerTracker;
    private readonly TrackChangeTracker _trackChangeTracker;
    private readonly ConfigService _configService;
    private readonly WreckfestWebWebhookService _webhookService;
    private readonly ILogger<SmartRestartService> _logger;
    private readonly TimeProvider _timeProvider;

    private SmartRestartState _state = SmartRestartState.Idle;
    private Event? _pendingEvent = null;
    private ITimer? _countdownTimer = null;
    private long _countdownTimestamp;
    private DateTime _countdownStartTime;
    private long _waitTimestamp;
    private int _lastWarningMinutes;
    private Action<Event>? _onRestartCompleteCallback = null;
    private Action<Event, RestartOutcome>? _onFinished;
    private long _restartId;
    private readonly object _stateLock = new();

    // Configuration
    private const int CountdownMinutes = 5;
    private const int MaxWaitMinutes = 10;
    private const int CheckIntervalSeconds = 30;

    public SmartRestartService(
        ServerManager serverManager,
        PlayerTracker playerTracker,
        TrackChangeTracker trackChangeTracker,
        ConfigService configService,
        WreckfestWebWebhookService webhookService,
        ILogger<SmartRestartService> logger)
        : this(serverManager, playerTracker, trackChangeTracker, configService, webhookService, logger, TimeProvider.System)
    {
    }

    public SmartRestartService(
        ServerManager serverManager,
        PlayerTracker playerTracker,
        TrackChangeTracker trackChangeTracker,
        ConfigService configService,
        WreckfestWebWebhookService webhookService,
        ILogger<SmartRestartService> logger,
        TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        _serverManager = serverManager;
        _playerTracker = playerTracker;
        _trackChangeTracker = trackChangeTracker;
        _configService = configService;
        _webhookService = webhookService;
        _logger = logger;

        // Subscribe to track changes
        _trackChangeTracker.TrackChanged += OnTrackChanged;
    }

    /// <summary>
    /// Gets the current state of the smart restart service
    /// </summary>
    public SmartRestartState GetState()
    {
        lock (_stateLock)
        {
            return _state;
        }
    }

    /// <summary>
    /// Gets the event that is currently pending restart
    /// </summary>
    public Event? GetPendingEvent()
    {
        lock (_stateLock)
        {
            return _pendingEvent;
        }
    }

    /// <summary>
    /// Initiates a smart restart for the given event
    /// </summary>
    /// <param name="event">The event to activate after restart</param>
    /// <param name="onComplete">Callback invoked after restart is complete</param>
    /// <returns>True if restart was initiated, false if already in progress</returns>
    public bool InitiateRestart(Event @event, Action<Event> onComplete,
        Action<Event, RestartOutcome>? onFinished = null)
    {
        bool startImmediately;
        long restartId;
        lock (_stateLock)
        {
            if (_state != SmartRestartState.Idle)
            {
                _logger.LogWarning(
                    "Cannot initiate restart for event {EventName} - restart already in progress (state: {State})",
                    @event.Name,
                    _state);
                return false;
            }

            _logger.LogInformation(
                "Initiating smart restart for event: {EventName} (ID: {EventId})",
                @event.Name,
                @event.Id);

            // Both manual and scheduled activation must finish configuration writes
            // before a restart is accepted. A failure propagates to the caller.
            ApplyEventConfiguration(@event);

            _pendingEvent = @event;
            _onRestartCompleteCallback = onComplete;
            _onFinished = onFinished;
            restartId = ++_restartId;

            // Check if any real players are online (excludes bots)
            if (!_playerTracker.HasPlayersOnline())
            {
                _logger.LogInformation("No real players online (only bots or empty) - skipping countdown and restarting immediately");
                _state = SmartRestartState.Pending; // Set state so ExecuteRestartAsync doesn't early-return
                _waitTimestamp = _timeProvider.GetTimestamp();
                startImmediately = true;
            }
            else
            {
                // Real players are online - start countdown
                var (onlinePlayers, _) = _playerTracker.GetPlayerCount();
                _logger.LogInformation("{PlayerCount} real players online - starting {Minutes}-minute countdown", onlinePlayers, CountdownMinutes);
                _state = SmartRestartState.Warning;
                _countdownTimestamp = _timeProvider.GetTimestamp();
                _countdownStartTime = _timeProvider.GetUtcNow().UtcDateTime;
                _lastWarningMinutes = CountdownMinutes + 1;

                // Each callback schedules the next elapsed-time boundary.
                _countdownTimer = _timeProvider.CreateTimer(
                    OnCountdownTick,
                    restartId,
                    TimeSpan.Zero,
                    Timeout.InfiniteTimeSpan);

                startImmediately = false;
            }
        }

        if (startImmediately)
            _ = Task.Run(() => ExecuteRestartAsync(restartId));

        return true;
    }

    /// <summary>
    /// Callback for countdown timer ticks
    /// </summary>
    private void OnCountdownTick(object? state)
    {
        Models.ServerRestartPendingEvent notification;
        string message;
        lock (_stateLock)
        {
            if (state is not long restartId || restartId != _restartId || _state != SmartRestartState.Warning)
                return;

            // Count elapsed monotonic time, not callbacks: delayed ticks and wall
            // clock corrections must not shorten the promised warning period.
            var remaining = TimeSpan.FromMinutes(CountdownMinutes)
                - _timeProvider.GetElapsedTime(_countdownTimestamp);
            var minutesRemaining = Math.Max(0, (int)Math.Ceiling(remaining.TotalMinutes));
            if (minutesRemaining > 0)
            {
                // A slightly early tick must retry at the boundary, not wait a
                // whole extra minute or declare the countdown finished early.
                var untilBoundary = remaining - TimeSpan.FromMinutes(minutesRemaining - 1);
                _countdownTimer?.Change(
                    untilBoundary < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : untilBoundary,
                    Timeout.InfiniteTimeSpan);
                if (minutesRemaining == _lastWarningMinutes)
                    return;
                _lastWarningMinutes = minutesRemaining;
            }

            notification = new Models.ServerRestartPendingEvent
            {
                MinutesRemaining = minutesRemaining,
                EventName = _pendingEvent?.Name,
                EventId = _pendingEvent?.Id,
                ScheduledRestartTime = _countdownStartTime.AddMinutes(CountdownMinutes)
            };

            if (minutesRemaining > 0)
            {
                message = minutesRemaining == 1
                    ? "Server will restart in 1 minute."
                    : $"Server will restart in {minutesRemaining} minutes.";
            }
            else
            {
                _logger.LogInformation("Countdown complete - entering pending state (waiting for lobby)");
                _state = SmartRestartState.Pending;
                _waitTimestamp = _timeProvider.GetTimestamp();
                _countdownTimer?.Dispose();
                message = "Server will restart at the next lobby.";
                _countdownTimer = _timeProvider.CreateTimer(
                    OnLobbyCheckTick, restartId, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
            }
        }

        // Async methods can run synchronously up to their first incomplete await.
        // Dispatch the captured message/payload only after releasing the state lock.
        _ = SendServerMessageAsync(message);
        _ = SendRestartPendingNotificationAsync(notification);
    }

    private async Task SendRestartPendingNotificationAsync(Models.ServerRestartPendingEvent notification)
    {
        try { await _webhookService.SendServerRestartPendingAsync(notification); }
        catch (Exception ex) { _logger.LogError(ex, "Failed to send server restart pending webhook"); }
    }
    /// <summary>
    /// Callback for lobby check timer ticks
    /// </summary>
    private void OnLobbyCheckTick(object? state)
    {
        if (state is not long restartId)
            return;
        bool shouldRestart = false;
        bool timedOut = false;
        lock (_stateLock)
        {
            if (restartId != _restartId || _state != SmartRestartState.Pending)
                return;

            var waitDuration = _timeProvider.GetElapsedTime(_waitTimestamp);
            if (waitDuration.TotalMinutes >= MaxWaitMinutes)
            {
                _logger.LogWarning(
                    "Max wait time ({Minutes} minutes) exceeded - forcing restart",
                    MaxWaitMinutes);
                timedOut = true;
                shouldRestart = true;
            }
            else
            {
                var (onlinePlayers, _) = _playerTracker.GetPlayerCount();
                if (onlinePlayers == 0)
                {
                    _logger.LogInformation("All players left - restarting immediately");
                    shouldRestart = true;
                }
                else
                {
                    var remaining = TimeSpan.FromMinutes(MaxWaitMinutes) - waitDuration;
                    var untilCheck = remaining < TimeSpan.FromSeconds(CheckIntervalSeconds)
                        ? remaining : TimeSpan.FromSeconds(CheckIntervalSeconds);
                    _countdownTimer?.Change(
                        untilCheck < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : untilCheck,
                        Timeout.InfiniteTimeSpan);
                    _logger.LogDebug(
                        "Still waiting for lobby. {OnlinePlayers} players online. Waited {Minutes:F1} of {MaxMinutes} minutes.",
                        onlinePlayers,
                        waitDuration.TotalMinutes,
                        MaxWaitMinutes);
                }
            }
        }

        if (timedOut)
            _ = SendServerMessageAsync("Server restarting now (timeout).");
        if (shouldRestart)
            _ = Task.Run(() => ExecuteRestartAsync(restartId));
    }

    /// <summary>
    /// Callback for track change events (indicates lobby)
    /// </summary>
    private void OnTrackChanged(TrackChangeEvent trackChangeEvent)
    {
        bool shouldRestart = false;
        long restartId;
        lock (_stateLock)
        {
            restartId = _restartId;
            if (_state == SmartRestartState.Pending)
            {
                _logger.LogInformation(
                    "Track changed to {TrackId} - lobby detected, initiating restart",
                    trackChangeEvent.TrackId);
                _ = SendServerMessageAsync("Server restarting now.");
                shouldRestart = true;
            }
        }

        if (shouldRestart)
            _ = Task.Run(() => ExecuteRestartAsync(restartId));
    }

    /// <summary>
    /// Executes the actual server restart and applies event configuration
    /// </summary>
    private async Task ExecuteRestartAsync(long restartId)
    {
        Event? eventToActivate;
        Action<Event>? callback;

        lock (_stateLock)
        {
            if (restartId != _restartId || _state != SmartRestartState.Pending)
            {
                _logger.LogWarning("Restart already in progress or cancelled");
                return;
            }

            _logger.LogInformation("Beginning server restart");
            _state = SmartRestartState.Restarting;
            eventToActivate = _pendingEvent;
            callback = _onRestartCompleteCallback;

            // Stop any running timers
            _countdownTimer?.Dispose();
            _countdownTimer = null;
        }

        if (eventToActivate == null)
        {
            _logger.LogError("No event to activate - this should not happen");
            FinishRestart(restartId, RestartOutcome.Failed);
            return;
        }

        var outcome = RestartOutcome.Failed;
        try
        {
            _logger.LogInformation(
                "Executing restart for event: {EventName} (ID: {EventId})",
                eventToActivate.Name,
                eventToActivate.Id);

            // Restart the server using in-game /restart command (faster and cleaner)
            var restartResult = await _serverManager.RestartServerViaCommandAsync();
            if (!restartResult.Success)
            {
                _logger.LogError("Server restart failed: {Message}", restartResult.Message);
                return;
            }

            _logger.LogInformation("Server restarted successfully");

            // Wait a moment for server to stabilize
            await Task.Delay(2000);

            _logger.LogInformation("Event {EventName} activated successfully", eventToActivate.Name);

            // Mark as completed
            lock (_stateLock)
            {
                _state = SmartRestartState.Completed;
            }

            // Invoke callback
            outcome = RestartOutcome.Succeeded;
            callback?.Invoke(eventToActivate);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during restart execution");
        }
        finally
        {
            FinishRestart(restartId, outcome);
        }
    }

    /// <summary>
    /// Sends a message to the server console that players will see
    /// </summary>
    private async Task SendServerMessageAsync(string message)
    {
        try
        {
            _logger.LogInformation("Sending server message: {Message}", message);

            var command = $"/message {message}";
            var result = await _serverManager.SendCommandAsync(command);

            if (!result.Success)
            {
                _logger.LogWarning("Failed to send server message: {Error}", result.Message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending server message");
        }
    }

    /// <summary>
    /// Applies the event's server configuration (tracks and server settings)
    /// </summary>
    private void ApplyEventConfiguration(Event @event)
    {
        try
        {
            _logger.LogInformation("Applying configuration for event: {EventName}", @event.Name);

            // Read current config
            var currentConfig = _configService.ReadBasicConfig();

            // Apply server config overrides if present
            if (@event.ServerConfig != null)
            {
                var eventConfig = @event.ServerConfig;

                if (!string.IsNullOrWhiteSpace(eventConfig.ServerName))
                    currentConfig.ServerName = eventConfig.ServerName;

                if (!string.IsNullOrWhiteSpace(eventConfig.WelcomeMessage))
                    currentConfig.WelcomeMessage = eventConfig.WelcomeMessage;

                if (eventConfig.Password != null)
                    currentConfig.Password = eventConfig.Password;

                if (eventConfig.MaxPlayers.HasValue)
                    currentConfig.MaxPlayers = eventConfig.MaxPlayers.Value;

                if (eventConfig.Bots.HasValue)
                    currentConfig.Bots = eventConfig.Bots.Value;

                if (!string.IsNullOrWhiteSpace(eventConfig.AiDifficulty))
                    currentConfig.AiDifficulty = eventConfig.AiDifficulty;

                if (eventConfig.Laps.HasValue)
                    currentConfig.Laps = eventConfig.Laps.Value;

                if (!string.IsNullOrWhiteSpace(eventConfig.VehicleDamage))
                    currentConfig.VehicleDamage = eventConfig.VehicleDamage;

                if (eventConfig.LobbyCountdown.HasValue)
                    currentConfig.LobbyCountdown = eventConfig.LobbyCountdown.Value;

                // Write updated config
                _configService.WriteBasicConfig(currentConfig);
                _logger.LogInformation("Server configuration updated");
            }

            // Apply track rotation if present
            if (@event.Tracks != null && @event.Tracks.Count > 0)
            {
                var collectionName = string.IsNullOrWhiteSpace(@event.CollectionName)
                    ? $"Event: {@event.Name}"
                    : @event.CollectionName;

                _configService.WriteEventLoopTracks(collectionName, @event.Tracks);
                _logger.LogInformation("Track rotation updated with {Count} tracks", @event.Tracks.Count);
            }

            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error applying event configuration");
            throw;
        }
    }

    /// <summary>
    /// Resets the service state to idle
    /// </summary>
    private void FinishRestart(long restartId, RestartOutcome outcome)
    {
        Action<Event, RestartOutcome>? finished;
        Event? completedEvent;
        lock (_stateLock)
        {
            if (restartId != _restartId || _state == SmartRestartState.Idle)
                return;
            finished = _onFinished;
            completedEvent = _pendingEvent;
            _logger.LogDebug("Resetting smart restart service state");

            _countdownTimer?.Dispose();
            _countdownTimer = null;

            _state = SmartRestartState.Idle;
            _pendingEvent = null;
            _onRestartCompleteCallback = null;
            _onFinished = null;
        }

        // Never invoke consumer callbacks under the state lock. Clear ownership
        // first so a reentrant callback can safely initiate another restart.
        if (completedEvent != null)
        {
            try { finished?.Invoke(completedEvent, outcome); }
            catch (Exception ex) { _logger.LogError(ex, "Error reporting restart completion"); }
        }
    }

    /// <summary>
    /// Cancels any ongoing restart operation
    /// </summary>
    public bool CancelRestart()
    {
        long restartId;
        lock (_stateLock)
        {
            if (_state != SmartRestartState.Warning && _state != SmartRestartState.Pending)
            {
                _logger.LogWarning("Cannot cancel - no restart in progress or already restarting");
                return false;
            }

            _logger.LogInformation("Cancelling restart for event: {EventName}", _pendingEvent?.Name ?? "Unknown");

            _ = SendServerMessageAsync("Server restart cancelled.");

            restartId = _restartId;
            // Claim cancellation before releasing the lock; queued execution can
            // no longer enter Restarting while the terminal callback is delivered.
            _state = SmartRestartState.Completed;
        }
        FinishRestart(restartId, RestartOutcome.Cancelled);
        return true;
    }
}

public enum RestartOutcome { Succeeded, Failed, Cancelled }

/// <summary>
/// State of the smart restart process
/// </summary>
public enum SmartRestartState
{
    /// <summary>
    /// No restart in progress
    /// </summary>
    Idle,

    /// <summary>
    /// Countdown phase - warning players (T-5 to T-1 minutes)
    /// </summary>
    Warning,

    /// <summary>
    /// Pending phase - waiting for lobby or timeout (T-0)
    /// </summary>
    Pending,

    /// <summary>
    /// Restarting phase - actively restarting server
    /// </summary>
    Restarting,

    /// <summary>
    /// Completed phase - restart finished, about to reset to idle
    /// </summary>
    Completed
}
