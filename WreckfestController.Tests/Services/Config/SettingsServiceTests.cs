using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using WreckfestController.Models;
using Xunit;
using WreckfestController.Services.Config;

namespace WreckfestController.Tests.Services.Config;

public class SettingsServiceTests
{
    [Fact]
    public void LoadSettings_LeavesTheFilesTracksAlone()
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
                })
                .Build();

            var service = new SettingsService(configuration, Mock.Of<ILogger<SettingsService>>());

            var settings = service.LoadSettings();

            // Votable tracks come from the catalogue now; nothing fills the file's list,
            // not even a Vote:AllowedTracks left in configuration.
            Assert.NotNull(settings.Vote);
            Assert.Empty(settings.Vote.AllowedTracks);
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
