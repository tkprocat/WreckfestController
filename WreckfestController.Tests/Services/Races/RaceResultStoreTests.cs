using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WreckfestController.Data.Races;
using WreckfestController.Services.Hook;
using WreckfestController.Services.Publishing;
using WreckfestController.Services.Races;
using WreckfestController.Services.ServerControl;
using WreckfestController.Services.Tracking;
using WreckfestController.Tests.Services.Cups;
using WreckfestController.Tests.Services.Hook;
using Xunit;

namespace WreckfestController.Tests.Services.Races;

public sealed class RaceResultStoreTests : IDisposable
{
    private readonly CupTestDatabase _database = new();
    private readonly RaceResultStore _store;

    private static readonly HookRaceRecord LiveRace = HookRaceRecord.TryParse(HookRaceRecordTests.LiveRecord)!;

    public RaceResultStoreTests()
    {
        _store = new RaceResultStore(_database.Contexts);
    }

    public void Dispose() => _database.Dispose();

    private async Task<List<Race>> AllRacesAsync()
    {
        await using var db = await _database.Contexts.CreateDbContextAsync();
        return await db.Races.Include(r => r.Entries).AsNoTracking().ToListAsync();
    }

    private async Task<int> ActivateCupAsync(string name, DateTime activatedAt)
    {
        var cup = await _database.CreateAsync(CupTestDatabase.Definition(name, new DateTime(2026, 10, 9, 19, 0, 0, DateTimeKind.Utc)));
        await using var db = await _database.Contexts.CreateDbContextAsync();
        await db.Cups.Where(c => c.Id == cup.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(c => c.IsActive, true).SetProperty(c => c.ActivatedAt, activatedAt));
        return cup.Id;
    }

    [Fact]
    public async Task Saves_the_race_and_every_car()
    {
        await _store.SaveAsync(LiveRace);

        var race = Assert.Single(await AllRacesAsync());
        Assert.Equal("speedway2_inner_oval", race.TrackId);
        Assert.Equal(1, race.Laps);
        Assert.Equal(LiveRace.EndedAt.UtcDateTime, race.EndedAt);
        Assert.Equal(DateTimeKind.Utc, race.EndedAt.Kind);
        Assert.Equal(LiveRace.StartedAt!.Value.UtcDateTime, race.StartedAt);
        Assert.Equal(11, race.Entries.Count);
        Assert.Null(race.CupId);
    }

    [Fact]
    public async Task Keeps_what_the_results_screen_showed_and_the_raw_values()
    {
        await _store.SaveAsync(LiveRace);

        var winner = (await AllRacesAsync()).Single().Entries.Single(e => e.Position == 1);
        Assert.Equal("Driver", winner.Name);
        Assert.False(winner.IsBot);
        Assert.Equal(76561190000000042, winner.SteamId);
        Assert.Equal("VEHICLE_NAME_2244970999_13", winner.VehicleKey);
        Assert.Equal("Sunrise Super", winner.VehicleName);
        Assert.Equal(RaceOutcome.Finished, winner.Outcome);
        Assert.Equal(0, winner.ClassIndex);
        Assert.Equal(346, winner.Rating);
        Assert.Equal(21292, winner.TimeMs);
        Assert.Equal(21292, winner.BestLapMs);
        Assert.Equal(30, winner.CupPointsTotal);
        Assert.Equal(10, winner.Slot);
        Assert.Equal(0x41, winner.CarFlags);
        Assert.Equal(21292, winner.FinishMs);
    }

    [Fact]
    public async Task Stores_bots_as_bots_with_their_projected_outcome()
    {
        await _store.SaveAsync(LiveRace);

        var bot = (await AllRacesAsync()).Single().Entries.Single(e => e.Name == "Djkevino");
        Assert.True(bot.IsBot);
        Assert.Null(bot.SteamId);
        Assert.Equal(RaceOutcome.Projected, bot.Outcome);
        Assert.Equal(2, bot.Position);
    }

    [Fact]
    public async Task Links_the_cup_that_is_active()
    {
        var activatedAt = new DateTime(2026, 10, 2, 18, 0, 0, DateTimeKind.Utc);
        var cupId = await ActivateCupAsync("Monday Night Wrecking", activatedAt);

        await _store.SaveAsync(LiveRace);

        var race = Assert.Single(await AllRacesAsync());
        Assert.Equal(cupId, race.CupId);
        Assert.Equal("Monday Night Wrecking", race.CupName);
        Assert.Equal(activatedAt, race.CupActivatedAt);
    }

    // History outlives the cup: the race stays, readable by the name copied at the time.
    [Fact]
    public async Task Keeps_the_race_and_the_cup_name_when_the_cup_is_deleted()
    {
        var cupId = await ActivateCupAsync("Monday Night Wrecking", DateTime.UtcNow);
        await _store.SaveAsync(LiveRace);

        await using (var db = await _database.Contexts.CreateDbContextAsync())
        {
            await db.Cups.Where(c => c.Id == cupId).ExecuteDeleteAsync();
        }

        var race = Assert.Single(await AllRacesAsync());
        Assert.Null(race.CupId);
        Assert.Equal("Monday Night Wrecking", race.CupName);
    }

    [Fact]
    public async Task Uses_the_fallback_track_only_when_the_hook_had_none()
    {
        await _store.SaveAsync(LiveRace with { TrackId = "" }, fallbackTrackId: "urban07");
        await _store.SaveAsync(LiveRace, fallbackTrackId: "urban07");

        Assert.Equal(
            new[] { "speedway2_inner_oval", "urban07" },
            (await AllRacesAsync()).Select(r => r.TrackId).Order());
    }

    [Fact]
    public async Task Lists_the_latest_races_first_with_entries_in_finishing_order()
    {
        var earlier = LiveRace with { EndedAt = LiveRace.EndedAt.AddMinutes(-10), TrackId = "earlier" };
        var unplaced = LiveRace.Cars[0] with { Position = null, Name = "Unplaced" };
        var latest = LiveRace with { Cars = LiveRace.Cars.Append(unplaced).ToList() };

        await _store.SaveAsync(earlier);
        await _store.SaveAsync(latest);

        var races = await _store.LatestAsync(10);

        Assert.Equal(new[] { "speedway2_inner_oval", "earlier" }, races.Select(r => r.TrackId));
        var positions = races[0].Entries.Select(e => e.Position).ToList();
        Assert.Equal(Enumerable.Range(1, 11).Select(p => (int?)p).Append(null), positions);
    }

    // The whole path: the line as the hook sends it, through ServerManager's demux and
    // the recorder's queue, into the database.
    [Fact]
    public async Task Records_a_race_the_hook_reports()
    {
        var events = new Mock<IServerEventPublisher>().Object;
        var tracks = new TrackChangeTracker(NullLogger<TrackChangeTracker>.Instance, events);
        var serverManager = new ServerManager(
            new Mock<IConfiguration>().Object,
            TestSettings.Server(),
            TestSettings.SteamCmd(),
            NullLogger<ServerManager>.Instance,
            new PlayerTracker(NullLogger<PlayerTracker>.Instance, events),
            tracks,
            new ServerInfoTracker(NullLogger<ServerInfoTracker>.Instance),
            events);
        using var recorder = new RaceResultRecorder(serverManager, _store, tracks, NullLogger<RaceResultRecorder>.Instance);
        await recorder.StartAsync(CancellationToken.None);

        Assert.True(serverManager.TryProcessHookRaceRecord(InjectedHookOutputReader.PrepareForFanout(HookRaceRecordTests.LiveRecord), serverManager.CurrentAttachmentGeneration));
        await recorder.FlushAsync();

        var race = Assert.Single(await AllRacesAsync());
        Assert.Equal(11, race.Entries.Count);
    }

    // A malformed record is consumed, so it never reaches the console text, and saves nothing.
    [Fact]
    public async Task A_malformed_record_is_consumed_and_saves_nothing()
    {
        var events = new Mock<IServerEventPublisher>().Object;
        var tracks = new TrackChangeTracker(NullLogger<TrackChangeTracker>.Instance, events);
        var serverManager = new ServerManager(
            new Mock<IConfiguration>().Object,
            TestSettings.Server(),
            TestSettings.SteamCmd(),
            NullLogger<ServerManager>.Instance,
            new PlayerTracker(NullLogger<PlayerTracker>.Instance, events),
            tracks,
            new ServerInfoTracker(NullLogger<ServerInfoTracker>.Instance),
            events);
        using var recorder = new RaceResultRecorder(serverManager, _store, tracks, NullLogger<RaceResultRecorder>.Instance);
        await recorder.StartAsync(CancellationToken.None);

        Assert.True(serverManager.TryProcessHookRaceRecord(HookRaceRecord.Marker + "\u001Fgarbage\u0013", serverManager.CurrentAttachmentGeneration));
        await recorder.FlushAsync();

        Assert.Empty(await AllRacesAsync());
    }
}
