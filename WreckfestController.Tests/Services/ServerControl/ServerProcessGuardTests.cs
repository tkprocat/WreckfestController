using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using WreckfestController.Services.Publishing;
using WreckfestController.Services.ServerControl;
using WreckfestController.Services.Tracking;

namespace WreckfestController.Tests.Services.ServerControl;

/// <summary>
/// Which processes the web API may attach to or inject into: only the dedicated server the
/// Configuration tab names. Attach decides what Force stop kills and what inject loads the
/// hook into, so anything else - the game client, another install, any process - is refused.
/// </summary>
public class ServerProcessGuardTests
{
    [Theory]
    [InlineData(@"C:\Servers\Wreckfest\Wreckfest_x64.exe", @"C:\Servers\Wreckfest\Wreckfest_x64.exe", true)]
    [InlineData(@"c:\servers\wreckfest\WRECKFEST_X64.EXE", @"C:\Servers\Wreckfest\Wreckfest_x64.exe", true)]
    [InlineData(@"C:\Servers\Wreckfest\bin\..\Wreckfest_x64.exe", @"C:\Servers\Wreckfest\Wreckfest_x64.exe", true)]
    // The game client has the same file name in another folder.
    [InlineData(@"D:\SteamLibrary\steamapps\common\Wreckfest\Wreckfest_x64.exe", @"C:\Servers\Wreckfest\Wreckfest_x64.exe", false)]
    [InlineData(@"C:\Servers\Other\Wreckfest_x64.exe", @"C:\Servers\Wreckfest\Wreckfest_x64.exe", false)]
    [InlineData(@"C:\Servers\Wreckfest\Wreckfest_x64.exe", "", false)]
    [InlineData("", @"C:\Servers\Wreckfest\Wreckfest_x64.exe", false)]
    [InlineData(@"C:\Servers\Wreckfest\Wreckfest_x64.exe", "C:\\bad\0path", false)]
    public void IsConfiguredServerPath_MatchesOnlyTheSameFile(string executable, string configured, bool expected)
    {
        Assert.Equal(expected, ServerManager.IsConfiguredServerPath(executable, configured));
    }

    [Fact]
    public void WithNoServerPathSet_NothingCanBeConfirmedAsTheServer()
    {
        var (allowed, reason) = Manager(serverPath: "").CheckConfiguredServerProcess(Environment.ProcessId);

        Assert.False(allowed);
        Assert.Contains("No server path is set", reason, StringComparison.Ordinal);
    }

    // This test process is not a Wreckfest server: refused, and the reason has no path.
    [Fact]
    public void AnyOtherProcess_IsRefused_WithoutNamingAPath()
    {
        var serverPath = @"C:\Servers\Wreckfest\Wreckfest_x64.exe";

        var (allowed, reason) = Manager(serverPath).CheckConfiguredServerProcess(Environment.ProcessId);

        Assert.False(allowed);
        Assert.Contains("not a running Wreckfest dedicated server", reason, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\", reason, StringComparison.OrdinalIgnoreCase);
    }

    private static ServerManager Manager(string serverPath)
    {
        var events = Mock.Of<IServerEventPublisher>();
        return new ServerManager(
            Mock.Of<IConfiguration>(),
            TestSettings.Server(serverPath: serverPath),
            TestSettings.SteamCmd(),
            Mock.Of<ILogger<ServerManager>>(),
            new PlayerTracker(Mock.Of<ILogger<PlayerTracker>>(), events),
            new TrackChangeTracker(Mock.Of<ILogger<TrackChangeTracker>>(), events),
            new ServerInfoTracker(Mock.Of<ILogger<ServerInfoTracker>>()),
            events);
    }
}
