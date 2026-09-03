using WreckfestController.Services;

namespace WreckfestController.Tests.Services;

public class VoteModesTests
{
    [Theory]
    [InlineData("Off", VoteModes.Off)]
    [InlineData("Voting", VoteModes.Voting)]
    [InlineData("Direct", VoteModes.Direct)]
    [InlineData("off", VoteModes.Off)]
    [InlineData("DIRECT", VoteModes.Direct)]
    [InlineData("  Voting  ", VoteModes.Voting)]
    public void Normalize_AcceptsKnownModes_IgnoringCaseAndWhitespace(string input, string expected)
    {
        Assert.Equal(expected, VoteModes.Normalize(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("banana")]
    public void Normalize_FallsBackToVoting_WhenModeUnusableAndNoLegacyFlag(string? input)
    {
        Assert.Equal(VoteModes.Voting, VoteModes.Normalize(input));
    }

    // Settings files written before Vote:Mode existed carry only Vote:Enabled.
    [Fact]
    public void Normalize_MapsLegacyEnabledFalse_ToOff()
    {
        Assert.Equal(VoteModes.Off, VoteModes.Normalize(null, legacyEnabled: false));
    }

    [Fact]
    public void Normalize_MapsLegacyEnabledTrue_ToVoting()
    {
        Assert.Equal(VoteModes.Voting, VoteModes.Normalize(null, legacyEnabled: true));
    }

    [Fact]
    public void Normalize_PrefersExplicitMode_OverLegacyFlag()
    {
        Assert.Equal(VoteModes.Direct, VoteModes.Normalize(VoteModes.Direct, legacyEnabled: false));
        Assert.Equal(VoteModes.Off, VoteModes.Normalize(VoteModes.Off, legacyEnabled: true));
    }

    [Fact]
    public void Normalize_FallsBackToLegacyFlag_WhenModeUnrecognised()
    {
        Assert.Equal(VoteModes.Off, VoteModes.Normalize("banana", legacyEnabled: false));
        Assert.Equal(VoteModes.Voting, VoteModes.Normalize("banana", legacyEnabled: true));
    }
}
