using System.IO;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
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

    // The page greys out what cannot be saved, instead of failing on Save: a field whose
    // key is commented out (admin_steam_ids is by default) or overridden below the loop.
    [Fact]
    public void Fields_SayWhichSettingsCanBeSaved_AndWhyNot()
    {
        File.WriteAllLines(_file,
        [
            "server_name=Old",
            "max_players=24",
            "#admin_steam_ids=12345678912345678",
            "laps=3",
            "",
            "# Event Loop",
            "laps=5",
            "el_add=urban09_1",
        ]);

        var fields = _controller.GetBasicConfigFields().Value!.ToDictionary(f => f.Field);

        Assert.True(fields["serverName"].Savable);
        Assert.Equal("server_name", fields["serverName"].Key);
        Assert.Null(fields["serverName"].Reason);
        Assert.False(fields["adminSteamIds"].Savable);
        Assert.Contains("no active line", fields["adminSteamIds"].Reason, StringComparison.Ordinal);
        Assert.False(fields["laps"].Savable);
        Assert.Contains("below '# Event Loop'", fields["laps"].Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("log", fields.Keys);
    }

    // log= names a file: not in what the web reads.
    [Fact]
    public void TheApisServerConfig_HasNoLogField()
    {
        var json = JsonSerializer.Serialize(new ServerConfig(), new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.DoesNotContain("\"log\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"serverName\"", json, StringComparison.Ordinal);
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

    // The file has no version of its own: the rotation's version is a hash of what it holds,
    // so any edit, from anywhere, changes it.
    [Fact]
    public void GetTracks_GivesTheNameAndAVersion_ThatAnyEditChanges()
    {
        File.WriteAllLines(_file, ["server_name=Old", "", "# Event Loop", "#CollectionName Evening", "el_add=urban09_1", "el_laps=3"]);

        var first = _controller.GetEventLoopTracks().Value!;
        Assert.Equal("Evening", first.CollectionName);
        Assert.Equal($"\"{first.Version}\"", _controller.Response.Headers.ETag.ToString());
        Assert.Equal(first.Version, _controller.GetEventLoopTracks().Value!.Version);

        File.WriteAllLines(_file, ["server_name=Old", "", "# Event Loop", "#CollectionName Evening", "el_add=urban09_1", "el_laps=4"]);
        Assert.NotEqual(first.Version, _controller.GetEventLoopTracks().Value!.Version);
    }

    // With the version a GET gave, a rotation changed since is not overwritten: 409 with it as it is.
    [Fact]
    public void PutTracks_WithAStaleVersion_IsAConflict_AndNothingIsWritten()
    {
        File.WriteAllLines(_file, ["server_name=Old", "", "# Event Loop", "el_add=urban09_1"]);
        var read = _controller.GetEventLoopTracks().Value!;
        File.WriteAllLines(_file, ["server_name=Old", "", "# Event Loop", "el_add=fields14"]);
        var before = File.ReadAllText(_file);

        var result = Put($"\"{read.Version}\"", "loop");

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal("fields14", Assert.IsType<EventLoopResponse>(conflict.Value).Tracks[0].Track);
        Assert.Equal(before, File.ReadAllText(_file));
    }

    [Fact]
    public void PutTracks_WithTheCurrentVersion_Saves_AndGivesTheNewOne()
    {
        File.WriteAllLines(_file, ["server_name=Old", "", "# Event Loop", "el_add=urban09_1"]);
        var read = _controller.GetEventLoopTracks().Value!;

        var saved = Put($"\"{read.Version}\"", "fields14").Value!;

        Assert.Equal("fields14", saved.Tracks[0].Track);
        Assert.NotEqual(read.Version, saved.Version);
        Assert.Equal(saved.Version, _controller.GetEventLoopTracks().Value!.Version);
    }

    // Two writers holding the same version: the check and the write are one step, so only
    // one of them succeeds and the other gets the rotation as it now is.
    [Fact]
    public void ConcurrentSavesWithTheSameVersion_OnlyOneWins()
    {
        File.WriteAllLines(_file, ["server_name=Old", "", "# Event Loop", "#CollectionName Start", "el_add=urban09_1"]);
        var service = new ConfigService(TestSettings.Server(workingDirectory: _folder), NullLogger<ConfigService>.Instance);
        var version = ConfigService.EventLoopVersion(service.GetCurrentCollectionName(), service.ReadEventLoopTracks());

        var wins = 0;
        Parallel.For(0, 8, i =>
        {
            if (service.TryWriteEventLoopTracks($"Writer {i}", [new EventLoopTrack { Track = $"track{i}" }], version, out _))
            {
                Interlocked.Increment(ref wins);
            }
        });

        Assert.Equal(1, wins);
        Assert.Single(service.ReadEventLoopTracks());
    }

    // Every writer reads the whole file and writes it back: run together, one of them would
    // put back what it read and undo the others. They take turns, so each change stays.
    [Fact]
    public void DifferentWritersTogether_EachChangeStays()
    {
        var service = new ConfigService(TestSettings.Server(workingDirectory: _folder), NullLogger<ConfigService>.Instance);
        for (var round = 0; round < 20; round++)
        {
            File.WriteAllLines(_file, ["server_name=Old", "session_mode=normal", "", "# Event Loop", "#CollectionName Start", "el_add=urban09_1"]);
            var version = ConfigService.EventLoopVersion(service.GetCurrentCollectionName(), service.ReadEventLoopTracks());

            Parallel.Invoke(
                () => service.TryWriteEventLoopTracks("Mine", [new EventLoopTrack { Track = "fields14" }], version, out _),
                () => service.WriteSettings(new Dictionary<string, string> { ["session_mode"] = "30p-aggr" }),
                () => service.WriteEventLoopTracks("Deployed", [new EventLoopTrack { Track = "loop" }, new EventLoopTrack { Track = "arena" }]));

            var text = File.ReadAllText(_file);
            Assert.Contains("session_mode=30p-aggr", text, StringComparison.Ordinal);
            // Whichever rotation came last, it is whole: one name, and its own tracks.
            Assert.Single(File.ReadAllLines(_file), line => line.StartsWith("#CollectionName", StringComparison.Ordinal));
        }
    }

    // The old #CollectionName was kept above the new one and read first: a rename never showed.
    [Fact]
    public void PutTracks_Renames_AndKeepsOneCollectionName()
    {
        File.WriteAllLines(_file, ["server_name=Old", "", "# Event Loop", "#CollectionName First", "el_add=urban09_1"]);

        Put(null, "fields14");
        _controller.UpdateEventLoopTracks(new UpdateEventLoopTracksRequest("Second", [new EventLoopTrack { Track = "loop" }]));
        var read = _controller.GetEventLoopTracks().Value!;

        Assert.Equal("Second", read.CollectionName);
        Assert.Single(File.ReadAllLines(_file), line => line.TrimStart().StartsWith("#CollectionName", StringComparison.Ordinal));
    }

    // If-Match stays optional: a caller without it writes as before.
    [Fact]
    public void PutTracks_WithoutIfMatch_Saves()
    {
        File.WriteAllLines(_file, ["server_name=Old", "", "# Event Loop", "el_add=urban09_1"]);

        Assert.Equal("fields14", Put(null, "fields14").Value!.Tracks[0].Track);
    }

    private ActionResult<EventLoopResponse> Put(string? ifMatch, string track)
    {
        _controller.ControllerContext.HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        if (ifMatch is not null)
        {
            _controller.Request.Headers.IfMatch = ifMatch;
        }

        return _controller.UpdateEventLoopTracks(new UpdateEventLoopTracksRequest("Evening", [new EventLoopTrack { Track = track }]));
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

    // A config that cannot be read or written is refused without the exception's text,
    // which names the file's local path (#153).
    [Fact]
    public void AFileThatCannotBeRead_IsRefused_WithoutItsPath()
    {
        Directory.CreateDirectory(_file);

        var read = ControllerTesting.RefusalOf(_controller.GetBasicConfig());
        var tracks = ControllerTesting.RefusalOf(_controller.GetEventLoopTracks());
        var write = ControllerTesting.RefusalOf(_controller.UpdateEventLoopTracks(new UpdateEventLoopTracksRequest(
            "Evening",
            [new EventLoopTrack { Track = "fields14" }])));

        foreach (var message in new[] { read, tracks, write })
        {
            Assert.DoesNotContain(_folder, message, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("log has the details", read, StringComparison.Ordinal);
        // A write says what to fix when it knows.
        Assert.Contains("was not found", write, StringComparison.Ordinal);
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
