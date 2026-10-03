using WreckfestController.Data.Cups;
using WreckfestController.Services.Hook;

namespace WreckfestController.Data.Races;

/// <summary>
/// One finished race, as the injected hook read it when the results screen opened.
/// Written once and never edited: it is history.
/// </summary>
public class Race
{
    public const int TrackIdMaxLength = 128;

    public int Id { get; set; }

    /// <summary>UTC. When the race started; null when the hook attached mid-race.</summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>UTC. When the last human finished and the results screen opened.</summary>
    public DateTime EndedAt { get; set; }

    /// <summary>The game's track id, such as <c>speedway2_inner_oval</c>: a catalogue variant's id.</summary>
    public string TrackId { get; set; } = string.Empty;

    public int Laps { get; set; }

    /// <summary>The game's raw game mode number, kept until the values are mapped.</summary>
    public int GameMode { get; set; }

    /// <summary>The server's event counter; it restarts from 0 with the server process.</summary>
    public int EventCounter { get; set; }

    /// <summary>The cup that was active when the race ended; cleared if the cup is deleted.</summary>
    public int? CupId { get; set; }

    public Cup? Cup { get; set; }

    /// <summary>The cup's name at the time, so history survives deleting or renaming it.</summary>
    public string CupName { get; set; } = string.Empty;

    /// <summary>
    /// UTC. When that cup last became active. A recurring cup runs many evenings; the
    /// races of one evening share this value.
    /// </summary>
    public DateTime? CupActivatedAt { get; set; }

    public List<RaceEntry> Entries { get; set; } = new();
}

/// <summary>One car's result in a <see cref="Race"/>, bots included.</summary>
public class RaceEntry
{
    public const int NameMaxLength = 96;
    public const int VehicleMaxLength = 96;

    public int Id { get; set; }

    public int RaceId { get; set; }

    public Race Race { get; set; } = null!;

    /// <summary>1-based finishing position; null when the game never placed the car.</summary>
    public int? Position { get; set; }

    /// <summary>The name as the results screen showed it, without colour codes or the bot star.</summary>
    public string Name { get; set; } = string.Empty;

    public bool IsBot { get; set; }

    /// <summary>A human's Steam ID: the identity to count by, since players rename. Null for bots.</summary>
    public long? SteamId { get; set; }

    /// <summary>The model's localisation key, such as <c>VEHICLE_NAME_2244970999_13</c>: stable across languages.</summary>
    public string VehicleKey { get; set; } = string.Empty;

    /// <summary>The model's name as the game showed it, such as <c>Sunrise Super</c>.</summary>
    public string VehicleName { get; set; } = string.Empty;

    public RaceOutcome Outcome { get; set; }

    /// <summary>0 A, 1 B, 2 C.</summary>
    public int ClassIndex { get; set; }

    public int Rating { get; set; }

    public int? TimeMs { get; set; }

    public int? BestLapMs { get; set; }

    /// <summary>The cup points total after this race, as the game keeps it for the session.</summary>
    public int CupPointsTotal { get; set; }

    // ---- Raw values, kept so outcomes can be reclassified as more is learned. ----

    public int Slot { get; set; }

    public int Lap { get; set; }

    public long CarFlags { get; set; }

    public int? FinishMs { get; set; }

    public int PlayerStatus { get; set; }

    public int PlayerFlags { get; set; }
}
