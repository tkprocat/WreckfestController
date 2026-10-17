using WreckfestController.Models;

namespace WreckfestController.Services.Assistant;

/// <summary>
/// The AI assistant's settings come from the <c>Assistant</c> section of the startup settings
/// (user-settings.json), read once when the API starts: the key is a secret for whoever runs
/// the PC, not for the web site, and every web user may use the assistant. Restart the
/// controller after changing them.
/// </summary>
public static class AssistantConfiguration
{
    public const string Section = "Assistant";

    public static AssistantSettings Read(IConfiguration configuration)
    {
        var section = configuration.GetSection(Section);
        var assistant = new AssistantSettings
        {
            Enabled = section.GetValue(nameof(AssistantSettings.Enabled), false),
            BaseUrl = section[nameof(AssistantSettings.BaseUrl)] ?? AssistantSettings.DefaultBaseUrl,
            Model = section[nameof(AssistantSettings.Model)] ?? AssistantSettings.DefaultModel,
            ApiKey = section[nameof(AssistantSettings.ApiKey)] ?? string.Empty,
            DailyRequestLimit = section.GetValue(nameof(AssistantSettings.DailyRequestLimit), AssistantSettings.DefaultDailyRequestLimit),
        };

        assistant.BaseUrl = string.IsNullOrWhiteSpace(assistant.BaseUrl)
            ? AssistantSettings.DefaultBaseUrl
            : assistant.BaseUrl.Trim().TrimEnd('/');
        assistant.Model = string.IsNullOrWhiteSpace(assistant.Model) ? AssistantSettings.DefaultModel : assistant.Model.Trim();
        assistant.ApiKey = assistant.ApiKey.Trim();
        assistant.DailyRequestLimit = Math.Clamp(assistant.DailyRequestLimit, 1, AssistantSettings.MaxDailyRequestLimit);
        return assistant;
    }
}
