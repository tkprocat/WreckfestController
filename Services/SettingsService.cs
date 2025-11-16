using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WreckfestController.Models;

namespace WreckfestController.Services;

/// <summary>
/// Service for managing user settings stored in user-settings.json
/// </summary>
public class SettingsService
{
    private readonly string _userSettingsPath;
    private readonly ILogger<SettingsService> _logger;
    private readonly IConfiguration _configuration;

    public SettingsService(IConfiguration configuration, ILogger<SettingsService> logger)
    {
        _logger = logger;
        _configuration = configuration;
        _userSettingsPath = ResolveUserSettingsPath(configuration);

        _logger.LogInformation("User settings path: {Path}", _userSettingsPath);
    }

    /// <summary>
    /// Resolves the user settings file path based on configuration
    /// </summary>
    private static string ResolveUserSettingsPath(IConfiguration configuration)
    {
        var configuredPath = configuration["UserSettingsPath"];

        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            // Default to %LocalAppData%\WreckfestController\user-settings.json
            var appDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WreckfestController"
            );
            Directory.CreateDirectory(appDataPath);
            return Path.Combine(appDataPath, "user-settings.json");
        }
        else
        {
            // Use configured path (expand environment variables)
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
    /// Gets the resolved user settings file path
    /// </summary>
    public string GetUserSettingsPath() => _userSettingsPath;

    /// <summary>
    /// Loads user settings from file, or returns defaults if file doesn't exist
    /// </summary>
    public UserSettings LoadSettings()
    {
        if (!File.Exists(_userSettingsPath))
        {
            _logger.LogInformation("No user settings file found at {Path}, using defaults", _userSettingsPath);
            return CreateDefaultSettings();
        }

        try
        {
            var json = File.ReadAllText(_userSettingsPath);
            var settings = JsonSerializer.Deserialize<UserSettings>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip
            });

            return settings ?? CreateDefaultSettings();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading user settings from {Path}", _userSettingsPath);
            return CreateDefaultSettings();
        }
    }

    /// <summary>
    /// Saves user settings to file
    /// </summary>
    public void SaveSettings(UserSettings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            File.WriteAllText(_userSettingsPath, json);
            _logger.LogInformation("Settings saved to {Path}", _userSettingsPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving settings to {Path}", _userSettingsPath);
            throw new InvalidOperationException($"Failed to save settings: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Creates default settings from appsettings.json configuration
    /// </summary>
    private UserSettings CreateDefaultSettings()
    {
        return new UserSettings
        {
            WreckfestServer = new WreckfestServerSettings
            {
                ServerPath = _configuration["WreckfestServer:ServerPath"],
                ServerArguments = _configuration["WreckfestServer:ServerArguments"],
                WorkingDirectory = _configuration["WreckfestServer:WorkingDirectory"],
                LogFilePath = _configuration["WreckfestServer:LogFilePath"]
            },
            WreckfestWeb = new WreckfestWebSettings
            {
                WebhookBaseUrl = _configuration["WreckfestWeb:WebhookBaseUrl"]
            },
            Kestrel = new KestrelSettings
            {
                Urls = _configuration["Kestrel:Urls"]
            }
        };
    }

    /// <summary>
    /// Gets a specific setting value with fallback to appsettings.json
    /// </summary>
    public string? GetSetting(string key)
    {
        return _configuration[key];
    }
}
