using WreckfestController.Data.Cups;
using WreckfestController.Services.Hook;
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

/// <summary>
/// The active cup. <see cref="Phase"/> is <c>Warmup</c> until <see cref="StartsAt"/>, then
/// <c>Running</c> until <see cref="EndsAt"/> (null: no end). Both times are null for an
/// activation that stood for no occurrence.
/// </summary>
public sealed record PublicActiveCup(
    string Name,
    DateTime? ActivatedAt,
    CupPhase? Phase,
    DateTime? StartsAt,
    DateTime? EndsAt);

/// <summary>
/// A cup's next occurrence: the server restarts into it at <see cref="WarmupAt"/> (the start,
/// without a warmup). Its server settings are never shown here.
/// </summary>
public sealed record PublicUpcomingCup(
    string Name,
    string Description,
    DateTime NextOccurrence,
    string? Repeat,
    DateTime? WarmupAt,
    DateTime? EndsAt);

/// <summary>
/// GET /api/public/races: the latest finished races, newest first. No Steam ids: a name
/// is shown as the results screen showed it.
/// </summary>
public sealed record PublicRace(
    int Id,
    DateTime? StartedAt,
    DateTime EndedAt,
    PublicTrack Track,
    int Laps,
    string? CupName,
    IReadOnlyList<PublicRaceEntry> Entries);

/// <summary>
/// One car, in finishing order; <see cref="Position"/> is null for a car the game never
/// placed. Bots keep their places.
/// </summary>
public sealed record PublicRaceEntry(
    int? Position,
    string Name,
    bool IsBot,
    string VehicleName,
    RaceOutcome Outcome,
    int? TimeMs,
    int? BestLapMs);
