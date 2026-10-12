using System.Text.Json.Serialization;
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
/// is shown as the results screen showed it. <see cref="Warmup"/>: the race ended in its
/// cup's warmup, so it does not count toward the cup.
/// </summary>
public sealed record PublicRace(
    int Id,
    DateTime? StartedAt,
    DateTime EndedAt,
    PublicTrack Track,
    int Laps,
    string? CupName,
    bool Warmup,
    IReadOnlyList<PublicRaceEntry> Entries);

/// <summary>
/// GET /api/public/feed: one item, newest first. <see cref="Race"/> is set for a
/// <c>Race</c> (one outside any cup), <see cref="Cup"/> for a <c>Cup</c> run.
/// </summary>
public sealed record PublicFeedItem(PublicFeedItemKind Kind, PublicRace? Race, PublicCupRun? Cup);

[JsonConverter(typeof(JsonStringEnumConverter<PublicFeedItemKind>))]
public enum PublicFeedItemKind
{
    Race,
    Cup,
}

/// <summary>
/// One run of a cup: one activation and every race in it. <see cref="StartsAt"/> and
/// <see cref="EndsAt"/> are known only while it is the active cup. <see cref="Races"/>
/// lists the newest of <see cref="RaceCount"/>, warmup races included and marked;
/// <see cref="Standings"/> counts only the races once it started, and is empty while
/// nobody has a cup point.
/// </summary>
public sealed record PublicCupRun(
    string Name,
    DateTime ActivatedAt,
    PublicCupRunStatus Status,
    DateTime? StartsAt,
    DateTime? EndsAt,
    IReadOnlyList<PublicCupStanding> Standings,
    int RaceCount,
    IReadOnlyList<PublicRace> Races);

[JsonConverter(typeof(JsonStringEnumConverter<PublicCupRunStatus>))]
public enum PublicCupRunStatus
{
    /// <summary>The active cup, before its start.</summary>
    Warmup,

    /// <summary>The active cup, started.</summary>
    Running,

    /// <summary>No longer the active cup.</summary>
    Ended,
}

/// <summary>A car's cup points total, as the game keeps it. Equal totals share a place.</summary>
public sealed record PublicCupStanding(int Position, string Name, bool IsBot, int Points);

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
