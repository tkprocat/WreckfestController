using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using WreckfestController.Data;
using WreckfestController.Services.Auth;
using WreckfestController.Services.Catalogue;

namespace WreckfestController.Controllers;

/// <summary>
/// Fills the catalogue in from a WreckfestTrackDump <c>tracks.json</c> (#121). The admin
/// uploads the file's content: the web never names a path on the PC, and the controller
/// does not read the game's files itself.
/// </summary>
[ApiController]
[Authorize(Policy = ApiAuthentication.AdminPolicy)]
[Route("api/catalogue/import")]
public class CatalogueImportController : ControllerBase
{
    /// <summary>
    /// The largest upload taken. A dump of 12 workshop packs (373 variants) is about 0.9 MB,
    /// so this leaves room for the base game and many more mods.
    /// </summary>
    public const long MaxBodyBytes = 16 * 1024 * 1024;

    /// <summary>
    /// The largest image batch taken, dump included. The web sends the images of a dump in
    /// batches well under this; the base game's small and large images are about 50 MB.
    /// </summary>
    public const long MaxImageBatchBytes = 64 * 1024 * 1024;

    /// <summary>The most images one batch may carry.</summary>
    public const int MaxImageBatchFiles = 500;

    private readonly ControllerDbContext _db;
    private readonly TimeProvider _time;
    private readonly ILogger<CatalogueImportController> _logger;

    public CatalogueImportController(ControllerDbContext db, TimeProvider time, ILogger<CatalogueImportController> logger)
    {
        _db = db;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Imports a tracks.json, sent as the request body. Adds the mods, tracks and variants
    /// the catalogue lacks and never changes an existing row, except a variant's track
    /// length. 400 for a file that is not formatVersion 1; 413 over 16 MB.
    /// </summary>
    [HttpPost("trackdump")]
    [RequestSizeLimit(MaxBodyBytes)]
    [RefuseDeclaredLargerBody(MaxBodyBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status413PayloadTooLarge, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    public async Task<ActionResult<TrackDumpImportReport>> ImportTrackDump([FromBody] JsonElement document)
    {
        var dump = TrackDump.Read(document, out var refusal);
        if (dump is null)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: refusal);
        }

        var report = await TrackDumpImporter.ImportAsync(_db, dump, HttpContext.RequestAborted);
        _logger.LogInformation(
            "{Caller} imported a track dump: mods {ModsAdded} added, {ModsUpdated} updated; tracks {TracksAdded} added; variants {VariantsAdded} added, {VariantsUpdated} updated, {VariantsSkipped} skipped",
            this.Caller(),
            report.Mods.Added,
            report.Mods.Updated,
            report.Tracks.Added,
            report.Variants.Added,
            report.Variants.Updated,
            report.Variants.Skipped);
        return report;
    }

    /// <summary>
    /// Stores the menu images <c>wftracks imagedump</c> wrote (#260), for the tracks and
    /// variants of the tracks.json in <c>dump</c>. Each file in <c>images</c> is named as
    /// imagedump wrote it, <c>&lt;source&gt;/&lt;name&gt;.png</c>. Import the tracks.json first:
    /// images only go to tracks and variants the catalogue has. Send the images in batches,
    /// each with the dump. 400 for a dump that is not formatVersion 1; 413 over 64 MB.
    /// </summary>
    [HttpPost("trackdump/images")]
    [RequestSizeLimit(MaxImageBatchBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxImageBatchBytes, ValueCountLimit = MaxImageBatchFiles + 16)]
    [RefuseDeclaredLargerBody(MaxImageBatchBytes)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status413PayloadTooLarge, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    public async Task<ActionResult<TrackDumpImageReport>> ImportTrackDumpImages([FromForm] TrackDumpImagesForm form)
    {
        var cancellationToken = HttpContext.RequestAborted;
        if (form.Dump is null || form.Dump.Length > MaxBodyBytes)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Send the tracks.json the images were dumped with, as dump.");
        }

        if (form.Images.Count > MaxImageBatchFiles)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: $"Send at most {MaxImageBatchFiles} images at a time.");
        }

        TrackDump? dump;
        string? refusal;
        try
        {
            await using var stream = form.Dump.OpenReadStream();
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            dump = TrackDump.Read(document.RootElement, out refusal);
        }
        catch (JsonException)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "This is not a WreckfestTrackDump tracks.json: it is not JSON.");
        }

        if (dump is null)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: refusal);
        }

        // An oversized file is left out unread; the importer counts it as refused only if a
        // row uses it, like any other file it cannot take, so it is passed on empty.
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var image in form.Images)
        {
            if (TrackDumpImageImporter.UploadKey(image.FileName) is not { } key)
            {
                continue;
            }

            if (image.Length > CatalogueImageStore.MaxBytes)
            {
                files[key] = [];
                continue;
            }

            using var buffer = new MemoryStream((int)image.Length);
            await image.CopyToAsync(buffer, cancellationToken);
            files[key] = buffer.ToArray();
        }

        var report = await TrackDumpImageImporter.ImportAsync(_db, dump, files, _time.GetUtcNow().UtcDateTime, cancellationToken);
        _logger.LogInformation(
            "{Caller} imported track dump images: {Stored} stored, {Unchanged} unchanged, {Removed} removed, {NotUsed} not used, {Refused} refused",
            this.Caller(),
            report.Stored,
            report.Unchanged,
            report.Removed,
            report.NotUsed,
            report.Refused);
        return report;
    }

    /// <summary>
    /// 413 for a body whose Content-Length is over the action's limit, before model
    /// binding reads any of it. Kestrel's <c>RequestSizeLimit</c> also stops a chunked body
    /// as it streams in; this answers the declared case with a problem the web can show,
    /// and holds on any server, TestServer included.
    /// </summary>
    private sealed class RefuseDeclaredLargerBodyAttribute(long limit) : Attribute, IResourceFilter
    {
        public void OnResourceExecuting(ResourceExecutingContext context)
        {
            if (context.HttpContext.Request.ContentLength > limit)
            {
                context.Result = new ObjectResult(new ProblemDetails
                {
                    Status = StatusCodes.Status413PayloadTooLarge,
                    Title = limit == MaxBodyBytes
                        ? $"The file is over the {limit / (1024 * 1024)} MB a track dump import takes."
                        : $"The upload is over the {limit / (1024 * 1024)} MB one image batch takes.",
                })
                {
                    StatusCode = StatusCodes.Status413PayloadTooLarge,
                };
            }
        }

        public void OnResourceExecuted(ResourceExecutedContext context)
        {
        }
    }
}

/// <summary>An image batch: the tracks.json, and the images imagedump wrote for it.</summary>
public sealed class TrackDumpImagesForm
{
    public IFormFile? Dump { get; set; }

    public List<IFormFile> Images { get; set; } = [];
}
