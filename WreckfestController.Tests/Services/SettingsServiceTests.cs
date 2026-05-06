using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using WreckfestController.Models;
using WreckfestController.Services;
using Xunit;

namespace WreckfestController.Tests.Services;

public class SettingsServiceTests
{
    [Fact]
    public void LoadSettings_WhenUserSettingsHasEmptyAllowedTracks_FillsConfiguredDefaultTracks()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), $"wreckfest-settings-{Guid.NewGuid():N}.json");
        try
        {
            var userSettings = new UserSettings
            {
                WreckfestServer = new WreckfestServerSettings(),
                Vote = new VoteSettings
                {
                    Enabled = true,
                    VoteTimeoutSeconds = 30,
                    MaxLapsAllowed = 10,
                    AllowedTracks = new List<AllowedVoteTrack>()
                }
            };
            File.WriteAllText(settingsPath, JsonSerializer.Serialize(userSettings));

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["UserSettingsPath"] = settingsPath,
                    ["Vote:AllowedTracks:0:Id"] = "misc_birkeland",
                    ["Vote:AllowedTracks:0:Name"] = "TVTP Misc Birkeland"
                })
                .Build();

            var service = new SettingsService(configuration, Mock.Of<ILogger<SettingsService>>());

            var settings = service.LoadSettings();

            Assert.NotNull(settings.Vote);
            var track = Assert.Single(settings.Vote.AllowedTracks);
            Assert.Equal("misc_birkeland", track.Id);
            Assert.Equal("TVTP Misc Birkeland", track.Name);
        }
        finally
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }
        }
    }
}
