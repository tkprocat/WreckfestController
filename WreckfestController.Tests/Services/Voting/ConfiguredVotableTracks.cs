using Microsoft.Extensions.Configuration;
using WreckfestController.Models;
using WreckfestController.Services.Voting;

namespace WreckfestController.Tests.Services.Voting;

/// <summary>
/// Votable tracks from a test's configuration (<c>Vote:AllowedTracks</c>), read on every
/// call as the catalogue is, so a test can change them after the service is built.
/// </summary>
internal sealed class ConfiguredVotableTracks(IConfiguration configuration) : IVotableTracks
{
    public List<AllowedVoteTrack> Get() =>
        configuration.GetSection("Vote:AllowedTracks").Get<List<AllowedVoteTrack>>() ?? [];
}
