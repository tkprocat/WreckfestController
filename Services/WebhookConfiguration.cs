using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace WreckfestController.Services;

/// <summary>
/// Resolves outbound webhook settings while preserving support for legacy keys.
/// </summary>
public static class WebhookConfiguration
{
    public const string DefaultBaseUrl = "http://localhost:8000/api/webhooks";

    /// <summary>
    /// Outbound webhooks are opt-in. Without this the base URL falls back to a
    /// default, so an unconfigured install would POST to an endpoint nobody asked for.
    /// </summary>
    public static bool IsEnabled(IConfiguration configuration) =>
        IsFlagSet(configuration) && HasApiKey(configuration);

    /// <summary>True when Webhooks:Enabled parses as true.</summary>
    public static bool IsFlagSet(IConfiguration configuration) =>
        bool.TryParse(configuration["Webhooks:Enabled"], out var enabled) && enabled;

    /// <summary>
    /// True when an outbound key is configured. Sending without one would post
    /// console output and player events to the endpoint unauthenticated.
    /// </summary>
    public static bool HasApiKey(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(GetApiKeyRaw(configuration));

    private static string? GetApiKeyRaw(IConfiguration configuration) =>
        FirstNonBlank(
            configuration["Webhooks:ApiKey"],
            configuration["Webhooks:WebhookApiKey"],
            configuration["WreckfestWeb:WebhookApiKey"]);

    private static string? FirstNonBlank(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    public static string? GetBaseUrl(IConfiguration configuration, ILogger logger)
    {
        var baseUrl = configuration["Webhooks:BaseUrl"];
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            return baseUrl;
        }

        // user-settings.json is a configuration source (Program.cs), and it persists
        // this section through WreckfestWebSettings, whose properties are still named
        // WebhookBaseUrl/WebhookApiKey. Anything saved from the Configuration tab
        // therefore arrives under these keys rather than the canonical ones.
        baseUrl = configuration["Webhooks:WebhookBaseUrl"];
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            return baseUrl;
        }

        baseUrl = configuration["WreckfestWeb:WebhookBaseUrl"];
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            LogDeprecationWarning(logger, "WreckfestWeb:WebhookBaseUrl", "Webhooks:BaseUrl");
            return baseUrl;
        }

        baseUrl = configuration["Laravel:WebhookBaseUrl"];
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            LogDeprecationWarning(logger, "Laravel:WebhookBaseUrl", "Webhooks:BaseUrl");
            return baseUrl;
        }

        return null;
    }

    public static string? GetApiKey(IConfiguration configuration, ILogger logger)
    {
        var apiKey = configuration["Webhooks:ApiKey"];
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            return apiKey;
        }

        // As above: the name the Configuration tab actually writes.
        apiKey = configuration["Webhooks:WebhookApiKey"];
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            return apiKey;
        }

        apiKey = configuration["WreckfestWeb:WebhookApiKey"];
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            LogDeprecationWarning(logger, "WreckfestWeb:WebhookApiKey", "Webhooks:ApiKey");
            return apiKey;
        }

        return null;
    }

    private static void LogDeprecationWarning(ILogger logger, string legacyKey, string replacementKey)
    {
        logger.LogWarning(
            "{LegacyKey} is deprecated; configure {ReplacementKey} instead",
            legacyKey,
            replacementKey);
    }
}
