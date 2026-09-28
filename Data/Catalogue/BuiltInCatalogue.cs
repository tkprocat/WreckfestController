namespace WreckfestController.Data.Catalogue;

public sealed record ShippedTag(string Name, string Slug, string? Color);

public sealed record ShippedVariant(
    string VariantId,
    string Name,
    GameMode GameMode,
    IReadOnlyList<string> Tags,
    bool AllowedForVoting = true);

public sealed record ShippedTrack(
    string Key,
    string Name,
    TrackOrigin Origin,
    IReadOnlyList<string> Weather,
    IReadOnlyList<ShippedVariant> Variants);

/// <summary>
/// The shipped values of every built-in row, for "Reset to shipped values". A later
/// catalogue migration that ships new or changed rows adds its data class here, after
/// <see cref="InitialCatalogueData"/>, so the newest definition of a row wins.
/// </summary>
public static class BuiltInCatalogue
{
    private static readonly IReadOnlyList<IReadOnlyList<ShippedTrack>> Releases =
    [
        InitialCatalogueData.Tracks,
    ];

    private static readonly Dictionary<string, ShippedTrack> TracksByKey =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, ShippedVariant> VariantsById =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, ShippedTag> TagsBySlug =
        InitialCatalogueData.Tags.ToDictionary(t => t.Slug, StringComparer.OrdinalIgnoreCase);

    static BuiltInCatalogue()
    {
        // Later releases overwrite earlier ones.
        foreach (var track in Releases.SelectMany(release => release))
        {
            TracksByKey[track.Key] = track;
            foreach (var variant in track.Variants)
            {
                VariantsById[variant.VariantId] = variant;
            }
        }
    }

    public static ShippedTrack? FindTrack(string key) => TracksByKey.GetValueOrDefault(key);

    public static ShippedVariant? FindVariant(string variantId) => VariantsById.GetValueOrDefault(variantId);

    public static ShippedTag? FindTag(string slug) => TagsBySlug.GetValueOrDefault(slug);
}
