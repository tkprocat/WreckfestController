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

    private SmartRestartState _state = SmartRestartState.Idle;
    private Event? _pendingEvent = null;
    private System.Threading.Timer? _countdownTimer = null;
    private int _countdownMinutesRemaining = 0;
    private DateTime _countdownStartTime;
    private DateTime _waitStartTime;
    private Action<Event>? _onRestartCompleteCallback = null;
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
    {
        _serverManager = serverManager;
        _playerTracker = playerTracker;
        _trackChangeTracker = trackChangeTracker;
        _configService = configService;
        _webhookService = webhookService;
        _logger = logger;

        // Subscribe to track changes
        _trackChangeTracker.SubscribeToTrackChange(OnTrackChanged);
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
    public bool InitiateRestart(Event @event, Action<Event> onComplete)
    {
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

            _pendingEvent = @event;
            _onRestartCompleteCallback = onComplete;

            // Check if any real players are online (excludes bots)
            var (onlinePlayers, _) = _playerTracker.GetPlayerCount();

            if (onlinePlayers == 0)
            {
                _logger.LogInformation("No real players online (only bots or empty) - skipping countdown and restarting immediately");
                _ = Task.Run(() => ExecuteRestartAsync());
                return true;
            }

            // Real players are online - start countdown
            _logger.LogInformation("{PlayerCount} real players online - starting {Minutes}-minute countdown", onlinePlayers, CountdownMinutes);
            _state = SmartRestartState.Warning;
            _countdownMinutesRemaining = CountdownMinutes;
            _countdownStartTime = DateTime.UtcNow;

            // Start countdown timer (fires every minute)
            _countdownTimer = new System.Threading.Timer(
                OnCountdownTick,
                null,
                TimeSpan.Zero,
                TimeSpan.FromMinutes(1));

            return true;
        }
    }

    /// <summary>
    /// Callback for countdown timer ticks
    /// </summary>
    private void OnCountdownTick(object? state)
    {
        lock (_stateLock)
        {
            if (_state == SmartRestartState.Warning && _countdownMinutesRemaining > 0)
            {
                // Send warning message
                var message = _countdownMinutesRemaining == 1
                    ? "Server will restart in 1 minute."
                    : $"Server will restart in {_countdownMinutesRemaining} minutes.";

                _ = SendServerMessageAsync(message);

                // Send webhook notification
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _webhookService.SendServerRestartPendingAsync(new Models.ServerRestartPendingEvent
                        {
                            MinutesRemaining = _countdownMinutesRemaining,
                            EventName = _pendingEvent?.Name,
                            EventId = _pendingEvent?.Id,
                            ScheduledRestartTime = _countdownStartTime.AddMinutes(CountdownMinutes)
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to send server restart pending webhook");
                    }
                });

                _countdownMinutesRemaining--;

                if (_countdownMinutesRemaining == 0)
                {
                    // Countdown complete - move to pending state
                    _logger.LogInformation("Countdown complete - entering pending state (waiting for lobby)");
                    _state = SmartRestartState.Pending;
                    _waitStartTime = DateTime.UtcNow;

                    // Stop countdown timer
                    _countdownTimer?.Dispose();
                    _countdownTimer = null;

                    // Send final message
                    _ = SendServerMessageAsync("Server will restart at the next lobby.");

                    // Send webhook notification for pending state (0 minutes)
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _webhookService.SendServerRestartPendingAsync(new Models.ServerRestartPendingEvent
                            {
                                MinutesRemaining = 0,
                                EventName = _pendingEvent?.Name,
                                EventId = _pendingEvent?.Id,
                                ScheduledRestartTime = DateTime.UtcNow
                            });
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to send server restart pending webhook");
                        }
                    });

                    // Start checking for lobby opportunity
                    _countdownTimer = new System.Threading.Timer(
                        OnLobbyCheckTick,
                        null,
                        TimeSpan.Zero,
                        TimeSpan.FromSeconds(CheckIntervalSeconds));
                }
            }
        }
    }

    /// <summary>
    /// Callback for lobby check timer ticks
    /// </summary>
    private void OnLobbyCheckTick(object? state)
    {
        lock (_stateLock)
        {
            if (_state != SmartRestartState.Pending)
            {
                return;
            }

            // Check if we've exceeded max wait time
            var waitDuration = DateTime.UtcNow - _waitStartTime;
            if (waitDuration.TotalMinutes >= MaxWaitMinutes)
            {
                _logger.LogWarning(
                    "Max wait time ({Minutes} minutes) exceeded - forcing restart",
                    MaxWaitMinutes);

                _ = SendServerMessageAsync("Server restarting now (timeout).");
                _ = Task.Run(() => ExecuteRestartAsync());
                return;
            }

            // Check if all players left
            var (onlinePlayers, _) = _playerTracker.GetPlayerCount();
            if (onlinePlayers == 0)
            {
                _logger.LogInformation("All players left - restarting immediately");
                _ = Task.Run(() => ExecuteRestartAsync());
                return;
            }

            _logger.LogDebug(
                "Still waiting for lobby. {OnlinePlayers} players online. Waited {Minutes:F1} of {MaxMinutes} minutes.",
                onlinePlayers,
                waitDuration.TotalMinutes,
                MaxWaitMinutes);
        }
    }

    /// <summary>
    /// Callback for track change events (indicates lobby)
    /// </summary>
    private void OnTrackChanged(TrackChangeEvent trackChangeEvent)
    {
        lock (_stateLock)
        {
            if (_state == SmartRestartState.Pending)
            {
                _logger.LogInformation(
                    "Track changed to {TrackId} - lobby detected, initiating restart",
                    trackChangeEvent.TrackId);

                _ = SendServerMessageAsync("Server restarting now.");
                _ = Task.Run(() => ExecuteRestartAsync());
            }
        }
    }

    /// <summary>
    /// Executes the actual server restart and applies event configuration
    /// </summary>
    private async Task ExecuteRestartAsync()
    {
        Event? eventToActivate;
        Action<Event>? callback;

        lock (_stateLock)
        {
            if (_state == SmartRestartState.Restarting || _state == SmartRestartState.Idle)
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
            ResetState();
            return;
        }

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
                ResetState();
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
            callback?.Invoke(eventToActivate);

            // Reset state after a short delay
            await Task.Delay(5000);
            ResetState();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during restart execution");
            ResetState();
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
    /// Resets the service state to idle
    /// </summary>
    private void ResetState()
    {
        lock (_stateLock)
        {
            _logger.LogDebug("Resetting smart restart service state");

            _countdownTimer?.Dispose();
            _countdownTimer = null;

            _state = SmartRestartState.Idle;
            _pendingEvent = null;
            _onRestartCompleteCallback = null;
            _countdownMinutesRemaining = 0;
        }
    }

    /// <summary>
    /// Cancels any ongoing restart operation
    /// </summary>
    public bool CancelRestart()
    {
        lock (_stateLock)
        {
            if (_state == SmartRestartState.Idle || _state == SmartRestartState.Restarting)
            {
                _logger.LogWarning("Cannot cancel - no restart in progress or already restarting");
                return false;
            }

            _logger.LogInformation("Cancelling restart for event: {EventName}", _pendingEvent?.Name ?? "Unknown");

            _ = SendServerMessageAsync("Server restart cancelled.");

            ResetState();
            return true;
        }
    }
}

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
