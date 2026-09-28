namespace WreckfestController.Data.Cups;

/// <summary>
/// The values a cup may set for <c>session_mode</c> and <c>grid_order</c>, as the dedicated
/// server's own commented server_config.cfg lists them (also printed by its
/// <c>sessionmodes</c> and <c>gridorders</c> console commands). Each value becomes a line
/// of server_config.cfg, so nothing outside these lists is accepted.
/// </summary>
public static class CupScoring
{
    /// <summary><c>normal</c> awards no cup points; the qualifying modes set the next race's grid.</summary>
    public static readonly IReadOnlyList<string> SessionModes =
    [
        "normal",
        "qualify-sprint",
        "qualify-lap",
        "30p-aggr",
        "25p-aggr",
        "25p-mod",
        "24p-lin",
        "16p-lin",
        "10p-double",
        "10p-lin",
        "35p-folk",
        "f1-1991",
        "f1-2003",
        "f1-2010",
        "player_count_1",
    ];

    /// <summary><c>cup_normal</c> and <c>cup_reverse</c> sort by cup points.</summary>
    public static readonly IReadOnlyList<string> GridOrders =
    [
        "random",
        "perf_normal",
        "perf_reverse",
        "qualifying",
        "cup_normal",
        "cup_reverse",
    ];
}
