using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WreckfestController.Models;

namespace WreckfestController.Services;

/// <summary>
/// Service responsible for persisting and loading the event schedule from disk.
/// Stores the schedule as JSON in %LocalAppData%\WreckfestController\ by default
/// (same location as user-settings.json) to avoid Windows folder permission issues.
/// </summary>
public class EventStorageService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<EventStorageService> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    public EventStorageService(IConfiguration configuration, ILogger<EventStorageService> logger)
    {
        _configuration = configuration;
        _logger = logger;

        // Configure JSON serialization options
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        _logger.LogInformation("EventStorageService initialized");
    }

    /// <summary>
    /// Gets the schedule file path from configuration (supports hot-reload)
    /// </summary>
    private string GetScheduleFilePath()
    {
        var configuredPath = _configuration["EventSchedulePath"];

        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            // Default to %LocalAppData%\WreckfestController\event-schedule.json
            // Same location as user-settings.json to avoid Windows folder permission issues
            var appDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WreckfestController"
            );
            Directory.CreateDirectory(appDataPath);
            return Path.Combine(appDataPath, "event-schedule.json");
        }
        else
        {
            // Use configured path (expand environment variables like %LocalAppData%)
            var expandedPath = Environment.ExpandEnvironmentVariables(configuredPath);

            // Ensure directory exists
            var directory = Path.GetDirectoryName(expandedPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            return expandedPath;
        }
    }

    /// <summary>
    /// Loads the event schedule from disk
    /// </summary>
    /// <returns>The loaded schedule, or an empty schedule if file doesn't exist</returns>
    public EventSchedule LoadSchedule()
    {
        var scheduleFilePath = GetScheduleFilePath();
        try
        {
            if (!File.Exists(scheduleFilePath))
            {
                _logger.LogInformation("No existing schedule file found at {Path}. Returning empty schedule.", scheduleFilePath);
                return new EventSchedule
                {
                    Events = new List<Event>(),
                    LastUpdated = DateTime.UtcNow
                };
            }

            var json = File.ReadAllText(scheduleFilePath);
            var schedule = JsonSerializer.Deserialize<EventSchedule>(json, _jsonOptions);

            if (schedule == null)
            {
                _logger.LogWarning("Failed to deserialize schedule from {Path}. Returning empty schedule.", scheduleFilePath);
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

            _logger.LogInformation("Loaded schedule with {Count} events from {Path}", schedule.Events.Count, scheduleFilePath);
            return schedule;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading schedule from {Path}. Returning empty schedule.", scheduleFilePath);
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
        var scheduleFilePath = GetScheduleFilePath();
        try
        {
            // Update last updated timestamp
            schedule.LastUpdated = DateTime.UtcNow;

            // Serialize to JSON
            var json = JsonSerializer.Serialize(schedule, _jsonOptions);

            // Write the replacement beside the original, then swap it in. File.Replace
            // overwrites in one step, so the schedule is never absent - the previous
            // version of this code deleted it first and could leave nothing behind.
            var tempPath = scheduleFilePath + ".tmp";
            File.WriteAllText(tempPath, json);

            if (File.Exists(scheduleFilePath))
            {
                File.Replace(tempPath, scheduleFilePath, null);
            }
            else
            {
                File.Move(tempPath, scheduleFilePath);
            }

            _logger.LogInformation("Saved schedule with {Count} events to {Path}", schedule.Events.Count, scheduleFilePath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving schedule to {Path}", scheduleFilePath);
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
    /// Checks if a schedule file exists
    /// </summary>
    public bool ScheduleExists() => File.Exists(GetScheduleFilePath());

    /// <summary>
    /// Deletes the schedule file (use with caution)
    /// </summary>
    /// <returns>True if file was deleted or didn't exist, false on error</returns>
    public bool DeleteSchedule()
    {
        var scheduleFilePath = GetScheduleFilePath();
        try
        {
            if (File.Exists(scheduleFilePath))
            {
                File.Delete(scheduleFilePath);
                _logger.LogInformation("Deleted schedule file at {Path}", scheduleFilePath);
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting schedule file at {Path}", scheduleFilePath);
            return false;
        }
    }

    /// <summary>
    /// Creates a backup of the current schedule
    /// </summary>
    /// <returns>Path to backup file if successful, null otherwise</returns>
    public string? BackupSchedule()
    {
        var scheduleFilePath = GetScheduleFilePath();
        try
        {
            if (!File.Exists(scheduleFilePath))
            {
                _logger.LogWarning("No schedule file to backup at {Path}", scheduleFilePath);
                return null;
            }

            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            var backupPath = scheduleFilePath.Replace(".json", $".backup.{timestamp}.json");

            File.Copy(scheduleFilePath, backupPath);
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
