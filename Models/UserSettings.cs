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

    [JsonPropertyName("Webhooks")]
    public WreckfestWebSettings? Webhooks { get; set; }

    // Kept only to deserialize existing user-settings.json files. SettingsService
    // moves this value to Webhooks while loading the file.
    [JsonPropertyName("WreckfestWeb")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
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

    [JsonPropertyName("OutputMode")]
    public string? OutputMode { get; set; }
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
    /// <summary>
    /// Legacy on/off flag. Kept in sync with <see cref="Mode"/> so settings files
    /// written before Mode existed keep resolving correctly.
    /// </summary>
    [JsonPropertyName("Enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Off, Voting or Direct. See <c>Services/VoteModes.cs</c>. Held as a string,
    /// not an enum, so an unrecognised value degrades instead of throwing.
    /// </summary>
    [JsonPropertyName("Mode")]
    public string Mode { get; set; } = "Voting";

    /// <summary>
    /// Seconds after a successful direct track change during which further direct
    /// changes are refused. 0 disables the cooldown.
    /// </summary>
    [JsonPropertyName("DirectCooldownSeconds")]
    public int DirectCooldownSeconds { get; set; } = 30;

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
/// Outbound webhook configuration settings
/// </summary>
public class WreckfestWebSettings
{
    [JsonPropertyName("WebhookBaseUrl")]
    public string? WebhookBaseUrl { get; set; }

    [JsonPropertyName("WebhookApiKey")]
    public string? WebhookApiKey { get; set; }
}
