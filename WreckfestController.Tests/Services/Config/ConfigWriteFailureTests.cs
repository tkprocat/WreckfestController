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

    // The setting at fault is named, so the admin looks in the right place.
    [Fact]
    public void AMissingLocation_NamesTheSettingToFix()
    {
        var noDirectory = ConfigWriteFailure.From(ConfigLocationException.NoWorkingDirectory());
        var noArgument = ConfigWriteFailure.From(ConfigLocationException.NoServerConfigArgument());

        Assert.Equal(ConfigWriteFailure.NotConfigured, noDirectory?.Reason);
        Assert.Contains("working directory", noDirectory!.Message, StringComparison.Ordinal);
        Assert.Equal(ConfigWriteFailure.NotConfigured, noArgument?.Reason);
        Assert.Contains("server_config=", noArgument!.Message, StringComparison.Ordinal);
    }

    // ConfigService throws the typed exception for each missing setting.
    [Theory]
    [InlineData("", "-s server_config=server_config.cfg", "working directory")]
    [InlineData(@"C:\somewhere", "-s", "server_config=")]
    public void ConfigService_SaysWhichSettingIsMissing(string workingDirectory, string arguments, string named)
    {
        var service = new ConfigService(
            TestSettings.Server(workingDirectory: workingDirectory, serverArguments: arguments),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ConfigService>.Instance);

        var thrown = Assert.Throws<ConfigLocationException>(() => service.ReadEventLoopTracks());

        Assert.Contains(named, thrown.Message, StringComparison.Ordinal);
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
