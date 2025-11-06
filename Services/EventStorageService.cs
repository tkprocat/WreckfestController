using System.Text.Json;
using WreckfestController.Models;

namespace WreckfestController.Services;

/// <summary>
/// Service responsible for persisting and loading the event schedule from disk.
/// Stores the schedule as JSON in the Data directory.
/// </summary>
public class EventStorageService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<EventStorageService> _logger;
    private readonly string _scheduleFilePath;
    private readonly JsonSerializerOptions _jsonOptions;

    public EventStorageService(IConfiguration configuration, ILogger<EventStorageService> logger)
    {
        _configuration = configuration;
        _logger = logger;

        // Determine storage path - use Data directory in working directory or application directory
        var workingDir = _configuration["WreckfestServer:WorkingDirectory"];
        var baseDir = !string.IsNullOrEmpty(workingDir)
            ? workingDir
            : AppDomain.CurrentDomain.BaseDirectory;

        var dataDir = Path.Combine(baseDir, "Data");

        // Ensure Data directory exists
        if (!Directory.Exists(dataDir))
        {
            Directory.CreateDirectory(dataDir);
            _logger.LogInformation("Created Data directory at: {DataDir}", dataDir);
        }

        _scheduleFilePath = Path.Combine(dataDir, "event-schedule.json");

        // Configure JSON serialization options
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        _logger.LogInformation("EventStorageService initialized. Schedule path: {Path}", _scheduleFilePath);
    }

    /// <summary>
    /// Loads the event schedule from disk
    /// </summary>
    /// <returns>The loaded schedule, or an empty schedule if file doesn't exist</returns>
    public EventSchedule LoadSchedule()
    {
        try
        {
            if (!File.Exists(_scheduleFilePath))
            {
                _logger.LogInformation("No existing schedule file found at {Path}. Returning empty schedule.", _scheduleFilePath);
                return new EventSchedule
                {
                    Events = new List<Event>(),
                    LastUpdated = DateTime.UtcNow
                };
            }

            var json = File.ReadAllText(_scheduleFilePath);
            var schedule = JsonSerializer.Deserialize<EventSchedule>(json, _jsonOptions);

            if (schedule == null)
            {
                _logger.LogWarning("Failed to deserialize schedule from {Path}. Returning empty schedule.", _scheduleFilePath);
                return new EventSchedule
                {
                    Events = new List<Event>(),
                    LastUpdated = DateTime.UtcNow
                };
            }

            // Ensure all event start times are converted to UTC for proper comparison
            // JSON may deserialize "2025-11-05T23:25:00+01:00" with Kind=Local or Unspecified
            foreach (var evt in schedule.Events)
            {
                if (evt.StartTime.Kind != DateTimeKind.Utc)
                {
                    evt.StartTime = evt.StartTime.ToUniversalTime();
                    _logger.LogDebug("Converted event {EventId} StartTime to UTC: {UtcTime}", evt.Id, evt.StartTime);
                }
            }

            _logger.LogInformation("Loaded schedule with {Count} events from {Path}", schedule.Events.Count, _scheduleFilePath);
            return schedule;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading schedule from {Path}. Returning empty schedule.", _scheduleFilePath);
            return new EventSchedule
            {
                Events = new List<Event>(),
                LastUpdated = DateTime.UtcNow
            };
        }
    }

    /// <summary>
    /// Saves the event schedule to disk
    /// </summary>
    /// <param name="schedule">The schedule to save</param>
    /// <returns>True if save was successful, false otherwise</returns>
    public bool SaveSchedule(EventSchedule schedule)
    {
        try
        {
            // Update last updated timestamp
            schedule.LastUpdated = DateTime.UtcNow;

            // Serialize to JSON
            var json = JsonSerializer.Serialize(schedule, _jsonOptions);

            // Write to file (atomic write using temp file)
            var tempPath = _scheduleFilePath + ".tmp";
            File.WriteAllText(tempPath, json);

            // Replace existing file
            if (File.Exists(_scheduleFilePath))
            {
                File.Delete(_scheduleFilePath);
            }
            File.Move(tempPath, _scheduleFilePath);

            _logger.LogInformation("Saved schedule with {Count} events to {Path}", schedule.Events.Count, _scheduleFilePath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving schedule to {Path}", _scheduleFilePath);
            return false;
        }
    }

    /// <summary>
    /// Replaces the entire schedule with a new schedule from Laravel
    /// </summary>
    /// <param name="events">List of events from Laravel</param>
    /// <returns>True if save was successful, false otherwise</returns>
    public bool ReplaceSchedule(List<Event> events)
    {
        var schedule = new EventSchedule
        {
            Events = events,
            LastUpdated = DateTime.UtcNow
        };

        var result = SaveSchedule(schedule);

        if (result)
        {
            _logger.LogInformation("Replaced schedule with {Count} events from Laravel", events.Count);
        }

        return result;
    }

    /// <summary>
    /// Gets the path to the schedule file
    /// </summary>
    public string GetScheduleFilePath() => _scheduleFilePath;

    /// <summary>
    /// Checks if a schedule file exists
    /// </summary>
    public bool ScheduleExists() => File.Exists(_scheduleFilePath);

    /// <summary>
    /// Deletes the schedule file (use with caution)
    /// </summary>
    /// <returns>True if file was deleted or didn't exist, false on error</returns>
    public bool DeleteSchedule()
    {
        try
        {
            if (File.Exists(_scheduleFilePath))
            {
                File.Delete(_scheduleFilePath);
                _logger.LogInformation("Deleted schedule file at {Path}", _scheduleFilePath);
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting schedule file at {Path}", _scheduleFilePath);
            return false;
        }
    }

    /// <summary>
    /// Creates a backup of the current schedule
    /// </summary>
    /// <returns>Path to backup file if successful, null otherwise</returns>
    public string? BackupSchedule()
    {
        try
        {
            if (!File.Exists(_scheduleFilePath))
            {
                _logger.LogWarning("No schedule file to backup at {Path}", _scheduleFilePath);
                return null;
            }

            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            var backupPath = _scheduleFilePath.Replace(".json", $".backup.{timestamp}.json");

            File.Copy(_scheduleFilePath, backupPath);
            _logger.LogInformation("Created backup at {BackupPath}", backupPath);

            return backupPath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating backup of schedule");
            return null;
        }
    }
}
