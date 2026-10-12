using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using WreckfestController.Data;
using WreckfestController.Models;
using WreckfestController.Services.Auth;
using WreckfestController.Services.Config;
using WreckfestController.Services.Cups;
using WreckfestController.Services.ServerControl;

namespace WreckfestController.Controllers;

/// <summary>
/// What anyone may see without signing in: the public home page's data. Live updates
/// come from the hub's public group.
/// </summary>
/// <remarks>
/// Built field by field into <see cref="PublicOverview"/>, never from
/// <see cref="ServerConfig"/> or an entity, so the server password, the admin and
/// moderator Steam ids, and a cup's server overrides cannot leak through it.
/// </remarks>
[ApiController]
[AllowAnonymous]
[EnableRateLimiting(RateLimits.PublicPolicy)]
// Declaring any response type stops ASP.NET Core inferring the 200 from ActionResult<T>,
// so it is declared too; with no type given, each action's own return type is used.
[ProducesResponseType(StatusCodes.Status200OK)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json")]
[Route("api/public")]
public class PublicController : ControllerBase
{
    /// <summary>How many upcoming cups the overview lists.</summary>
    public const int UpcomingCupLimit = 5;

    /// <summary>How many finished races the race list returns.</summary>
    public const int RaceLimit = 10;

    private readonly ServerManager _serverManager;
    private readonly ConfigService _config;
    private readonly CupStore _cups;
    private readonly ISettingsStore _settings;
    private readonly ControllerDbContext _db;
    private readonly TimeProvider _time;
    private readonly ILogger<PublicController> _logger;

    public PublicController(
        ServerManager serverManager,
        ConfigService config,
        CupStore cups,
        ISettingsStore settings,
        ControllerDbContext db,
        TimeProvider time,
        ILogger<PublicController> logger)
    {
        _serverManager = serverManager;
        _config = config;
        _cups = cups;
        _settings = settings;
        _db = db;
        _time = time;
        _logger = logger;
    }

    [HttpGet("overview")]
    public async Task<ActionResult<PublicOverview>> Overview(CancellationToken cancellationToken)
    {
        var status = _serverManager.GetStatus();
        var players = _serverManager.GetPlayerList().Players;

        // The server config may be missing or unreadable (not set up yet, or another
        // program has it open); the overview still answers.
        string? serverName = null;
        int? maxPlayers = null;
        string? rotationName = null;
        List<EventLoopTrack> rotation = [];
        try
        {
            var config = _config.ReadBasicConfig();
            serverName = config.ServerName;
            maxPlayers = config.MaxPlayers;
            rotation = _config.ReadEventLoopTracks();
            rotationName = _config.GetCurrentCollectionName();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Public overview without the server config");
        }

        Dictionary<string, CatalogueTrackName> names;
        (CupSummary? Active, List<CupSummary> Upcoming) cups;
        try
        {
            var ids = rotation.Select(t => t.Track).Append(status.CurrentTrack).Where(id => !string.IsNullOrEmpty(id)).ToList();
            names = await TrackNamesAsync(ids, cancellationToken);
            cups = await _cups.ScheduleAsync(UpcomingCupLimit, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Anonymous callers get no detail: an exception can name files, tables or queries.
            _logger.LogError(ex, "The public overview could not read the database");
            return Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "The server overview is unavailable right now.");
        }

        return new PublicOverview(
            serverName,
            maxPlayers,
            new PublicStatus(status.IsRunning, status.Uptime is { } uptime ? (long)uptime.TotalSeconds : null),
            string.IsNullOrEmpty(status.CurrentTrack) ? null : PublicFeed.Track(status.CurrentTrack, names),
            new PublicPlayers(
                players.Count(p => !p.IsBot),
                players.Count(p => p.IsBot),
                players.Select(p => new PublicPlayer(p.Name, p.IsBot)).ToList()),
            new PublicRotation(
                string.IsNullOrWhiteSpace(rotationName) ? null : rotationName,
                rotation.Select(t =>
                {
                    var track = PublicFeed.Track(t.Track, names);
                    return new PublicRotationTrack(t.Track, track.Name, track.TrackKey, t.Gamemode, t.Laps);
                }).ToList()),
            cups.Active is { } active
                ? new PublicActiveCup(active.Name, active.ActivatedAt, active.Phase, active.StartsAt, active.EndsAt)
                : null,
            cups.Upcoming
                .Select(c => new PublicUpcomingCup(
                    c.Name,
                    c.Description,
                    c.NextOccurrence!.Value,
                    c.Repeat is null ? null : CupRecurrence.Describe(c.Repeat),
                    c.WarmupAt,
                    c.EndsAt))
                .ToList(),
            _time.GetUtcNow());
    }

    /// <summary>The latest <see cref="RaceLimit"/> finished races, newest first, bots included.</summary>
    [HttpGet("races")]
    public async Task<ActionResult<List<PublicRace>>> Races(CancellationToken cancellationToken)
    {
        try
        {
            var races = await PublicFeed.Races(_db.Races
                    .OrderByDescending(r => r.EndedAt)
                    .ThenByDescending(r => r.Id)
                    .Take(RaceLimit))
                .ToListAsync(cancellationToken);

            var names = await TrackNamesAsync(races.Select(r => r.TrackId).Distinct().ToList(), cancellationToken);
            return races.Select(r => PublicFeed.ToPublic(r, names)).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Anonymous callers get no detail, as for the overview.
            _logger.LogError(ex, "The public race list could not read the database");
            return Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Recent races are unavailable right now.");
        }
    }

    /// <summary>
    /// The front page's results, newest first: races outside a cup one by one, and each cup
    /// run as one item with its standings and races. As many items as the public site
    /// settings say.
    /// </summary>
    [HttpGet("feed")]
    public async Task<ActionResult<List<PublicFeedItem>>> Feed(CancellationToken cancellationToken)
    {
        var count = _settings.Get<PublicSiteSettings>().FeedItemCount;
        try
        {
            var (active, _) = await _cups.ScheduleAsync(0, cancellationToken);
            return await PublicFeed.BuildAsync(_db, active, count, TrackNamesAsync, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Anonymous callers get no detail, as for the overview.
            _logger.LogError(ex, "The public results feed could not read the database");
            return Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Recent results are unavailable right now.");
        }
    }

    /// <summary>
    /// Catalogue names for just these variant ids, hidden ones too: this is display, not a
    /// picker. Ids match ignoring case, as the catalogue stores them.
    /// </summary>
    private async Task<Dictionary<string, CatalogueTrackName>> TrackNamesAsync(List<string> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return new Dictionary<string, CatalogueTrackName>(StringComparer.OrdinalIgnoreCase);
        }

        var variants = await _db.TrackVariants
            .AsNoTracking()
            .Where(v => ids.Contains(v.VariantId))
            .Select(v => new
            {
                v.VariantId,
                Name = v.Track.Name + " - " + v.Name,
                // Linked only where the public track page shows it.
                TrackKey = v.IsHidden || v.Track.IsHidden ? null : v.Track.Key,
            })
            .ToListAsync(cancellationToken);
        return variants.ToDictionary(v => v.VariantId, v => new CatalogueTrackName(v.Name, v.TrackKey), StringComparer.OrdinalIgnoreCase);
    }
}
