using System.Text.Json.Serialization;

namespace WreckfestController.Models;

/// <summary>
/// The settings a person edits, one property per database section. In 1.x this was the
/// shape of user-settings.json; 2.0 keeps the sections in the database.
/// </summary>
public class UserSettings
{
    [JsonPropertyName("WreckfestServer")]
    public WreckfestServerSettings? WreckfestServer { get; set; }

    [JsonPropertyName("SteamCmd")]
    public SteamCmdSettings? SteamCmd { get; set; }

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
    /// Off, Voting or Direct. See <c>Services/Voting/VoteModes.cs</c>. Held as a string,
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

    /// <summary>Pause between the lines of a multi-line chat reply, so the game shows them in order.</summary>
    [JsonPropertyName("MessageDelayMs")]
    public int MessageDelayMs { get; set; } = 250;

    /// <summary>Ignore chat commands while a race is running.</summary>
    [JsonPropertyName("SuppressCommandsDuringRace")]
    public bool SuppressCommandsDuringRace { get; set; }
}

/// <summary>The public home page's settings.</summary>
public class PublicSiteSettings
{
    public const int DefaultFeedItemCount = 10;
    public const int MaxFeedItemCount = 50;

    /// <summary>
    /// How many items the front page's results feed shows: a race outside a cup is one
    /// item, and so is a cup run, however many races it had.
    /// </summary>
    [JsonPropertyName("FeedItemCount")]
    public int FeedItemCount { get; set; } = DefaultFeedItemCount;
}

/// <summary>
/// The AI assistant on the public site: an OpenAI-compatible chat API that the controller
/// calls with its own key, never the visitor's browser.
/// </summary>
public class AssistantSettings
{
    public const string DefaultBaseUrl = "https://api.openai.com/v1";
    public const string DefaultModel = "gpt-5-mini";
    public const int DefaultDailyRequestLimit = 200;
    public const int MaxDailyRequestLimit = 100_000;

    /// <summary>Off until an admin turns it on; it also needs <see cref="ApiKey"/>.</summary>
    [JsonPropertyName("Enabled")]
    public bool Enabled { get; set; }

    /// <summary>
    /// The API's base address, ending before <c>/chat/completions</c>. OpenAI by default;
    /// any OpenAI-compatible service (OpenRouter, a local Ollama) works.
    /// </summary>
    [JsonPropertyName("BaseUrl")]
    public string BaseUrl { get; set; } = DefaultBaseUrl;

    [JsonPropertyName("Model")]
    public string Model { get; set; } = DefaultModel;

    /// <summary>Write-only over the API: reads show only whether one is set.</summary>
    [JsonPropertyName("ApiKey")]
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Questions answered per UTC day, across every visitor, to cap the bill. Counted in
    /// memory, so a restart starts the day's count again.
    /// </summary>
    [JsonPropertyName("DailyRequestLimit")]
    public int DailyRequestLimit { get; set; } = DefaultDailyRequestLimit;
}

/// <summary>
/// A votable track as voting uses it: the game's variant id and a display name. Comes
/// from the catalogue (<c>IVotableTracks</c>).
/// </summary>
public class AllowedVoteTrack
{
    [JsonPropertyName("Id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("Name")]
    public string Name { get; set; } = string.Empty;
}
