using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WreckfestController.Data;
using WreckfestController.Data.Catalogue;
using WreckfestController.Services.Auth;
using WreckfestController.Services.Catalogue;

namespace WreckfestController.Controllers;

/// <summary>
/// One stored image, without its bytes. Exactly one of <c>TrackId</c> and <c>VariantId</c> is
/// set. <c>IsOverlay</c>: a layout outline on a clear background, shown over a picture.
/// </summary>
public sealed record CatalogueImageResponse(int? TrackId, int? VariantId, ImageSize Size, ImageOrigin Origin, string Hash, int Bytes, bool IsOverlay)
{
    public static CatalogueImageResponse From(ImageSlot slot) =>
        new(slot.TrackId, slot.TrackVariantId, slot.Size, slot.Origin, slot.Hash, slot.Length, slot.IsOverlay);
}

/// <summary>An image upload: one PNG, JPEG or WebP file.</summary>
public sealed class ImageUploadForm
{
    public IFormFile? File { get; set; }
}

/// <summary>
/// The catalogue's menu images for admins (#260): which tracks and variants have which, and
/// an admin's own image for a row, which shows instead of an imported one. The images
/// themselves are served by <see cref="ImagesController"/>. The upload is the file's
/// content: the web never names a path on the PC.
/// </summary>
[ApiController]
[Authorize(Policy = ApiAuthentication.AdminPolicy)]
[Route("api/catalogue")]
public class CatalogueImagesController : ControllerBase
{
    /// <summary>An upload's limit, with room for the form around the file.</summary>
    private const long MaxUploadBytes = CatalogueImageStore.MaxBytes + (64 * 1024);

    private readonly ControllerDbContext _db;
    private readonly TimeProvider _time;
    private readonly ILogger<CatalogueImagesController> _logger;

    public CatalogueImagesController(ControllerDbContext db, TimeProvider time, ILogger<CatalogueImagesController> logger)
    {
        _db = db;
        _time = time;
        _logger = logger;
    }

    /// <summary>Every stored image, imported and uploaded, without the bytes.</summary>
    [HttpGet("images")]
    public async Task<ActionResult<List<CatalogueImageResponse>>> List(CancellationToken cancellationToken) =>
        (await CatalogueImageStore.SlotsAsync(_db, cancellationToken)).Select(CatalogueImageResponse.From).ToList();

    /// <summary>
    /// Sets the track's own image of this size, replacing an earlier upload. Answers the
    /// track's images. 400 for a file that is not PNG, JPEG or WebP, or over 4 MB.
    /// </summary>
    [HttpPut("tracks/{id:int}/images/{size}")]
    [RequestSizeLimit(MaxUploadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadBytes)]
    [Consumes("multipart/form-data")]
    public async Task<ActionResult<List<CatalogueImageResponse>>> UploadTrackImage(int id, ImageSize size, [FromForm] ImageUploadForm form)
    {
        var key = await _db.Tracks.Where(t => t.Id == id).Select(t => t.Key).SingleOrDefaultAsync(HttpContext.RequestAborted);
        return key is null ? NotFound() : await UploadAsync(ImageOwner.Track(id), $"track {key}", size, form);
    }

    /// <summary>Removes the track's uploaded image of this size; an imported one shows again. 204, or 404 when there was none.</summary>
    [HttpDelete("tracks/{id:int}/images/{size}")]
    public async Task<IActionResult> RemoveTrackImage(int id, ImageSize size) =>
        await RemoveAsync(ImageOwner.Track(id), $"track {id}", size);

    /// <inheritdoc cref="UploadTrackImage"/>
    [HttpPut("variants/{id:int}/images/{size}")]
    [RequestSizeLimit(MaxUploadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadBytes)]
    [Consumes("multipart/form-data")]
    public async Task<ActionResult<List<CatalogueImageResponse>>> UploadVariantImage(int id, ImageSize size, [FromForm] ImageUploadForm form)
    {
        var variantId = await _db.TrackVariants.Where(v => v.Id == id).Select(v => v.VariantId).SingleOrDefaultAsync(HttpContext.RequestAborted);
        return variantId is null ? NotFound() : await UploadAsync(ImageOwner.Variant(id), $"variant {variantId}", size, form);
    }

    /// <inheritdoc cref="RemoveTrackImage"/>
    [HttpDelete("variants/{id:int}/images/{size}")]
    public async Task<IActionResult> RemoveVariantImage(int id, ImageSize size) =>
        await RemoveAsync(ImageOwner.Variant(id), $"variant {id}", size);

    private async Task<ActionResult<List<CatalogueImageResponse>>> UploadAsync(ImageOwner owner, string what, ImageSize size, ImageUploadForm form)
    {
        var cancellationToken = HttpContext.RequestAborted;
        if (form.File is not { Length: > 0 } file)
        {
            return this.Invalid("file", "Choose an image to upload.");
        }

        if (file.Length > CatalogueImageStore.MaxBytes)
        {
            return this.Invalid("file", $"The image is over {CatalogueImageStore.MaxBytes / (1024 * 1024)} MB.");
        }

        using var buffer = new MemoryStream((int)file.Length);
        await file.CopyToAsync(buffer, cancellationToken);
        var data = buffer.ToArray();
        if (CatalogueImageStore.Sniff(data) is not { } contentType)
        {
            return this.Invalid("file", "The file is not a PNG, JPEG or WebP image.");
        }

        await CatalogueImageStore.PutAsync(_db, owner, size, ImageOrigin.Upload, data, contentType, _time.GetUtcNow().UtcDateTime, cancellationToken);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (CatalogueHttp.IsForeignKeyViolation(ex))
        {
            // Deleted between the lookup and the save.
            return NotFound();
        }
        catch (DbUpdateException ex) when (CatalogueHttp.IsUniqueViolation(ex))
        {
            return this.Refused("Another upload for this image landed at the same time. Reload and try again.");
        }

        _logger.LogInformation("{Caller} uploaded the {Size} image of {What}", this.Caller(), size, what);
        return (await CatalogueImageStore.SlotsAsync(_db, owner, cancellationToken)).Select(CatalogueImageResponse.From).ToList();
    }

    private async Task<IActionResult> RemoveAsync(ImageOwner owner, string what, ImageSize size)
    {
        if (!await CatalogueImageStore.RemoveAsync(_db, owner, size, ImageOrigin.Upload, HttpContext.RequestAborted))
        {
            return NotFound();
        }

        _logger.LogInformation("{Caller} removed the uploaded {Size} image of {What}", this.Caller(), size, what);
        return NoContent();
    }
}
