using WreckfestController.Models;

namespace WreckfestController.Services;

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
    Task EventActivatedAsync(int eventId, string eventName);
    Task ServerStartedAsync(ServerStartedEvent serverEvent);
    Task ServerStoppedAsync(ServerStoppedEvent serverEvent);
    Task ServerRestartedAsync(ServerRestartedEvent serverEvent);
    Task ServerAttachedAsync(ServerAttachedEvent serverEvent);
    Task ServerRestartPendingAsync(ServerRestartPendingEvent serverEvent);

    /// <summary>Queues one console line. Lines go out in batches, to signed-in clients only.</summary>
    void AddConsoleLog(string line);
}
