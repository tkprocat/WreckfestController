namespace WreckfestController.Models;

/// <summary>
/// Event triggered when the server starts successfully
/// </summary>
public class ServerStartedEvent
{
    public int ProcessId { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
}

/// <summary>
/// Event triggered when the server stops
/// </summary>
public class ServerStoppedEvent
{
    public int ProcessId { get; set; }
    public string StopMethod { get; set; } = string.Empty; // "Graceful" or "Force"
}

/// <summary>
/// Event triggered when the server restarts
/// </summary>
public class ServerRestartedEvent
{
    public int? OldProcessId { get; set; }
    public int NewProcessId { get; set; }
    public string RestartMethod { get; set; } = string.Empty; // "Command" or "Full"
}

/// <summary>
/// Event triggered when WreckfestController attaches to a running server process
/// </summary>
public class ServerAttachedEvent
{
    public int ProcessId { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
}

/// <summary>
/// Event triggered when a server restart is pending (countdown warnings)
/// </summary>
public class ServerRestartPendingEvent
{
    public int MinutesRemaining { get; set; }
    public string? EventName { get; set; }
    public int? EventId { get; set; }
    public DateTime? ScheduledRestartTime { get; set; }
}
