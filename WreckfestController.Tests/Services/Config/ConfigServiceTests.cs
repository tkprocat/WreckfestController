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

        // The commented line is uncommented where it is; a key the file lacks is added above
        // the loop, marked; nothing goes into the loop.
        Assert.Equal(
            [
                "server_name=Race night",
                "session_mode=30p-aggr",
                "",
                "# Added by WreckfestController",
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
                "# Added by WreckfestController",
                "session_mode=10p-lin",
                "",
                "# Event Loop",
                "el_add=urban09_1",
                "session_mode=inside_the_loop",
            ],
            File.ReadAllLines(_file));
    }

    // Only the setting itself, commented out, is uncommented: a comment that talks about
    // it is prose, and stays.
    [Fact]
    public void WriteSettings_LeavesProseCommentsAlone()
    {
        File.WriteAllLines(_file,
        [
            "# Leave blank for random weather",
            "# weather is one of clear, overcast, fog, rain, storm",
            "# session_mode=normal is the default",
            "## grid_order=random",
            "#weather=rain",
            "",
            "# Event Loop",
        ]);

        _service.WriteSettings(new Dictionary<string, string> { ["weather"] = "fog", ["session_mode"] = "30p-aggr", ["grid_order"] = "cup_reverse" });

        // Only "#key=value" is the setting itself; the rest is documentation, kept, and those
        // keys are added above the loop instead.
        Assert.Equal(
            [
                "# Leave blank for random weather",
                "# weather is one of clear, overcast, fog, rain, storm",
                "# session_mode=normal is the default",
                "## grid_order=random",
                "weather=fog",
                "",
                "# Added by WreckfestController",
                "session_mode=30p-aggr",
                "grid_order=cup_reverse",
                "",
                "# Event Loop",
            ],
            File.ReadAllLines(_file));
    }

    // An active line wins over a commented one: it is replaced, the comment stays, and there
    // is still one active line.
    [Fact]
    public void WriteSettings_ReplacesTheActiveLine_AndLeavesACommentedCopy()
    {
        File.WriteAllLines(_file, ["#max_players=24", "max_players=16", "", "# Event Loop"]);

        _service.WriteSettings(new Dictionary<string, string> { ["max_players"] = "20" });

        Assert.Equal(["#max_players=24", "max_players=20", "", "# Event Loop"], File.ReadAllLines(_file));
    }

    // Every other line, comments and blank lines included, stays byte for byte.
    [Fact]
    public void WriteSettings_ChangesNothingElse()
    {
        string[] original =
        [
            "# Wreckfest server config",
            "server_name=Race night",
            "  # indented comment",
            "max_players=24",
            "",
            "welcome_message=Hi",
            "# Event Loop",
            "el_add=urban09_1",
        ];
        File.WriteAllLines(_file, original);

        _service.WriteSettings(new Dictionary<string, string> { ["max_players"] = "12" });

        var expected = original.ToArray();
        expected[3] = "max_players=12";
        Assert.Equal(expected, File.ReadAllLines(_file));
    }

    // The first commented copy is the one uncommented; the others stay comments.
    [Fact]
    public void WriteSettings_UncommentsOnlyTheFirstCommentedCopy()
    {
        File.WriteAllLines(_file, ["#max_players=24", "#max_players=32", "", "# Event Loop"]);

        _service.WriteSettings(new Dictionary<string, string> { ["max_players"] = "20" });

        Assert.Equal(["max_players=20", "#max_players=32", "", "# Event Loop"], File.ReadAllLines(_file));
    }

    // A key set twice above the loop is set to the same value on both lines.
    [Fact]
    public void WriteSettings_ReplacesEveryActiveCopy()
    {
        File.WriteAllLines(_file, ["max_players=24", "bots=2", "max_players=32", "", "# Event Loop"]);

        _service.WriteSettings(new Dictionary<string, string> { ["max_players"] = "20" });

        Assert.Equal(["max_players=20", "bots=2", "max_players=20", "", "# Event Loop"], File.ReadAllLines(_file));
    }

    // A value can be a password: what is logged names the keys, never their values.
    [Fact]
    public void WriteSettings_NeverLogsAValue()
    {
        var logger = new CollectingLogger<ConfigService>();
        var service = new ConfigService(TestSettings.Server(workingDirectory: _folder), logger);
        File.WriteAllLines(_file, ["server_name=Race night", "", "# Event Loop"]);

        service.WriteSettings(new Dictionary<string, string> { ["password"] = "hunter2-secret" });

        Assert.Contains("password=hunter2-secret", File.ReadAllLines(_file));
        Assert.NotEmpty(logger.Messages);
        Assert.DoesNotContain(logger.Messages, m => m.Contains("hunter2-secret", StringComparison.Ordinal));
    }

    // ---- Restorable writes (#224) ----

    private static readonly Dictionary<string, string> CupSettings = new() { ["session_mode"] = "30p-aggr" };

    [Fact]
    public void WriteRestorably_ThenRestore_PutsTheFileBackByteForByte()
    {
        // A BOM and mixed line endings: the writers would normalise both.
        byte[] original = [0xEF, 0xBB, 0xBF, .. "server_name=Race night\r\nsession_mode=normal\n\r\n# Event Loop\n"u8];
        File.WriteAllBytes(_file, original);

        var point = _service.WriteRestorably(() => _service.WriteSettings(CupSettings));

        Assert.NotNull(point);
        Assert.Contains("session_mode=30p-aggr", File.ReadAllLines(_file));
        Assert.True(_service.Restore(point));
        Assert.Equal(original, File.ReadAllBytes(_file));
    }

    [Fact]
    public void WriteRestorably_ThatChangesNothing_HasNothingToRestore()
    {
        File.WriteAllLines(_file, ["session_mode=30p-aggr", "", "# Event Loop"]);

        Assert.Null(_service.WriteRestorably(() => _service.WriteSettings(CupSettings)));
    }

    [Fact]
    public void Restore_LeavesAFileChangedSince()
    {
        File.WriteAllLines(_file, ["server_name=Race night", "session_mode=normal", "", "# Event Loop"]);
        var point = _service.WriteRestorably(() => _service.WriteSettings(CupSettings))!;
        _service.WriteSettings(new Dictionary<string, string> { ["password"] = "admin-edit" });
        var edited = File.ReadAllBytes(_file);

        Assert.False(_service.Restore(point));
        Assert.Equal(edited, File.ReadAllBytes(_file));
    }

    // A write that fails partway is undone before any other write can land.
    [Fact]
    public void WriteRestorably_ThatThrowsPartway_PutsTheFileBack()
    {
        File.WriteAllLines(_file, ["server_name=Race night", "session_mode=normal", "", "# Event Loop"]);
        var original = File.ReadAllBytes(_file);

        Assert.Throws<IOException>(() => _service.WriteRestorably(() =>
        {
            _service.WriteSettings(CupSettings);
            throw new IOException("Disk full");
        }));

        Assert.Equal(original, File.ReadAllBytes(_file));
    }

    // Another writer waits for the whole change, so its write can be neither part of the
    // change's "after" nor overwritten by a rollback.
    [Fact]
    public async Task WriteRestorably_HoldsOtherWritersOffUntilItEnds()
    {
        File.WriteAllLines(_file, ["server_name=Race night", "session_mode=normal", "", "# Event Loop"]);
        Task? admin = null;

        var point = _service.WriteRestorably(() =>
        {
            _service.WriteSettings(CupSettings);
            admin = Task.Run(() => _service.WriteSettings(new Dictionary<string, string> { ["password"] = "admin-edit" }));
            Assert.False(admin.Wait(TimeSpan.FromMilliseconds(300)));
        })!;

        await admin!.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.DoesNotContain("password=admin-edit", System.Text.Encoding.UTF8.GetString(point.After));
        Assert.False(_service.Restore(point));
        Assert.Contains("password=admin-edit", File.ReadAllLines(_file));
    }

    private sealed class CollectingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
