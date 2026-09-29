using WreckfestController.Services.Config;

namespace WreckfestController.Tests.Services.Config;

/// <summary>How a failed config write is explained to an admin.</summary>
public class ConfigWriteFailureTests
{
    // A file without the heading is where the settings say; calling it "not set up" would
    // send the admin looking at the wrong thing.
    [Fact]
    public void AMissingEventLoopHeading_IsItsOwnReason_NotAnUnconfiguredLocation()
    {
        var failure = ConfigWriteFailure.From(new EventLoopHeadingMissingException());

        Assert.NotNull(failure);
        Assert.Equal(ConfigWriteFailure.MissingEventLoop, failure.Reason);
        Assert.Contains("# Event Loop", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnconfiguredLocation_IsStillNotConfigured()
    {
        var failure = ConfigWriteFailure.From(new InvalidOperationException("WorkingDirectory not configured"));

        Assert.Equal(ConfigWriteFailure.NotConfigured, failure?.Reason);
    }
}
