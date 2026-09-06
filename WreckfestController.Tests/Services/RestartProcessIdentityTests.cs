using WreckfestController.Services;
using Xunit;

namespace WreckfestController.Tests.Services;

public class RestartProcessIdentityTests
{
    private static readonly DateTime Requested = new(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Exited = Requested.AddSeconds(2);
    private static readonly RestartProcessIdentity Original = new(
        100, 50, @"C:\server\Wreckfest_x64.exe", @"Wreckfest_x64.exe -s server_config=A.cfg", Requested.AddHours(-1));
    private static readonly RestartProcessIdentity Replacement = Original with {
        ProcessId = 200, ParentProcessId = 100, CreatedUtc = Requested.AddSeconds(1) };

    [Fact]
    public void GenuineReplacementIsSelectedAmongUnrelatedNewServers()
    {
        var unrelated = Replacement with { ProcessId = 300, ParentProcessId = 80, CommandLine = "Wreckfest_x64.exe -s server_config=B.cfg" };
        var result = Select(unrelated, Replacement);
        Assert.Equal(Replacement, result.Process);
        Assert.Empty(result.Error);
    }

    [Fact]
    public void UnrelatedServerCannotMaskFailedRestartEvenWithIdenticalConfiguration()
    {
        var unrelated = Replacement with { ProcessId = 300, ParentProcessId = 80 };
        Assert.Null(Select(unrelated).Process);
    }

    [Fact]
    public void MultipleMatchingChildrenAreAmbiguous()
    {
        var result = Select(Replacement, Replacement with { ProcessId = 300 });
        Assert.Null(result.Process);
        Assert.Contains("ambiguous", result.Error);
    }

    [Fact]
    public void MissingOrUnreadableCandidatesFailExplicitly()
    {
        Assert.Null(Select().Process);
        Assert.Null(Select(Replacement, null).Process);
        Assert.Null(Select(Replacement with { CommandLine = "" }).Process);
        Assert.Null(Select(Replacement with { ExecutablePath = "" }).Process);
    }

    [Theory]
    [InlineData("executable")]
    [InlineData("configuration")]
    [InlineData("old process")]
    [InlineData("before request")]
    [InlineData("reused parent")]
    public void MismatchedIdentitiesAreRejected(string mismatch)
    {
        var candidate = mismatch switch {
            "executable" => Replacement with { ExecutablePath = @"C:\other\Wreckfest_x64.exe" },
            "configuration" => Replacement with { CommandLine = "Wreckfest_x64.exe -s server_config=B.cfg" },
            "old process" => Replacement with { ProcessId = 150 },
            "before request" => Replacement with { CreatedUtc = Requested.AddSeconds(-1) },
            "reused parent" => Replacement with { CreatedUtc = Exited.AddSeconds(1) },
            _ => throw new ArgumentException(mismatch)
        };
        var result = Select(candidate);
        Assert.Null(result.Process);
        Assert.NotEmpty(result.Error);
    }

    [Fact]
    public void UnknownOriginalIdentityCannotAuthorizeAReplacement()
    {
        var result = RestartProcessIdentity.SelectReplacement(Original with { CommandLine = "" },
            [100, 150], [Replacement], Requested, Exited);
        Assert.Null(result.Process);
        Assert.Contains("Original", result.Error);
    }

    private static (RestartProcessIdentity? Process, string Error) Select(params RestartProcessIdentity?[] candidates)
        => RestartProcessIdentity.SelectReplacement(Original, [100, 150], candidates, Requested, Exited);
}
