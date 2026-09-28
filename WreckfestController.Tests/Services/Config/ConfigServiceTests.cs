using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using WreckfestController.Services.Config;

namespace WreckfestController.Tests.Services.Config;

/// <summary>ConfigService against a real server_config.cfg in a temporary folder.</summary>
public sealed class ConfigServiceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"wf-config-{Guid.NewGuid():N}");
    private readonly string _file;
    private readonly ConfigService _service;

    public ConfigServiceTests()
    {
        Directory.CreateDirectory(_folder);
        _file = Path.Combine(_folder, "server_config.cfg");
        _service = new ConfigService(TestSettings.Server(workingDirectory: _folder), NullLogger<ConfigService>.Instance);
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void WriteSettings_ReplacesActiveLines_AndLeavesTheRestAlone()
    {
        File.WriteAllLines(_file,
        [
            "server_name=Race night",
            "# session_mode=normal is the default",
            "session_mode=normal",
            "grid_order=perf_normal",
            "",
            "# Event Loop",
            "el_add=urban09_1",
            "el_laps=5",
        ]);

        _service.WriteSettings(new Dictionary<string, string> { ["session_mode"] = "30p-aggr", ["grid_order"] = "cup_reverse" });

        Assert.Equal(
            [
                "server_name=Race night",
                "# session_mode=normal is the default",
                "session_mode=30p-aggr",
                "grid_order=cup_reverse",
                "",
                "# Event Loop",
                "el_add=urban09_1",
                "el_laps=5",
            ],
            File.ReadAllLines(_file));
    }

    [Fact]
    public void WriteSettings_AddsAMissingOrCommentedOutKey_AboveTheEventLoop()
    {
        File.WriteAllLines(_file,
        [
            "server_name=Race night",
            "#session_mode=normal",
            "",
            "# Event Loop",
            "el_add=urban09_1",
        ]);

        _service.WriteSettings(new Dictionary<string, string> { ["session_mode"] = "30p-aggr", ["grid_order"] = "cup_reverse" });

        // The commented line stays; the settings go where the server reads them, not into the loop.
        Assert.Equal(
            [
                "server_name=Race night",
                "#session_mode=normal",
                "",
                "session_mode=30p-aggr",
                "grid_order=cup_reverse",
                "",
                "# Event Loop",
                "el_add=urban09_1",
            ],
            File.ReadAllLines(_file));
        Assert.Equal(("30p-aggr", "cup_reverse"), (_service.ReadBasicConfig().SessionMode, _service.ReadBasicConfig().GridOrder));
    }

    [Fact]
    public void WriteSettings_NeverTouchesTheEventLoop()
    {
        File.WriteAllLines(_file,
        [
            "server_name=Race night",
            "# Event Loop",
            "el_add=urban09_1",
            "session_mode=inside_the_loop",
        ]);

        _service.WriteSettings(new Dictionary<string, string> { ["session_mode"] = "10p-lin" });

        Assert.Equal(
            [
                "server_name=Race night",
                "session_mode=10p-lin",
                "",
                "# Event Loop",
                "el_add=urban09_1",
                "session_mode=inside_the_loop",
            ],
            File.ReadAllLines(_file));
    }
}
