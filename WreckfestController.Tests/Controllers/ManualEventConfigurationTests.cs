using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using System.Reflection;
using WreckfestController.Controllers;
using WreckfestController.Models;
using WreckfestController.Services;
using Xunit;

namespace WreckfestController.Tests.Controllers;

public class ManualEventConfigurationTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"manual-event-{Guid.NewGuid()}.json");
    private readonly Mock<ConfigService> _config;
    private readonly Mock<ServerManager> _server;
    private readonly EventStorageService _storage;
    private readonly SmartRestartService _restart;
    private readonly EventsController _controller;
    private readonly TaskCompletionSource _activated = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ManualEventConfigurationTests()
    {
        var settings = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["EventSchedulePath"] = _path }).Build();
        var webhook = new WreckfestWebWebhookService(Mock.Of<ILogger<WreckfestWebWebhookService>>(), settings, new HttpClient());
        var players = new PlayerTracker(Mock.Of<ILogger<PlayerTracker>>(), webhook);
        var tracks = new TrackChangeTracker(Mock.Of<ILogger<TrackChangeTracker>>(), webhook);
        _server = new Mock<ServerManager>(settings, Mock.Of<ILogger<ServerManager>>(), players, tracks,
            new ServerInfoTracker(Mock.Of<ILogger<ServerInfoTracker>>()), webhook,
            new ConsoleLogWebhookSender(new HttpClient(), settings, Mock.Of<ILogger<ConsoleLogWebhookSender>>()));
        _config = new Mock<ConfigService>(settings, Mock.Of<ILogger<ConfigService>>());
        _config.Setup(c => c.ReadBasicConfig()).Returns(new ServerConfig { ServerName = "Old name" });
        _restart = new SmartRestartService(_server.Object, players, tracks, _config.Object, webhook,
            Mock.Of<ILogger<SmartRestartService>>());
        _storage = new EventStorageService(settings, Mock.Of<ILogger<EventStorageService>>());
        var logger = new Mock<ILogger<EventsController>>();
        logger.Setup(l => l.Log(It.IsAny<LogLevel>(), It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(), It.IsAny<Exception?>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback(new InvocationAction(call => {
                if (call.Arguments[2].ToString()!.StartsWith("Manual event activation workflow completed"))
                    _activated.TrySetResult();
            }));
        _controller = new EventsController(_storage, _restart, webhook, logger.Object);
        Assert.True(_storage.SaveSchedule(new EventSchedule { Events = [new Event {
            Id = 1, Name = "Future event", StartTime = DateTime.UtcNow.AddDays(1),
            ServerConfig = new EventServerConfig { ServerName = "New name" },
            CollectionName = "Event tracks", Tracks = [new EventLoopTrack { Track = "urban09_1" }]
        }] }));
    }

    [Fact]
    public async Task ManualActivationWritesSettingsAndTracksBeforeRestartAndMarksActiveAfterSuccess()
    {
        var writes = new List<string>();
        _config.Setup(c => c.WriteBasicConfig(It.IsAny<ServerConfig>()))
            .Callback<ServerConfig>(c => { Assert.Equal("New name", c.ServerName); writes.Add("settings"); });
        _config.Setup(c => c.WriteEventLoopTracks("Event tracks", It.IsAny<List<EventLoopTrack>>()))
            .Callback<string, List<EventLoopTrack>>((_, tracks) => { Assert.Equal("urban09_1", Assert.Single(tracks).Track); writes.Add("tracks"); });
        var restarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _server.Setup(s => s.RestartServerViaCommandAsync()).Returns(() => {
            Assert.Equal(new[] { "settings", "tracks" }, writes);
            restarted.SetResult();
            return Task.FromResult((true, "Restarted"));
        });

        Assert.IsType<OkObjectResult>(await _controller.ActivateEvent(1));
        await restarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await _activated.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(_storage.LoadSchedule().GetEventById(1)!.IsActive);
    }

    [Fact]
    public async Task BusyRestartDoesNotOverwriteConfiguration()
    {
        var release = new TaskCompletionSource<(bool, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        _server.Setup(s => s.RestartServerViaCommandAsync()).Returns(release.Task);
        try
        {
            Assert.IsType<OkObjectResult>(await _controller.ActivateEvent(1));
            Assert.IsType<ConflictObjectResult>(await _controller.ActivateEvent(1));
            _config.Verify(c => c.WriteBasicConfig(It.IsAny<ServerConfig>()), Times.Once);
            _config.Verify(c => c.WriteEventLoopTracks(It.IsAny<string>(), It.IsAny<List<EventLoopTrack>>()), Times.Once);
        }
        finally { release.TrySetResult((false, "Test finished")); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfigurationFailureDoesNotRestartOrMarkActive(bool failTracks)
    {
        if (failTracks)
            _config.Setup(c => c.WriteEventLoopTracks(It.IsAny<string>(), It.IsAny<List<EventLoopTrack>>()))
                .Throws(new IOException("Track write failed"));
        else
            _config.Setup(c => c.WriteBasicConfig(It.IsAny<ServerConfig>())).Throws(new IOException("Settings write failed"));

        var result = Assert.IsType<ObjectResult>(await _controller.ActivateEvent(1));
        Assert.Equal(500, result.StatusCode);
        Assert.Equal(SmartRestartState.Idle, _restart.GetState());
        Assert.False(_storage.LoadSchedule().GetEventById(1)!.IsActive);
        _server.Verify(s => s.RestartServerViaCommandAsync(), Times.Never);
    }

    [Fact]
    public async Task SchedulerConfigurationExceptionReleasesProcessingAndAllowsAnotherEvent()
    {
        var settings = new ConfigurationBuilder().Build();
        var scheduler = new EventSchedulerService(_storage, _restart,
            new RecurringEventService(Mock.Of<ILogger<RecurringEventService>>()), _config.Object,
            new WreckfestWebWebhookService(Mock.Of<ILogger<WreckfestWebWebhookService>>(), settings, new HttpClient()),
            Mock.Of<ILogger<EventSchedulerService>>());
        using (scheduler)
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var processing = typeof(EventSchedulerService).GetField("_isProcessingEvent", flags)!;
            processing.SetValue(scheduler, true);
            _config.Setup(c => c.WriteBasicConfig(It.IsAny<ServerConfig>())).Throws(new IOException("Write failed"));
            typeof(EventSchedulerService).GetMethod("ActivateEvent", flags)!
                .Invoke(scheduler, [_storage.LoadSchedule().GetEventById(1)]);

            Assert.False((bool)processing.GetValue(scheduler)!);
            Assert.Equal(SmartRestartState.Idle, _restart.GetState());
            _server.Verify(s => s.RestartServerViaCommandAsync(), Times.Never);
            Assert.False(_storage.LoadSchedule().GetEventById(1)!.IsActive);

            _config.Setup(c => c.WriteBasicConfig(It.IsAny<ServerConfig>()));
            var restarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _server.Setup(s => s.RestartServerViaCommandAsync()).Returns(() => {
                restarted.TrySetResult();
                return Task.FromResult((false, "Test finished"));
            });
            Assert.True(_storage.ReplaceSchedule([new Event { Id = 2, StartTime = DateTime.UtcNow.AddMinutes(-1) }]));
            typeof(EventSchedulerService).GetMethod("CheckForDueEvents", flags)!.Invoke(scheduler, [null]);
            await restarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }

    public void Dispose() => File.Delete(_path);
}
