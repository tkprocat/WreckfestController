using Microsoft.EntityFrameworkCore;
using WreckfestController.Data;
using WreckfestController.Models;

namespace WreckfestController.Services.Voting;

/// <summary>The tracks players may vote for or pick directly.</summary>
public interface IVotableTracks
{
    /// <summary>Every votable track, by track name then variant name.</summary>
    List<AllowedVoteTrack> Get();
}

/// <summary>
/// Votable tracks from the catalogue: variants allowed for voting, leaving out hidden
/// ones and those of a hidden track. Named "Track - Variant", as 1.x's list was.
/// </summary>
/// <remarks>
/// Read on every call, so a catalogue edit counts at once. There are a few hundred rows,
/// and chat commands are rare. While the database is unavailable (recovery mode) there
/// are none, and voting says so.
/// </remarks>
public sealed class CatalogueVotableTracks : IVotableTracks
{
    private readonly IDbContextFactory<ControllerDbContext> _contexts;
    private readonly DatabaseState _database;
    private readonly ILogger<CatalogueVotableTracks> _logger;

    public CatalogueVotableTracks(
        IDbContextFactory<ControllerDbContext> contexts,
        DatabaseState database,
        ILogger<CatalogueVotableTracks> logger)
    {
        _contexts = contexts;
        _database = database;
        _logger = logger;
    }

    public List<AllowedVoteTrack> Get()
    {
        if (!_database.IsReady)
        {
            return [];
        }

        try
        {
            using var db = _contexts.CreateDbContext();
            return db.TrackVariants
                .AsNoTracking()
                .Where(v => v.AllowedForVoting && !v.IsHidden && !v.Track.IsHidden)
                .OrderBy(v => v.Track.Name)
                .ThenBy(v => v.Name)
                .ThenBy(v => v.VariantId)
                .Select(v => new AllowedVoteTrack { Id = v.VariantId, Name = v.Track.Name + " - " + v.Name })
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not read the votable tracks from the catalogue");
            return [];
        }
    }
}
