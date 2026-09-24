using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WreckfestController.Services;

namespace WreckfestController.Tests.Services;

public class GuiLoggerProviderTests
{
    private readonly List<(string Level, string Message, DateTime LoggedAt)> _entries = [];

    [Fact]
    public void EntriesLoggedBeforeTheTabExists_AreReplayedInOrderWithTheirOwnTime()
    {
        var provider = new GuiLoggerProvider();
        var logger = provider.CreateLogger("WreckfestController.Data.DatabaseBootstrapper");
        var before = DateTime.Now;

        logger.LogError("first");
        logger.LogWarning("second");
        provider.SetSink((level, message, at) => _entries.Add((level, message, at)));

        Assert.Equal(
            ["[DatabaseBootstrapper] first", "[DatabaseBootstrapper] second"],
            _entries.Select(e => e.Message));
        Assert.Equal(["ERROR", "WARN"], _entries.Select(e => e.Level));
        Assert.All(_entries, e => Assert.InRange(e.LoggedAt, before, DateTime.Now));
    }

    [Fact]
    public void EntriesAfterTheTabExists_GoStraightThrough()
    {
        var provider = new GuiLoggerProvider();
        provider.SetSink((level, message, at) => _entries.Add((level, message, at)));

        provider.CreateLogger("Cat").LogInformation("live");

        Assert.Equal("[Cat] live", Assert.Single(_entries).Message);
    }

    [Fact]
    public void PendingEntries_AreCapped_KeepingTheNewest()
    {
        var provider = new GuiLoggerProvider();
        var logger = provider.CreateLogger("Cat");

        for (var i = 0; i < GuiLoggerProvider.MaxPendingEntries + 5; i++)
        {
            logger.LogInformation("{Index}", i);
        }

        provider.SetSink((level, message, at) => _entries.Add((level, message, at)));

        Assert.Equal(GuiLoggerProvider.MaxPendingEntries, _entries.Count);
        Assert.Equal("[Cat] 5", _entries[0].Message);
        Assert.Equal($"[Cat] {GuiLoggerProvider.MaxPendingEntries + 4}", _entries[^1].Message);
    }

    // EF Core logs every SQL statement at Information, which buried the Controller Log.
    [Fact]
    public void MainHost_LogsEntityFrameworkOnlyFromWarning()
    {
        using var host = Program.CreateHostBuilder([]).Build();
        var logger = host.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Microsoft.EntityFrameworkCore.Database.Command");

        Assert.False(logger.IsEnabled(LogLevel.Information));
        Assert.True(logger.IsEnabled(LogLevel.Warning));
    }
}
