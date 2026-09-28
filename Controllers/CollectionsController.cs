using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using WreckfestController.Data;
using WreckfestController.Data.Collections;
using WreckfestController.Models;
using WreckfestController.Services;

namespace WreckfestController.Controllers;

/// <summary>
/// Named track rotations. A collection is saved whole, name and tracks in order, and
/// deploying it writes it to the server's event loop.
/// </summary>
[ApiController]
[Authorize(Policy = ApiAuthentication.AdminPolicy)]
[Route("api/collections")]
public class CollectionsController : ControllerBase
{
    private readonly ControllerDbContext _db;
    private readonly ConfigService _config;
    private readonly TimeProvider _time;
    private readonly ILogger<CollectionsController> _logger;

    public CollectionsController(
        ControllerDbContext db,
        ConfigService config,
        TimeProvider time,
        ILogger<CollectionsController> logger)
    {
        _db = db;
        _config = config;
        _time = time;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IEnumerable<CollectionSummaryResponse>> List() =>
        await _db.TrackCollections
            .AsNoTracking()
            .OrderBy(c => c.Name)
            .Select(c => new CollectionSummaryResponse(
                c.Id, c.Name, c.Version, c.Entries.Count, c.CreatedAt, c.UpdatedAt))
            .ToListAsync();

    [HttpGet("{id:int}")]
    public async Task<ActionResult<CollectionResponse>> Get(int id)
    {
        var collection = await LoadAsync(id);
        if (collection is null)
        {
            return NotFound();
        }

        this.SetETag(collection.Version);
        return CollectionResponse.From(collection);
    }

    [HttpPost]
    public async Task<ActionResult<CollectionResponse>> Create(CollectionRequest request)
    {
        if (Validate(request) is { } invalid)
        {
            return invalid;
        }

        var name = request.Name.Trim();
        if (await NameTakenAsync(name, exceptId: null))
        {
            return DuplicateName(name);
        }

        var now = _time.GetUtcNow();
        var collection = new TrackCollection
        {
            Name = name,
            CreatedAt = now,
            UpdatedAt = now,
            Entries = await BuildEntriesAsync(request.Tracks!),
        };

        return await AddAsync(collection);
    }

    /// <summary>
    /// Replaces the name and every track, in the order given. Needs If-Match, so a
    /// reorder or edit that started from an older version gets 409 instead of
    /// overwriting someone else's save.
    /// </summary>
    [HttpPut("{id:int}")]
    public async Task<ActionResult<CollectionResponse>> Update(int id, CollectionRequest request)
    {
        if (this.ReadIfMatch(out var expected) is { } badPrecondition)
        {
            return badPrecondition;
        }

        var collection = await LoadAsync(id);
        if (collection is null)
        {
            return NotFound();
        }

        if (collection.Version != expected)
        {
            return this.VersionConflict(CollectionResponse.From(collection), collection.Version);
        }

        if (Validate(request) is { } invalid)
        {
            return invalid;
        }

        var name = request.Name.Trim();
        if (await NameTakenAsync(name, exceptId: id))
        {
            return DuplicateName(name);
        }

        collection.Name = name;
        collection.UpdatedAt = _time.GetUtcNow();
        _db.TrackCollectionEntries.RemoveRange(collection.Entries);
        collection.Entries = await BuildEntriesAsync(request.Tracks!);

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            _db.ChangeTracker.Clear();
            var current = await LoadAsync(id);
            return current is null
                ? NotFound()
                : this.VersionConflict(CollectionResponse.From(current), current.Version);
        }
        catch (DbUpdateException ex) when (CatalogueHttp.IsUniqueViolation(ex))
        {
            return DuplicateName(name);
        }

        _logger.LogInformation(
            "{Caller} saved collection {Name} with {Count} tracks", this.Caller(), name, collection.Entries.Count);
        return await RespondAsync(id);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var collection = await _db.TrackCollections.FindAsync(id);
        if (collection is null)
        {
            return NotFound();
        }

        _db.TrackCollections.Remove(collection);
        await _db.SaveChangesAsync();
        _logger.LogInformation("{Caller} deleted collection {Name}", this.Caller(), collection.Name);
        return NoContent();
    }

    /// <summary>Copies the collection's tracks into a new one, named "&lt;name&gt; (copy)" unless a name is given.</summary>
    [HttpPost("{id:int}/duplicate")]
    public async Task<ActionResult<CollectionResponse>> Duplicate(
        int id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] DuplicateCollectionRequest? request)
    {
        var source = await LoadAsync(id, tracked: false);
        if (source is null)
        {
            return NotFound();
        }

        string name;
        if (request?.Name is not null)
        {
            if (EventLoopTrackRules.ValidateName("name", request.Name) is { } error)
            {
                return this.Invalid(error.Field, error.Message);
            }

            name = request.Name.Trim();
            if (await NameTakenAsync(name, exceptId: null))
            {
                return DuplicateName(name);
            }
        }
        else
        {
            name = await CopyNameAsync(source.Name);
        }

        var now = _time.GetUtcNow();
        var copy = new TrackCollection
        {
            Name = name,
            CreatedAt = now,
            UpdatedAt = now,
            Entries = source.Entries
                .OrderBy(e => e.Position)
                .Select(e => new TrackCollectionEntry
                {
                    Position = e.Position,
                    TrackVariantId = e.TrackVariantId,
                    TrackId = e.TrackId,
                    Gamemode = e.Gamemode,
                    Laps = e.Laps,
                    Bots = e.Bots,
                    NumTeams = e.NumTeams,
                    CarResetDisabled = e.CarResetDisabled,
                    WrongWayLimiterDisabled = e.WrongWayLimiterDisabled,
                    CarClassRestriction = e.CarClassRestriction,
                    CarRestriction = e.CarRestriction,
                    Weather = e.Weather,
                })
                .ToList(),
        };

        return await AddAsync(copy);
    }

    /// <summary>
    /// Writes the collection to the event loop in server_config.cfg, as
    /// <c>PUT /api/config/tracks</c> does. Linked tracks deploy their variant's current id.
    /// </summary>
    [HttpPost("{id:int}/deploy")]
    public async Task<ActionResult<DeployCollectionResponse>> Deploy(int id)
    {
        var collection = await LoadAsync(id, tracked: false);
        if (collection is null)
        {
            return NotFound();
        }

        if (collection.Entries.Count == 0)
        {
            return this.Refused("An empty collection cannot be deployed: the server would have no rotation.");
        }

        var tracks = collection.Entries
            .OrderBy(e => e.Position)
            .Select(CollectionMapping.ToEventLoopTrack)
            .ToList();

        try
        {
            _config.WriteEventLoopTracks(collection.Name, tracks);
        }
        catch (Exception ex) when (ConfigWriteFailure.From(ex) is { } failure)
        {
            _logger.LogWarning(ex, "Could not deploy collection {Name}: {Reason}", collection.Name, failure.Reason);
            return this.Refused(failure.Message, ("reason", failure.Reason));
        }

        _logger.LogInformation(
            "{Caller} deployed collection {Name} with {Count} tracks", this.Caller(), collection.Name, tracks.Count);
        return new DeployCollectionResponse(
            $"Deployed {collection.Name} to the server config.", collection.Name, tracks.Count);
    }

    private ActionResult? Validate(CollectionRequest request)
    {
        var error = EventLoopTrackRules.ValidateName("name", request.Name)
            ?? EventLoopTrackRules.ValidateTracks(request.Tracks);
        return error is null ? null : this.Invalid(error.Field, error.Message);
    }

    /// <summary>
    /// Entries in the order given, each linked to the catalogue variant with its id
    /// when there is one. Unknown ids are kept as they are.
    /// </summary>
    private async Task<List<TrackCollectionEntry>> BuildEntriesAsync(IReadOnlyList<EventLoopTrack?> tracks)
    {
        var entries = tracks.Select((t, i) => CollectionMapping.ToEntry(t!, i)).ToList();

        var ids = entries.Select(e => e.TrackId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var variants = (await _db.TrackVariants.Where(v => ids.Contains(v.VariantId)).ToListAsync())
            .ToDictionary(v => v.VariantId, StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            if (variants.TryGetValue(entry.TrackId, out var variant))
            {
                entry.TrackVariant = variant;
                entry.TrackId = variant.VariantId;
            }
        }

        return entries;
    }

    private async Task<ActionResult<CollectionResponse>> AddAsync(TrackCollection collection)
    {
        _db.TrackCollections.Add(collection);
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (CatalogueHttp.IsUniqueViolation(ex))
        {
            return DuplicateName(collection.Name);
        }

        _logger.LogInformation(
            "{Caller} created collection {Name} with {Count} tracks",
            this.Caller(),
            collection.Name,
            collection.Entries.Count);

        _db.ChangeTracker.Clear();
        var created = (await LoadAsync(collection.Id))!;
        this.SetETag(created.Version);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, CollectionResponse.From(created));
    }

    private async Task<ActionResult<CollectionResponse>> RespondAsync(int id)
    {
        _db.ChangeTracker.Clear();
        var saved = (await LoadAsync(id))!;
        this.SetETag(saved.Version);
        return CollectionResponse.From(saved);
    }

    private Task<bool> NameTakenAsync(string name, int? exceptId) =>
        _db.TrackCollections.AnyAsync(c => c.Name == name && c.Id != exceptId);

    /// <summary>"&lt;name&gt; (copy)", then "(copy 2)" and on, cut to fit the name's length.</summary>
    private async Task<string> CopyNameAsync(string name)
    {
        for (var n = 1; ; n++)
        {
            var suffix = n == 1 ? " (copy)" : $" (copy {n})";
            var stem = name.Length + suffix.Length > TrackCollection.NameMaxLength
                ? name[..(TrackCollection.NameMaxLength - suffix.Length)].TrimEnd()
                : name;
            var candidate = stem + suffix;
            if (!await NameTakenAsync(candidate, exceptId: null))
            {
                return candidate;
            }
        }
    }

    private ActionResult DuplicateName(string name) =>
        this.Invalid("name", $"A collection named '{name}' already exists.");

    private Task<TrackCollection?> LoadAsync(int id, bool tracked = true)
    {
        IQueryable<TrackCollection> query = _db.TrackCollections;
        if (!tracked)
        {
            query = query.AsNoTracking();
        }

        return query
            .AsSplitQuery()
            .Include(c => c.Entries.OrderBy(e => e.Position))
            .ThenInclude(e => e.TrackVariant)
            .ThenInclude(v => v!.Track)
            .SingleOrDefaultAsync(c => c.Id == id);
    }
}
