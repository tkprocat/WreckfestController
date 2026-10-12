using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Net.Http.Headers;
using WreckfestController.Data;
using WreckfestController.Data.Catalogue;
using WreckfestController.Services.Auth;
using WreckfestController.Services.Catalogue;

namespace WreckfestController.Controllers;

/// <summary>
/// Serves the catalogue's menu images to anyone (#260): they are the game's own pictures,
/// or ones an admin chose to show. Which image a row shows, with its fallbacks, is
/// <see cref="CatalogueImageStore"/>'s rule. Tracks and variants are named by key and
/// variant id, so links survive a re-import.
/// </summary>
/// <remarks>
/// A link carrying <c>?v=&lt;hash&gt;</c> of the image it gets is cached for a year: the
/// hash changes with the image, so a page that knows it links the new one. Without it, or
/// with an old hash, the answer is cached for five minutes. Either way the ETag is the hash.
/// </remarks>
[ApiController]
[AllowAnonymous]
[EnableRateLimiting(RateLimits.ImagesPolicy)]
[Route("api/images")]
public class ImagesController : ControllerBase
{
    private readonly ControllerDbContext _db;

    public ImagesController(ControllerDbContext db)
    {
        _db = db;
    }

    /// <summary>The track's image of this size: its own, else its first variant's. 404 when there is none.</summary>
    [HttpGet("tracks/{key}/{size}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    public async Task<IActionResult> Track(string key, ImageSize size, string? v) =>
        Send(await CatalogueImageStore.ForTrackAsync(_db, key, size, HttpContext.RequestAborted), v);

    /// <summary>The variant's image of this size, else its track's. 404 when there is none.</summary>
    [HttpGet("variants/{variantId}/{size}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    public async Task<IActionResult> Variant(string variantId, ImageSize size, string? v) =>
        Send(await CatalogueImageStore.ForVariantAsync(_db, variantId, size, HttpContext.RequestAborted), v);

    /// <summary>
    /// The variant's layout outline, on a clear background, to draw over its picture. 404
    /// when it has none.
    /// </summary>
    [HttpGet("variants/{variantId}/layout")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> VariantLayout(string variantId, string? v) =>
        Send(await CatalogueImageStore.ForVariantLayoutAsync(_db, variantId, HttpContext.RequestAborted), v);

    private IActionResult Send(StoredImage? image, string? version)
    {
        if (image is null)
        {
            return NotFound();
        }

        var headers = Response.GetTypedHeaders();
        headers.ETag = new EntityTagHeaderValue($"\"{image.Hash}\"");
        headers.CacheControl = string.Equals(version, image.Hash, StringComparison.Ordinal)
            ? new CacheControlHeaderValue { Public = true, MaxAge = TimeSpan.FromDays(365), Extensions = { new NameValueHeaderValue("immutable") } }
            : new CacheControlHeaderValue { Public = true, MaxAge = TimeSpan.FromMinutes(5) };

        // The type is the one the bytes were checked to be: never sniff it into another.
        Response.Headers.XContentTypeOptions = "nosniff";

        var ifNoneMatch = Request.GetTypedHeaders().IfNoneMatch;
        if (ifNoneMatch.Any(tag => tag.Compare(headers.ETag, useStrongComparison: false) || tag.Equals(EntityTagHeaderValue.Any)))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return File(image.Data, image.ContentType);
    }
}
