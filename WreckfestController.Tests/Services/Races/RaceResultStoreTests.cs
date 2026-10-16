using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WreckfestController.Data.Cups;
using WreckfestController.Data.Races;
using WreckfestController.Services.Cups;
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
        await _store.SaveAsync(LiveRace, cancellationToken: TestContext.Current.CancellationToken);

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
        await _store.SaveAsync(LiveRace, cancellationToken: TestContext.Current.CancellationToken);

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
        Assert.Equal(21292, winner.BestLapSetAtMs);
    }

    [Fact]
    public async Task Stores_bots_as_bots_with_their_projected_outcome()
    {
        await _store.SaveAsync(LiveRace, cancellationToken: TestContext.Current.CancellationToken);

        var bot = (await AllRacesAsync()).Single().Entries.Single(e => e.Name == "Djkevino");
        Assert.True(bot.IsBot);
        Assert.Null(bot.SteamId);
        Assert.Equal(RaceOutcome.Projected, bot.Outcome);
        Assert.Equal(2, bot.Position);
    }

    private static readonly DateTime BeforeTheRace = LiveRace.EndedAt.UtcDateTime.AddHours(-1);

    [Fact]
    public async Task Links_the_cup_the_race_ended_under()
    {
        var cupId = await ActivateCupAsync("Monday Night Wrecking", BeforeTheRace);

        await _store.SaveAsync(LiveRace, new ActiveCupSnapshot(cupId, "Monday Night Wrecking", BeforeTheRace), cancellationToken: TestContext.Current.CancellationToken);

        var race = Assert.Single(await AllRacesAsync());
        Assert.Equal(cupId, race.CupId);
        Assert.Equal("Monday Night Wrecking", race.CupName);
        Assert.Equal(BeforeTheRace, race.CupActivatedAt);
    }

    // The race ended under a cup that was deleted before the save: the link cannot be
    // kept, but the race still happened under that cup on that evening.
    [Fact]
    public async Task Keeps_the_cup_name_when_the_cup_was_deleted_before_the_save()
    {
        var cupId = await ActivateCupAsync("Gone Cup", BeforeTheRace);
        await using (var db = await _database.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            await db.Cups.Where(c => c.Id == cupId).ExecuteDeleteAsync(cancellationToken: TestContext.Current.CancellationToken);
        }

        await _store.SaveAsync(LiveRace, new ActiveCupSnapshot(cupId, "Gone Cup", BeforeTheRace), cancellationToken: TestContext.Current.CancellationToken);

        var race = Assert.Single(await AllRacesAsync());
        Assert.Null(race.CupId);
        Assert.Equal("Gone Cup", race.CupName);
        Assert.Equal(BeforeTheRace, race.CupActivatedAt);
    }

    // A cup renamed before the race was saved is saved under its name now.
    [Fact]
    public async Task Saves_a_linked_cup_under_its_current_name()
    {
        var cupId = await ActivateCupAsync("Renamed Since", BeforeTheRace);

        await _store.SaveAsync(LiveRace, new ActiveCupSnapshot(cupId, "Old Name", BeforeTheRace), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("Renamed Since", Assert.Single(await AllRacesAsync()).CupName);
    }

    private static readonly DateTime End = new(2026, 10, 2, 16, 0, 0, DateTimeKind.Utc);
    private static readonly ActiveCupSnapshot CupA = new(1, "A", End.AddHours(-2));
    private static readonly ActiveCupSnapshot CupBBefore = new(2, "B", End.AddMinutes(-1));
    private static readonly ActiveCupSnapshot CupBAfter = new(2, "B", End.AddMinutes(1));

    // The note was taken between B's activation committing and the cache catching up:
    // the database knows better, and B was active when the race ended.
    [Fact]
    public void The_database_wins_when_its_cup_was_active_at_the_end() =>
        Assert.Equal(CupBBefore, RaceResultStore.CupAtEnd(active: CupBBefore, noted: CupA, End));

    // B was activated while the race waited in the queue: the note says A.
    [Fact]
    public void The_note_wins_when_the_active_cup_came_after_the_end() =>
        Assert.Equal(CupA, RaceResultStore.CupAtEnd(active: CupBAfter, noted: CupA, End));

    // #206 review: a warmup race saved after the cup started is still a warmup race. The same
    // run in the note and the database: the note's phase is the one at the end.
    [Fact]
    public void The_note_keeps_its_phase_when_the_run_moved_on_before_the_save() =>
        Assert.Equal(
            CupPhase.Warmup,
            RaceResultStore.CupAtEnd(active: CupA with { Phase = CupPhase.Running }, noted: CupA with { Phase = CupPhase.Warmup }, End)!.Phase);

    [Fact]
    public void No_cup_when_both_came_after_the_end() =>
        Assert.Null(RaceResultStore.CupAtEnd(active: CupBAfter, noted: CupBAfter, End));

    [Fact]
    public void No_cup_when_none_was_active() =>
        Assert.Null(RaceResultStore.CupAtEnd(active: null, noted: null, End));

    // A cup deleted after the race ended leaves only the note.
    [Fact]
    public void The_note_stands_when_no_cup_is_active_now() =>
        Assert.Equal(CupA, RaceResultStore.CupAtEnd(active: null, noted: CupA, End));

    // History outlives the cup: the race stays, readable by the name copied at the time.
    [Fact]
    public async Task Keeps_the_race_and_the_cup_name_when_the_cup_is_deleted()
    {
        var cupId = await ActivateCupAsync("Monday Night Wrecking", BeforeTheRace);
        await _store.SaveAsync(LiveRace, new ActiveCupSnapshot(cupId, "Monday Night Wrecking", BeforeTheRace), cancellationToken: TestContext.Current.CancellationToken);

        await using (var db = await _database.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            await db.Cups.Where(c => c.Id == cupId).ExecuteDeleteAsync(cancellationToken: TestContext.Current.CancellationToken);
        }

        var race = Assert.Single(await AllRacesAsync());
        Assert.Null(race.CupId);
        Assert.Equal("Monday Night Wrecking", race.CupName);
    }

    [Fact]
    public async Task Uses_the_fallback_track_only_when_the_hook_had_none()
    {
        await _store.SaveAsync(LiveRace with { TrackId = "" }, fallbackTrackId: "urban07", cancellationToken: TestContext.Current.CancellationToken);
        await _store.SaveAsync(LiveRace, fallbackTrackId: "urban07", cancellationToken: TestContext.Current.CancellationToken);

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

        await _store.SaveAsync(earlier, cancellationToken: TestContext.Current.CancellationToken);
        await _store.SaveAsync(latest, cancellationToken: TestContext.Current.CancellationToken);

        var races = await _store.LatestAsync(10, TestContext.Current.CancellationToken);

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
        using var recorder = new RaceResultRecorder(serverManager, _store, _database.Store, tracks, events, NullLogger<RaceResultRecorder>.Instance);
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
        using var recorder = new RaceResultRecorder(serverManager, _store, _database.Store, tracks, events, NullLogger<RaceResultRecorder>.Instance);
        await recorder.StartAsync(CancellationToken.None);

        Assert.True(serverManager.TryProcessHookRaceRecord(HookRaceRecord.Marker + "\u001Fgarbage\u0013", serverManager.CurrentAttachmentGeneration));
        await recorder.FlushAsync();

        Assert.Empty(await AllRacesAsync());
    }
    // #234: a hook injected again while the results screen is still up has a fresh
    // watcher, which reports the race on screen once more: later, without a start.
    [Fact]
    public async Task The_same_race_reported_again_is_not_saved_twice()
    {
        var first = await _store.SaveOrFindAsync(LiveRace, cancellationToken: TestContext.Current.CancellationToken);
        var again = await _store.SaveOrFindAsync(LiveRace with
        {
            StartedAt = null,
            EndedAt = LiveRace.EndedAt.AddSeconds(30),
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(first.Added);
        Assert.False(again.Added);
        Assert.Equal(first.Race.Id, again.Race.Id);
        Assert.Single(await AllRacesAsync());
    }

    // A server restarted soon after a race starts its counter again: the next race can
    // share the counter and the track, but not every car's result.
    [Fact]
    public async Task A_different_race_with_the_same_counter_and_track_is_saved()
    {
        await _store.SaveAsync(LiveRace, cancellationToken: TestContext.Current.CancellationToken);
        var cars = LiveRace.Cars.Select((car, i) => i == 0 ? car with { TimeMs = (car.TimeMs ?? 0) + 1000 } : car).ToList();

        var (_, added) = await _store.SaveOrFindAsync(LiveRace with { Cars = cars, EndedAt = LiveRace.EndedAt.AddMinutes(1) }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(added);
        Assert.Equal(2, (await AllRacesAsync()).Count);
    }

    // A player who leaves the results screen is cleared from the player table, which the
    // Steam id and vehicle are read from: the repeat lacks them, and is still the race.
    [Fact]
    public async Task A_repeat_read_after_a_player_left_is_still_the_same_race()
    {
        await _store.SaveAsync(LiveRace, cancellationToken: TestContext.Current.CancellationToken);
        var cars = LiveRace.Cars.Select(car => car with { SteamId = null }).ToList();

        var (_, added) = await _store.SaveOrFindAsync(LiveRace with { Cars = cars, StartedAt = null, EndedAt = LiveRace.EndedAt.AddSeconds(20) }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(added);
    }

    // Known, different starts are two races, however alike their results.
    [Fact]
    public async Task Different_known_starts_are_different_races()
    {
        await _store.SaveAsync(LiveRace, cancellationToken: TestContext.Current.CancellationToken);

        var (_, added) = await _store.SaveOrFindAsync(LiveRace with
        {
            StartedAt = LiveRace.StartedAt!.Value.AddSeconds(45),
            EndedAt = LiveRace.EndedAt.AddSeconds(45),
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(added);
    }

    [Fact]
    public async Task The_same_results_long_after_are_another_race()
    {
        await _store.SaveAsync(LiveRace, cancellationToken: TestContext.Current.CancellationToken);

        var (_, added) = await _store.SaveOrFindAsync(LiveRace with { EndedAt = LiveRace.EndedAt + RaceResultStore.DuplicateWindow + TimeSpan.FromMinutes(1) }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(added);
    }
}
