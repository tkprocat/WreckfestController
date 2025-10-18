namespace WreckfestController.Models;

public class Player
{
    public string Name { get; set; } = string.Empty;
    public DateTime JoinedAt { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public bool IsOnline { get; set; }
    public bool IsBot { get; set; }  // True if player is a bot (name prefixed with *)
    public int? Slot { get; set; }  // Player slot number if available
}

public class PlayerListResponse
{
    public int TotalPlayers { get; set; }
    public int MaxPlayers { get; set; }
    public List<Player> Players { get; set; } = new();
    public DateTime LastUpdated { get; set; }
}
