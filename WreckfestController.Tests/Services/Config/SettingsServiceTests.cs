using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WreckfestController.Data;
using WreckfestController.Models;
using WreckfestController.Services.Config;
using WreckfestController.Services.Voting;

namespace WreckfestController.Tests.Services.Config;

/// <summary>
/// The Configuration tab's settings over a real store and database, with a 1.x-style
/// user-settings.json beside them.
/// </summary>
public sealed class SettingsServiceTests : IDisposable
{
    /// <summary>A 1.x user-settings.json: startup keys, and keys 2.0 keeps in the database.</summary>
    private const string OneXUserSettings = """
        {
          "Api": { "Enabled": true, "Key": "from-the-file", "HttpPort": 5200 },
          "Database": { "Path": "%DATA%\\controller.db" },
          "WreckfestServer": {
            "ServerPath": "D:\\Old\\Wreckfest_x64.exe",
            "SupportedBuild": "1.308438"
          },
          "Vote": { "Mode": "Off", "MaxLapsAllowed": 3, "AllowedTracks": [ { "Id": "urban09_1" } ] }
        }
        """;

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "wfc-settings-service-tests", Guid.NewGuid().ToString("N"));

    private readonly string _userSettingsPath;
    private readonly string _databasePath;
    private readonly IConfigurationRoot _configuration;
    private readonly DatabaseState _database;
    private readonly SettingsStore _store;
    private readonly ListLogger<SettingsService> _log = new();

    public SettingsServiceTests()
    {
        Directory.CreateDirectory(_directory);
        _userSettingsPath = Path.Combine(_directory, "user-settings.json");
        File.WriteAllText(_userSettingsPath, OneXUserSettings.Replace("%DATA%", _directory.Replace("\\", "\\\\")));

        // As Program builds it: shipped defaults, then the user's file on top.
        var shipped = new Dictionary<string, string?>
        {
            ["Vote:Mode"] = "Voting",
            ["Vote:MaxLapsAllowed"] = "10",
            ["WreckfestServer:SupportedBuild"] = "1.308438",
        };
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(shipped)
            .AddInMemoryCollection(new Dictionary<string, string?> { ["UserSettingsPath"] = _userSettingsPath })
            .AddJsonFile(_userSettingsPath)
            .Build();

        _databasePath = DatabasePath.Resolve(_configuration, _directory);
        var options = new DbContextOptionsBuilder<ControllerDbContext>();
        ControllerDbContext.Configure(options, _databasePath);
        var contexts = new Factory(options.Options);
        using (var db = contexts.CreateDbContext())
        {
            db.Database.Migrate();
        }

        _database = new DatabaseState(_databasePath);
        _database.MarkReady(null);
        _store = new SettingsStore(
            contexts,
            _database,
            new ShippedSettings(new ConfigurationBuilder().AddInMemoryCollection(shipped).Build()),
            NullLogger<SettingsStore>.Instance);
    }

    public void Dispose()
    {
        SqlitePools.ReleaseFolder(_directory);
        Directory.Delete(_directory, recursive: true);
    }

    private SettingsService CreateService() => new(_configuration, _store, _database, _log);

    [Fact]
    public void FirstStart_HonoursStartupKeys_IgnoresOneXKeys_AndLeavesTheFileUntouched()
    {
        var before = File.ReadAllBytes(_userSettingsPath);

        var service = CreateService();
        var loaded = service.LoadForEdit();
        loaded.Settings.Vote!.Mode = VoteModes.Direct;
        service.SaveSettings(loaded.Settings, loaded);

        // Startup keys still come from the file.
        Assert.Equal("from-the-file", _configuration["Api:Key"]);
        Assert.Equal(Path.Combine(_directory, "controller.db"), _databasePath);

        // The sections start from the shipped defaults, not the file's 1.x values.
        var stored = _store.Get<VoteSettings>();
        Assert.Equal((VoteModes.Direct, 10), (stored.Mode, stored.MaxLapsAllowed));
        Assert.Equal(string.Empty, _store.Get<WreckfestServerSettings>().ServerPath);

        // Each 1.x key is named once; the build key, still read, is not.
        Assert.Equal(
            [
                "user-settings.json: 'Vote:AllowedTracks' is no longer read; set it in Settings",
                "user-settings.json: 'Vote:MaxLapsAllowed' is no longer read; set it in Settings",
                "user-settings.json: 'Vote:Mode' is no longer read; set it in Settings",
                "user-settings.json: 'WreckfestServer:ServerPath' is no longer read; set it in Settings",
            ],
            _log.Warnings.Order());

        // Byte for byte, so rolling back to 1.x finds the file as it was.
        Assert.Equal(before, File.ReadAllBytes(_userSettingsPath));
    }

    [Fact]
    public async Task Save_WhenASectionItChanges_WasChangedElsewhere_SavesNothing()
    {
        var service = CreateService();
        var loaded = service.LoadForEdit();

        // The web app changes the Vote settings meanwhile.
        await SaveElsewhereAsync<VoteSettings>(v => v.MaxLapsAllowed = 7);

        loaded.Settings.WreckfestServer!.ServerPath = @"C:\New\Wreckfest_x64.exe";
        loaded.Settings.Vote!.Mode = VoteModes.Off;
        var error = Assert.Throws<SettingsConflictException>(() => service.SaveSettings(loaded.Settings, loaded));

        // One transaction: the server section, checked and written first, was rolled back.
        Assert.Contains("voting settings were changed elsewhere", error.Message, StringComparison.Ordinal);
        Assert.Equal(string.Empty, _store.Get<WreckfestServerSettings>().ServerPath);
        Assert.Equal(1, _store.GetEntry<WreckfestServerSettings>().Version);
        Assert.Equal((VoteModes.Voting, 7), (_store.Get<VoteSettings>().Mode, _store.Get<VoteSettings>().MaxLapsAllowed));
    }

    [Fact]
    public async Task Save_LeavesAloneWhatItDidNotChange_EvenWhenChangedElsewhere()
    {
        var service = CreateService();
        var loaded = service.LoadForEdit();
        await SaveElsewhereAsync<VoteSettings>(v => v.MaxLapsAllowed = 7);

        loaded.Settings.SteamCmd!.SteamCmdPath = @"C:\steamcmd\steamcmd.exe";
        var saved = service.SaveSettings(loaded.Settings, loaded);

        // Only SteamCMD was written; the other editor's Vote change stands.
        Assert.Equal(loaded.Versions with { SteamCmd = loaded.Versions.SteamCmd + 1 }, saved.Versions);
        Assert.Equal(@"C:\steamcmd\steamcmd.exe", _store.Get<SteamCmdSettings>().SteamCmdPath);
        Assert.Equal(7, _store.Get<VoteSettings>().MaxLapsAllowed);
    }

    [Fact]
    public void Save_ReturnsWhatTheEditorNowHolds()
    {
        var service = CreateService();
        var loaded = service.LoadForEdit();

        loaded.Settings.Vote!.Mode = "direct";
        var saved = service.SaveSettings(loaded.Settings, loaded);

        // As stored (the mode spelled the store's way), at the version it was stored at.
        Assert.Equal(VoteModes.Direct, saved.Settings.Vote!.Mode);
        Assert.Equal(_store.GetEntry<VoteSettings>().Version, saved.Versions.Vote);

        // Saving the unchanged form again writes nothing.
        Assert.Equal(saved.Versions, service.SaveSettings(saved.Settings, saved).Versions);
    }

    [Fact]
    public void Save_InRecoveryMode_Refuses()
    {
        var service = CreateService();
        var loaded = service.LoadForEdit();
        _database.MarkFailed("disk gone", null);

        loaded.Settings.Vote!.Mode = VoteModes.Off;
        var error = Assert.Throws<InvalidOperationException>(() => service.SaveSettings(loaded.Settings, loaded));

        Assert.Contains("database is unavailable", error.Message, StringComparison.Ordinal);
    }

    private async Task SaveElsewhereAsync<T>(Action<T> change)
        where T : class
    {
        var entry = _store.GetEntry<T>();
        change(entry.Value);
        Assert.Equal(SettingsSaveStatus.Saved, (await _store.SaveAsync(entry.Value, entry.Version)).Status);
    }

    private sealed class Factory(DbContextOptions<ControllerDbContext> options) : IDbContextFactory<ControllerDbContext>
    {
        public ControllerDbContext CreateDbContext() => new(options);
    }

    /// <summary>Keeps the warnings a service logs.</summary>
    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Warnings { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
