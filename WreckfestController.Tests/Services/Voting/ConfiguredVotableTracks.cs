using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using WreckfestController.Models;
using WreckfestController.Services.Config;
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

/// <summary>
/// The Vote settings from a test's configuration, read on every call and brought into
/// range as the store does. A <c>Reload()</c> of the configuration counts as a saved
/// change, so the tests written against configuration keep their bodies.
/// </summary>
internal sealed class ConfiguredVoteSettings(IConfiguration configuration) : IOptionsMonitor<VoteSettings>
{
    public VoteSettings CurrentValue
    {
        get
        {
            var section = configuration.GetSection(SettingsSections.Vote);
            var vote = new VoteSettings();
            section.Bind(vote);

            // Mode falls back to the legacy Enabled flag only when Mode is not set.
            vote.Mode = VoteModes.Normalize(section["Mode"], section.GetValue<bool?>("Enabled"));
            return (VoteSettings)SettingsSections.Normalize(typeof(VoteSettings), vote);
        }
    }

    public VoteSettings Get(string? name) => CurrentValue;

    public IDisposable OnChange(Action<VoteSettings, string?> listener) =>
        ChangeToken.OnChange(configuration.GetReloadToken, () => listener(CurrentValue, Options.DefaultName));
}
