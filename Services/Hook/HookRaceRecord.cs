using System.Globalization;
using System.Text.Json.Serialization;

namespace WreckfestController.Services.Hook;

/// <summary>
/// One finished race as the injected hook read it from the game's memory, at the moment
/// the last human was done and the results screen opened.
///
/// Wire format, one record per line on the output pipe, every field separated by 0x1F:
/// <code>
/// \x12RACE  version eventCounter trackId laps gameMode startedUnixMs endedUnixMs carCount
///           { slot playerStatus playerFlags steamId name position lap carFlags
///             timeMs bestLapMs finishMs classIndex rating cupPoints vehicleKey vehicleName }
/// \x13
/// </code>
/// The hook sends raw values only, and the interpretation lives here, where it is unit
/// tested. The layout is documented in docs/finding-rvas.md.
/// </summary>
public sealed record HookRaceRecord(
    int EventCounter,
    string TrackId,
    int Laps,
    int GameMode,
    DateTimeOffset? StartedAt,
    DateTimeOffset EndedAt,
    IReadOnlyList<HookRaceCar> Cars)
{
    public const char RecordStart = '\u0012';
    public const char RecordEnd = '\u0013';
    public const char FieldSeparator = '\u001F';

    /// <summary>Enough to claim a line as a race record, so a truncated one is still ours.</summary>
    public const string Marker = "\u0012RACE";

    public const int SupportedVersion = 1;
    public const int HeaderFieldCount = 8;
    public const int CarFieldCount = 16;

    /// <summary>The game has 24 slots; the hook caps at 32. Anything larger is not a record.</summary>
    public const int MaxCars = 64;

    /// <summary>The last millisecond <see cref="DateTimeOffset"/> can hold.</summary>
    private const ulong MaxUnixMs = 253402300799999;

    private const string Prefix = Marker + "\u001F";

    public static bool LooksLikeRecord(string? line) =>
        line != null && line.StartsWith(Marker, StringComparison.Ordinal);

    /// <summary>
    /// Returns null for anything that is not a complete, well-formed record of a
    /// supported version. Never throws: this runs on the thread draining the hook pipe.
    /// </summary>
    public static HookRaceRecord? TryParse(string? line)
    {
        if (line == null ||
            !line.StartsWith(Prefix, StringComparison.Ordinal) ||
            line.Length <= Prefix.Length ||
            line[^1] != RecordEnd)
        {
            return null;
        }

        // The hook rewrites control bytes in every string to '?', so a separator can
        // only be one the hook wrote.
        var fields = line[Prefix.Length..^1].Split(FieldSeparator);
        if (fields.Length < HeaderFieldCount ||
            !TryInt(fields[0], out var version) || version != SupportedVersion ||
            !TryInt(fields[1], out var eventCounter) ||
            !TryInt(fields[3], out var laps) ||
            !TryInt(fields[4], out var gameMode) ||
            !TryULong(fields[5], out var startedMs) || startedMs > MaxUnixMs ||
            !TryULong(fields[6], out var endedMs) || endedMs == 0 || endedMs > MaxUnixMs ||
            !TryInt(fields[7], out var carCount) || carCount is < 0 or > MaxCars ||
            fields.Length != HeaderFieldCount + carCount * CarFieldCount)
        {
            return null;
        }

        var cars = new List<HookRaceCar>(carCount);
        for (var i = 0; i < carCount; i++)
        {
            var car = HookRaceCar.TryParse(fields.AsSpan(HeaderFieldCount + i * CarFieldCount, CarFieldCount));
            if (car == null)
            {
                return null;
            }

            cars.Add(car);
        }

        return new HookRaceRecord(
            eventCounter,
            fields[2],
            laps,
            gameMode,
            startedMs == 0 ? null : FromUnixMs(startedMs),
            FromUnixMs(endedMs),
            cars);
    }

    internal static bool TryInt(string text, out int value) =>
        int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);

    internal static bool TryULong(string text, out ulong value) =>
        ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    /// <summary>Only called with values already checked against <see cref="MaxUnixMs"/>.</summary>
    private static DateTimeOffset FromUnixMs(ulong ms) =>
        DateTimeOffset.FromUnixTimeMilliseconds((long)ms);
}

/// <summary>How a car's race ended, as far as the game's flags tell us.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RaceOutcome>))]
public enum RaceOutcome
{
    /// <summary>Crossed the line.</summary>
    Finished,

    /// <summary>
    /// A bot the game gave a finishing time by simulating its remaining laps when the
    /// last human finished. Its position and time are the game's estimate.
    /// </summary>
    Projected,

    /// <summary>Did not finish.</summary>
    DidNotFinish,

    /// <summary>No flag we recognise; the raw flags are kept for a later look.</summary>
    Unknown,
}

/// <summary>One car's row of a <see cref="HookRaceRecord"/>.</summary>
public sealed record HookRaceCar(
    int Slot,
    int PlayerStatus,
    int PlayerFlags,
    ulong? SteamId,
    string Name,
    bool IsBot,
    int? Position,
    int Lap,
    uint CarFlags,
    int? TimeMs,
    int? BestLapMs,
    int? FinishMs,
    int ClassIndex,
    int Rating,
    int CupPoints,
    string VehicleKey,
    string VehicleName)
{
    /// <summary>Player-table flag the game tests to tell a bot from a human.</summary>
    public const int PlayerFlagBot = 0x08;

    public const uint CarFlagFinished = 0x01;
    public const uint CarFlagWrecked = 0x02;
    public const uint CarFlagDidNotFinish = 0x10;

    /// <summary>
    /// Seen only on a human who crossed the line; never on a bot whose finish the game
    /// simulated. Provisional: no race yet had a bot finish ahead of the last human.
    /// </summary>
    public const uint CarFlagCrossedLine = 0x40;

    /// <summary>Unset position byte: the car was never placed.</summary>
    private const int UnsetPosition = 0xFF;

    public RaceOutcome Outcome =>
        (CarFlags & CarFlagDidNotFinish) != 0 ? RaceOutcome.DidNotFinish
        : (CarFlags & CarFlagFinished) == 0 ? RaceOutcome.Unknown
        : (CarFlags & CarFlagCrossedLine) != 0 ? RaceOutcome.Finished
        : IsBot ? RaceOutcome.Projected
        : RaceOutcome.Unknown;

    /// <summary>"A", "B", "C" for the classes seen so far; the raw index otherwise.</summary>
    public string ClassName => ClassIndex is >= 0 and < 3 ? ((char)('A' + ClassIndex)).ToString() : ClassIndex.ToString(CultureInfo.InvariantCulture);

    internal static HookRaceCar? TryParse(ReadOnlySpan<string> f)
    {
        if (!HookRaceRecord.TryInt(f[0], out var slot) ||
            !HookRaceRecord.TryInt(f[1], out var playerStatus) ||
            !HookRaceRecord.TryInt(f[2], out var playerFlags) ||
            !HookRaceRecord.TryULong(f[3], out var steamId) ||
            !HookRaceRecord.TryInt(f[5], out var position) ||
            !HookRaceRecord.TryInt(f[6], out var lap) ||
            !uint.TryParse(f[7], NumberStyles.None, CultureInfo.InvariantCulture, out var carFlags) ||
            !HookRaceRecord.TryInt(f[8], out var timeMs) ||
            !HookRaceRecord.TryInt(f[9], out var bestLapMs) ||
            !HookRaceRecord.TryInt(f[10], out var finishMs) ||
            !HookRaceRecord.TryInt(f[11], out var classIndex) ||
            !HookRaceRecord.TryInt(f[12], out var rating) ||
            !HookRaceRecord.TryInt(f[13], out var cupPoints))
        {
            return null;
        }

        var isBot = (playerFlags & PlayerFlagBot) != 0;
        var name = CleanName(f[4], isBot);
        if (name.Length == 0)
        {
            return null;
        }

        return new HookRaceCar(
            slot,
            playerStatus,
            playerFlags,
            // A SteamID64 keeps its top byte for the universe (1 today), so a real one is
            // far inside a long; anything beyond is not an ID and must not be stored as one.
            isBot || steamId == 0 || steamId > long.MaxValue ? null : steamId,
            name,
            isBot,
            position == UnsetPosition ? null : position + 1,
            lap,
            carFlags,
            Positive(timeMs),
            Positive(bestLapMs),
            Positive(finishMs),
            classIndex,
            rating,
            cupPoints,
            f[14],
            f[15]);
    }

    /// <summary>
    /// Drops the game's colour codes, and for a bot the '*' the game prefixes its name
    /// with ("^2*^0eRacer" is the bot eRacer).
    /// </summary>
    private static string CleanName(string raw, bool isBot)
    {
        var name = InjectedHookOutputReader.NormalizeLine(raw);
        return isBot && name.StartsWith('*') ? name[1..].TrimStart() : name;
    }

    private static int? Positive(int value) => value > 0 ? value : null;
}
