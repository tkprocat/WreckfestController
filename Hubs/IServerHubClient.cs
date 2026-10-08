using WreckfestController.Data.Cups;
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
    Task CupActivated(CupActivatedMessage message);
    Task CupStarted(CupStartedMessage message);
    Task CupEnded(CupEndedMessage message);
    Task ServerStarted(ServerStartedMessage message);
    Task ServerStopped(ServerStoppedMessage message);
    Task ServerRestarted(ServerRestartedMessage message);
    Task ServerAttached(ServerAttachedMessage message);
    Task ServerRestartPending(ServerRestartPendingMessage message);
    Task RaceRecorded(RaceRecordedMessage message);

    // admin group
    Task ConsoleLog(ConsoleLogMessage message);
    Task CupOccurrenceEnded(CupOccurrenceEndedMessage message);
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

public sealed record CupActivatedMessage(int CupId, string CupName, DateTime Timestamp);

/// <summary>The active cup's warmup is over: cup points were reset and the cup counts.</summary>
public sealed record CupStartedMessage(int CupId, string CupName, DateTime Timestamp);

/// <summary>The active cup reached its end time and is no longer the active cup.</summary>
public sealed record CupEndedMessage(int CupId, string CupName, DateTime Timestamp);

public sealed record CupOccurrenceEndedMessage(
    int CupId,
    string CupName,
    DateTime Occurrence,
    OccurrenceOutcome Outcome,
    DateTime Timestamp);

public sealed record ServerStartedMessage(int ProcessId, string ProcessName, DateTime StartTime, DateTime Timestamp);

public sealed record ServerStoppedMessage(int ProcessId, string StopMethod, DateTime Timestamp);

public sealed record ServerRestartedMessage(int? OldProcessId, int NewProcessId, string RestartMethod, DateTime Timestamp);

public sealed record ServerAttachedMessage(int ProcessId, string ProcessName, DateTime StartTime, DateTime Timestamp);

public sealed record ServerRestartPendingMessage(
    int MinutesRemaining,
    string? CupName,
    int? CupId,
    DateTime? ScheduledRestartTime,
    DateTime Timestamp);

/// <summary>A finished race was saved: GET /api/public/races now lists it.</summary>
public sealed record RaceRecordedMessage(int RaceId, string TrackId, DateTime EndedAt);

/// <summary>Console lines in arrival order, batched about once a second.</summary>
public sealed record ConsoleLogMessage(IReadOnlyList<string> Logs);
