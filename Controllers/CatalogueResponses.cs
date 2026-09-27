using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WreckfestController.Data.Catalogue;

namespace WreckfestController.Controllers;

public sealed record TagResponse(int Id, string Name, string Slug, string? Color)
{
    public static TagResponse From(Tag tag) => new(tag.Id, tag.Name, tag.Slug, tag.Color);
}

public sealed record ModResponse(int Id, string Name, string FolderName, string? WorkshopId)
{
    public static ModResponse From(Mod mod) => new(mod.Id, mod.Name, mod.FolderName, mod.WorkshopId);
}

/// <summary>A variant as the API returns it. Expects <see cref="TrackVariant.Track"/> and its tags loaded.</summary>
public sealed record VariantResponse(
    int Id,
    string VariantId,
    string Name,
    GameMode GameMode,
    int TrackId,
    string TrackKey,
    string TrackName,
    bool AllowedForVoting,
    bool IsBuiltIn,
    bool IsHidden,
    int Version,
    IReadOnlyList<TagResponse> Tags)
{
    public static VariantResponse From(TrackVariant variant) => new(
        variant.Id,
        variant.VariantId,
        variant.Name,
        variant.GameMode,
        variant.TrackId,
        variant.Track.Key,
        variant.Track.Name,
        variant.AllowedForVoting,
        variant.IsBuiltIn,
        variant.IsHidden,
        variant.Version,
        variant.Tags.OrderBy(t => t.Name).Select(TagResponse.From).ToList());
}

/// <summary>A track as the API returns it, with the variants that were loaded.</summary>
public sealed record TrackResponse(
    int Id,
    string Key,
    string Name,
    TrackOrigin Origin,
    string? DlcName,
    ModResponse? Mod,
    bool IsBuiltIn,
    bool IsHidden,
    int Version,
    IReadOnlyList<string> Weather,
    IReadOnlyList<VariantResponse> Variants)
{
    public static TrackResponse From(Track track) => new(
        track.Id,
        track.Key,
        track.Name,
        track.Origin,
        track.DlcName,
        track.Mod is null ? null : ModResponse.From(track.Mod),
        track.IsBuiltIn,
        track.IsHidden,
        track.Version,
        track.WeatherConditions.OrderBy(w => w.Id).Select(w => w.Name).ToList(),
        track.Variants.OrderBy(v => v.Name).Select(VariantResponse.From).ToList());
}

public sealed record TrackRequest
{
    [Required, StringLength(Track.KeyMaxLength), RegularExpression(TrackVariant.VariantIdPattern)]
    public string Key { get; init; } = string.Empty;

    [Required, StringLength(Track.NameMaxLength)]
    public string Name { get; init; } = string.Empty;

    [Required]
    public TrackOrigin? Origin { get; init; }

    /// <summary>Only for Origin Dlc.</summary>
    [StringLength(Track.NameMaxLength)]
    public string? DlcName { get; init; }

    /// <summary>Only for Origin Workshop, and optional there.</summary>
    public int? ModId { get; init; }
}

public sealed record CreateVariantRequest
{
    [Required]
    public int? TrackId { get; init; }

    [Required, RegularExpression(TrackVariant.VariantIdPattern)]
    public string VariantId { get; init; } = string.Empty;

    [Required, StringLength(TrackVariant.NameMaxLength)]
    public string Name { get; init; } = string.Empty;

    [Required]
    public GameMode? GameMode { get; init; }

    public bool AllowedForVoting { get; init; }
}

public sealed record UpdateVariantRequest
{
    [Required, RegularExpression(TrackVariant.VariantIdPattern)]
    public string VariantId { get; init; } = string.Empty;

    [Required, StringLength(TrackVariant.NameMaxLength)]
    public string Name { get; init; } = string.Empty;

    [Required]
    public GameMode? GameMode { get; init; }
}

public sealed record VotingRequest(bool Allowed);

public sealed record TagSlugsRequest
{
    [Required]
    public IReadOnlyList<string> Tags { get; init; } = [];
}

public sealed record WeatherNamesRequest
{
    [Required]
    public IReadOnlyList<string> Weather { get; init; } = [];
}

public sealed record TagRequest
{
    [Required, StringLength(Tag.NameMaxLength)]
    public string Name { get; init; } = string.Empty;

    [Required, StringLength(Tag.SlugMaxLength), RegularExpression(Tag.SlugPattern)]
    public string Slug { get; init; } = string.Empty;

    [RegularExpression(Tag.ColorPattern)]
    public string? Color { get; init; }
}

public sealed record ModRequest
{
    public const string FolderNamePattern = @"^(?!\.{1,2}$)[A-Za-z0-9_.\- ]{1,128}$";

    [Required, StringLength(Mod.NameMaxLength)]
    public string Name { get; init; } = string.Empty;

    /// <summary>A plain folder name: no separators, and not "." or "..".</summary>
    [Required, StringLength(Mod.FolderNameMaxLength), RegularExpression(FolderNamePattern)]
    public string FolderName { get; init; } = string.Empty;

    [StringLength(Mod.WorkshopIdMaxLength), RegularExpression("^[0-9]+$")]
    public string? WorkshopId { get; init; }
}

/// <summary>
/// Optimistic concurrency over HTTP: GET returns the row's version as an ETag, and a
/// PUT must send it back in If-Match. A stale If-Match gets 409 with the current row,
/// so the editor can offer "reload or overwrite".
/// </summary>
internal static class CatalogueHttp
{
    public static void SetETag(this ControllerBase controller, int version) =>
        controller.Response.Headers.ETag = $"\"{version.ToString(CultureInfo.InvariantCulture)}\"";

    /// <summary>
    /// Reads the version from If-Match. Returns the response to send instead when the
    /// header is missing (428) or is not one of our ETags (400).
    /// </summary>
    public static ActionResult? ReadIfMatch(this ControllerBase controller, out int version)
    {
        version = 0;
        var header = controller.Request.Headers.IfMatch.ToString().Trim();
        if (header.Length == 0)
        {
            return controller.Problem(
                statusCode: StatusCodes.Status428PreconditionRequired,
                title: "Send If-Match with the ETag you read the row at.");
        }

        if (header.Length < 3
            || header[0] != '"'
            || header[^1] != '"'
            || !int.TryParse(header[1..^1], NumberStyles.None, CultureInfo.InvariantCulture, out version))
        {
            return controller.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "If-Match must be an ETag this API returned, such as \"3\".");
        }

        return null;
    }

    /// <summary>409 with the row as it is now, and its ETag.</summary>
    public static ActionResult VersionConflict(this ControllerBase controller, object current, int version)
    {
        controller.SetETag(version);
        return controller.Conflict(current);
    }

    public static ObjectResult Refused(this ControllerBase controller, string title) =>
        controller.Problem(statusCode: StatusCodes.Status409Conflict, title: title);

    public static ActionResult Invalid(this ControllerBase controller, string field, string message)
    {
        controller.ModelState.AddModelError(field, message);
        return controller.ValidationProblem(controller.ModelState);
    }

    /// <summary>A UNIQUE constraint failed: someone created the same id between our check and our insert.</summary>
    public static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is SqliteException { SqliteErrorCode: 19, SqliteExtendedErrorCode: 2067 };

    /// <summary>For the log: the signed-in user's name, or "ApiKey" for a script.</summary>
    public static string Caller(this ControllerBase controller) =>
        controller.User.Identity?.Name ?? "unknown";

    /// <summary>A LIKE pattern matching <paramref name="text"/> anywhere. Escapes '_', which every id contains.</summary>
    public static string ContainsPattern(string text) =>
        "%" + text.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%";

    public const string LikeEscape = @"\";
}
