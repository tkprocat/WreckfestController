using WreckfestController.Data.Catalogue;

namespace WreckfestController.Controllers;

// The public track pages' data (#261). As with PublicResponses: every field is safe for
// anyone to read, so add to these deliberately and never return an entity in their place.

/// <summary>
/// The hash of the picture each image link would answer with, or null where it answers 404.
/// Link with <c>?v=&lt;hash&gt;</c> to have it cached for good. <see cref="Layout"/>: a
/// variant's layout outline, drawn over its picture.
/// </summary>
public sealed record PublicImages(string? Small, string? Large, string? Layout);

/// <summary>GET /api/public/tracks: one track and its visible variants.</summary>
public sealed record PublicTrackSummary(
    string Key,
    string Name,
    TrackOrigin Origin,
    string? ModName,
    PublicImages Images,
    IReadOnlyList<PublicVariantSummary> Variants);

/// <summary>
/// A variant. <see cref="TrackLengthM"/>: one lap in metres, null for a derby arena or one no
/// dump has measured. <see cref="RaceCount"/>: races logged on it, filled in on a track's page.
/// </summary>
public sealed record PublicVariantSummary(
    string VariantId,
    string Name,
    GameMode GameMode,
    double? TrackLengthM,
    PublicImages Images,
    int RaceCount);

/// <summary>GET /api/public/tracks/{key}. <see cref="WorkshopId"/>: the Steam workshop item of a mod track.</summary>
public sealed record PublicTrackDetail(
    string Key,
    string Name,
    TrackOrigin Origin,
    string? ModName,
    string? WorkshopId,
    PublicImages Images,
    IReadOnlyList<PublicVariantSummary> Variants);

/// <summary>
/// GET /api/public/variants/{variantId}: what the race log says about one variant.
/// <see cref="TypicalLapMs"/>: the median of race time over laps for every finish here
/// (<see cref="TypicalLapFinishes"/> of them), so it includes the standing start; null
/// before anyone has finished a race here.
/// </summary>
public sealed record PublicVariantDetail(
    string VariantId,
    int RaceCount,
    int? TypicalLapMs,
    int TypicalLapFinishes,
    IReadOnlyList<PublicRace> RecentRaces,
    IReadOnlyList<PublicLapRecord> FastestLaps,
    IReadOnlyList<PublicWinRecord> MostWins,
    IReadOnlyList<PublicTrackCup> Cups);

/// <summary>A player's best lap here. Players only: bots set no records.</summary>
public sealed record PublicLapRecord(string Name, string VehicleName, int LapMs, DateTime EndedAt);

/// <summary>A player's wins here, out of the races they ran here.</summary>
public sealed record PublicWinRecord(string Name, int Wins, int Races);

/// <summary>A cup whose rotation has the variant: its name and schedule only.</summary>
public sealed record PublicTrackCup(string Name, bool IsActive, DateTime? NextOccurrence, string? Repeat);
