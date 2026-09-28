using WreckfestController.Models;
using WreckfestController.Data.Cups;

namespace WreckfestController.Services.Publishing;

/// <summary>
/// Where the services that notice server events report them. The one implementation,
/// <see cref="HubServerEventPublisher"/>, forwards them to the web UI over SignalR;
/// the interface exists so those services can be tested with a mock.
/// Every method is fire-and-forget safe: none of them throws.
/// </summary>
public interface IServerEventPublisher
{
    Task PlayersUpdatedAsync(IReadOnlyList<Player> players);
    Task PlayerJoinedAsync(string playerName, bool isBot);
    Task PlayerLeftAsync(string playerName);
    Task TrackChangedAsync(string trackId);
    Task CupActivatedAsync(int cupId, string cupName);

    /// <summary>A scheduled occurrence has been dealt with. Signed-in clients only.</summary>
    Task CupOccurrenceEndedAsync(int cupId, string cupName, DateTime occurrence, OccurrenceOutcome outcome);
    Task ServerStartedAsync(ServerStartedEvent serverEvent);
    Task ServerStoppedAsync(ServerStoppedEvent serverEvent);
    Task ServerRestartedAsync(ServerRestartedEvent serverEvent);
    Task ServerAttachedAsync(ServerAttachedEvent serverEvent);
    Task ServerRestartPendingAsync(ServerRestartPendingEvent serverEvent);

    /// <summary>Queues one console line. Lines go out in batches, to signed-in clients only.</summary>
    void AddConsoleLog(string line);
}
