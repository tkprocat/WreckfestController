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

            settings ??= CreateDefaultSettings();
            var migratedWebhookSettings = settings.WreckfestWeb is not null;
            var normalizedSettings = NormalizeSettings(settings);

            if (migratedWebhookSettings)
            {
                try
                {
                    SaveSettings(normalizedSettings);
                    _logger.LogInformation("Migrated legacy WreckfestWeb user settings to Webhooks");
                }
                catch (Exception ex)
                {
                    // The in-memory migration still lets the current run work when the
                    // settings file cannot be rewritten.
                    _logger.LogError(ex, "Unable to persist migrated webhook user settings to {Path}", _userSettingsPath);
                }
            }

            return normalizedSettings;
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
            settings = NormalizeSettings(settings);
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
    /// Creates default settings with sensible defaults
    /// </summary>
    private UserSettings CreateDefaultSettings()
    {
        return new UserSettings
        {
            WreckfestServer = new WreckfestServerSettings
            {
                ServerPath = _configuration["WreckfestServer:ServerPath"] ?? "",
                ServerArguments = _configuration["WreckfestServer:ServerArguments"] ?? "-s server_config=server_config.cfg",
                WorkingDirectory = _configuration["WreckfestServer:WorkingDirectory"] ?? "",
                LogFilePath = _configuration["WreckfestServer:LogFilePath"] ?? "",
                OutputMode = ServerOutputModes.InjectedHook
            },
            SteamCmd = new SteamCmdSettings
            {
                SteamCmdPath = _configuration["SteamCmd:SteamCmdPath"] ?? "",
                WreckfestAppId = _configuration["SteamCmd:WreckfestAppId"] ?? "361580"
            },
            Webhooks = new WreckfestWebSettings
            {
                WebhookBaseUrl = WebhookConfiguration.GetBaseUrl(_configuration, _logger) ?? WebhookConfiguration.DefaultBaseUrl,
                WebhookApiKey = WebhookConfiguration.GetApiKey(_configuration, _logger) ?? ""
            },
            Vote = new VoteSettings
            {
                Enabled = _configuration.GetValue("Vote:Enabled", true),
                Mode = VoteModes.Normalize(
                    _configuration["Vote:Mode"],
                    _configuration.GetValue<bool?>("Vote:Enabled")),
                DirectCooldownSeconds = _configuration.GetValue<int?>("Vote:DirectCooldownSeconds") ?? 30,
                VoteTimeoutSeconds = _configuration.GetValue<int?>("Vote:VoteTimeoutSeconds") ?? 30,
                MaxLapsAllowed = _configuration.GetValue<int?>("Vote:MaxLapsAllowed") ?? 10,
                AllowedTracks = _configuration.GetSection("Vote:AllowedTracks").Get<List<AllowedVoteTrack>>() ?? new()
            }
        };
    }

    private UserSettings NormalizeSettings(UserSettings settings)
    {
        var defaultTracks = _configuration.GetSection("Vote:AllowedTracks").Get<List<AllowedVoteTrack>>() ?? new();

        settings.Vote ??= new VoteSettings
        {
            Enabled = _configuration.GetValue("Vote:Enabled", true),
            Mode = VoteModes.Normalize(
                _configuration["Vote:Mode"],
                _configuration.GetValue<bool?>("Vote:Enabled")),
            DirectCooldownSeconds = _configuration.GetValue<int?>("Vote:DirectCooldownSeconds") ?? 30,
            VoteTimeoutSeconds = _configuration.GetValue<int?>("Vote:VoteTimeoutSeconds") ?? 30,
            MaxLapsAllowed = _configuration.GetValue<int?>("Vote:MaxLapsAllowed") ?? 10
        };

        // Canonicalise the non-null path too, and keep the legacy Enabled flag mirroring
        // Mode. SaveSettings rewrites the whole file, so leaving a stale Enabled behind
        // would let the two disagree.
        settings.Vote.Mode = VoteModes.Normalize(settings.Vote.Mode, settings.Vote.Enabled);
        settings.Vote.Enabled = settings.Vote.Mode != VoteModes.Off;

        if (settings.Vote.AllowedTracks.Count == 0 && defaultTracks.Count > 0)
        {
            settings.Vote.AllowedTracks = defaultTracks;
        }

        settings.WreckfestServer ??= new WreckfestServerSettings();
        settings.WreckfestServer.OutputMode = ServerOutputModes.InjectedHook;

        settings.Webhooks ??= settings.WreckfestWeb ?? new WreckfestWebSettings();
        settings.WreckfestWeb = null;

        return settings;
    }

    /// <summary>
    /// Gets a specific setting value with fallback to appsettings.json
    /// </summary>
    public string? GetSetting(string key)
    {
        return _configuration[key];
    }
}
