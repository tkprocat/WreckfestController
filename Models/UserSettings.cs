using System.Text.Json.Serialization;

namespace WreckfestController.Models;

/// <summary>
/// Root model for user-settings.json
/// </summary>
public class UserSettings
{
    [JsonPropertyName("WreckfestServer")]
    public WreckfestServerSettings? WreckfestServer { get; set; }

    [JsonPropertyName("WreckfestWeb")]
    public WreckfestWebSettings? WreckfestWeb { get; set; }

    [JsonPropertyName("Kestrel")]
    public KestrelSettings? Kestrel { get; set; }
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
}

/// <summary>
/// WreckfestWeb webhook configuration settings
/// </summary>
public class WreckfestWebSettings
{
    [JsonPropertyName("WebhookBaseUrl")]
    public string? WebhookBaseUrl { get; set; }
}

/// <summary>
/// Kestrel web server configuration settings
/// </summary>
public class KestrelSettings
{
    [JsonPropertyName("Urls")]
    public string? Urls { get; set; }
}
