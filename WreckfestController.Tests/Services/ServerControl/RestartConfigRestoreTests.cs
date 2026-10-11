using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WreckfestController.Models;
using WreckfestController.Services.Config;
using WreckfestController.Services.Publishing;
using WreckfestController.Services.ServerControl;
using WreckfestController.Services.Tracking;

namespace WreckfestController.Tests.Services.ServerControl;

/// <summary>
/// #224: a cup's settings are written when its restart is asked for, so a restart that is
/// cancelled or fails must take them back out - against a real server_config.cfg.
/// </summary>
public sealed class RestartConfigRestoreTests : IDisposable
{
    private static readonly string[] Original =
    [
        "server_name=Everyday server",
        "session_mode=normal",
        "",
        "# Event Loop",
        "el_add=urban09_1",
    ];

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"wf-restore-{Guid.NewGuid():N}");
    private readonly string _file;
    private readonly byte[] _originalBytes;
    private readonly Mock<ServerManager> _server;
    private readonly PlayerTracker _players;
    private readonly SmartRestartService _restart;

    public RestartConfigRestoreTests()
    {
        Directory.CreateDirectory(_folder);
        _file = Path.Combine(_folder, "server_config.cfg");
        File.WriteAllLines(_file, Original);
        _originalBytes = File.ReadAllBytes(_file);

        var events = Mock.Of<IServerEventPublisher>();
        _players = new PlayerTracker(Mock.Of<ILogger<PlayerTracker>>(), events);
        var tracks = new TrackChangeTracker(Mock.Of<ILogger<TrackChangeTracker>>(), events);
        _server = new Mock<ServerManager>(new ConfigurationBuilder().Build(), TestSettings.Server(), TestSettings.SteamCmd(),
            Mock.Of<ILogger<ServerManager>>(), _players, tracks, new ServerInfoTracker(Mock.Of<ILogger<ServerInfoTracker>>()), events);
        _server.Setup(s => s.SendCommandAsync(It.IsAny<AttachmentSession?>(), It.IsAny<string>())).ReturnsAsync((true, "Sent"));
        var config = new ConfigService(TestSettings.Server(workingDirectory: _folder), NullLogger<ConfigService>.Instance);
        _restart = new SmartRestartService(_server.Object, _players, tracks, config, events,
            NullLogger<SmartRestartService>.Instance);
    }

    public void Dispose()
    {
        _restart.CancelRestart();
        Directory.Delete(_folder, recursive: true);
    }

    private static Event Cup => new()
    {
        Id = 1,
        Name = "Race night",
        ServerConfig = new EventServerConfig { ServerName = "Race night" },
        SessionMode = "30p-aggr",
        Tracks = [new EventLoopTrack { Track = "speedway2_figure_8", Laps = 3 }],
    };

    private void PlayerOnline() =>
        _players.ProcessHookPlayerSnapshot([new Player { PlayerId = 1, Name = "Player", IsBot = false }]);

    private async Task<RestartOutcome> RunToOutcome(bool restartSucceeds)
    {
        _server.Setup(s => s.RestartServerViaCommandAsync(It.IsAny<AttachmentSession?>()))
            .ReturnsAsync((restartSucceeds, restartSucceeds ? "Restarted" : "No answer"));
        var finished = new TaskCompletionSource<RestartOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(_restart.InitiateRestart(Cup, _ => { }, (_, outcome) => finished.TrySetResult(outcome)));
        return await finished.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Cancelling_PutsTheFileBackExactly()
    {
        PlayerOnline();
        Assert.True(_restart.InitiateRestart(Cup, _ => { }));
        Assert.Contains("session_mode=30p-aggr", File.ReadAllLines(_file));

        Assert.True(_restart.CancelRestart());

        Assert.Equal(_originalBytes, File.ReadAllBytes(_file));
    }

    [Fact]
    public async Task AFailedRestart_PutsTheFileBack()
    {
        Assert.Equal(RestartOutcome.Failed, await RunToOutcome(restartSucceeds: false));

        Assert.Equal(_originalBytes, File.ReadAllBytes(_file));
    }

    [Fact]
    public async Task ASuccessfulRestart_KeepsTheCupsSettings()
    {
        Assert.Equal(RestartOutcome.Succeeded, await RunToOutcome(restartSucceeds: true));

        var lines = File.ReadAllLines(_file);
        Assert.Contains("server_name=Race night", lines);
        Assert.Contains("session_mode=30p-aggr", lines);
        Assert.Contains("el_add=speedway2_figure_8", lines);
    }

    // An admin's edit, or a cup run's, made during the countdown is kept: restoring the
    // whole file would silently undo it.
    [Fact]
    public void Cancelling_AfterTheFileChangedMeanwhile_LeavesItAsItIs()
    {
        PlayerOnline();
        Assert.True(_restart.InitiateRestart(Cup, _ => { }));
        File.AppendAllLines(_file, ["el_add=urban09_2"]);
        var edited = File.ReadAllBytes(_file);

        Assert.True(_restart.CancelRestart());

        Assert.Equal(edited, File.ReadAllBytes(_file));
    }
}
