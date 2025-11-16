namespace WreckfestController.Models;

public class ServerProcessInfo
{
    public int ProcessId { get; set; }
    public string ConfigFile { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public TimeSpan Uptime => DateTime.Now - StartTime;
    public bool IsAttached { get; set; }
    public string ExecutablePath { get; set; } = string.Empty;
    public long MemoryUsageMB { get; set; }

    public string UptimeString => $"{(int)Uptime.TotalHours}h {Uptime.Minutes}m";
    public string StatusString => IsAttached ? "Attached" : "Free";
}
