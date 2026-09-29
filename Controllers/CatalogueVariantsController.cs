using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WreckfestController.Data;
using WreckfestController.Data.Catalogue;
using WreckfestController.Data.Collections;
using WreckfestController.Services.Auth;

namespace WreckfestController.Controllers;

/// <summary>
/// Track variants: the ids the game loads. The same rules as tracks apply: built-ins
/// are hidden rather than deleted, reset restores what shipped, and a built-in's id
/// is fixed.
/// </summary>
[ApiController]
[Authorize(Policy = ApiAuthentication.AdminPolicy)]
[Route("api/catalogue/variants")]
public class CatalogueVariantsController : ControllerBase
{
    private readonly ControllerDbContext _db;
    private readonly TimeProvider _time;
    private readonly ILogger<CatalogueVariantsController> _logger;

    public CatalogueVariantsController(
        ControllerDbContext db,
        TimeProvider time,
        ILogger<CatalogueVariantsController> logger)
    {
        _db = db;
        _time = time;
        _logger = logger;
    }

    /// <param name="search">Matches the variant id, the variant name or the track name.</param>
    /// <param name="votingOnly">Only variants players can vote for.</param>
    /// <param name="includeHidden">Also hidden variants, and variants of hidden tracks.</param>
    [HttpGet]
    public async Task<IEnumerable<VariantResponse>> List(
        string? search,
        int? trackId,
        string? tag,
        GameMode? gameMode,
        string? weather,
        bool votingOnly,
        bool includeHidden)
    {
        IQueryable<TrackVariant> query = _db.TrackVariants
            .AsNoTracking()
            .AsSplitQuery()
            .Include(v => v.Track)
            .Include(v => v.Tags);

        if (!includeHidden)
        {
            query = query.Where(v => !v.IsHidden && !v.Track.IsHidden);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = CatalogueHttp.ContainsPattern(search.Trim());
            query = query.Where(v =>
                EF.Functions.Like(v.VariantId, pattern, CatalogueHttp.LikeEscape)
                || EF.Functions.Like(v.Name, pattern, CatalogueHttp.LikeEscape)
                || EF.Functions.Like(v.Track.Name, pattern, CatalogueHttp.LikeEscape));
        }

        if (trackId is not null)
        {
            query = query.Where(v => v.TrackId == trackId);
        }

        if (tag is not null)
        {
            query = query.Where(v => v.Tags.Any(g => g.Slug == tag));
        }

        if (gameMode is not null)
        {
            query = query.Where(v => v.GameMode == gameMode);
        }

        if (weather is not null)
        {
            query = query.Where(v => v.Track.WeatherConditions.Any(w => w.Name == weather));
        }

        if (votingOnly)
        {
            query = query.Where(v => v.AllowedForVoting);
        }

        var variants = await query.OrderBy(v => v.Track.Name).ThenBy(v => v.Name).ToListAsync();
        return variants.Select(VariantResponse.From).ToList();
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<VariantResponse>> Get(int id)
    {
        var variant = await LoadAsync(id);
        if (variant is null)
        {
            return NotFound();
        }

        this.SetETag(variant.Version);
        return VariantResponse.From(variant);
    }

    /// <summary>Adds a variant under an existing track.</summary>
    [HttpPost]
    public async Task<ActionResult<VariantResponse>> Create(CreateVariantRequest request)
    {
        if (!await _db.Tracks.AnyAsync(t => t.Id == request.TrackId))
        {
            return this.Invalid("trackId", $"There is no track with id {request.TrackId}.");
        }

        if (await _db.TrackVariants.AnyAsync(v => v.VariantId == request.VariantId))
        {
            return DuplicateId(request.VariantId);
        }

        var variant = new TrackVariant
        {
            TrackId = request.TrackId!.Value,
            VariantId = request.VariantId,
            Name = request.Name.Trim(),
            GameMode = request.GameMode!.Value,
            AllowedForVoting = request.AllowedForVoting,
        };
        _db.TrackVariants.Add(variant);
        await _db.LinkEntriesAsync(variant);

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (CatalogueHttp.IsUniqueViolation(ex))
        {
            return DuplicateId(request.VariantId);
        }

        _logger.LogInformation("{Caller} added track variant {VariantId}", this.Caller(), variant.VariantId);
        var created = (await LoadAsync(variant.Id))!;
        this.SetETag(created.Version);
        return CreatedAtAction(nameof(Get), new { id = variant.Id }, VariantResponse.From(created));
    }

    /// <summary>Changes the variant's own fields. Needs If-Match. A built-in variant's id is fixed.</summary>
    [HttpPut("{id:int}")]
    public async Task<ActionResult<VariantResponse>> Update(int id, UpdateVariantRequest request)
    {
        if (this.ReadIfMatch(out var expected) is { } badPrecondition)
        {
            return badPrecondition;
        }

        var variant = await LoadAsync(id);
        if (variant is null)
        {
            return NotFound();
        }

        if (variant.Version != expected)
        {
            return this.VersionConflict(VariantResponse.From(variant), variant.Version);
        }

        if (!string.Equals(variant.VariantId, request.VariantId, StringComparison.Ordinal))
        {
            if (variant.IsBuiltIn)
            {
                return this.Invalid("variantId", "A built-in variant's id is the game's and cannot change.");
            }

            if (await _db.TrackVariants.AnyAsync(v => v.VariantId == request.VariantId && v.Id != id))
            {
                return DuplicateId(request.VariantId);
            }

            // Entries already linked follow the variant; ones naming the new id join them.
            // Both change what those collections deploy, so they get new versions too.
            await _db.TouchCollectionsForRenameAsync(variant, request.VariantId, _time.GetUtcNow());
            variant.VariantId = request.VariantId;
            await _db.LinkEntriesAsync(variant);
        }

        variant.Name = request.Name.Trim();
        variant.GameMode = request.GameMode!.Value;
        return await SaveAsync(variant);
    }

    /// <summary>
    /// Deletes an admin-added variant that no collection uses. Built-in variants are
    /// hidden instead.
    /// </summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var variant = await _db.TrackVariants.FindAsync(id);
        if (variant is null)
        {
            return NotFound();
        }

        if (variant.IsBuiltIn)
        {
            return this.Refused("Built-in variants cannot be deleted. Hide it instead.");
        }

        var collections = await _db.CollectionsUsingAsync(_db.TrackVariants.Where(v => v.Id == id).Select(v => v.Id));
        if (collections.Count > 0)
        {
            return this.RefusedInUse($"Variant '{variant.VariantId}'", collections);
        }

        _db.TrackVariants.Remove(variant);
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (CatalogueHttp.IsForeignKeyViolation(ex))
        {
            return this.Refused("A collection started using it. Reload and try again.");
        }

        _logger.LogInformation("{Caller} deleted track variant {VariantId}", this.Caller(), variant.VariantId);
        return NoContent();
    }

    [HttpPost("{id:int}/hide")]
    public Task<ActionResult<VariantResponse>> Hide(int id) => UpdateAsync(id, v => v.IsHidden = true);

    [HttpPost("{id:int}/unhide")]
    public Task<ActionResult<VariantResponse>> Unhide(int id) => UpdateAsync(id, v => v.IsHidden = false);

    [HttpPut("{id:int}/voting")]
    public Task<ActionResult<VariantResponse>> SetVoting(int id, VotingRequest request) =>
        UpdateAsync(id, v => v.AllowedForVoting = request.Allowed);

    /// <summary>
    /// Restores a built-in variant's name, mode, voting flag and tags to what shipped,
    /// recreating a shipped tag an admin deleted. Leaves whether it is hidden alone.
    /// </summary>
    [HttpPost("{id:int}/reset")]
    public async Task<ActionResult<VariantResponse>> Reset(int id)
    {
        var variant = await LoadAsync(id);
        if (variant is null)
        {
            return NotFound();
        }

        var shipped = variant.IsBuiltIn ? BuiltInCatalogue.FindVariant(variant.VariantId) : null;
        if (shipped is null)
        {
            return this.Refused("Only built-in variants can be reset.");
        }

        var tags = await _db.Tags.Where(t => shipped.Tags.Contains(t.Slug)).ToListAsync();
        foreach (var slug in shipped.Tags.Except(tags.Select(t => t.Slug), StringComparer.OrdinalIgnoreCase))
        {
            var definition = BuiltInCatalogue.FindTag(slug)!;
            var tag = new Tag { Name = definition.Name, Slug = definition.Slug, Color = definition.Color };
            _db.Tags.Add(tag);
            tags.Add(tag);
        }

        variant.Name = shipped.Name;
        variant.GameMode = shipped.GameMode;
        variant.AllowedForVoting = shipped.AllowedForVoting;
        variant.Tags = tags;
        // The tags are a join table: mark the variant, so its version moves even when only they changed.
        _db.Entry(variant).Property(v => v.Version).IsModified = true;

        _logger.LogInformation(
            "{Caller} reset track variant {VariantId} to its shipped values", this.Caller(), variant.VariantId);
        return await SaveAsync(variant);
    }

    /// <summary>
    /// Replaces the variant's tags, by slug. Takes If-Match optionally: with it, a variant
    /// saved since answers 409. Changing the tags moves the variant's version.
    /// </summary>
    [HttpPut("{id:int}/tags")]
    public async Task<ActionResult<VariantResponse>> SetTags(int id, TagSlugsRequest request)
    {
        if (this.ReadOptionalIfMatch(out var expected) is { } badPrecondition)
        {
            return badPrecondition;
        }

        var variant = await LoadAsync(id);
        if (variant is null)
        {
            return NotFound();
        }

        if (expected is not null && variant.Version != expected)
        {
            return this.VersionConflict(VariantResponse.From(variant), variant.Version);
        }

        var slugs = request.Tags.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var tags = await _db.Tags.Where(t => slugs.Contains(t.Slug)).ToListAsync();
        var unknown = slugs.Except(tags.Select(t => t.Slug), StringComparer.OrdinalIgnoreCase).ToList();
        if (unknown.Count > 0)
        {
            return this.Invalid("tags", $"Unknown tags: {string.Join(", ", unknown)}.");
        }

        variant.Tags = tags;
        // A join-table change leaves the variant row unchanged: mark it, so its version moves.
        _db.Entry(variant).Property(v => v.Version).IsModified = true;
        return await SaveAsync(variant);
    }

    private async Task<ActionResult<VariantResponse>> UpdateAsync(int id, Action<TrackVariant> change)
    {
        var variant = await LoadAsync(id);
        if (variant is null)
        {
            return NotFound();
        }

        change(variant);
        return await SaveAsync(variant);
    }

    /// <summary>Saves, turning a write that raced another into 409 with the winner's row.</summary>
    private async Task<ActionResult<VariantResponse>> SaveAsync(TrackVariant variant)
    {
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException ex) when (ex.Entries.All(e => e.Entity is not TrackVariant))
        {
            // Not this variant: a collection a rename touches was saved in between.
            return this.Refused("A collection using this variant changed meanwhile. Try again.");
        }
        catch (DbUpdateConcurrencyException)
        {
            _db.ChangeTracker.Clear();
            var current = await LoadAsync(variant.Id);
            return current is null
                ? NotFound()
                : this.VersionConflict(VariantResponse.From(current), current.Version);
        }
        catch (DbUpdateException ex) when (CatalogueHttp.IsUniqueViolation(ex))
        {
            return DuplicateId(variant.VariantId);
        }

        var saved = (await LoadAsync(variant.Id))!;
        this.SetETag(saved.Version);
        return VariantResponse.From(saved);
    }

    private ActionResult DuplicateId(string variantId) =>
        this.Invalid("variantId", $"A variant with id '{variantId}' already exists.");

    private Task<TrackVariant?> LoadAsync(int id) => _db.TrackVariants
        .Include(v => v.Track)
        .Include(v => v.Tags)
        .SingleOrDefaultAsync(v => v.Id == id);
}
