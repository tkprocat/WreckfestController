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

    // Exception text can hold local paths; the web never sees those (#153).
    [Theory]
    [InlineData("access")]
    [InlineData("missing")]
    [InlineData("io")]
    [InlineData("unconfigured")]
    public void TheMessage_NeverCarriesTheExceptionsText(string kind)
    {
        const string secret = @"C:\Users\someone\Wreckfest\server_config.cfg";
        Exception exception = kind switch
        {
            "access" => new UnauthorizedAccessException($"Access to the path '{secret}' is denied."),
            "missing" => new FileNotFoundException($"Could not find file '{secret}'.", secret),
            "io" => new IOException($"The disk is full writing '{secret}'."),
            _ => new InvalidOperationException($"WorkingDirectory not configured ({secret})"),
        };

        var failure = ConfigWriteFailure.From(exception);

        Assert.NotNull(failure);
        Assert.DoesNotContain("someone", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"C:\", failure.Message, StringComparison.OrdinalIgnoreCase);
    }
}
