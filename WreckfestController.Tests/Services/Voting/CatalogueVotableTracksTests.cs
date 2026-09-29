using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WreckfestController.Data;
using WreckfestController.Services.Voting;

namespace WreckfestController.Tests.Services.Voting;

/// <summary>Votable tracks from the shipped catalogue, in a migrated SQLite file.</summary>
public sealed class CatalogueVotableTracksTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "wfc-votable-tests", Guid.NewGuid().ToString("N"));

    private readonly IDbContextFactory<ControllerDbContext> _contexts;
    private readonly DatabaseState _database;
    private readonly CatalogueVotableTracks _tracks;

    public CatalogueVotableTracksTests()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "controller.db");
        var options = new DbContextOptionsBuilder<ControllerDbContext>();
        ControllerDbContext.Configure(options, path);
        _contexts = new Factory(options.Options);
        using (var db = _contexts.CreateDbContext())
        {
            db.Database.Migrate();
        }

        _database = new DatabaseState(path);
        _database.MarkReady(null);
        _tracks = new CatalogueVotableTracks(_contexts, _database, NullLogger<CatalogueVotableTracks>.Instance);
    }

    public void Dispose()
    {
        SqlitePools.ReleaseFolder(_directory);
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void ShippedVariants_AreVotable_NamedTrackThenVariant()
    {
        var tracks = _tracks.Get();

        var arena = Assert.Single(tracks, t => t.Id == "bigstadium_demolition_arena");
        Assert.Equal("Madman Stadium - Demolition Arena", arena.Name);
        Assert.Equal(tracks.OrderBy(t => t.Name, StringComparer.Ordinal).Select(t => t.Id), tracks.Select(t => t.Id));
    }

    [Fact]
    public void NotAllowed_HiddenVariants_AndHiddenTracks_AreLeftOut()
    {
        using (var db = _contexts.CreateDbContext())
        {
            db.TrackVariants.Single(v => v.VariantId == "bigstadium_demolition_arena").AllowedForVoting = false;
            db.TrackVariants.Single(v => v.VariantId == "bigstadium_figure_8").IsHidden = true;
            var fairfield = db.TrackVariants.Include(v => v.Track).Single(v => v.VariantId == "smallstadium_demolition_arena").Track;
            fairfield.IsHidden = true;
            db.SaveChanges();
        }

        var ids = _tracks.Get().Select(t => t.Id).ToList();

        Assert.DoesNotContain("bigstadium_demolition_arena", ids);
        Assert.DoesNotContain("bigstadium_figure_8", ids);
        Assert.DoesNotContain("smallstadium_demolition_arena", ids);
        Assert.Contains("mudpit_demolition_arena", ids);
    }

    [Fact]
    public void WithoutTheDatabase_NothingIsVotable()
    {
        _database.MarkFailed("disk gone", null);

        Assert.Empty(_tracks.Get());
    }

    private sealed class Factory(DbContextOptions<ControllerDbContext> options) : IDbContextFactory<ControllerDbContext>
    {
        public ControllerDbContext CreateDbContext() => new(options);
    }
}
