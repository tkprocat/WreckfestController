using System.Text.Json.Serialization;

namespace WreckfestController.Data.Catalogue;

/// <summary>Where a track comes from. Informational: it drives filters and badges, nothing else.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TrackOrigin>))]
public enum TrackOrigin
{
    BaseGame,
    Dlc,
    Workshop,
    Custom,
}

[JsonConverter(typeof(JsonStringEnumConverter<GameMode>))]
public enum GameMode
{
    Racing,
    Derby,
}

/// <summary>
/// A row whose <see cref="Version"/> is a concurrency token.
/// <see cref="ControllerDbContext"/> bumps it on every save that changes the row, so a
/// write that started from an older version fails instead of overwriting.
/// </summary>
public interface IVersioned
{
    int Version { get; set; }
}

/// <summary>A track location, such as Madman Stadium. The playable layouts are its variants.</summary>
public class Track : IVersioned
{
    public const int KeyMaxLength = 64;
    public const int NameMaxLength = 128;

    public int Id { get; set; }

    /// <summary>A stable id for the location, such as "madman_stadium". Unique, ignoring case.</summary>
    public string Key { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public TrackOrigin Origin { get; set; }

    /// <summary>The pack a DLC track belongs to. Only set when <see cref="Origin"/> is Dlc.</summary>
    public string? DlcName { get; set; }

    /// <summary>The workshop mod that provides the track. Only set when <see cref="Origin"/> is Workshop.</summary>
    public int? ModId { get; set; }

    public Mod? Mod { get; set; }

    /// <summary>
    /// True for rows the catalogue migrations shipped. Drives the "Built-in" badge and
    /// "Reset to shipped values"; built-in rows are otherwise fully editable.
    /// </summary>
    public bool IsBuiltIn { get; set; }

    /// <summary>Retired without being deleted: gone from pickers and voting, still resolvable.</summary>
    public bool IsHidden { get; set; }

    public int Version { get; set; } = 1;

    public List<TrackVariant> Variants { get; set; } = new();

    public List<WeatherCondition> WeatherConditions { get; set; } = new();
}

/// <summary>A playable layout of a track: what goes into <c>el_add=</c> and <c>!track</c>.</summary>
public class TrackVariant : IVersioned
{
    public const int VariantIdMaxLength = 64;
    public const int NameMaxLength = 128;

    /// <summary>What the game accepts as a track id. The shipped list keeps case, such as "misc_rbRace".</summary>
    public const string VariantIdPattern = "^[A-Za-z0-9_]{1,64}$";

    public int Id { get; set; }

    /// <summary>The game's id for the layout. Unique, ignoring case, as VotingService compares ids.</summary>
    public string VariantId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public GameMode GameMode { get; set; }

    public int TrackId { get; set; }

    public Track Track { get; set; } = null!;

    public bool AllowedForVoting { get; set; }

    /// <inheritdoc cref="Track.IsBuiltIn"/>
    public bool IsBuiltIn { get; set; }

    /// <inheritdoc cref="Track.IsHidden"/>
    public bool IsHidden { get; set; }

    public int Version { get; set; } = 1;

    public List<Tag> Tags { get; set; } = new();
}

public class Tag
{
    public const int NameMaxLength = 64;
    public const int SlugMaxLength = 64;
    public const string SlugPattern = "^[a-z0-9]+(-[a-z0-9]+)*$";
    public const string ColorPattern = "^#[0-9A-Fa-f]{6}$";

    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Slug { get; set; } = string.Empty;

    /// <summary>A "#RRGGBB" colour for the tag's chip, or null for the default.</summary>
    public string? Color { get; set; }
}

public class WeatherCondition
{
    public const int NameMaxLength = 32;

    public int Id { get; set; }

    /// <summary>The game's weather name, such as "overcast".</summary>
    public string Name { get; set; } = string.Empty;
}

/// <summary>A workshop mod whose tracks are in the catalogue.</summary>
public class Mod
{
    public const int NameMaxLength = 128;
    public const int FolderNameMaxLength = 128;
    public const int WorkshopIdMaxLength = 20;

    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>The mod's folder under the server's <c>mods</c> directory, as the <c>mods=</c> setting names it.</summary>
    public string FolderName { get; set; } = string.Empty;

    public string? WorkshopId { get; set; }

    public List<Track> Tracks { get; set; } = new();
}
