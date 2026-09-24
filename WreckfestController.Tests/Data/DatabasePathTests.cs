using Microsoft.Extensions.Configuration;
using WreckfestController.Data;

namespace WreckfestController.Tests.Data;

public class DatabasePathTests
{
    private static readonly string BaseDirectory = Path.Combine(Path.GetTempPath(), "wfc-base");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_WithoutConfiguredPath_UsesLocalAppData(string? configured)
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WreckfestController",
            "controller.db");

        Assert.Equal(expected, DatabasePath.Resolve(Config(configured), BaseDirectory));
    }

    [Fact]
    public void Resolve_ExpandsEnvironmentVariables()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "WreckfestController",
            "controller.db");

        var resolved = DatabasePath.Resolve(
            Config(@"%ProgramData%\WreckfestController\controller.db"),
            BaseDirectory);

        Assert.Equal(expected, resolved, ignoreCase: true);
    }

    // A service's working directory is System32, so a relative path must not follow it.
    [Fact]
    public void Resolve_RelativePath_IsRelativeToBaseDirectory()
    {
        var resolved = DatabasePath.Resolve(Config(@"data\controller.db"), BaseDirectory);

        Assert.Equal(Path.Combine(BaseDirectory, "data", "controller.db"), resolved);
    }

    [Fact]
    public void Resolve_AbsolutePath_IsUsedAsIs()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "elsewhere", "my.db");

        Assert.Equal(absolute, DatabasePath.Resolve(Config(absolute), BaseDirectory));
    }

    private static IConfiguration Config(string? path) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:Path"] = path })
            .Build();
}
