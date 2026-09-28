namespace WreckfestController.Controllers;

// The public home page's data. Every field here is safe for anyone to read: add to
// these records deliberately, and never return a model or entity in their place.

/// <summary>GET /api/public/overview. Nulls mean "not known right now", such as before setup.</summary>
public sealed record PublicOverview(
    string? ServerName,
    int? MaxPlayers,
    PublicStatus Status,
    PublicTrack? CurrentTrack,
    PublicPlayers Players,
    PublicRotation Rotation,
    PublicActiveCup? ActiveCup,
    IReadOnlyList<PublicUpcomingCup> UpcomingCups,
    DateTimeOffset UpdatedAt);

public sealed record PublicStatus(bool IsRunning, long? UptimeSeconds);

/// <summary>A track by the game's variant id, with the catalogue's name when it has one.</summary>
public sealed record PublicTrack(string Id, string Name);

/// <summary><see cref="Humans"/> and <see cref="Bots"/> count <see cref="List"/>.</summary>
public sealed record PublicPlayers(int Humans, int Bots, IReadOnlyList<PublicPlayer> List);

public sealed record PublicPlayer(string Name, bool IsBot);

public sealed record PublicRotation(string? Name, IReadOnlyList<PublicRotationTrack> Tracks);

public sealed record PublicRotationTrack(string Id, string Name, string? GameMode, int? Laps);

public sealed record PublicActiveCup(string Name, DateTime? ActivatedAt);

/// <summary>A cup's next occurrence. Its server settings are never shown here.</summary>
public sealed record PublicUpcomingCup(string Name, string Description, DateTime NextOccurrence, string? Repeat);
