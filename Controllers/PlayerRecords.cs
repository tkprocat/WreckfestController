namespace WreckfestController.Controllers;

/// <summary>
/// Joins a layout's per-name race rows into players for its public records (#261). A player
/// is their Steam id. A row the log has no Steam id for (it can lose one when a player
/// leaves before the results screen) joins the one Steam id that raced under that name; a
/// name more than one Steam id used, or none, stays a player of its own.
/// </summary>
public static class PlayerRecords
{
    /// <summary>One (Steam id, name) group of a layout's entries, summed in the database.</summary>
    public sealed record Row(long? SteamId, string Name, int? BestLapMs, int Wins, int Races, DateTime LastRaced);

    /// <summary>
    /// A player: shown by the name they last raced under. <see cref="NameOnlyNames"/> are the
    /// names of the rows without a Steam id that were joined to it.
    /// </summary>
    public sealed record Player(long? SteamId, string Name, int? BestLapMs, int Wins, int Races, DateTime LastRaced, IReadOnlyList<string> NameOnlyNames);

    public static List<Player> Join(IReadOnlyList<Row> rows)
    {
        var steamIdsByName = rows
            .Where(r => r.SteamId is not null)
            .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(r => r.SteamId!.Value).Distinct().ToList(), StringComparer.OrdinalIgnoreCase);

        return rows
            .GroupBy(r => r.SteamId is { } id
                ? (SteamId: (long?)id, Name: (string?)null)
                : steamIdsByName.TryGetValue(r.Name, out var ids) && ids.Count == 1
                    ? (ids[0], null)
                    : (null, r.Name))
            .Select(g =>
            {
                var latest = g.OrderByDescending(r => r.LastRaced).First();
                return new Player(
                    g.Key.SteamId,
                    latest.Name,
                    g.Min(r => r.BestLapMs),
                    g.Sum(r => r.Wins),
                    g.Sum(r => r.Races),
                    latest.LastRaced,
                    g.Where(r => r.SteamId is null).Select(r => r.Name).Distinct().ToList());
            })
            .ToList();
    }
}
