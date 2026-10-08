using System.Xml.Linq;
using WreckfestController.Services.Desktop;
using Xunit;

namespace WreckfestController.Tests.Services.Desktop;

public class AppInfoTests
{
    // The About tab must show the csproj's <Version> in full: AssemblyVersion keeps only
    // major.minor, so a patch release would otherwise show as .0.
    [Fact]
    public void Version_IsTheProjectVersion()
    {
        var csproj = Path.Combine(FindRepositoryRoot(), "WreckfestController.csproj");
        var expected = XDocument.Load(csproj).Descendants("Version").First().Value;

        Assert.Equal(expected, AppInfo.Version);
    }

    [Fact]
    public void BuildTime_IsReadFromTheFileVersion()
    {
        Assert.Equal(new DateTime(2026, 10, 8, 9, 42, 0, DateTimeKind.Utc), AppInfo.ParseBuildTime("2026.1008.0942"));
        Assert.Equal(DateTimeKind.Utc, AppInfo.ParseBuildTime("2026.1008.0942")!.Value.Kind);
        Assert.Null(AppInfo.ParseBuildTime("2.0.0.0"));
        Assert.Null(AppInfo.ParseBuildTime(null));
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "WreckfestController.csproj")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("WreckfestController.csproj not found above the test output");
    }
}
