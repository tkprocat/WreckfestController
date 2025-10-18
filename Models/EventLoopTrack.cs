namespace WreckfestController.Models;

public class EventLoopTrack
{
    public string Track { get; set; } = string.Empty;
    public string? Gamemode { get; set; }
    public int? Laps { get; set; }
    public int? Bots { get; set; }
    public int? NumTeams { get; set; }
    public int? CarResetDisabled { get; set; }
    public int? WrongWayLimiterDisabled { get; set; }
    public string? CarClassRestriction { get; set; }
    public string? CarRestriction { get; set; }
    public string? Weather { get; set; }
}
