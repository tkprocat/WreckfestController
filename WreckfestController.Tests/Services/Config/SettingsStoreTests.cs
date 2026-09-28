using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using WreckfestController.Data;
using WreckfestController.Data.Settings;
using WreckfestController.Models;
using WreckfestController.Services.Config;
using WreckfestController.Services.Voting;

namespace WreckfestController.Tests.Services.Config;

/// <summary>The settings store and its IOptionsMonitor adapter, over a migrated SQLite file.</summary>
public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "wfc-settings-tests", Guid.NewGuid().ToString("N"));

    private readonly IDbContextFactory<ControllerDbContext> _contexts;
    private readonly DatabaseState _database;

    /// <summary>What appsettings.json ships with, as far as these tests care.</summary>
    private readonly ShippedSettings _shipped = new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Vote:Mode"] = "Direct",
            ["Vote:MessageDelayMs"] = "100",
            ["Vote:AllowedTracks:0:Id"] = "urban09_1",
            ["WreckfestServer:SupportedBuild"] = "1.308438",
        })
        .Build());

    public SettingsStoreTests()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "controller.db");
        var options = new DbContextOptionsBuilder<ControllerDbContext>();
        ControllerDbContext.Configure(options, path);
        _contexts = new Factory(options.Options);
        using (var db = _contexts.CreateDbContext())
        {
            db.Database.Migrate();
        }

        _database = new DatabaseState(path);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    private SettingsStore CreateStore() => new(_contexts, _database, _shipped, NullLogger<SettingsStore>.Instance);

    private List<SettingsSection> Rows()
    {
        using var db = _contexts.CreateDbContext();
        return db.SettingsSections.AsNoTracking().OrderBy(s => s.Section).ToList();
    }

    [Fact]
    public async Task WithoutTheDatabase_ReadsTheShippedDefaults_AndRefusesToSave()
    {
        var store = CreateStore();

        var vote = store.GetEntry<VoteSettings>();

        Assert.Equal((VoteModes.Direct, 100, 0), (vote.Value.Mode, vote.Value.MessageDelayMs, vote.Version));
        Assert.Equal(SettingsSections.DefaultServerArguments, store.Get<WreckfestServerSettings>().ServerArguments);
        await Assert.ThrowsAsync<SettingsUnavailableException>(() => store.SaveAsync(vote.Value, 0));
        Assert.Empty(Rows());
    }

    [Fact]
    public void FirstRun_CreatesEachSectionFromTheShippedDefaults()
    {
        _database.MarkReady(null);
        var store = CreateStore();

        var vote = store.GetEntry<VoteSettings>();

        Assert.Equal((VoteModes.Direct, 100, 1), (vote.Value.Mode, vote.Value.MessageDelayMs, vote.Version));
        Assert.Equal(
            [SettingsSections.SteamCmd, SettingsSections.Vote, SettingsSections.WreckfestServer],
            Rows().Select(r => r.Section));

        // Votable tracks move to the catalogue; they are not copied into the section.
        Assert.Empty(vote.Value.AllowedTracks);
    }

    [Fact]
    public void AnExistingRow_IsNeverReplacedByTheDefaults()
    {
        using (var db = _contexts.CreateDbContext())
        {
            db.SettingsSections.Add(new SettingsSection { Section = SettingsSections.Vote, Json = """{"Mode":"Off","MessageDelayMs":0}""", Version = 7 });
            db.SaveChanges();
        }

        _database.MarkReady(null);
        var store = CreateStore();

        var vote = store.GetEntry<VoteSettings>();

        Assert.Equal((VoteModes.Off, 0, 7), (vote.Value.Mode, vote.Value.MessageDelayMs, vote.Version));
        Assert.Equal(3, Rows().Count);
    }

    [Fact]
    public async Task Save_BumpsTheVersion_AndAStaleVersionGetsAConflict()
    {
        _database.MarkReady(null);
        var store = CreateStore();
        var changes = new List<Type>();
        store.Changed += (_, e) => changes.Add(e.Section);
        var read = store.GetEntry<VoteSettings>();

        read.Value.Mode = VoteModes.Off;
        var saved = await store.SaveAsync(read.Value, read.Version);

        Assert.Equal(SettingsSaveStatus.Saved, saved.Status);
        Assert.Equal((VoteModes.Off, 2), (saved.Current.Value.Mode, saved.Current.Version));
        Assert.Equal(VoteModes.Off, store.Get<VoteSettings>().Mode);
        Assert.Equal([typeof(VoteSettings)], changes);

        // Another editor still holding version 1.
        read.Value.Mode = VoteModes.Voting;
        var stale = await store.SaveAsync(read.Value, read.Version);

        Assert.Equal(SettingsSaveStatus.Conflict, stale.Status);
        Assert.Equal((VoteModes.Off, 2), (stale.Current.Value.Mode, stale.Current.Version));
        Assert.Equal(VoteModes.Off, store.Get<VoteSettings>().Mode);
        Assert.Single(changes);
    }

    [Fact]
    public async Task Save_BringsValuesIntoRange_WithoutChangingTheCallersObject()
    {
        _database.MarkReady(null);
        var store = CreateStore();
        var read = store.GetEntry<VoteSettings>();
        var edit = read.Value;
        edit.MessageDelayMs = 99_999;
        edit.VoteTimeoutSeconds = 0;
        edit.Mode = "direct";
        edit.AllowedTracks = [new AllowedVoteTrack { Id = "urban09_1" }];

        var saved = (await store.SaveAsync(edit, read.Version)).Current.Value;

        Assert.Equal((5000, 1, VoteModes.Direct, true), (saved.MessageDelayMs, saved.VoteTimeoutSeconds, saved.Mode, saved.Enabled));
        Assert.Empty(saved.AllowedTracks);
        Assert.Equal(99_999, edit.MessageDelayMs);
    }

    [Fact]
    public void Get_ReturnsACopy()
    {
        _database.MarkReady(null);
        var store = CreateStore();

        store.Get<SteamCmdSettings>().SteamCmdPath = "changed by a reader";

        Assert.Equal(string.Empty, store.Get<SteamCmdSettings>().SteamCmdPath);
    }

    [Fact]
    public void LeavingRecoveryMode_LoadsTheDatabase_AndAnnouncesEverySection()
    {
        using (var db = _contexts.CreateDbContext())
        {
            db.SettingsSections.Add(new SettingsSection { Section = SettingsSections.Vote, Json = """{"Mode":"Off"}""", Version = 3 });
            db.SaveChanges();
        }

        var store = CreateStore();
        var changes = new List<Type>();
        store.Changed += (_, e) => changes.Add(e.Section);
        Assert.Equal(VoteModes.Direct, store.Get<VoteSettings>().Mode);

        _database.MarkReady(null);

        Assert.Equal(VoteModes.Off, store.Get<VoteSettings>().Mode);
        Assert.Equal(SettingsSections.Types.OrderBy(t => t.Name), changes.OrderBy(t => t.Name));
    }

    [Fact]
    public async Task AFailingListener_DoesNotKeepOthersFromHearing()
    {
        _database.MarkReady(null);
        var store = CreateStore();
        var heard = 0;
        store.Changed += (_, _) => throw new InvalidOperationException("broken listener");
        store.Changed += (_, _) => heard++;
        var read = store.GetEntry<SteamCmdSettings>();

        var saved = await store.SaveAsync(read.Value, read.Version);

        Assert.Equal(SettingsSaveStatus.Saved, saved.Status);
        Assert.Equal(1, heard);
    }

    [Fact]
    public async Task Monitor_ReflectsASaveAtOnce_AndOnChangeFires()
    {
        _database.MarkReady(null);
        var store = CreateStore();
        using var monitor = new SettingsStoreOptionsMonitor<VoteSettings>(store);
        var heard = new List<string>();
        var registration = monitor.OnChange((vote, _) => heard.Add(vote.Mode));
        var read = store.GetEntry<VoteSettings>();

        read.Value.Mode = VoteModes.Off;
        await store.SaveAsync(read.Value, read.Version);

        Assert.Equal(VoteModes.Off, monitor.CurrentValue.Mode);
        Assert.Equal([VoteModes.Off], heard);

        // Another section's save, or a disposed registration, is not heard.
        var steam = store.GetEntry<SteamCmdSettings>();
        await store.SaveAsync(steam.Value, steam.Version);
        registration!.Dispose();
        var again = store.GetEntry<VoteSettings>();
        await store.SaveAsync(again.Value, again.Version);

        Assert.Single(heard);
    }

    private sealed class Factory(DbContextOptions<ControllerDbContext> options) : IDbContextFactory<ControllerDbContext>
    {
        public ControllerDbContext CreateDbContext() => new(options);
    }
}
