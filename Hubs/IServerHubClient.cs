namespace WreckfestController.Hubs;

/// <summary>
/// The messages <see cref="ServerHub"/> sends. Each method name is the SignalR
/// message name a client subscribes to, e.g. <c>connection.on("TrackChanged", ...)</c>.
/// Payloads serialize with camelCase property names.
/// </summary>
public interface IServerHubClient
{
    // public group
    Task PlayersUpdated(PlayersUpdatedMessage message);
    Task PlayerJoined(PlayerJoinedMessage message);
    Task PlayerLeft(PlayerLeftMessage message);
    Task TrackChanged(TrackChangedMessage message);
    Task EventActivated(EventActivatedMessage message);
    Task ServerStarted(ServerStartedMessage message);
    Task ServerStopped(ServerStoppedMessage message);
    Task ServerRestarted(ServerRestartedMessage message);
    Task ServerAttached(ServerAttachedMessage message);
    Task ServerRestartPending(ServerRestartPendingMessage message);

    // admin group
    Task ConsoleLog(ConsoleLogMessage message);
}

public sealed record PlayerSummary(
    string Name,
    int? PlayerId,
    int? Score,
    string? Vehicle,
    int? Slot,
    bool IsBot,
    DateTime JoinedAt);

public sealed record PlayersUpdatedMessage(IReadOnlyList<PlayerSummary> Players);

public sealed record PlayerJoinedMessage(string PlayerName, bool IsBot);

public sealed record PlayerLeftMessage(string PlayerName);

public sealed record TrackChangedMessage(string TrackId);

public sealed record EventActivatedMessage(int EventId, string EventName, DateTime Timestamp);

public sealed record ServerStartedMessage(int ProcessId, string ProcessName, DateTime StartTime, DateTime Timestamp);

public sealed record ServerStoppedMessage(int ProcessId, string StopMethod, DateTime Timestamp);

public sealed record ServerRestartedMessage(int? OldProcessId, int NewProcessId, string RestartMethod, DateTime Timestamp);

public sealed record ServerAttachedMessage(int ProcessId, string ProcessName, DateTime StartTime, DateTime Timestamp);

public sealed record ServerRestartPendingMessage(
    int MinutesRemaining,
    string? EventName,
    int? EventId,
    DateTime? ScheduledRestartTime,
    DateTime Timestamp);

/// <summary>Console lines in arrival order, batched about once a second.</summary>
public sealed record ConsoleLogMessage(IReadOnlyList<string> Logs);
