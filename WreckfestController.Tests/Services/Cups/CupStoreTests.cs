using WreckfestController.Data.Collections;
using WreckfestController.Data.Cups;
using WreckfestController.Models;
using WreckfestController.Services.Cups;

namespace WreckfestController.Tests.Services.Cups;

/// <summary>
/// The store's writes touch one cup each, so concurrent writers keep each other's
/// changes: an admin's edit and the scheduler's bookkeeping never overwrite each other.
/// </summary>
public sealed class CupStoreTests : IDisposable
{
    private readonly CupTestDatabase _db = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DateTime InOneHour => _db.Clock.UtcNow.AddHours(1);

    private static RepeatSchedule Daily(string time) => new() { Frequency = "daily", Time = time };

    [Fact]
    public async Task Create_WaitsForTheStartTime()
    {
        var cup = await _db.CreateAsync(CupTestDatabase.Definition("Race night", InOneHour));

        Assert.Equal(1, cup.Version);
        Assert.Equal(InOneHour, cup.NextOccurrence);
        Assert.Equal(DateTimeKind.Utc, cup.NextOccurrence!.Value.Kind);
        Assert.False(cup.IsActive);
    }

    [Fact]
    public async Task TwoUpdatesFromTheSameVersion_TheSecondConflicts()
    {
        var cup = await _db.CreateAsync(CupTestDatabase.Definition("Race night", InOneHour));

        var (first, saved) = await _db.Store.UpdateAsync(cup.Id, CupTestDatabase.Definition("First", InOneHour), 1, Ct);
        var (second, current) = await _db.Store.UpdateAsync(cup.Id, CupTestDatabase.Definition("Second", InOneHour), 1, Ct);

        Assert.Equal(CupWriteStatus.Saved, first);
        Assert.Equal(2, saved!.Version);
        Assert.Equal(CupWriteStatus.Conflict, second);
        Assert.Equal("First", current!.Name);
        Assert.Equal("First", (await _db.ReloadAsync(cup.Id)).Name);
    }

    [Fact]
    public async Task SchedulerBookkeeping_AndAnAdminEdit_BothSurvive()
    {
        var start = _db.Clock.UtcNow.AddMinutes(2);
        var cup = await _db.CreateAsync(CupTestDatabase.Definition("Daily", start, Daily("12:02")));

        // The scheduler marks the occurrence done while an editor holds version 1.
        Assert.True(await _db.Store.SetActiveAsync(cup.Id, Ct));
        Assert.True(await _db.Store.AdvanceAsync(cup.Id, start, OccurrenceOutcome.Activated, Ct));

        var (status, saved) = await _db.Store.UpdateAsync(
            cup.Id, CupTestDatabase.Definition("Renamed", start, Daily("12:02")), expectedVersion: 1, Ct);

        Assert.Equal(CupWriteStatus.Saved, status);
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
        var cup = await _db.CreateAsync(CupTestDatabase.Definition("Daily", start, Daily("12:02")));
        await _db.Store.AdvanceAsync(cup.Id, start, OccurrenceOutcome.Activated, Ct);

        var (status, saved) = await _db.Store.UpdateAsync(cup.Id, CupTestDatabase.Definition("Daily", start), 1, Ct);

        Assert.Equal(CupWriteStatus.Saved, status);
        Assert.Null(saved!.NextOccurrence);
    }

    [Fact]
    public async Task ChangingTheRepeat_AfterItsOccurrenceRan_MovesToTheNewRulesNextOccurrence()
    {
        var start = _db.Clock.UtcNow.AddMinutes(2);
        var cup = await _db.CreateAsync(CupTestDatabase.Definition("Daily", start, Daily("12:02")));
        await _db.Store.AdvanceAsync(cup.Id, start, OccurrenceOutcome.Activated, Ct);

        var (_, saved) = await _db.Store.UpdateAsync(cup.Id, CupTestDatabase.Definition("Daily", start, Daily("18:00")), 1, Ct);

        Assert.Equal(_db.Clock.UtcNow.Date.AddHours(18), saved!.NextOccurrence);
    }

    [Fact]
    public async Task MovingACancelledOneOffToAnEarlierFutureTime_RunsIt()
    {
        // 12:05 was cancelled at 12:01; 12:04 is a different occurrence, still ahead.
        var cancelled = _db.Clock.UtcNow.AddMinutes(5);
        var cup = await _db.CreateAsync(CupTestDatabase.Definition("Race night", cancelled));
        await _db.Store.AdvanceAsync(cup.Id, cancelled, OccurrenceOutcome.Cancelled, Ct);

        var earlier = _db.Clock.UtcNow.AddMinutes(4);
        var (_, saved) = await _db.Store.UpdateAsync(cup.Id, CupTestDatabase.Definition("Race night", earlier), 1, Ct);

        Assert.Equal(earlier, saved!.NextOccurrence);
    }

    [Fact]
    public async Task RemovingTheRepeat_DoesNotBringBackAnEarlierOccurrenceAlreadyDealtWith()
    {
        // Starts 12:04, repeats daily at 12:10. Both of today's occurrences were
        // cancelled; the admin then removes the repeat, leaving the start as it was.
        var start = _db.Clock.UtcNow.AddMinutes(4);
        var repeatAt = _db.Clock.UtcNow.AddMinutes(10);
        var cup = await _db.CreateAsync(CupTestDatabase.Definition("Daily", start, Daily("12:10")));
        await _db.Store.AdvanceAsync(cup.Id, start, OccurrenceOutcome.Cancelled, Ct);
        Assert.Equal(repeatAt, (await _db.ReloadAsync(cup.Id)).NextOccurrence);
        await _db.Store.AdvanceAsync(cup.Id, repeatAt, OccurrenceOutcome.Cancelled, Ct);
        _db.Clock.Now = _db.Clock.Now.AddMinutes(6); // 12:10 was dealt with in its lead-in.

        var (_, saved) = await _db.Store.UpdateAsync(cup.Id, CupTestDatabase.Definition("Daily", start), 1, Ct);

        Assert.Null(saved!.NextOccurrence);
    }

    [Fact]
    public async Task ChangingTheRepeatToAnEarlierFutureTime_RunsIt()
    {
        // Daily 12:05 since yesterday; today's was cancelled at 12:01 in its lead-in.
        // Moving the repeat to 12:04 gives a new occurrence, still ahead.
        var yesterday = _db.Clock.UtcNow.AddDays(-1);
        var cup = await _db.CreateAsync(CupTestDatabase.Definition("Daily", yesterday, Daily("12:05")));
        var cancelled = _db.Clock.UtcNow.AddMinutes(5);
        _db.Clock.Now = _db.Clock.Now.AddMinutes(1);
        await _db.Store.AdvanceAsync(cup.Id, cancelled, OccurrenceOutcome.Cancelled, Ct);

        var (_, saved) = await _db.Store.UpdateAsync(cup.Id, CupTestDatabase.Definition("Daily", yesterday, Daily("12:04")), 1, Ct);

        Assert.Equal(_db.Clock.UtcNow.Date.AddHours(12).AddMinutes(4), saved!.NextOccurrence);
    }

    [Fact]
    public async Task ALaterEdit_KeepsAnAcceptedEarlierReschedule()
    {
        var cancelled = _db.Clock.UtcNow.AddMinutes(5);
        var cup = await _db.CreateAsync(CupTestDatabase.Definition("Race night", cancelled));
        await _db.Store.AdvanceAsync(cup.Id, cancelled, OccurrenceOutcome.Cancelled, Ct);
        var earlier = _db.Clock.UtcNow.AddMinutes(4);
        await _db.Store.UpdateAsync(cup.Id, CupTestDatabase.Definition("Race night", earlier), 1, Ct);

        // Same UTC start, different zone: the schedule changed, the occurrence did not.
        var (_, saved) = await _db.Store.UpdateAsync(
            cup.Id, CupTestDatabase.Definition("Race night", earlier, timeZone: "Europe/Copenhagen"), 2, Ct);

        Assert.Equal(earlier, saved!.NextOccurrence);
    }

    [Fact]
    public async Task OverlappingLeadIns_DoNotBringBackACancelledOccurrence()
    {
        // Starts 12:04, repeats daily at 12:05: both cancelled while still ahead, then
        // the repeat is removed. 12:04 is earlier than the last one but was dealt with.
        var start = _db.Clock.UtcNow.AddMinutes(4);
        var cup = await _db.CreateAsync(CupTestDatabase.Definition("Daily", start, Daily("12:05")));
        await _db.Store.AdvanceAsync(cup.Id, start, OccurrenceOutcome.Cancelled, Ct);
        _db.Clock.Now = _db.Clock.Now.AddMinutes(1);
        await _db.Store.AdvanceAsync(cup.Id, start.AddMinutes(1), OccurrenceOutcome.Cancelled, Ct);
        _db.Clock.Now = _db.Clock.Now.AddMinutes(1);

        var (_, saved) = await _db.Store.UpdateAsync(cup.Id, CupTestDatabase.Definition("Daily", start), 1, Ct);

        Assert.Null(saved!.NextOccurrence);
    }

    [Fact]
    public async Task ARescheduleIntoTheRecentPast_Runs()
    {
        // A cancelled 12:05 one-off moved, at 12:02, to 12:00: never scheduled before,
        // and within the scheduler's grace.
        var cancelled = _db.Clock.UtcNow.AddMinutes(5);
        var cup = await _db.CreateAsync(CupTestDatabase.Definition("Race night", cancelled));
        _db.Clock.Now = _db.Clock.Now.AddMinutes(1);
        await _db.Store.AdvanceAsync(cup.Id, cancelled, OccurrenceOutcome.Cancelled, Ct);
        _db.Clock.Now = _db.Clock.Now.AddMinutes(1);
        var noon = cancelled.AddMinutes(-5);

        var (_, saved) = await _db.Store.UpdateAsync(cup.Id, CupTestDatabase.Definition("Race night", noon), 1, Ct);

        Assert.Equal(noon, saved!.NextOccurrence);
    }

    [Fact]
    public async Task History_RecordsEachOccurrence_AndGoesWithItsCup()
    {
        var start = _db.Clock.UtcNow.AddMinutes(2);
        var cup = await _db.CreateAsync(CupTestDatabase.Definition("Daily", start, Daily("12:02")));
        await _db.Store.AdvanceAsync(cup.Id, start, OccurrenceOutcome.Activated, Ct);
        await _db.Store.AdvanceAsync(cup.Id, start.AddDays(1), OccurrenceOutcome.Missed, Ct);

        await using (var db = await _db.Contexts.CreateDbContextAsync(Ct))
        {
            var history = db.CupOccurrences.Where(o => o.CupId == cup.Id).OrderBy(o => o.Occurrence).ToList();
            Assert.Equal([OccurrenceOutcome.Activated, OccurrenceOutcome.Missed], history.Select(o => o.Outcome));
        }

        await _db.Store.DeleteAsync(cup.Id, 1, Ct);

        await using (var db = await _db.Contexts.CreateDbContextAsync(Ct))
        {
            Assert.Empty(db.CupOccurrences);
        }
    }

    [Fact]
    public async Task AdvancingPastAReschedule_SkipsOccurrencesAlreadyDealtWith()
    {
        // Daily 12:05; today's 12:05 cancelled at 12:01. At 12:02 the start moves to 12:04,
        // which is then cancelled too. The next occurrence must be tomorrow, not 12:05.
        var cancelled = _db.Clock.UtcNow.AddMinutes(5);
        var cup = await _db.CreateAsync(CupTestDatabase.Definition("Daily", cancelled, Daily("12:05")));
        _db.Clock.Now = _db.Clock.Now.AddMinutes(1);
        await _db.Store.AdvanceAsync(cup.Id, cancelled, OccurrenceOutcome.Cancelled, Ct);
        _db.Clock.Now = _db.Clock.Now.AddMinutes(1);
        var moved = cancelled.AddMinutes(-1);
        var (_, rescheduled) = await _db.Store.UpdateAsync(cup.Id, CupTestDatabase.Definition("Daily", moved, Daily("12:05")), 1, Ct);
        Assert.Equal(moved, rescheduled!.NextOccurrence);

        Assert.True(await _db.Store.AdvanceAsync(cup.Id, moved, OccurrenceOutcome.Cancelled, Ct));

        Assert.Equal(cancelled.AddDays(1), (await _db.ReloadAsync(cup.Id)).NextOccurrence);
    }

    [Fact]
    public async Task Advance_AfterAnAdminRescheduled_LeavesTheNewTime()
    {
        var start = _db.Clock.UtcNow.AddMinutes(2);
        var cup = await _db.CreateAsync(CupTestDatabase.Definition("Race night", start));
        var moved = start.AddDays(1);
        await _db.Store.UpdateAsync(cup.Id, CupTestDatabase.Definition("Race night", moved), 1, Ct);

        Assert.False(await _db.Store.AdvanceAsync(cup.Id, start, OccurrenceOutcome.Activated, Ct));

        var current = await _db.ReloadAsync(cup.Id);
        Assert.Equal(moved, current.NextOccurrence);
        Assert.Null(current.LastOccurrence);
    }

    [Fact]
    public async Task Advance_FinishesAOneOffCup()
    {
        var cup = await _db.CreateAsync(CupTestDatabase.Definition("Race night", InOneHour));

        Assert.True(await _db.Store.AdvanceAsync(cup.Id, InOneHour, OccurrenceOutcome.Missed, Ct));

        var current = await _db.ReloadAsync(cup.Id);
        Assert.Null(current.NextOccurrence);
        Assert.Equal(InOneHour, current.LastOccurrence);
        Assert.Equal(OccurrenceOutcome.Missed, current.LastOutcome);
        Assert.Equal(1, current.Version);
    }

    [Fact]
    public async Task Update_OfTheRepeat_RecomputesTheNextOccurrence()
    {
        var past = _db.Clock.UtcNow.AddDays(-7);
        var cup = await _db.CreateAsync(CupTestDatabase.Definition("Daily", past, Daily("20:00")));
        Assert.Equal(_db.Clock.UtcNow.Date.AddHours(20), cup.NextOccurrence);

        var (_, saved) = await _db.Store.UpdateAsync(cup.Id, CupTestDatabase.Definition("Daily", past, Daily("09:00")), 1, Ct);

        Assert.Equal(_db.Clock.UtcNow.Date.AddDays(1).AddHours(9), saved!.NextOccurrence);
    }

    [Fact]
    public async Task ConcurrentActivations_LeaveExactlyOneActiveCup()
    {
        var ids = new List<int>();
        for (var i = 0; i < 8; i++)
        {
            ids.Add((await _db.CreateAsync(CupTestDatabase.Definition($"Cup {i}", InOneHour))).Id);
        }

        for (var round = 0; round < 5; round++)
        {
            var results = await Task.WhenAll(ids.Select(id => Task.Run(() => _db.Store.SetActiveAsync(id, Ct), Ct)));

            Assert.All(results, Assert.True);
            Assert.Single(await _db.Store.ListAsync(Ct), e => e.IsActive);
        }
    }

    [Fact]
    public async Task ActivatingAMissingCup_KeepsTheCurrentOne()
    {
        var cup = await _db.CreateAsync(CupTestDatabase.Definition("Race night", InOneHour));
        await _db.Store.SetActiveAsync(cup.Id, Ct);

        Assert.False(await _db.Store.SetActiveAsync(cup.Id + 100, Ct));

        Assert.True((await _db.ReloadAsync(cup.Id)).IsActive);
    }

    [Fact]
    public async Task Delete_NeedsTheCurrentVersion()
    {
        var cup = await _db.CreateAsync(CupTestDatabase.Definition("Race night", InOneHour));
        await _db.Store.UpdateAsync(cup.Id, CupTestDatabase.Definition("Renamed", InOneHour), 1, Ct);

        var (stale, current) = await _db.Store.DeleteAsync(cup.Id, 1, Ct);
        var (fresh, _) = await _db.Store.DeleteAsync(cup.Id, 2, Ct);
        var (gone, _) = await _db.Store.DeleteAsync(cup.Id, 2, Ct);

        Assert.Equal(CupWriteStatus.Conflict, stale);
        Assert.Equal("Renamed", current!.Name);
        Assert.Equal(CupWriteStatus.Saved, fresh);
        Assert.Equal(CupWriteStatus.NotFound, gone);
    }

    [Fact]
    public async Task ALinkedCup_DeploysItsCollectionAsItIsAtActivation()
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

        var cup = await _db.CreateAsync(CupTestDatabase.Definition("Race night", InOneHour, collectionId: collectionId));
        Assert.Equal("speedway2_figure_8", Assert.Single(cup.Tracks).Track);

        await using (var db = await _db.Contexts.CreateDbContextAsync(Ct))
        {
            db.TrackCollectionEntries.Add(new TrackCollectionEntry { CollectionId = collectionId, Position = 1, TrackId = "urban09_1" });
            await db.SaveChangesAsync(Ct);
        }

        var deployed = CupStore.ToRestartEvent(await _db.ReloadAsync(cup.Id));
        Assert.Equal("Ovals", deployed.CollectionName);
        Assert.Equal(["speedway2_figure_8", "urban09_1"], deployed.Tracks.Select(t => t.Track));
    }

    [Fact]
    public async Task LinkingAMissingCollection_IsRefused()
    {
        var (status, cup) = await _db.Store.CreateAsync(
            CupTestDatabase.Definition("Race night", InOneHour, collectionId: 999), createdById: null, Ct);

        Assert.Equal(CupWriteStatus.UnknownCollection, status);
        Assert.Null(cup);
        Assert.Empty(await _db.Store.ListAsync(Ct));
    }

    public void Dispose() => _db.Dispose();
}
