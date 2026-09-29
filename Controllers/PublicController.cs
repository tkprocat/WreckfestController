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
[Route("api/public")]
public class PublicController : ControllerBase
{
    /// <summary>How many upcoming cups the overview lists.</summary>
    public const int UpcomingCupLimit = 5;

    private readonly ServerManager _serverManager;
    private readonly ConfigService _config;
    private readonly CupStore _cups;
    private readonly ControllerDbContext _db;
    private readonly TimeProvider _time;
    private readonly ILogger<PublicController> _logger;

    public PublicController(
        ServerManager serverManager,
        ConfigService config,
        CupStore cups,
        ControllerDbContext db,
        TimeProvider time,
        ILogger<PublicController> logger)
    {
        _serverManager = serverManager;
        _config = config;
        _cups = cups;
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

        Dictionary<string, string> names;
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
            string.IsNullOrEmpty(status.CurrentTrack) ? null : Track(status.CurrentTrack, names),
            new PublicPlayers(
                players.Count(p => !p.IsBot),
                players.Count(p => p.IsBot),
                players.Select(p => new PublicPlayer(p.Name, p.IsBot)).ToList()),
            new PublicRotation(
                string.IsNullOrWhiteSpace(rotationName) ? null : rotationName,
                rotation.Select(t => new PublicRotationTrack(t.Track, Name(t.Track, names), t.Gamemode, t.Laps)).ToList()),
            cups.Active is { } active ? new PublicActiveCup(active.Name, active.ActivatedAt) : null,
            cups.Upcoming
                .Select(c => new PublicUpcomingCup(
                    c.Name,
                    c.Description,
                    c.NextOccurrence!.Value,
                    c.Repeat is null ? null : CupRecurrence.Describe(c.Repeat)))
                .ToList(),
            _time.GetUtcNow());
    }

    /// <summary>
    /// Catalogue names for just these variant ids, hidden ones too: this is display, not a
    /// picker. Ids match ignoring case, as the catalogue stores them.
    /// </summary>
    private async Task<Dictionary<string, string>> TrackNamesAsync(List<string> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var variants = await _db.TrackVariants
            .AsNoTracking()
            .Where(v => ids.Contains(v.VariantId))
            .Select(v => new { v.VariantId, Name = v.Track.Name + " - " + v.Name })
            .ToListAsync(cancellationToken);
        return variants.ToDictionary(v => v.VariantId, v => v.Name, StringComparer.OrdinalIgnoreCase);
    }

    private static PublicTrack Track(string id, Dictionary<string, string> names) => new(id, Name(id, names));

    /// <summary>The catalogue's name, or the id itself for a track the catalogue does not know.</summary>
    private static string Name(string id, Dictionary<string, string> names) =>
        names.TryGetValue(id, out var name) ? name : id;
}
