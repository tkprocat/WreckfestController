using WreckfestController.Controllers;
using Row = WreckfestController.Controllers.PublicFeed.StandingRow;

namespace WreckfestController.Tests.Controllers;

public class PublicFeedStandingsTests
{
    private static readonly DateTime Start = new(2026, 10, 9, 19, 0, 0, DateTimeKind.Utc);

    private static Row Car(int race, string name, int points, long? steamId = null, bool isBot = false, int? position = 1) =>
        new(race, Start.AddMinutes(race * 10), steamId, name, isBot, position, points);

    private static List<string> Shown(IEnumerable<Row> rows) =>
        PublicFeed.Standings(rows).Select(s => $"{s.Position} {s.Name}{(s.IsBot ? "*" : "")} {s.Points}").ToList();

    [Fact]
    public void ACarsTotal_IsFromTheLastRaceItWasIn_NotTheHighest()
    {
        // The game's total drops when the server restarts mid-cup; the table follows the game.
        Assert.Equal(["1 Alice 4"], Shown([Car(1, "Alice", 20, steamId: 1), Car(2, "Alice", 4, steamId: 1)]));
    }

    [Fact]
    public void ABotAndAHumanOfTheSameName_AreTwoRows()
    {
        Assert.Equal(
            ["1 Ann 10", "2 Ann* 5"],
            Shown([Car(1, "Ann", 10, steamId: 1), Car(1, "Ann", 5, isBot: true, position: 2)]));
    }

    [Fact]
    public void EqualTotals_ShareAPlace_AndTheNextPlaceSkips()
    {
        Assert.Equal(
            ["1 B 10", "1 A 10", "3 C 2"],
            Shown([Car(1, "A", 10, steamId: 1, position: 2), Car(1, "B", 10, steamId: 2, position: 1), Car(1, "C", 2, steamId: 3, position: 3)]));
    }

    [Fact]
    public void AHumanWithoutASteamId_JoinsTheOneSteamIdOfTheSameName()
    {
        Assert.Equal(["1 Alice 25"], Shown([Car(1, "Alice", 20, steamId: 1), Car(2, "Alice", 25)]));
        Assert.Equal(["1 Alice 25"], Shown([Car(2, "Alice", 25, steamId: 1), Car(1, "Alice", 20)]));
    }

    [Fact]
    public void AHumanWithoutASteamId_JoinsAPlayerWhoHasSinceRenamed()
    {
        // Alice (ID 1), then Alice without an ID, then the same player as Bob.
        Assert.Equal(["1 Bob 30"], Shown([Car(1, "Alice", 20, steamId: 1), Car(2, "Alice", 25), Car(3, "Bob", 30, steamId: 1)]));

        // As the feed calls it: only each identity's latest row, and the names used beside.
        Assert.Equal(
            ["1 Bob 30"],
            PublicFeed.Standings([Car(2, "Alice", 25), Car(3, "Bob", 30, steamId: 1)], [(1, "Alice"), (1, "Bob")])
                .Select(s => $"{s.Position} {s.Name} {s.Points}"));
    }

    [Fact]
    public void AHumanWithoutASteamId_StaysApart_WhenTheNameIsAmbiguous()
    {
        // Two Steam IDs share the name: which one it was is not known.
        Assert.Equal(
            ["1 Alice 30", "2 Alice 25", "3 Alice 20"],
            Shown([Car(1, "Alice", 20, steamId: 1), Car(1, "Alice", 30, steamId: 2), Car(2, "Alice", 25)]));

        // And a bot of that name is never merged.
        Assert.Equal(["1 Alice 20", "2 Alice* 5"], Shown([Car(1, "Alice", 20, steamId: 1), Car(2, "Alice", 5, isBot: true)]));
    }

    [Fact]
    public void WithoutAPointAnywhere_ThereIsNoTable()
    {
        Assert.Empty(PublicFeed.Standings([Car(1, "Alice", 0, steamId: 1), Car(1, "Bot", 0, isBot: true)]));
        Assert.Empty(PublicFeed.Standings([]));
    }
}
