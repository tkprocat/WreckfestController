using System.Text.RegularExpressions;
using WreckfestController.Data.Catalogue;
using WreckfestController.Data.Collections;

namespace WreckfestController.Models;

/// <summary>The request field that broke a rule, and why.</summary>
public sealed record EventLoopError(string Field, string Message);

/// <summary>
/// What an event loop must look like before it is written to server_config.cfg, or
/// saved as a collection that will be. Every value becomes a line of the config, so
/// line breaks are refused, and the track id must be one the game could load.
/// </summary>
public static class EventLoopTrackRules
{
    private static readonly Regex TrackIdPattern = new(TrackVariant.VariantIdPattern, RegexOptions.CultureInvariant);

    public static EventLoopError? Validate(string? collectionName, IReadOnlyList<EventLoopTrack?>? tracks) =>
        ValidateName("collectionName", collectionName) ?? ValidateTracks(tracks);

    public static EventLoopError? ValidateName(string field, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return new(field, $"{field} is required.");
        }

        if (HasLineBreak(name))
        {
            return new(field, $"{field} must not contain line breaks.");
        }

        if (name.Trim().Length > TrackCollection.NameMaxLength)
        {
            return new(field, $"{field} must be at most {TrackCollection.NameMaxLength} characters.");
        }

        return null;
    }

    public static EventLoopError? ValidateTracks(IReadOnlyList<EventLoopTrack?>? tracks)
    {
        if (tracks is null)
        {
            return new("tracks", "tracks is required.");
        }

        for (var i = 0; i < tracks.Count; i++)
        {
            var field = $"tracks[{i}]";
            var track = tracks[i];
            if (track is null || string.IsNullOrWhiteSpace(track.Track))
            {
                return new($"{field}.track", $"{field}.track is required.");
            }

            string?[] values = [track.Track, track.Gamemode, track.CarClassRestriction, track.CarRestriction, track.Weather];
            if (values.Any(HasLineBreak))
            {
                return new(field, $"{field} must not contain line breaks.");
            }

            if (!TrackIdPattern.IsMatch(track.Track))
            {
                return new($"{field}.track", $"{field}.track must be a game track id: letters, digits and '_'.");
            }

            if (values.Skip(1).Any(v => v?.Length > CollectionLimits.TextMaxLength))
            {
                return new(field, $"{field} values must be at most {CollectionLimits.TextMaxLength} characters.");
            }

            if (track.Laps < 0 || track.Bots < 0 || track.NumTeams < 0)
            {
                return new(field, $"{field} laps, bots and numTeams must not be negative.");
            }

            if (track.CarResetDisabled is not (null or 0 or 1)
                || track.WrongWayLimiterDisabled is not (null or 0 or 1))
            {
                return new(field, $"{field} carResetDisabled and wrongWayLimiterDisabled must be 0 or 1.");
            }
        }

        return null;
    }

    private static bool HasLineBreak(string? value) =>
        value is not null && value.AsSpan().IndexOfAny('\r', '\n') >= 0;
}
