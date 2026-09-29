using System.IO;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WreckfestController.Controllers;
using WreckfestController.Models;
using WreckfestController.Services.Config;
using WreckfestController.Services.Publishing;
using WreckfestController.Services.ServerControl;
using WreckfestController.Services.Tracking;

namespace WreckfestController.Tests.Controllers;

/// <summary>
/// The config endpoints against a real server_config.cfg: what they answer must be what
/// the file now holds, never what was asked for.
/// </summary>
public sealed class ConfigControllerFileTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"wf-config-api-{Guid.NewGuid():N}");
    private readonly string _file;
    private readonly ConfigController _controller;

    public ConfigControllerFileTests()
    {
        Directory.CreateDirectory(_folder);
        _file = Path.Combine(_folder, "server_config.cfg");
        var server = TestSettings.Server(workingDirectory: _folder);
        var events = Mock.Of<IServerEventPublisher>();
        var manager = new Mock<ServerManager>(
            Mock.Of<Microsoft.Extensions.Configuration.IConfiguration>(), server, TestSettings.SteamCmd(),
            Mock.Of<ILogger<ServerManager>>(),
            new PlayerTracker(Mock.Of<ILogger<PlayerTracker>>(), events),
            new TrackChangeTracker(Mock.Of<ILogger<TrackChangeTracker>>(), events),
            new ServerInfoTracker(Mock.Of<ILogger<ServerInfoTracker>>()),
            events) { CallBase = false };
        _controller = new ConfigController(
            new ConfigService(server, NullLogger<ConfigService>.Instance),
            manager.Object,
            NullLogger<ConfigController>.Instance).Hosted();
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void PutBasic_AnswersWithWhatTheFileNowHolds()
    {
        File.WriteAllLines(_file, ["server_name=Old", "max_players=24", "", "# Event Loop", "el_add=urban09_1"]);

        var result = _controller.UpdateBasicConfig(Json("""{"serverName":"New","maxPlayers":20}"""));

        Assert.Equal(("New", 20), (result.Value!.ServerName, result.Value.MaxPlayers));
        Assert.Contains("max_players=20", File.ReadAllLines(_file));
    }

    // WriteBasicConfig only rewrites lines that exist: a commented-out or missing key would
    // be silently dropped while the answer claimed it was saved.
    [Theory]
    [InlineData("# max_players=24")]
    [InlineData("")]
    public void PutBasic_AKeyWithNoActiveLine_IsRefused_AndNothingIsWritten(string maxPlayersLine)
    {
        File.WriteAllLines(_file, ["server_name=Old", maxPlayersLine, "", "# Event Loop", "el_add=urban09_1"]);
        var before = File.ReadAllText(_file);

        var result = _controller.UpdateBasicConfig(Json("""{"serverName":"New","maxPlayers":20}"""));

        var refusal = ControllerTesting.RefusalOf(result);
        Assert.Contains("max_players", refusal, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(_file));
    }

    // The writer leaves everything below the heading alone, and the later value wins, so the
    // change would not take effect: refused up front rather than "saved" and read back as 12.
    [Fact]
    public void PutBasic_AKeySetAgainBelowTheEventLoop_IsRefused_AndNothingIsWritten()
    {
        File.WriteAllLines(_file, ["server_name=Old", "max_players=24", "", "# Event Loop", "max_players=12", "el_add=urban09_1"]);
        var before = File.ReadAllText(_file);

        var result = _controller.UpdateBasicConfig(Json("""{"maxPlayers":20}"""));

        var refusal = ControllerTesting.RefusalOf(result);
        Assert.Contains("max_players", refusal, StringComparison.Ordinal);
        Assert.Contains("below '# Event Loop'", refusal, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(_file));
    }

    [Fact]
    public void PutTracks_AnswersWithWhatTheFileNowHolds()
    {
        File.WriteAllLines(_file, ["server_name=Old", "", "# Event Loop", "el_add=urban09_1"]);

        var result = _controller.UpdateEventLoopTracks(new UpdateEventLoopTracksRequest(
            "Evening",
            [new EventLoopTrack { Track = "fields14", Gamemode = "racing", Laps = 3 }]));

        Assert.Equal(1, result.Value!.Count);
        Assert.Equal(("fields14", 3), (result.Value.Tracks[0].Track, result.Value.Tracks[0].Laps));
    }

    // Without the heading there is nowhere to write the loop: it used to answer 200 anyway.
    [Fact]
    public void PutTracks_WithoutAnEventLoopHeading_IsRefused_AndNothingIsWritten()
    {
        File.WriteAllLines(_file, ["server_name=Old", "el_add=urban09_1"]);
        var before = File.ReadAllText(_file);

        var result = _controller.UpdateEventLoopTracks(new UpdateEventLoopTracksRequest(
            "Evening",
            [new EventLoopTrack { Track = "fields14" }]));

        Assert.Contains("# Event Loop", ControllerTesting.RefusalOf(result), StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(_file));
    }

    // The 409 names keys by ServerConfigPatch.KeyOf: every field the patch can set must map
    // to the key the file uses, or a present line would be reported missing.
    [Fact]
    public void EveryPatchableField_MapsToTheKeyTheConfigFileUses()
    {
        var patchable = typeof(ServerConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.Name != nameof(ServerConfig.Log));

        foreach (var property in patchable)
        {
            var config = new ServerConfig();
            var sample = property.PropertyType == typeof(int) ? "4321" : "sample-value";
            config.ApplyConfigValue(ServerConfigPatch.KeyOf(property.Name), sample);

            Assert.True(
                Equals(property.GetValue(config)?.ToString(), sample),
                $"{property.Name}: key '{ServerConfigPatch.KeyOf(property.Name)}' is not the one server_config.cfg uses");
        }
    }
}
