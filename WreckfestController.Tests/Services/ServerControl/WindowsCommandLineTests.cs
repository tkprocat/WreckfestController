using WreckfestController.Services.ServerControl;

namespace WreckfestController.Tests.Services.ServerControl;

/// <summary>
/// Whether a command line starts a dedicated server: a standalone -s argument, split the
/// way Windows splits it. A substring test missed "-s" at the very end and took a folder
/// named "-s copy" for the flag.
/// </summary>
public class WindowsCommandLineTests
{
    [Theory]
    [InlineData(@"""C:\Servers\Wreckfest\Wreckfest_x64.exe"" -s server_config=server_config.cfg", true)]
    [InlineData(@"""C:\Servers\Wreckfest\Wreckfest_x64.exe"" -s", true)]
    [InlineData(@"C:\Servers\Wreckfest\Wreckfest_x64.exe -S server_config=a.cfg", true)]
    [InlineData(@"""C:\Servers -s copy\Wreckfest_x64.exe""", false)]
    [InlineData(@"""C:\Servers -s copy\Wreckfest_x64.exe"" -windowed", false)]
    [InlineData(@"""D:\SteamLibrary\steamapps\common\Wreckfest\Wreckfest_x64.exe""", false)]
    [InlineData(@"""C:\Servers\Wreckfest\Wreckfest_x64.exe"" -server_config=a.cfg", false)]
    [InlineData(@"""C:\Servers\Wreckfest\Wreckfest_x64.exe"" ""-s x""", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void HasServerFlag_NeedsAStandaloneDashS(string? commandLine, bool expected)
    {
        Assert.Equal(expected, WindowsCommandLine.HasServerFlag(commandLine));
    }

    [Fact]
    public void Split_KeepsQuotedPathsWhole()
    {
        Assert.Equal(
            [@"C:\Servers -s copy\Wreckfest_x64.exe", "-s", "server_config=a b.cfg"],
            WindowsCommandLine.Split(@"""C:\Servers -s copy\Wreckfest_x64.exe"" -s ""server_config=a b.cfg"""));
    }
}
