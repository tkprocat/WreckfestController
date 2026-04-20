using System.Text.RegularExpressions;

namespace WreckfestController.Services;

public class TrackChangeTracker
{
    private readonly ILogger<TrackChangeTracker> _logger;
    private readonly WreckfestWebWebhookService _webhookService;
    private string? _currentTrack = null;
    private readonly object _lock = new();

    /// <summary>
    /// Event raised when the track changes
    /// </summary>
    public event Action<TrackChangeEvent>? TrackChanged;

    public TrackChangeTracker(ILogger<TrackChangeTracker> logger, WreckfestWebWebhookService webhookService)
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
            OnTrackChanged(trackId);
        }
    }

    /// <summary>
    /// Handle track change event
    /// </summary>
    private void OnTrackChanged(string trackId)
    {
        lock (_lock)
        {
            _currentTrack = trackId;
            _logger.LogInformation("Track changed to: {TrackId}", trackId);
            TrackChanged?.Invoke(new TrackChangeEvent(trackId));

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

}

public class TrackChangeEvent
{
    public string TrackId { get; set; } = string.Empty;
    public DateTime ChangedAt { get; set; }

    public TrackChangeEvent(string trackId)
    {
        TrackId = trackId;
        ChangedAt = DateTime.UtcNow;
    }
}
