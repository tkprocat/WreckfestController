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
        bool.TryParse(configuration["Webhooks:Enabled"], out var enabled) && enabled;

    public static string? GetBaseUrl(IConfiguration configuration, ILogger logger)
    {
        var baseUrl = configuration["Webhooks:BaseUrl"];
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
