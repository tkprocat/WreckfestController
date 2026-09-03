using WreckfestController.Models;
using WreckfestController.Services;

namespace WreckfestController.Tests.Services;

internal static class PlayerTrackerTestExtensions
{
    /// <summary>
    /// Seeds the roster through the hook-snapshot path, the one that survives the
    /// removal of console list parsing. A "*" name prefix marks a bot, matching the
    /// fixture convention the console-text tests used.
    /// </summary>
    internal static void Seed(this PlayerTracker tracker, params string[] names)
    {
        var players = tracker.GetPlayers();
        players.AddRange(names.Select(name =>
        {
            var isBot = name.StartsWith("*", StringComparison.Ordinal);
            return new Player { Name = isBot ? name[1..] : name, IsBot = isBot };
        }));
        tracker.ProcessHookPlayerSnapshot(players);
    }
}
