namespace WreckfestController.Models;

public class ServerProcessInfo
{
    public int ProcessId { get; set; }
    public string ConfigFile { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public TimeSpan Uptime => DateTime.Now - StartTime;
    public bool IsAttached { get; set; }
    public string ExecutablePath { get; set; } = string.Empty;

    /// <summary>
    /// Whether this is the server the Configuration tab names (the same executable as
    /// ServerPath). Another install folder's server is listed too, but marked.
    /// </summary>
    public bool IsConfiguredServer { get; set; }

    /// <summary>Which controller's marker the command line carries (issue #201).</summary>
    public ServerOwner Owner { get; set; }

    public long MemoryUsageMB { get; set; }

    public string UptimeString => $"{(int)Uptime.TotalHours}h {Uptime.Minutes}m";
    public string StatusString => IsAttached ? "Attached" : "Free";
    public string ServerString => !IsConfiguredServer ? "Other install"
        : Owner switch
        {
            ServerOwner.ThisController => "This controller",
            ServerOwner.OtherController => "Other controller",
            _ => "Configured",
        };
}

/// <summary>
/// Whose server a process is, by the <c>-wfc_controller</c> marker on its command line
/// (<see cref="Services.ServerControl.ControllerInstance"/>).
/// </summary>
public enum ServerOwner
{
    /// <summary>No marker: started by hand, or by a controller from before the marker.</summary>
    Unmarked,

    /// <summary>This controller's marker.</summary>
    ThisController,

    /// <summary>Another controller's marker: never attached to or injected into from here.</summary>
    OtherController,
}
