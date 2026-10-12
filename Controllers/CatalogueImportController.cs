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

    private readonly ControllerDbContext _db;
    private readonly ILogger<CatalogueImportController> _logger;

    public CatalogueImportController(ControllerDbContext db, ILogger<CatalogueImportController> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Imports a tracks.json, sent as the request body. Adds the mods, tracks and variants
    /// the catalogue lacks and never changes an existing row, except a variant's track
    /// length. 400 for a file that is not formatVersion 1; 413 over 16 MB.
    /// </summary>
    [HttpPost("trackdump")]
    [RequestSizeLimit(MaxBodyBytes)]
    [RefuseDeclaredLargerBody]
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
    /// 413 for a body whose Content-Length is over <see cref="MaxBodyBytes"/>, before model
    /// binding reads any of it. Kestrel's <c>RequestSizeLimit</c> also stops a chunked body
    /// as it streams in; this answers the declared case with a problem the web can show,
    /// and holds on any server, TestServer included.
    /// </summary>
    private sealed class RefuseDeclaredLargerBodyAttribute : Attribute, IResourceFilter
    {
        public void OnResourceExecuting(ResourceExecutingContext context)
        {
            if (context.HttpContext.Request.ContentLength > MaxBodyBytes)
            {
                context.Result = new ObjectResult(new ProblemDetails
                {
                    Status = StatusCodes.Status413PayloadTooLarge,
                    Title = $"The file is over the {MaxBodyBytes / (1024 * 1024)} MB a track dump import takes.",
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
