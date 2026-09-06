using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using WreckfestController.Models;
using WreckfestController.Services;
using Xunit;

namespace WreckfestController.Tests.Services;

public class RestartCountdownTests : IDisposable
{
    private readonly ManualClock _clock = new();
    private readonly CaptureHandler _http = new();
    private readonly List<string> _messages = [];
    private readonly SmartRestartService _restart;
    private readonly Mock<ServerManager> _server;

    public RestartCountdownTests()
    {
        var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Webhooks:Enabled"] = "true", ["Webhooks:BaseUrl"] = "http://test.invalid",
            ["Webhooks:ApiKey"] = "test-only" }).Build();
        var webhook = new WreckfestWebWebhookService(Mock.Of<ILogger<WreckfestWebWebhookService>>(), settings, new HttpClient(_http));
        var players = new PlayerTracker(Mock.Of<ILogger<PlayerTracker>>(), webhook);
        players.ProcessHookPlayerSnapshot([new Player { PlayerId = 1, Name = "Player", IsBot = false }]);
        var tracks = new TrackChangeTracker(Mock.Of<ILogger<TrackChangeTracker>>(), webhook);
        _server = new Mock<ServerManager>(settings, Mock.Of<ILogger<ServerManager>>(), players, tracks,
            new ServerInfoTracker(Mock.Of<ILogger<ServerInfoTracker>>()), webhook,
            new ConsoleLogWebhookSender(new HttpClient(_http), settings, Mock.Of<ILogger<ConsoleLogWebhookSender>>()));
        _server.Setup(s => s.SendCommandAsync(It.IsAny<string>())).Returns((string command) => {
            _messages.Add(command);
            return Task.FromResult((true, "Sent"));
        });
        _restart = new SmartRestartService(_server.Object, players, tracks,
            new ConfigService(settings, Mock.Of<ILogger<ConfigService>>()), webhook,
            Mock.Of<ILogger<SmartRestartService>>(), _clock);
        Assert.True(_restart.InitiateRestart(new Event { Id = 7, Name = "Test event" }, _ => { }));
    }

    [Fact]
    public async Task CountdownWarnsAtMinuteFourAndBecomesPendingAtMinuteFive()
    {
        var deadline = _clock.GetUtcNow().UtcDateTime.AddMinutes(5);
        for (var minute = 0; minute <= 5; minute++)
        {
            _clock.Elapsed = TimeSpan.FromMinutes(minute);
            _clock.Timer!.Fire();
            var payload = await _http.Next();
            Assert.Equal(5 - minute, payload.GetProperty("minutesRemaining").GetInt32());
            Assert.Equal(7, payload.GetProperty("eventId").GetInt32());
            Assert.Equal(deadline, payload.GetProperty("scheduledRestartTime").GetDateTime());
            Assert.Equal(minute < 5 ? SmartRestartState.Warning : SmartRestartState.Pending, _restart.GetState());
        }
        Assert.Equal(new[] {
            "/message Server will restart in 5 minutes.", "/message Server will restart in 4 minutes.",
            "/message Server will restart in 3 minutes.", "/message Server will restart in 2 minutes.",
            "/message Server will restart in 1 minute.", "/message Server will restart at the next lobby."
        }, _messages);
        _server.Verify(s => s.RestartServerViaCommandAsync(), Times.Never);
    }

    [Fact]
    public async Task DelayedAndRepeatedTicksUseElapsedTime()
    {
        _clock.Elapsed = TimeSpan.FromMinutes(4.5);
        for (var i = 0; i < 3; i++)
        {
            _clock.Timer!.Fire();
            Assert.Equal(1, (await _http.Next()).GetProperty("minutesRemaining").GetInt32());
            Assert.Equal(SmartRestartState.Warning, _restart.GetState());
        }
        _clock.Elapsed = TimeSpan.FromMinutes(5.5);
        _clock.Timer!.Fire();
        Assert.Equal(0, (await _http.Next()).GetProperty("minutesRemaining").GetInt32());
        Assert.Equal(SmartRestartState.Pending, _restart.GetState());
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(2)]
    public async Task WallClockChangesDoNotShortenOrExtendCountdown(int hours)
    {
        _clock.WallClockAdjustment = TimeSpan.FromHours(hours);
        _clock.Elapsed = TimeSpan.FromMinutes(4);
        _clock.Timer!.Fire();
        Assert.Equal(1, (await _http.Next()).GetProperty("minutesRemaining").GetInt32());
        Assert.Equal(SmartRestartState.Warning, _restart.GetState());
        _clock.Elapsed = TimeSpan.FromMinutes(5);
        _clock.Timer!.Fire();
        Assert.Equal(0, (await _http.Next()).GetProperty("minutesRemaining").GetInt32());
        Assert.Equal(SmartRestartState.Pending, _restart.GetState());
    }

    public void Dispose() => _restart.CancelRestart();

    private sealed class ManualClock : TimeProvider
    {
        public TimeSpan Elapsed { get; set; }
        public TimeSpan WallClockAdjustment { get; set; }
        public ManualTimer? Timer { get; private set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Elapsed.Ticks;
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero) + Elapsed + WallClockAdjustment;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => Timer = new ManualTimer(callback, state);
    }

    private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
    {
        private bool _disposed;
        public void Fire() { if (!_disposed) callback(state); }
        public bool Change(TimeSpan dueTime, TimeSpan period) => !_disposed;
        public void Dispose() => _disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        private readonly Channel<JsonElement> _payloads = Channel.CreateUnbounded<JsonElement>();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("server-restart-pending"))
            {
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                _payloads.Writer.TryWrite(json.RootElement.Clone());
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
        public async Task<JsonElement> Next()
            => await _payloads.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }
}
