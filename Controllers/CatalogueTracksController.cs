using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WreckfestController.Data;
using WreckfestController.Data.Catalogue;
using WreckfestController.Services;

namespace WreckfestController.Controllers;

/// <summary>
/// Track locations in the catalogue. Built-in tracks are fully editable but cannot be
/// deleted: hiding retires them, and reset restores what shipped.
/// </summary>
[ApiController]
[Authorize(Policy = ApiAuthentication.AdminPolicy)]
[Route("api/catalogue/tracks")]
public class CatalogueTracksController : ControllerBase
{
    private readonly ControllerDbContext _db;
    private readonly ConfigService _config;
    private readonly ILogger<CatalogueTracksController> _logger;

    public CatalogueTracksController(
        ControllerDbContext db,
        ConfigService config,
        ILogger<CatalogueTracksController> logger)
    {
        _db = db;
        _config = config;
        _logger = logger;
    }

    /// <param name="tag">Tracks with a variant carrying this tag slug.</param>
    /// <param name="gameMode">Tracks with a variant of this mode.</param>
    /// <param name="weather">Tracks that support this weather.</param>
    /// <param name="mod">Tracks from this mod id.</param>
    /// <param name="availableOnly">
    /// Only tracks the server can load: those without a mod, and those whose mod is in
    /// the server config's <c>mods=</c>.
    /// </param>
    /// <param name="includeHidden">Also hidden tracks, and hidden variants inside each track.</param>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<TrackResponse>>> List(
        string? tag,
        GameMode? gameMode,
        string? weather,
        TrackOrigin? origin,
        string? dlc,
        int? mod,
        bool availableOnly,
        bool includeHidden)
    {
        IQueryable<Track> query = _db.Tracks
            .AsNoTracking()
            .AsSplitQuery()
            .Include(t => t.Mod)
            .Include(t => t.WeatherConditions)
            .Include(t => t.Variants.Where(v => includeHidden || !v.IsHidden))
            .ThenInclude(v => v.Tags);

        if (!includeHidden)
        {
            query = query.Where(t => !t.IsHidden);
        }

        if (tag is not null)
        {
            query = query.Where(t => t.Variants.Any(v =>
                (includeHidden || !v.IsHidden) && v.Tags.Any(g => g.Slug == tag)));
        }

        if (gameMode is not null)
        {
            query = query.Where(t => t.Variants.Any(v =>
                (includeHidden || !v.IsHidden) && v.GameMode == gameMode));
        }

        if (weather is not null)
        {
            query = query.Where(t => t.WeatherConditions.Any(w => w.Name == weather));
        }

        if (origin is not null)
        {
            query = query.Where(t => t.Origin == origin);
        }

        if (dlc is not null)
        {
            query = query.Where(t => t.DlcName == dlc);
        }

        if (mod is not null)
        {
            query = query.Where(t => t.ModId == mod);
        }

        if (availableOnly)
        {
            List<string> activeMods;
            try
            {
                activeMods = _config.ReadBasicConfig().Mods
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return this.Refused($"Cannot tell which mods are active: {ex.Message}");
            }

            query = query.Where(t => t.ModId == null || activeMods.Contains(t.Mod!.FolderName));
        }

        var tracks = await query.OrderBy(t => t.Name).ToListAsync();
        return tracks.Select(TrackResponse.From).ToList();
    }

    /// <summary>One track with all its variants, hidden ones included.</summary>
    [HttpGet("{id:int}")]
    public async Task<ActionResult<TrackResponse>> Get(int id)
    {
        var track = await LoadAsync(id);
        if (track is null)
        {
            return NotFound();
        }

        this.SetETag(track.Version);
        return TrackResponse.From(track);
    }

    /// <summary>Adds a track, supporting every weather until told otherwise.</summary>
    [HttpPost]
    public async Task<ActionResult<TrackResponse>> Create(TrackRequest request)
    {
        if (await ValidateAsync(request) is { } invalid)
        {
            return invalid;
        }

        if (await _db.Tracks.AnyAsync(t => t.Key == request.Key))
        {
            return this.Invalid("key", $"A track with key '{request.Key}' already exists.");
        }

        var track = new Track
        {
            Key = request.Key,
            WeatherConditions = await _db.WeatherConditions.ToListAsync(),
        };
        Apply(track, request);
        _db.Tracks.Add(track);

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (CatalogueHttp.IsUniqueViolation(ex))
        {
            return this.Invalid("key", $"A track with key '{request.Key}' already exists.");
        }

        _logger.LogInformation("{Caller} added track {Key}", this.Caller(), track.Key);
        var created = (await LoadAsync(track.Id))!;
        this.SetETag(created.Version);
        return CreatedAtAction(nameof(Get), new { id = track.Id }, TrackResponse.From(created));
    }

    /// <summary>Changes the track's own fields. Needs If-Match. A built-in track's key is fixed.</summary>
    [HttpPut("{id:int}")]
    public async Task<ActionResult<TrackResponse>> Update(int id, TrackRequest request)
    {
        if (this.ReadIfMatch(out var expected) is { } badPrecondition)
        {
            return badPrecondition;
        }

        var track = await LoadAsync(id);
        if (track is null)
        {
            return NotFound();
        }

        if (track.Version != expected)
        {
            return this.VersionConflict(TrackResponse.From(track), track.Version);
        }

        if (await ValidateAsync(request) is { } invalid)
        {
            return invalid;
        }

        if (!string.Equals(track.Key, request.Key, StringComparison.Ordinal))
        {
            if (track.IsBuiltIn)
            {
                return this.Invalid("key", "A built-in track's key cannot change.");
            }

            if (await _db.Tracks.AnyAsync(t => t.Key == request.Key && t.Id != id))
            {
                return this.Invalid("key", $"A track with key '{request.Key}' already exists.");
            }

            track.Key = request.Key;
        }

        Apply(track, request);
        return await SaveAsync(track);
    }

    /// <summary>Deletes an admin-added track and its variants. Built-in tracks are hidden instead.</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var track = await _db.Tracks.FindAsync(id);
        if (track is null)
        {
            return NotFound();
        }

        if (track.IsBuiltIn)
        {
            return this.Refused("Built-in tracks cannot be deleted. Hide it instead.");
        }

        _db.Tracks.Remove(track);
        await _db.SaveChangesAsync();
        _logger.LogInformation("{Caller} deleted track {Key}", this.Caller(), track.Key);
        return NoContent();
    }

    [HttpPost("{id:int}/hide")]
    public Task<ActionResult<TrackResponse>> Hide(int id) => SetHiddenAsync(id, hidden: true);

    [HttpPost("{id:int}/unhide")]
    public Task<ActionResult<TrackResponse>> Unhide(int id) => SetHiddenAsync(id, hidden: false);

    /// <summary>
    /// Restores a built-in track's name, origin and weather to what shipped. Leaves
    /// whether it is hidden, and its variants, alone.
    /// </summary>
    [HttpPost("{id:int}/reset")]
    public async Task<ActionResult<TrackResponse>> Reset(int id)
    {
        var track = await LoadAsync(id);
        if (track is null)
        {
            return NotFound();
        }

        var shipped = track.IsBuiltIn ? BuiltInCatalogue.FindTrack(track.Key) : null;
        if (shipped is null)
        {
            return this.Refused("Only built-in tracks can be reset.");
        }

        track.Name = shipped.Name;
        track.Origin = shipped.Origin;
        track.DlcName = null;
        track.ModId = null;
        track.Mod = null;
        track.WeatherConditions = await _db.WeatherConditions
            .Where(w => shipped.Weather.Contains(w.Name))
            .ToListAsync();

        _logger.LogInformation("{Caller} reset track {Key} to its shipped values", this.Caller(), track.Key);
        return await SaveAsync(track);
    }

    /// <summary>Replaces the weather the track supports.</summary>
    [HttpPut("{id:int}/weather")]
    public async Task<ActionResult<TrackResponse>> SetWeather(int id, WeatherNamesRequest request)
    {
        var track = await LoadAsync(id);
        if (track is null)
        {
            return NotFound();
        }

        var names = request.Weather.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var weather = await _db.WeatherConditions.Where(w => names.Contains(w.Name)).ToListAsync();
        var unknown = names.Except(weather.Select(w => w.Name), StringComparer.OrdinalIgnoreCase).ToList();
        if (unknown.Count > 0)
        {
            return this.Invalid("weather", $"Unknown weather: {string.Join(", ", unknown)}.");
        }

        track.WeatherConditions = weather;
        return await SaveAsync(track);
    }

    private async Task<ActionResult<TrackResponse>> SetHiddenAsync(int id, bool hidden)
    {
        var track = await LoadAsync(id);
        if (track is null)
        {
            return NotFound();
        }

        track.IsHidden = hidden;
        return await SaveAsync(track);
    }

    /// <summary>Saves, turning a write that raced another into 409 with the winner's row.</summary>
    private async Task<ActionResult<TrackResponse>> SaveAsync(Track track)
    {
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            _db.ChangeTracker.Clear();
            var current = await LoadAsync(track.Id);
            return current is null ? NotFound() : this.VersionConflict(TrackResponse.From(current), current.Version);
        }
        catch (DbUpdateException ex) when (CatalogueHttp.IsUniqueViolation(ex))
        {
            return this.Invalid("key", $"A track with key '{track.Key}' already exists.");
        }

        var saved = (await LoadAsync(track.Id))!;
        this.SetETag(saved.Version);
        return TrackResponse.From(saved);
    }

    private async Task<ActionResult?> ValidateAsync(TrackRequest request)
    {
        if (request.DlcName is not null && request.Origin != TrackOrigin.Dlc)
        {
            return this.Invalid("dlcName", "Only a DLC track has a DLC name.");
        }

        if (request.ModId is not null)
        {
            if (request.Origin != TrackOrigin.Workshop)
            {
                return this.Invalid("modId", "Only a workshop track comes from a mod.");
            }

            if (!await _db.Mods.AnyAsync(m => m.Id == request.ModId))
            {
                return this.Invalid("modId", $"There is no mod with id {request.ModId}.");
            }
        }

        return null;
    }

    private static void Apply(Track track, TrackRequest request)
    {
        track.Name = request.Name.Trim();
        track.Origin = request.Origin!.Value;
        track.DlcName = string.IsNullOrWhiteSpace(request.DlcName) ? null : request.DlcName.Trim();
        track.ModId = request.ModId;
    }

    private Task<Track?> LoadAsync(int id) => _db.Tracks
        .AsSplitQuery()
        .Include(t => t.Mod)
        .Include(t => t.WeatherConditions)
        .Include(t => t.Variants)
        .ThenInclude(v => v.Tags)
        .SingleOrDefaultAsync(t => t.Id == id);
}
