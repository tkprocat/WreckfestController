using WreckfestController.Data.Catalogue;

namespace WreckfestController.Data.Collections;

/// <summary>
/// A named track rotation. Deploying one writes it to the server's event loop, with
/// its name on the <c>#CollectionName</c> line.
/// </summary>
public class TrackCollection : IVersioned
{
    public const int NameMaxLength = 128;

    public int Id { get; set; }

    /// <summary>Unique, ignoring case, so the name in a deployed config finds one collection.</summary>
    public string Name { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Also moves when only the tracks change, which is what bumps <see cref="Version"/>.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    public int Version { get; set; } = 1;

    /// <summary>In rotation order when loaded through <c>OrderBy(e => e.Position)</c>.</summary>
    public List<TrackCollectionEntry> Entries { get; set; } = new();
}

/// <summary>
/// One slot in a rotation: a track and the <c>el_*</c> settings that go with it.
/// </summary>
public class TrackCollectionEntry
{
    public int Id { get; set; }

    public int CollectionId { get; set; }

    public TrackCollection Collection { get; set; } = null!;

    /// <summary>Zero-based place in the rotation, unique within the collection.</summary>
    public int Position { get; set; }

    /// <summary>
    /// The catalogue variant, when the catalogue knows <see cref="TrackId"/>. Null for a
    /// workshop track it does not know yet; the entry is linked when the variant is added.
    /// </summary>
    public int? TrackVariantId { get; set; }

    public TrackVariant? TrackVariant { get; set; }

    /// <summary>
    /// The game id as it was saved. A linked entry deploys its variant's current id
    /// instead, so renaming an admin-added variant carries its collections along.
    /// </summary>
    public string TrackId { get; set; } = string.Empty;

    public string? Gamemode { get; set; }

    public int? Laps { get; set; }

    public int? Bots { get; set; }

    public int? NumTeams { get; set; }

    public bool? CarResetDisabled { get; set; }

    public bool? WrongWayLimiterDisabled { get; set; }

    public string? CarClassRestriction { get; set; }

    public string? CarRestriction { get; set; }

    public string? Weather { get; set; }

    /// <summary>What goes into <c>el_add=</c>.</summary>
    public string EffectiveTrackId => TrackVariant?.VariantId ?? TrackId;
}

public static class CollectionLimits
{
    /// <summary>For the free-text <c>el_*</c> values: game mode, car class, car and weather.</summary>
    public const int TextMaxLength = 128;
}
