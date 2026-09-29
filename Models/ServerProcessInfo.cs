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
    public long MemoryUsageMB { get; set; }

    public string UptimeString => $"{(int)Uptime.TotalHours}h {Uptime.Minutes}m";
    public string StatusString => IsAttached ? "Attached" : "Free";
    public string ServerString => IsConfiguredServer ? "Configured" : "Other install";
}
