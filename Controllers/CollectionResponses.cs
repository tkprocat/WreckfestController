using System.ComponentModel.DataAnnotations;
using WreckfestController.Data.Catalogue;
using WreckfestController.Data.Collections;
using WreckfestController.Models;

namespace WreckfestController.Controllers;

/// <summary>The catalogue variant an entry is linked to, for labels and warnings.</summary>
public sealed record CollectionVariantResponse(
    int Id,
    string Name,
    int TrackId,
    string TrackName,
    GameMode GameMode,
    bool IsHidden)
{
    public static CollectionVariantResponse From(TrackVariant variant) => new(
        variant.Id,
        variant.Name,
        variant.TrackId,
        variant.Track.Name,
        variant.GameMode,
        variant.IsHidden || variant.Track.IsHidden);
}

/// <summary>
/// One slot of a rotation: the fields of <see cref="EventLoopTrack"/>, as
/// <c>GET /api/config/tracks</c> returns them, plus the linked variant when the catalogue
/// knows the track.
/// </summary>
public sealed record CollectionTrackResponse(
    string Track,
    CollectionVariantResponse? Variant,
    string? Gamemode,
    int? Laps,
    int? Bots,
    int? NumTeams,
    int? CarResetDisabled,
    int? WrongWayLimiterDisabled,
    string? CarClassRestriction,
    string? CarRestriction,
    string? Weather)
{
    /// <summary>Expects the entry's variant, and the variant's track, loaded.</summary>
    public static CollectionTrackResponse From(TrackCollectionEntry entry)
    {
        var track = CollectionMapping.ToEventLoopTrack(entry);
        return new(
            track.Track,
            entry.TrackVariant is null ? null : CollectionVariantResponse.From(entry.TrackVariant),
            track.Gamemode,
            track.Laps,
            track.Bots,
            track.NumTeams,
            track.CarResetDisabled,
            track.WrongWayLimiterDisabled,
            track.CarClassRestriction,
            track.CarRestriction,
            track.Weather);
    }
}

public sealed record CollectionResponse(
    int Id,
    string Name,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<CollectionTrackResponse> Tracks)
{
    /// <summary>Expects the entries loaded in position order, each with its variant and track.</summary>
    public static CollectionResponse From(TrackCollection collection) => new(
        collection.Id,
        collection.Name,
        collection.Version,
        collection.CreatedAt,
        collection.UpdatedAt,
        collection.Entries.OrderBy(e => e.Position).Select(CollectionTrackResponse.From).ToList());
}

/// <summary>A row of the collection list: no tracks, just how many.</summary>
public sealed record CollectionSummaryResponse(
    int Id,
    string Name,
    int Version,
    int TrackCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>The whole collection: its name and its tracks in rotation order.</summary>
public sealed record CollectionRequest
{
    [Required]
    public string Name { get; init; } = string.Empty;

    /// <summary>The same shape <c>GET /api/config/tracks</c> returns, so a deployed rotation can be saved as is.</summary>
    [Required]
    public IReadOnlyList<EventLoopTrack?> Tracks { get; init; } = [];
}

public sealed record DuplicateCollectionRequest
{
    /// <summary>Defaults to "&lt;name&gt; (copy)", numbered when that is taken.</summary>
    public string? Name { get; init; }
}

public sealed record DeployCollectionResponse(string Message, string CollectionName, int Count);

/// <summary>Collection entries to and from the event loop's <see cref="EventLoopTrack"/>.</summary>
public static class CollectionMapping
{
    /// <summary>Expects the entry's variant loaded when it has one.</summary>
    public static EventLoopTrack ToEventLoopTrack(TrackCollectionEntry entry) => new()
    {
        Track = entry.EffectiveTrackId,
        Gamemode = entry.Gamemode,
        Laps = entry.Laps,
        Bots = entry.Bots,
        NumTeams = entry.NumTeams,
        CarResetDisabled = ToFlag(entry.CarResetDisabled),
        WrongWayLimiterDisabled = ToFlag(entry.WrongWayLimiterDisabled),
        CarClassRestriction = entry.CarClassRestriction,
        CarRestriction = entry.CarRestriction,
        Weather = entry.Weather,
    };

    /// <summary>An unlinked entry at <paramref name="position"/>. Assumes the track passed <see cref="EventLoopTrackRules"/>.</summary>
    public static TrackCollectionEntry ToEntry(EventLoopTrack track, int position) => new()
    {
        Position = position,
        TrackId = track.Track.Trim(),
        Gamemode = Text(track.Gamemode),
        Laps = track.Laps,
        Bots = track.Bots,
        NumTeams = track.NumTeams,
        CarResetDisabled = track.CarResetDisabled is { } reset ? reset == 1 : null,
        WrongWayLimiterDisabled = track.WrongWayLimiterDisabled is { } wrongWay ? wrongWay == 1 : null,
        CarClassRestriction = Text(track.CarClassRestriction),
        CarRestriction = Text(track.CarRestriction),
        Weather = Text(track.Weather),
    };

    private static int? ToFlag(bool? value) => value is null ? null : value.Value ? 1 : 0;

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
