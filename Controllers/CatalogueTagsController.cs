using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WreckfestController.Data;
using WreckfestController.Data.Catalogue;
using WreckfestController.Services;

namespace WreckfestController.Controllers;

/// <summary>Tags for grouping variants. Deleting a tag removes it from every variant.</summary>
[ApiController]
[Authorize(Policy = ApiAuthentication.AdminPolicy)]
[Route("api/catalogue/tags")]
public class CatalogueTagsController : ControllerBase
{
    private readonly ControllerDbContext _db;
    private readonly ILogger<CatalogueTagsController> _logger;

    public CatalogueTagsController(ControllerDbContext db, ILogger<CatalogueTagsController> logger)
    {
        _db = db;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IEnumerable<TagResponse>> List()
    {
        var tags = await _db.Tags.AsNoTracking().OrderBy(t => t.Name).ToListAsync();
        return tags.Select(TagResponse.From).ToList();
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<TagResponse>> Get(int id)
    {
        var tag = await _db.Tags.FindAsync(id);
        return tag is null ? NotFound() : TagResponse.From(tag);
    }

    [HttpPost]
    public async Task<ActionResult<TagResponse>> Create(TagRequest request)
    {
        var tag = new Tag();
        Apply(tag, request);
        _db.Tags.Add(tag);
        if (await SaveAsync(tag) is { } failed)
        {
            return failed;
        }

        _logger.LogInformation("{Caller} added tag {Slug}", this.Caller(), tag.Slug);
        return CreatedAtAction(nameof(Get), new { id = tag.Id }, TagResponse.From(tag));
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<TagResponse>> Update(int id, TagRequest request)
    {
        var tag = await _db.Tags.FindAsync(id);
        if (tag is null)
        {
            return NotFound();
        }

        Apply(tag, request);
        if (await SaveAsync(tag) is { } failed)
        {
            return failed;
        }

        return TagResponse.From(tag);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var tag = await _db.Tags.FindAsync(id);
        if (tag is null)
        {
            return NotFound();
        }

        _db.Tags.Remove(tag);
        await _db.SaveChangesAsync();
        _logger.LogInformation("{Caller} deleted tag {Slug}", this.Caller(), tag.Slug);
        return NoContent();
    }

    private static void Apply(Tag tag, TagRequest request)
    {
        tag.Name = request.Name.Trim();
        tag.Slug = request.Slug;
        tag.Color = request.Color;
    }

    /// <summary>Saves, or returns the validation problem for a slug another tag already has.</summary>
    private async Task<ActionResult?> SaveAsync(Tag tag)
    {
        if (await _db.Tags.AnyAsync(t => t.Slug == tag.Slug && t.Id != tag.Id))
        {
            return DuplicateSlug(tag.Slug);
        }

        try
        {
            await _db.SaveChangesAsync();
            return null;
        }
        catch (DbUpdateException ex) when (CatalogueHttp.IsUniqueViolation(ex))
        {
            return DuplicateSlug(tag.Slug);
        }
    }

    private ActionResult DuplicateSlug(string slug) =>
        this.Invalid("slug", $"A tag with slug '{slug}' already exists.");
}
