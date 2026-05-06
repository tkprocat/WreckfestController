using System.Text.Json.Serialization;

namespace WreckfestController.Models;

/// <summary>
/// Root model for user-settings.json
/// </summary>
public class UserSettings
{
    [JsonPropertyName("WreckfestServer")]
    public WreckfestServerSettings? WreckfestServer { get; set; }

    [JsonPropertyName("SteamCmd")]
    public SteamCmdSettings? SteamCmd { get; set; }

    [JsonPropertyName("WreckfestWeb")]
    public WreckfestWebSettings? WreckfestWeb { get; set; }

    [JsonPropertyName("Vote")]
    public VoteSettings? Vote { get; set; }
}

/// <summary>
/// Wreckfest server configuration settings
/// </summary>
public class WreckfestServerSettings
{
    [JsonPropertyName("ServerPath")]
    public string? ServerPath { get; set; }

    [JsonPropertyName("ServerArguments")]
    public string? ServerArguments { get; set; }

    [JsonPropertyName("WorkingDirectory")]
    public string? WorkingDirectory { get; set; }

    [JsonPropertyName("LogFilePath")]
    public string? LogFilePath { get; set; }

    [JsonPropertyName("UseConsoleMonitoring")]
    public bool? UseConsoleMonitoring { get; set; }

    [JsonPropertyName("OutputMode")]
    public string? OutputMode { get; set; }

    [JsonPropertyName("InputMode")]
    public string? InputMode { get; set; }
}

/// <summary>
/// SteamCmd configuration settings
/// </summary>
public class SteamCmdSettings
{
    [JsonPropertyName("SteamCmdPath")]
    public string? SteamCmdPath { get; set; }

    [JsonPropertyName("WreckfestAppId")]
    public string? WreckfestAppId { get; set; }
}

/// <summary>
/// Vote system configuration settings
/// </summary>
public class VoteSettings
{
    [JsonPropertyName("Enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("VoteTimeoutSeconds")]
    public int VoteTimeoutSeconds { get; set; } = 30;

    [JsonPropertyName("MaxLapsAllowed")]
    public int MaxLapsAllowed { get; set; } = 10;

    [JsonPropertyName("AllowedTracks")]
    public List<AllowedVoteTrack> AllowedTracks { get; set; } = new();
}

/// <summary>
/// Track allowed for player-initiated votes.
/// </summary>
public class AllowedVoteTrack
{
    [JsonPropertyName("Id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("Name")]
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// WreckfestWeb webhook configuration settings
/// </summary>
public class WreckfestWebSettings
{
    [JsonPropertyName("WebhookBaseUrl")]
    public string? WebhookBaseUrl { get; set; }

    [JsonPropertyName("WebhookApiKey")]
    public string? WebhookApiKey { get; set; }
}
