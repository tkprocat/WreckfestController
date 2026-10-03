using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WreckfestController.Data;
using WreckfestController.Services.Hook;
using WreckfestController.Services.Publishing;
using WreckfestController.Services.Races;
using WreckfestController.Services.ServerControl;
using WreckfestController.Services.Tracking;
using WreckfestController.Tests.Services.Cups;
using WreckfestController.Tests.Services.Hook;
using Xunit;

namespace WreckfestController.Tests.Services.Races;

public sealed class RaceResultRecorderTests : IDisposable
{
    private readonly CupTestDatabase _database = new();
    private readonly Mock<ILogger<RaceResultRecorder>> _logger = new();
    private readonly ServerManager _serverManager;
    private readonly TrackChangeTracker _tracks;

    public RaceResultRecorderTests()
    {
        var events = new Mock<IServerEventPublisher>().Object;
        _tracks = new TrackChangeTracker(NullLogger<TrackChangeTracker>.Instance, events);
        _serverManager = new ServerManager(
            new Mock<IConfiguration>().Object,
            TestSettings.Server(),
            TestSettings.SteamCmd(),
            NullLogger<ServerManager>.Instance,
            new PlayerTracker(NullLogger<PlayerTracker>.Instance, events),
            _tracks,
            new ServerInfoTracker(NullLogger<ServerInfoTracker>.Instance),
            events);
    }

    public void Dispose() => _database.Dispose();

    private RaceResultRecorder Recorder(IDbContextFactory<ControllerDbContext> contexts) =>
        new(_serverManager, new RaceResultStore(contexts), _database.Store, _tracks, _logger.Object)
        {
            RetryDelays = [TimeSpan.Zero, TimeSpan.Zero],
        };

    private void ReportRace() =>
        Assert.True(_serverManager.TryProcessHookRaceRecord(
            HookRaceRecordTests.LiveRecord, _serverManager.CurrentAttachmentGeneration));

    private async Task<int> RaceCountAsync()
    {
        await using var db = await _database.Contexts.CreateDbContextAsync();
        return await db.Races.CountAsync();
    }

    private void VerifyErrorLogged(string fragment, Times times) =>
        _logger.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains(fragment)),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            times);

    [Fact]
    public async Task A_save_that_fails_once_is_retried()
    {
        using var recorder = Recorder(new FailingFactory(_database.Contexts, failures: 1));
        await recorder.StartAsync(CancellationToken.None);

        ReportRace();
        await recorder.FlushAsync();

        Assert.Equal(1, await RaceCountAsync());
        VerifyErrorLogged("could not be saved", Times.Never());
    }

    [Fact]
    public async Task A_save_that_keeps_failing_is_logged_after_the_retries()
    {
        using var recorder = Recorder(new FailingFactory(_database.Contexts, failures: 10));
        await recorder.StartAsync(CancellationToken.None);

        ReportRace();
        await recorder.FlushAsync();

        Assert.Equal(0, await RaceCountAsync());
        VerifyErrorLogged("could not be saved", Times.Once());
    }

    // The drop modes of a bounded channel report success while discarding, which is
    // how a full queue once lost races without a word. A full queue must say so.
    [Fact]
    public async Task A_full_queue_logs_every_race_it_cannot_take()
    {
        var gate = new BlockingFactory(_database.Contexts);
        using var recorder = Recorder(gate);
        await recorder.StartAsync(CancellationToken.None);

        try
        {
            ReportRace();
            await gate.Entered.Task;

            // One race is held in the save; the queue takes 64 more, and refuses two.
            for (var i = 0; i < 66; i++)
            {
                ReportRace();
            }

            VerifyErrorLogged("the results queue is full", Times.Exactly(2));
        }
        finally
        {
            gate.Release();
        }

        await recorder.FlushAsync();
        Assert.Equal(65, await RaceCountAsync());
    }

    private async Task<int?> SavedCupIdAsync()
    {
        await using var db = await _database.Contexts.CreateDbContextAsync();
        return await db.Races.Select(r => r.CupId).SingleAsync();
    }

    private async Task<int> CreateCupAsync(string name) =>
        (await _database.CreateAsync(CupTestDatabase.Definition(name, new DateTime(2026, 10, 9, 19, 0, 0, DateTimeKind.Utc)))).Id;

    // The race belongs to the cup it ended under. An admin switching cups while it
    // waits in the queue must neither move it to the new cup nor drop the link.
    [Fact]
    public async Task Credits_the_cup_active_when_the_race_ended_even_if_it_changes_before_the_save()
    {
        var cupA = await CreateCupAsync("Cup A");
        var cupB = await CreateCupAsync("Cup B");
        await _database.Store.SetActiveAsync(cupA);

        var gate = new BlockingFactory(_database.Contexts);
        using var recorder = Recorder(gate);
        await recorder.StartAsync(CancellationToken.None);
        try
        {
            ReportRace();
            await gate.Entered.Task;
            await _database.Store.SetActiveAsync(cupB);
        }
        finally
        {
            gate.Release();
        }

        await recorder.FlushAsync();
        Assert.Equal(cupA, await SavedCupIdAsync());
    }

    [Fact]
    public async Task Does_not_credit_a_cup_activated_after_the_race_ended()
    {
        var cup = await CreateCupAsync("Too Late");
        _database.Clock.Now = HookRaceRecord.TryParse(HookRaceRecordTests.LiveRecord)!.EndedAt.AddSeconds(5);
        await _database.Store.SetActiveAsync(cup);

        using var recorder = Recorder(_database.Contexts);
        await recorder.StartAsync(CancellationToken.None);
        ReportRace();
        await recorder.FlushAsync();

        Assert.Null(await SavedCupIdAsync());
    }

    // The cache is what races are credited from, so it must come from the database at
    // start, not wait for the next activation.
    [Fact]
    public async Task Credits_a_cup_that_was_active_before_the_recorder_started()
    {
        var cup = await CreateCupAsync("Already Running");
        await _database.Store.SetActiveAsync(cup);
        var freshStore = new WreckfestController.Services.Cups.CupStore(_database.Contexts, _database.Clock);

        using var recorder = new RaceResultRecorder(
            _serverManager, new RaceResultStore(_database.Contexts), freshStore, _tracks, _logger.Object);
        await recorder.StartAsync(CancellationToken.None);
        ReportRace();
        await recorder.FlushAsync();

        Assert.Equal(cup, await SavedCupIdAsync());
    }

    [Fact]
    public async Task A_race_reported_after_shutdown_is_logged_as_such()
    {
        using var recorder = Recorder(_database.Contexts);
        await recorder.StartAsync(CancellationToken.None);
        await recorder.FlushAsync();

        // As if the pipe thread had taken the handler before the recorder unsubscribed.
        recorder.GetType()
            .GetMethod("OnRaceFinished", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(recorder, [HookRaceRecord.TryParse(HookRaceRecordTests.LiveRecord)!]);

        VerifyErrorLogged("the recorder is shutting down", Times.Once());
    }

    private sealed class FailingFactory(IDbContextFactory<ControllerDbContext> inner, int failures)
        : IDbContextFactory<ControllerDbContext>
    {
        private int _remaining = failures;

        public ControllerDbContext CreateDbContext() =>
            Interlocked.Decrement(ref _remaining) >= 0
                ? throw new InvalidOperationException("database is locked")
                : inner.CreateDbContext();
    }

    /// <summary>Holds the first save until released, so the queue behind it can fill.</summary>
    private sealed class BlockingFactory(IDbContextFactory<ControllerDbContext> inner)
        : IDbContextFactory<ControllerDbContext>
    {
        private readonly ManualResetEventSlim _released = new();
        private int _calls;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _released.Set();

        public ControllerDbContext CreateDbContext()
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                Entered.SetResult();
                // No timeout: an early release would let the queue drain mid-test.
                // Every test that blocks releases in a finally.
                _released.Wait();
            }

            return inner.CreateDbContext();
        }
    }
}
