using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using WreckfestController.Models;
using WreckfestController.Services;
using Xunit;

namespace WreckfestController.Tests.Services;

public class WebhookConfigurationTests
{
    private static readonly ILogger Logger = Mock.Of<ILogger>();

    [Fact]
    public void GetBaseUrl_UsesNewSettingBeforeLegacySettings()
    {
        Assert.Equal(
            "https://new.example/webhooks",
            GetBaseUrl(new Dictionary<string, string?>
            {
                ["Webhooks:BaseUrl"] = "https://new.example/webhooks",
                ["WreckfestWeb:WebhookBaseUrl"] = "https://wreckfestweb.example/webhooks",
                ["Laravel:WebhookBaseUrl"] = "https://laravel.example/webhooks"
            }));

        Assert.Equal(
            "https://wreckfestweb.example/webhooks",
            GetBaseUrl(new Dictionary<string, string?>
            {
                ["WreckfestWeb:WebhookBaseUrl"] = "https://wreckfestweb.example/webhooks",
                ["Laravel:WebhookBaseUrl"] = "https://laravel.example/webhooks"
            }));

        Assert.Equal(
            "https://laravel.example/webhooks",
            GetBaseUrl(new Dictionary<string, string?>
            {
                ["Laravel:WebhookBaseUrl"] = "https://laravel.example/webhooks"
            }));

        Assert.Null(GetBaseUrl(new Dictionary<string, string?>()));
    }

    [Fact]
    public void GetApiKey_UsesNewSettingBeforeLegacySetting()
    {
        Assert.Equal(
            "new-api-key",
            GetApiKey(new Dictionary<string, string?>
            {
                ["Webhooks:ApiKey"] = "new-api-key",
                ["WreckfestWeb:WebhookApiKey"] = "legacy-api-key"
            }));

        Assert.Equal(
            "legacy-api-key",
            GetApiKey(new Dictionary<string, string?>
            {
                ["WreckfestWeb:WebhookApiKey"] = "legacy-api-key"
            }));

        Assert.Null(GetApiKey(new Dictionary<string, string?>()));
    }

    [Fact]
    public void LoadSettings_MigratesLegacyWebhookSettingsWithoutLosingValues()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), $"wreckfest-settings-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(settingsPath, """
                {
                  "WreckfestServer": {
                    "ServerPath": "C:\\Wreckfest\\Wreckfest_x64.exe"
                  },
                  "WreckfestWeb": {
                    "WebhookBaseUrl": "https://legacy.example/api/webhooks",
                    "WebhookApiKey": "legacy-api-key"
                  }
                }
                """);

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["UserSettingsPath"] = settingsPath
                })
                .Build();
            var service = new SettingsService(configuration, Mock.Of<ILogger<SettingsService>>());

            var settings = service.LoadSettings();

            Assert.NotNull(settings.Webhooks);
            Assert.Equal("https://legacy.example/api/webhooks", settings.Webhooks.WebhookBaseUrl);
            Assert.Equal("legacy-api-key", settings.Webhooks.WebhookApiKey);
            Assert.Null(settings.WreckfestWeb);
            Assert.Equal("C:\\Wreckfest\\Wreckfest_x64.exe", settings.WreckfestServer?.ServerPath);

            using var migratedDocument = JsonDocument.Parse(File.ReadAllText(settingsPath));
            var root = migratedDocument.RootElement;
            Assert.True(root.TryGetProperty("Webhooks", out var webhooks));
            Assert.False(root.TryGetProperty("WreckfestWeb", out _));
            Assert.Equal("https://legacy.example/api/webhooks", webhooks.GetProperty("WebhookBaseUrl").GetString());
            Assert.Equal("legacy-api-key", webhooks.GetProperty("WebhookApiKey").GetString());
        }
        finally
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }
        }
    }

    private static string? GetBaseUrl(Dictionary<string, string?> values) =>
        WebhookConfiguration.GetBaseUrl(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), Logger);

    private static string? GetApiKey(Dictionary<string, string?> values) =>
        WebhookConfiguration.GetApiKey(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), Logger);

    // Both integrations are opt-in. Absent means off, so an install that configures
    // neither never binds a port and never POSTs to the fallback base URL.
    [Fact]
    public void Webhooks_AreDisabled_WhenTheFlagIsAbsent()
    {
        Assert.False(WebhookConfiguration.IsEnabled(Build(new Dictionary<string, string?>())));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void Webhooks_FollowTheEnabledFlag(string value, bool expected)
    {
        Assert.Equal(
            expected,
            WebhookConfiguration.IsEnabled(Build(new Dictionary<string, string?>
            {
                ["Webhooks:Enabled"] = value,
                ["Webhooks:BaseUrl"] = "https://example.invalid/webhooks",
                ["Webhooks:ApiKey"] = "secret"
            })));
    }

    [Fact]
    public void Api_IsDisabled_WhenTheFlagIsAbsent()
    {
        Assert.False(ApiServer.IsEnabled(Build(new Dictionary<string, string?>())));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void Api_FollowsTheEnabledFlag(string value, bool expected)
    {
        Assert.Equal(
            expected,
            ApiServer.IsEnabled(Build(new Dictionary<string, string?>
            {
                ["Api:Enabled"] = value,
                ["Api:Key"] = "secret"
            })));
    }

    private static IConfiguration Build(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    // Enabled alone is not enough: a blank key would mean posting unauthenticated
    // outbound, and serving nothing but 401s inbound.
    [Fact]
    public void Webhooks_AreDisabled_WhenEnabledButKeyIsBlank()
    {
        Assert.False(WebhookConfiguration.IsEnabled(Build(new Dictionary<string, string?>
        {
            ["Webhooks:Enabled"] = "true",
            ["Webhooks:BaseUrl"] = "https://example.invalid/webhooks",
            ["Webhooks:ApiKey"] = "   "
        })));
    }

    [Fact]
    public void Webhooks_AreEnabled_WhenFlagSetAndKeyPresent()
    {
        Assert.True(WebhookConfiguration.IsEnabled(Build(new Dictionary<string, string?>
        {
            ["Webhooks:Enabled"] = "true",
            ["Webhooks:ApiKey"] = "secret"
        })));
    }

    [Fact]
    public void Webhooks_AcceptALegacyKey_WhenDecidingIfEnabled()
    {
        Assert.True(WebhookConfiguration.IsEnabled(Build(new Dictionary<string, string?>
        {
            ["Webhooks:Enabled"] = "true",
            ["WreckfestWeb:WebhookApiKey"] = "legacy-secret"
        })));
    }

    [Fact]
    public void Api_IsDisabled_WhenEnabledButKeyIsBlank()
    {
        Assert.False(ApiServer.IsEnabled(Build(new Dictionary<string, string?>
        {
            ["Api:Enabled"] = "true",
            ["Api:Key"] = "   "
        })));
    }

    [Fact]
    public void Api_IsEnabled_WhenFlagSetAndKeyPresent()
    {
        Assert.True(ApiServer.IsEnabled(Build(new Dictionary<string, string?>
        {
            ["Api:Enabled"] = "true",
            ["Api:Key"] = "secret"
        })));
    }

    [Fact]
    public void Api_IsDisabled_WhenKeyPresentButFlagUnset()
    {
        Assert.False(ApiServer.IsEnabled(Build(new Dictionary<string, string?>
        {
            ["Api:Key"] = "secret"
        })));
    }
}
