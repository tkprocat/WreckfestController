using Xunit;
using WreckfestController.Services.Hook;

namespace WreckfestController.Tests.Services.Hook;

public class HookRaceRecordTests
{
    private const char S = HookRaceRecord.FieldSeparator;
    private const char E = HookRaceRecord.RecordEnd;

    // Values from a live race on 2026-10-02, checked against the in-game results screen:
    // a one-lap Banger Race on speedway1_figure_8 that Procat won in 26.418 s.
    private const ulong ProcatSteamId = 76561197985810610;

    private static string Car(
        int slot, int playerFlags, ulong steamId, string name, int position, uint carFlags,
        int timeMs, int bestLapMs, int finishMs, int classIndex, int rating, int cupPoints,
        string vehicleKey, string vehicleName, int playerStatus = 4, int lap = 2) =>
        string.Join(S, slot, playerStatus, playerFlags, steamId, name, position, lap, carFlags,
            timeMs, bestLapMs, finishMs, classIndex, rating, cupPoints, vehicleKey, vehicleName);

    private static readonly string Procat = Car(10, 0x32, ProcatSteamId, "Procat", 0, 0x41,
        26418, 26418, 26418, 0, 346, 60, "VEHICLE_NAME_2244970999_13", "Sunrise Super", playerStatus: 2);

    private static readonly string Djkevino = Car(1, 0x0A, 0, "^2*^0Djkevino", 1, 0x01,
        26972, 26972, 0, 1, 209, 42, "VEHICLE_NAME_3154256208_9", "KillerBee");

    private static readonly string Nykaa = Car(9, 0x0A, 0, "^2*^0Nykaa", 5, 0x01,
        32237, 32237, 0, 2, 110, 33, "VEHICLE_NAME_4156104396_8", "Warwagon");

    private static string Record(params string[] cars) => RecordWith(1, "speedway1_figure_8", 1_790_000_000_000, cars);

    private static string RecordWith(int version, string track, ulong startedMs, params string[] cars) =>
        $"{HookRaceRecord.Marker}{S}{string.Join(S, version, 2, track, 1, 1, startedMs, 1_790_000_030_000, cars.Length)}" +
        (cars.Length == 0 ? "" : S + string.Join(S, cars)) + E;

    // The record exactly as the hook sent it after a live race on 2026-10-02, a one-lap
    // Banger Race on Speedway 2's inner oval, with the in-game results screen alongside.
    internal const string LiveRecord =
        "\u0012RACE\u001F1\u001F1\u001Fspeedway2_inner_oval\u001F1\u001F1\u001F1790957073896\u001F1790957099708\u001F11" +
        "\u001F0\u001F9\u001F10\u001F0\u001F^2*^0eRacer\u001F8\u001F2\u001F1\u001F26264\u001F26264\u001F0\u001F1\u001F195\u001F16\u001FVEHICLE_NAME_0834068683_9\u001FSpeedbird" +
        "\u001F1\u001F9\u001F10\u001F0\u001F^2*^0Djkevino\u001F1\u001F2\u001F1\u001F22089\u001F22089\u001F0\u001F1\u001F209\u001F27\u001FVEHICLE_NAME_3154256208_9\u001FKillerBee" +
        "\u001F2\u001F9\u001F10\u001F0\u001F^2*^0Smidgey87\u001F3\u001F2\u001F1\u001F24022\u001F24022\u001F0\u001F2\u001F124\u001F23\u001FVEHICLE_NAME_3521416707_9\u001FStarbeast" +
        "\u001F3\u001F9\u001F10\u001F0\u001F^2*^0Nzo_009\u001F5\u001F2\u001F1\u001F24560\u001F24560\u001F0\u001F2\u001F146\u001F19\u001FVEHICLE_NAME_2790273595_7\u001FFirefly" +
        "\u001F4\u001F9\u001F10\u001F0\u001F^2*^0SemnteX\u001F9\u001F2\u001F1\u001F26555\u001F26555\u001F0\u001F1\u001F211\u001F15\u001FVEHICLE_NAME_4156104396_8\u001FWarwagon" +
        "\u001F5\u001F9\u001F10\u001F0\u001F^2*^0Darkin20\u001F10\u001F2\u001F1\u001F26785\u001F26785\u001F0\u001F2\u001F146\u001F14\u001FVEHICLE_NAME_2790273595_7\u001FFirefly" +
        "\u001F6\u001F9\u001F10\u001F0\u001F^2*^0StarFall\u001F7\u001F2\u001F1\u001F25916\u001F25916\u001F0\u001F2\u001F119\u001F17\u001FVEHICLE_NAME_4292131867_11\u001FGatecrasher" +
        "\u001F7\u001F9\u001F10\u001F0\u001F^2*^0hazy33\u001F6\u001F2\u001F1\u001F25135\u001F25135\u001F0\u001F2\u001F103\u001F18\u001FVEHICLE_NAME_1549136679_8\u001FNexus RX" +
        "\u001F8\u001F9\u001F10\u001F0\u001F^2*^0gl3nyd\u001F4\u001F2\u001F1\u001F24348\u001F24348\u001F0\u001F1\u001F181\u001F20\u001FVEHICLE_NAME_3180232497_10\u001FRoadcutter" +
        "\u001F9\u001F9\u001F10\u001F0\u001F^2*^0Nykaa\u001F2\u001F2\u001F1\u001F23744\u001F23744\u001F0\u001F2\u001F110\u001F25\u001FVEHICLE_NAME_4156104396_8\u001FWarwagon" +
        "\u001F10\u001F6\u001F50\u001F76561197985810610\u001FProcat\u001F0\u001F2\u001F65\u001F21292\u001F21292\u001F21292\u001F0\u001F346\u001F30\u001FVEHICLE_NAME_2244970999_13\u001FSunrise Super\u0013";

    [Fact]
    public void Parses_a_record_captured_live_as_the_results_screen_showed_it()
    {
        var race = HookRaceRecord.TryParse(LiveRecord);

        Assert.NotNull(race);
        Assert.Equal("speedway2_inner_oval", race!.TrackId);
        Assert.Equal(11, race.Cars.Count);

        // Pos, name, class, rating, car, pts, time, as on screen.
        var top = race.Cars.OrderBy(car => car.Position)
            .Select(car => (car.Position, car.Name, car.ClassName, car.Rating, car.VehicleName, car.CupPoints, car.TimeMs))
            .Take(3);
        Assert.Equal(
            new (int?, string, string, int, string, int, int?)[]
            {
                (1, "Procat", "A", 346, "Sunrise Super", 30, 21292),
                (2, "Djkevino", "B", 209, "KillerBee", 27, 22089),
                (3, "Nykaa", "C", 110, "Warwagon", 25, 23744),
            },
            top);

        // Two bots in the same model share its key: the field is the model, not the car.
        var warwagons = race.Cars.Where(car => car.VehicleName == "Warwagon").Select(car => car.VehicleKey).Distinct();
        Assert.Single(warwagons);

        Assert.Equal(ProcatSteamId, race.Cars.Single(car => !car.IsBot).SteamId);
        Assert.Equal(10, race.Cars.Count(car => car.Outcome == RaceOutcome.Projected));
    }

    [Fact]
    public void Parses_the_race_header()
    {
        var race = HookRaceRecord.TryParse(Record(Procat, Djkevino));

        Assert.NotNull(race);
        Assert.Equal(2, race!.EventCounter);
        Assert.Equal("speedway1_figure_8", race.TrackId);
        Assert.Equal(1, race.Laps);
        Assert.Equal(1, race.GameMode);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_790_000_000_000), race.StartedAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_790_000_030_000), race.EndedAt);
        Assert.Equal(2, race.Cars.Count);
    }

    [Fact]
    public void Parses_a_human_who_crossed_the_line()
    {
        var car = HookRaceRecord.TryParse(Record(Procat))!.Cars.Single();

        Assert.Equal("Procat", car.Name);
        Assert.False(car.IsBot);
        Assert.Equal(ProcatSteamId, car.SteamId);
        Assert.Equal(1, car.Position);
        Assert.Equal(26418, car.TimeMs);
        Assert.Equal(26418, car.BestLapMs);
        Assert.Equal("A", car.ClassName);
        Assert.Equal(346, car.Rating);
        Assert.Equal(60, car.CupPoints);
        Assert.Equal("VEHICLE_NAME_2244970999_13", car.VehicleKey);
        Assert.Equal("Sunrise Super", car.VehicleName);
        Assert.Equal(RaceOutcome.Finished, car.Outcome);
    }

    // The game simulates the remaining laps of every bot still driving when the last
    // human finishes, and marks them finished without the crossed-the-line flag.
    [Fact]
    public void A_bot_finish_without_the_crossed_line_flag_is_projected()
    {
        var car = HookRaceRecord.TryParse(Record(Djkevino))!.Cars.Single();

        Assert.True(car.IsBot);
        Assert.Equal(RaceOutcome.Projected, car.Outcome);
        Assert.Equal(2, car.Position);
        Assert.Equal(26972, car.TimeMs);
        Assert.Null(car.FinishMs);
    }

    [Fact]
    public void Strips_colour_codes_and_the_bot_star_from_a_bot_name()
    {
        var car = HookRaceRecord.TryParse(Record(Nykaa))!.Cars.Single();

        Assert.Equal("Nykaa", car.Name);
        Assert.Equal("C", car.ClassName);
    }

    // Bots carry no Steam ID worth keeping, whatever the slot holds.
    [Fact]
    public void A_bot_never_gets_a_steam_id()
    {
        var bot = Car(3, 0x0A, 12345, "^2*^0Nzo_009", 3, 0x01, 30146, 30146, 0, 2, 146, 40, "k", "Firefly");

        Assert.Null(HookRaceRecord.TryParse(Record(bot))!.Cars.Single().SteamId);
    }

    // A human's name may start with '*'; only a bot's star is the game's marker.
    [Fact]
    public void Keeps_a_star_in_a_human_name()
    {
        var human = Car(0, 0x32, 1, "*Star", 0, 0x41, 1, 1, 1, 0, 1, 0, "k", "v");

        Assert.Equal("*Star", HookRaceRecord.TryParse(Record(human))!.Cars.Single().Name);
    }

    [Fact]
    public void Flags_a_did_not_finish()
    {
        var dnf = Car(0, 0x32, 1, "Quitter", 4, 0x10, 0, 0, 0, 0, 1, 0, "k", "v");

        var car = HookRaceRecord.TryParse(Record(dnf))!.Cars.Single();

        Assert.Equal(RaceOutcome.DidNotFinish, car.Outcome);
        Assert.Null(car.TimeMs);
        Assert.Null(car.BestLapMs);
    }

    // A human finished without the crossed-the-line flag has not been seen; rather
    // than guess, it is left for a person to look at.
    [Fact]
    public void A_human_finish_without_the_crossed_line_flag_is_unknown()
    {
        var odd = Car(0, 0x32, 1, "Human", 0, 0x01, 1000, 1000, 0, 0, 1, 0, "k", "v");

        Assert.Equal(RaceOutcome.Unknown, HookRaceRecord.TryParse(Record(odd))!.Cars.Single().Outcome);
    }

    [Fact]
    public void An_unset_position_is_null()
    {
        var unplaced = Car(0, 0x32, 1, "Late", 0xFF, 0, 0, 0, 0, 0, 1, 0, "k", "v");

        Assert.Null(HookRaceRecord.TryParse(Record(unplaced))!.Cars.Single().Position);
    }

    // Attaching mid-race means the start was never seen.
    [Fact]
    public void An_unseen_start_is_null()
    {
        Assert.Null(HookRaceRecord.TryParse(RecordWith(1, "t", 0, Procat))!.StartedAt);
    }

    // A vehicle the hook could not follow arrives as two empty strings.
    [Fact]
    public void Accepts_an_unreadable_vehicle()
    {
        var car = Car(0, 0x32, 1, "NoCar", 0, 0x41, 1, 1, 1, 0, 1, 0, "", "");

        var parsed = HookRaceRecord.TryParse(Record(car))!.Cars.Single();

        Assert.Equal("", parsed.VehicleKey);
        Assert.Equal("", parsed.VehicleName);
    }

    [Fact]
    public void Accepts_a_race_with_no_cars()
    {
        Assert.Empty(HookRaceRecord.TryParse(Record())!.Cars);
    }

    [Fact]
    public void Rejects_an_unsupported_version()
    {
        Assert.Null(HookRaceRecord.TryParse(RecordWith(2, "t", 1, Procat)));
    }

    [Fact]
    public void Rejects_a_car_count_that_does_not_match_the_fields()
    {
        var record = Record(Procat, Djkevino);
        var oneCarMissing = record[..record.LastIndexOf(S + Djkevino, StringComparison.Ordinal)] + E;

        Assert.Null(HookRaceRecord.TryParse(oneCarMissing));
    }

    // 268435456 * 16 overflows int to 0, which would match a header-only record and
    // then ask for a list of 268 million cars. TryParse must never throw.
    [Fact]
    public void Rejects_a_car_count_that_would_overflow()
    {
        var header = $"{HookRaceRecord.Marker}{S}{string.Join(S, 1, 2, "t", 1, 1, 1, 1, 268435456)}{E}";

        Assert.Null(HookRaceRecord.TryParse(header));
    }

    [Theory]
    [InlineData(253402300800000UL, 1UL)]
    [InlineData(1UL, 253402300800000UL)]
    [InlineData(1UL, ulong.MaxValue)]
    public void Rejects_a_time_past_what_a_date_can_hold(ulong startedMs, ulong endedMs)
    {
        var record = $"{HookRaceRecord.Marker}{S}{string.Join(S, 1, 2, "t", 1, 1, startedMs, endedMs, 0)}{E}";

        Assert.Null(HookRaceRecord.TryParse(record));
    }

    [Fact]
    public void Rejects_a_truncated_record()
    {
        Assert.Null(HookRaceRecord.TryParse(Record(Procat)[..^1]));
    }

    [Fact]
    public void Rejects_a_non_numeric_field()
    {
        Assert.Null(HookRaceRecord.TryParse(Record(Procat.Replace("26418", "fast"))));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Event ended!")]
    [InlineData("\u0012CHAT\u001F1\u001Fline\u001Fmessage\u0013")]
    public void Rejects_anything_else(string? line)
    {
        Assert.Null(HookRaceRecord.TryParse(line));
        Assert.False(HookRaceRecord.LooksLikeRecord(line));
    }

    // A race record must reach the demux untouched: normalising strips "^2" and "^0"
    // from bot names, which would leave a record that parses with the wrong names.
    [Fact]
    public void Passes_through_the_fanout_unnormalised()
    {
        var record = Record(Djkevino);

        Assert.Equal(record, InjectedHookOutputReader.PrepareForFanout(record));
    }
}
