using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace WreckfestController.Services;

public class TrackChangeTracker
{
    private readonly ConcurrentBag<Action<TrackChangeEvent>> _trackChangeSubscribers = new();
    private readonly ILogger<TrackChangeTracker> _logger;
    private readonly LaravelWebhookService _webhookService;
    private string? _currentTrack = null;
    private readonly object _lock = new();

    public TrackChangeTracker(ILogger<TrackChangeTracker> logger, LaravelWebhookService webhookService)
    {
        _logger = logger;
        _webhookService = webhookService;
    }

    /// <summary>
    /// Parse a log line and detect track changes
    /// </summary>
    public void ProcessLogLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        // Parse track change events: "22:45:27 Current track loaded! (speedway2_inner_oval)"
        var trackChangeMatch = Regex.Match(line, @"Current track loaded! \((.+)\)");
        if (trackChangeMatch.Success)
        {
            var trackId = trackChangeMatch.Groups[1].Value;
            TrackChanged(trackId);
        }
    }

    /// <summary>
    /// Handle track change event
    /// </summary>
    private void TrackChanged(string trackId)
    {
        lock (_lock)
        {
            _currentTrack = trackId;
            _logger.LogInformation("Track changed to: {TrackId}", trackId);
            NotifyTrackChangeSubscribers(new TrackChangeEvent(trackId));

            // Send webhook to Laravel
            _ = _webhookService.SendTrackChangedAsync(trackId);
        }
    }

    /// <summary>
    /// Get current track
    /// </summary>
    public string? GetCurrentTrack()
    {
        lock (_lock)
        {
            return _currentTrack;
        }
    }

    /// <summary>
    /// Clear current track (used when server stops)
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _currentTrack = null;
            _logger.LogInformation("Track tracking cleared");
        }
    }

    public void SubscribeToTrackChange(Action<TrackChangeEvent> trackChangeEvent)
    {
        _trackChangeSubscribers.Add(trackChangeEvent);
    }

    public void UnsubscribeFromTrackChange(Action<TrackChangeEvent> trackChangeEvent)
    {
        // ConcurrentBag doesn't support removal, but we can handle it by checking if callback is null
        // For simplicity, we'll keep the subscriber list as is
    }

    private void NotifyTrackChangeSubscribers(TrackChangeEvent trackChangeEvent)
    {
        foreach (var subscriber in _trackChangeSubscribers)
        {
            try
            {
                subscriber(trackChangeEvent);
            }
            catch
            {
                // Ignore subscriber errors
            }
        }
    }
}

public class TrackChangeEvent
{
    public string TrackId { get; set; } = string.Empty;
    public DateTime ChangedAt { get; set; }

    public TrackChangeEvent(string trackId)
    {
        TrackId = trackId;
        ChangedAt = DateTime.Now;
    }
}
