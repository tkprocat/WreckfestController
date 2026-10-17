using Microsoft.Extensions.Configuration;
using WreckfestController.Models;
using WreckfestController.Services.Assistant;

namespace WreckfestController.Tests.Services.Assistant;

/// <summary>The AI assistant's settings, from user-settings.json's Assistant section.</summary>
public sealed class AssistantConfigurationTests
{
    private static AssistantSettings Read(Dictionary<string, string?> values) =>
        AssistantConfiguration.Read(new ConfigurationBuilder().AddInMemoryCollection(values).Build());

    [Fact]
    public void WithNoSection_ItIsOff_WithTheDefaults()
    {
        var settings = Read([]);

        Assert.False(settings.Enabled);
        Assert.Equal(string.Empty, settings.ApiKey);
        Assert.Equal(AssistantSettings.DefaultBaseUrl, settings.BaseUrl);
        Assert.Equal(AssistantSettings.DefaultModel, settings.Model);
        Assert.Equal(AssistantSettings.DefaultDailyRequestLimit, settings.DailyRequestLimit);
    }

    [Fact]
    public void TheSection_IsRead_AndTidied()
    {
        var settings = Read(new()
        {
            ["Assistant:Enabled"] = "true",
            ["Assistant:ApiKey"] = " sk-test ",
            ["Assistant:BaseUrl"] = " http://localhost:11434/v1/ ",
            ["Assistant:Model"] = " qwen3:8b ",
            ["Assistant:DailyRequestLimit"] = "999999999",
        });

        Assert.True(settings.Enabled);
        Assert.Equal("sk-test", settings.ApiKey);
        Assert.Equal("http://localhost:11434/v1", settings.BaseUrl);
        Assert.Equal("qwen3:8b", settings.Model);
        Assert.Equal(AssistantSettings.MaxDailyRequestLimit, settings.DailyRequestLimit);
    }
}
