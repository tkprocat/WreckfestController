using WreckfestController.Data.Collections;
using WreckfestController.Data.Events;
using WreckfestController.Models;
using WreckfestController.Services;

namespace WreckfestController.Tests.Services;

/// <summary>
/// The store's writes touch one event each, so concurrent writers keep each other's
/// changes: an admin's edit and the scheduler's bookkeeping never overwrite each other.
/// </summary>
public sealed class EventStoreTests : IDisposable
{
    private readonly EventTestDatabase _db = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DateTime InOneHour => _db.Clock.UtcNow.AddHours(1);

    private static RepeatSchedule Daily(string time) => new() { Frequency = "daily", Time = time };

    [Fact]
    public async Task Create_WaitsForTheStartTime()
    {
        var evt = await _db.CreateAsync(EventTestDatabase.Definition("Race night", InOneHour));

        Assert.Equal(1, evt.Version);
        Assert.Equal(InOneHour, evt.NextOccurrence);
        Assert.Equal(DateTimeKind.Utc, evt.NextOccurrence!.Value.Kind);
        Assert.False(evt.IsActive);
    }

    [Fact]
    public async Task TwoUpdatesFromTheSameVersion_TheSecondConflicts()
    {
        var evt = await _db.CreateAsync(EventTestDatabase.Definition("Race night", InOneHour));

        var (first, saved) = await _db.Store.UpdateAsync(evt.Id, EventTestDatabase.Definition("First", InOneHour), 1, Ct);
        var (second, current) = await _db.Store.UpdateAsync(evt.Id, EventTestDatabase.Definition("Second", InOneHour), 1, Ct);

        Assert.Equal(EventWriteStatus.Saved, first);
        Assert.Equal(2, saved!.Version);
        Assert.Equal(EventWriteStatus.Conflict, second);
        Assert.Equal("First", current!.Name);
        Assert.Equal("First", (await _db.ReloadAsync(evt.Id)).Name);
    }

    [Fact]
    public async Task SchedulerBookkeeping_AndAnAdminEdit_BothSurvive()
    {
        var start = _db.Clock.UtcNow.AddMinutes(2);
        var evt = await _db.CreateAsync(EventTestDatabase.Definition("Daily", start, Daily("12:02")));

        // The scheduler marks the occurrence done while an editor holds version 1.
        Assert.True(await _db.Store.SetActiveAsync(evt.Id, Ct));
        Assert.True(await _db.Store.AdvanceAsync(evt.Id, start, OccurrenceOutcome.Activated, Ct));

        var (status, saved) = await _db.Store.UpdateAsync(
            evt.Id, EventTestDatabase.Definition("Renamed", start, Daily("12:02")), expectedVersion: 1, Ct);

        Assert.Equal(EventWriteStatus.Saved, status);
        Assert.Equal("Renamed", saved!.Name);
        Assert.True(saved.IsActive);
        Assert.Equal(start, saved.LastOccurrence);
        Assert.Equal(start.AddDays(1), saved.NextOccurrence);
    }

    [Fact]
    public async Task RemovingTheRepeat_AfterItsOccurrenceRan_DoesNotRunItAgain()
    {
        // The scheduler ran today's occurrence during the lead-in and moved on to
        // tomorrow; the admin, holding version 1, then makes it a one-off.
        var start = _db.Clock.UtcNow.AddMinutes(2);
        var evt = await _db.CreateAsync(EventTestDatabase.Definition("Daily", start, Daily("12:02")));
        await _db.Store.AdvanceAsync(evt.Id, start, OccurrenceOutcome.Activated, Ct);

        var (status, saved) = await _db.Store.UpdateAsync(evt.Id, EventTestDatabase.Definition("Daily", start), 1, Ct);

        Assert.Equal(EventWriteStatus.Saved, status);
        Assert.Null(saved!.NextOccurrence);
    }

    [Fact]
    public async Task ChangingTheRepeat_AfterItsOccurrenceRan_MovesToTheNewRulesNextOccurrence()
    {
        var start = _db.Clock.UtcNow.AddMinutes(2);
        var evt = await _db.CreateAsync(EventTestDatabase.Definition("Daily", start, Daily("12:02")));
        await _db.Store.AdvanceAsync(evt.Id, start, OccurrenceOutcome.Activated, Ct);

        var (_, saved) = await _db.Store.UpdateAsync(evt.Id, EventTestDatabase.Definition("Daily", start, Daily("18:00")), 1, Ct);

        Assert.Equal(_db.Clock.UtcNow.Date.AddHours(18), saved!.NextOccurrence);
    }

    [Fact]
    public async Task MovingACancelledOneOffToAnEarlierFutureTime_RunsIt()
    {
        // 12:05 was cancelled at 12:01; 12:04 is a different occurrence, still ahead.
        var cancelled = _db.Clock.UtcNow.AddMinutes(5);
        var evt = await _db.CreateAsync(EventTestDatabase.Definition("Race night", cancelled));
        await _db.Store.AdvanceAsync(evt.Id, cancelled, OccurrenceOutcome.Cancelled, Ct);

        var earlier = _db.Clock.UtcNow.AddMinutes(4);
        var (_, saved) = await _db.Store.UpdateAsync(evt.Id, EventTestDatabase.Definition("Race night", earlier), 1, Ct);

        Assert.Equal(earlier, saved!.NextOccurrence);
    }

    [Fact]
    public async Task RemovingTheRepeat_DoesNotBringBackAnEarlierOccurrenceAlreadyDealtWith()
    {
        // Starts 12:04, repeats daily at 12:10. Both of today's occurrences were
        // cancelled; the admin then removes the repeat, leaving the start as it was.
        var start = _db.Clock.UtcNow.AddMinutes(4);
        var repeatAt = _db.Clock.UtcNow.AddMinutes(10);
        var evt = await _db.CreateAsync(EventTestDatabase.Definition("Daily", start, Daily("12:10")));
        await _db.Store.AdvanceAsync(evt.Id, start, OccurrenceOutcome.Cancelled, Ct);
        Assert.Equal(repeatAt, (await _db.ReloadAsync(evt.Id)).NextOccurrence);
        await _db.Store.AdvanceAsync(evt.Id, repeatAt, OccurrenceOutcome.Cancelled, Ct);

        var (_, saved) = await _db.Store.UpdateAsync(evt.Id, EventTestDatabase.Definition("Daily", start), 1, Ct);

        Assert.Null(saved!.NextOccurrence);
    }

    [Fact]
    public async Task Advance_AfterAnAdminRescheduled_LeavesTheNewTime()
    {
        var start = _db.Clock.UtcNow.AddMinutes(2);
        var evt = await _db.CreateAsync(EventTestDatabase.Definition("Race night", start));
        var moved = start.AddDays(1);
        await _db.Store.UpdateAsync(evt.Id, EventTestDatabase.Definition("Race night", moved), 1, Ct);

        Assert.False(await _db.Store.AdvanceAsync(evt.Id, start, OccurrenceOutcome.Activated, Ct));

        var current = await _db.ReloadAsync(evt.Id);
        Assert.Equal(moved, current.NextOccurrence);
        Assert.Null(current.LastOccurrence);
    }

    [Fact]
    public async Task Advance_FinishesAOneOffEvent()
    {
        var evt = await _db.CreateAsync(EventTestDatabase.Definition("Race night", InOneHour));

        Assert.True(await _db.Store.AdvanceAsync(evt.Id, InOneHour, OccurrenceOutcome.Missed, Ct));

        var current = await _db.ReloadAsync(evt.Id);
        Assert.Null(current.NextOccurrence);
        Assert.Equal(InOneHour, current.LastOccurrence);
        Assert.Equal(OccurrenceOutcome.Missed, current.LastOutcome);
        Assert.Equal(1, current.Version);
    }

    [Fact]
    public async Task Update_OfTheRepeat_RecomputesTheNextOccurrence()
    {
        var past = _db.Clock.UtcNow.AddDays(-7);
        var evt = await _db.CreateAsync(EventTestDatabase.Definition("Daily", past, Daily("20:00")));
        Assert.Equal(_db.Clock.UtcNow.Date.AddHours(20), evt.NextOccurrence);

        var (_, saved) = await _db.Store.UpdateAsync(evt.Id, EventTestDatabase.Definition("Daily", past, Daily("09:00")), 1, Ct);

        Assert.Equal(_db.Clock.UtcNow.Date.AddDays(1).AddHours(9), saved!.NextOccurrence);
    }

    [Fact]
    public async Task ConcurrentActivations_LeaveExactlyOneActiveEvent()
    {
        var ids = new List<int>();
        for (var i = 0; i < 8; i++)
        {
            ids.Add((await _db.CreateAsync(EventTestDatabase.Definition($"Event {i}", InOneHour))).Id);
        }

        for (var round = 0; round < 5; round++)
        {
            var results = await Task.WhenAll(ids.Select(id => Task.Run(() => _db.Store.SetActiveAsync(id, Ct), Ct)));

            Assert.All(results, Assert.True);
            Assert.Single(await _db.Store.ListAsync(Ct), e => e.IsActive);
        }
    }

    [Fact]
    public async Task ActivatingAMissingEvent_KeepsTheCurrentOne()
    {
        var evt = await _db.CreateAsync(EventTestDatabase.Definition("Race night", InOneHour));
        await _db.Store.SetActiveAsync(evt.Id, Ct);

        Assert.False(await _db.Store.SetActiveAsync(evt.Id + 100, Ct));

        Assert.True((await _db.ReloadAsync(evt.Id)).IsActive);
    }

    [Fact]
    public async Task Delete_NeedsTheCurrentVersion()
    {
        var evt = await _db.CreateAsync(EventTestDatabase.Definition("Race night", InOneHour));
        await _db.Store.UpdateAsync(evt.Id, EventTestDatabase.Definition("Renamed", InOneHour), 1, Ct);

        var (stale, current) = await _db.Store.DeleteAsync(evt.Id, 1, Ct);
        var (fresh, _) = await _db.Store.DeleteAsync(evt.Id, 2, Ct);
        var (gone, _) = await _db.Store.DeleteAsync(evt.Id, 2, Ct);

        Assert.Equal(EventWriteStatus.Conflict, stale);
        Assert.Equal("Renamed", current!.Name);
        Assert.Equal(EventWriteStatus.Saved, fresh);
        Assert.Equal(EventWriteStatus.NotFound, gone);
    }

    [Fact]
    public async Task ALinkedEvent_DeploysItsCollectionAsItIsAtActivation()
    {
        int collectionId;
        await using (var db = await _db.Contexts.CreateDbContextAsync(Ct))
        {
            var collection = new TrackCollection
            {
                Name = "Ovals",
                Entries = [new TrackCollectionEntry { Position = 0, TrackId = "speedway2_figure_8" }],
            };
            db.TrackCollections.Add(collection);
            await db.SaveChangesAsync(Ct);
            collectionId = collection.Id;
        }

        var evt = await _db.CreateAsync(EventTestDatabase.Definition("Race night", InOneHour, collectionId: collectionId));
        Assert.Equal("speedway2_figure_8", Assert.Single(evt.Tracks).Track);

        await using (var db = await _db.Contexts.CreateDbContextAsync(Ct))
        {
            db.TrackCollectionEntries.Add(new TrackCollectionEntry { CollectionId = collectionId, Position = 1, TrackId = "urban09_1" });
            await db.SaveChangesAsync(Ct);
        }

        var deployed = EventStore.ToRestartEvent(await _db.ReloadAsync(evt.Id));
        Assert.Equal("Ovals", deployed.CollectionName);
        Assert.Equal(["speedway2_figure_8", "urban09_1"], deployed.Tracks.Select(t => t.Track));
    }

    [Fact]
    public async Task LinkingAMissingCollection_IsRefused()
    {
        var (status, evt) = await _db.Store.CreateAsync(
            EventTestDatabase.Definition("Race night", InOneHour, collectionId: 999), createdById: null, Ct);

        Assert.Equal(EventWriteStatus.UnknownCollection, status);
        Assert.Null(evt);
        Assert.Empty(await _db.Store.ListAsync(Ct));
    }

    public void Dispose() => _db.Dispose();
}
