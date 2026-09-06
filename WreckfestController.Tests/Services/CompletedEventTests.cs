using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using WreckfestController.Models;
using WreckfestController.Services;
using Xunit;

namespace WreckfestController.Tests.Services;

public class CompletedEventTests
{
    [Fact]
    public void SwitchingAndDeactivatingEventsDoesNotRequeueCompletedOccurrences()
    {
        var schedule = new EventSchedule { Events = [
            new Event { Id = 1, StartTime = DateTime.UtcNow.AddHours(-2) },
            new Event { Id = 2, StartTime = DateTime.UtcNow.AddHours(-1) }] };
        Assert.True(schedule.ActivateEvent(1));
        Assert.True(schedule.ActivateEvent(2));
        schedule.DeactivateAllEvents();
        Assert.Empty(schedule.GetDueEvents());
        Assert.Null(schedule.GetNextEvent());
        Assert.Equal(0, schedule.GetScheduleSummary().Due);
        Assert.True(schedule.ActivateEvent(1)); // Explicit manual reactivation remains possible.
    }

    [Fact]
    public void LegacyActiveEventIsRememberedWhenAnotherEventActivates()
    {
        var schedule = new EventSchedule { Events = [
            new Event { Id = 1, StartTime = DateTime.UtcNow.AddHours(-2), IsActive = true },
            new Event { Id = 2, StartTime = DateTime.UtcNow.AddHours(-1) }] };
        schedule.ActivateEvent(2);
        Assert.Empty(schedule.GetDueEvents());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompletionSurvivesStorageAndScheduleReplacementButNewOccurrenceCanRun(bool useLocalTime)
    {
        var path = Path.Combine(Path.GetTempPath(), $"schedule-{Guid.NewGuid()}.json");
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["EventSchedulePath"] = path }).Build();
        var storage = new EventStorageService(config, NullLogger<EventStorageService>.Instance);
        try
        {
            var start = DateTime.UtcNow.AddHours(-1);
            var schedule = new EventSchedule { Events = [new Event { Id = 1, StartTime = start }] };
            schedule.ActivateEvent(1);
            schedule.DeactivateAllEvents();
            Assert.True(storage.SaveSchedule(schedule));
            var refreshedStart = useLocalTime ? start.ToLocalTime() : start;
            Assert.True(storage.ReplaceSchedule([new Event { Id = 1, StartTime = refreshedStart }]));
            var loaded = storage.LoadSchedule();
            Assert.Empty(loaded.GetDueEvents());
            loaded.AddOrUpdateEvent(new Event { Id = 1, StartTime = refreshedStart });
            Assert.Empty(loaded.GetDueEvents());
            loaded.UpdateEventStartTime(1, start.AddMinutes(30));
            Assert.Single(loaded.GetDueEvents());
            loaded.ActivateEvent(1);
            loaded.DeactivateAllEvents();
            Assert.Empty(loaded.GetDueEvents());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void EarlyRecurringActivationAdvancesBeyondTheCompletedOccurrence()
    {
        var path = Path.Combine(Path.GetTempPath(), $"schedule-{Guid.NewGuid()}.json");
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["EventSchedulePath"] = path }).Build();
        var storage = new EventStorageService(config, NullLogger<EventStorageService>.Instance);
        try
        {
            var start = DateTime.UtcNow.AddMinutes(4);
            var evt = new Event { Id = 1, StartTime = start,
                Repeat = new RepeatSchedule { Frequency = "daily", Time = start.ToString("HH:mm") } };
            var schedule = new EventSchedule { Events = [evt] };
            schedule.ActivateEvent(1);
            var recurring = new RecurringEventService(NullLogger<RecurringEventService>.Instance);
            Assert.True(recurring.RescheduleEvent(evt, storage, schedule));
            Assert.True(evt.StartTime > start);
            Assert.Empty(schedule.GetDueEvents());
            Assert.Single(schedule.GetUpcomingEvents());
        }
        finally { File.Delete(path); }
    }
}
