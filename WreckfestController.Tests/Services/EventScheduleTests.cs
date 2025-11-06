using WreckfestController.Models;
using Xunit;

namespace WreckfestController.Tests.Services;

public class EventScheduleTests
{
    [Fact]
    public void GetUpcomingEvents_ReturnsOnlyFutureEvents()
    {
        // Arrange
        var schedule = new EventSchedule
        {
            Events = new List<Event>
            {
                new Event { Id = 1, Name = "Past Event", StartTime = DateTime.UtcNow.AddHours(-1), IsActive = false },
                new Event { Id = 2, Name = "Future Event 1", StartTime = DateTime.UtcNow.AddHours(1), IsActive = false },
                new Event { Id = 3, Name = "Future Event 2", StartTime = DateTime.UtcNow.AddHours(2), IsActive = false },
                new Event { Id = 4, Name = "Active Event", StartTime = DateTime.UtcNow.AddHours(-2), IsActive = true }
            }
        };

        // Act
        var upcoming = schedule.GetUpcomingEvents();

        // Assert
        Assert.Equal(2, upcoming.Count);
        Assert.Equal("Future Event 1", upcoming[0].Name);
        Assert.Equal("Future Event 2", upcoming[1].Name);
    }

    [Fact]
    public void GetUpcomingEvents_OrdersByStartTime()
    {
        // Arrange
        var schedule = new EventSchedule
        {
            Events = new List<Event>
            {
                new Event { Id = 1, Name = "Later Event", StartTime = DateTime.UtcNow.AddHours(3), IsActive = false },
                new Event { Id = 2, Name = "Earlier Event", StartTime = DateTime.UtcNow.AddHours(1), IsActive = false },
                new Event { Id = 3, Name = "Middle Event", StartTime = DateTime.UtcNow.AddHours(2), IsActive = false }
            }
        };

        // Act
        var upcoming = schedule.GetUpcomingEvents();

        // Assert
        Assert.Equal(3, upcoming.Count);
        Assert.Equal("Earlier Event", upcoming[0].Name);
        Assert.Equal("Middle Event", upcoming[1].Name);
        Assert.Equal("Later Event", upcoming[2].Name);
    }

    [Fact]
    public void GetDueEvents_ReturnsOnlyPastNonActiveEvents()
    {
        // Arrange
        var schedule = new EventSchedule
        {
            Events = new List<Event>
            {
                new Event { Id = 1, Name = "Due Event 1", StartTime = DateTime.UtcNow.AddMinutes(-30), IsActive = false },
                new Event { Id = 2, Name = "Due Event 2", StartTime = DateTime.UtcNow.AddMinutes(-10), IsActive = false },
                new Event { Id = 3, Name = "Future Event", StartTime = DateTime.UtcNow.AddHours(1), IsActive = false },
                new Event { Id = 4, Name = "Active Event", StartTime = DateTime.UtcNow.AddHours(-1), IsActive = true }
            }
        };

        // Act
        var due = schedule.GetDueEvents();

        // Assert
        Assert.Equal(2, due.Count);
        Assert.All(due, e => Assert.False(e.IsActive));
        Assert.All(due, e => Assert.True(e.StartTime <= DateTime.UtcNow));
    }

    [Fact]
    public void GetActiveEvent_ReturnsSingleActiveEvent()
    {
        // Arrange
        var schedule = new EventSchedule
        {
            Events = new List<Event>
            {
                new Event { Id = 1, Name = "Inactive Event", StartTime = DateTime.UtcNow, IsActive = false },
                new Event { Id = 2, Name = "Active Event", StartTime = DateTime.UtcNow, IsActive = true }
            }
        };

        // Act
        var active = schedule.GetActiveEvent();

        // Assert
        Assert.NotNull(active);
        Assert.Equal("Active Event", active.Name);
        Assert.True(active.IsActive);
    }

    [Fact]
    public void GetActiveEvent_ReturnsNullWhenNoneActive()
    {
        // Arrange
        var schedule = new EventSchedule
        {
            Events = new List<Event>
            {
                new Event { Id = 1, Name = "Event 1", StartTime = DateTime.UtcNow, IsActive = false },
                new Event { Id = 2, Name = "Event 2", StartTime = DateTime.UtcNow, IsActive = false }
            }
        };

        // Act
        var active = schedule.GetActiveEvent();

        // Assert
        Assert.Null(active);
    }

    [Fact]
    public void GetNextEvent_ReturnsSoonestDueEvent()
    {
        // Arrange
        var schedule = new EventSchedule
        {
            Events = new List<Event>
            {
                new Event { Id = 1, Name = "Oldest Due", StartTime = DateTime.UtcNow.AddMinutes(-60), IsActive = false },
                new Event { Id = 2, Name = "Newest Due", StartTime = DateTime.UtcNow.AddMinutes(-10), IsActive = false },
                new Event { Id = 3, Name = "Future Event", StartTime = DateTime.UtcNow.AddHours(1), IsActive = false }
            }
        };

        // Act
        var next = schedule.GetNextEvent();

        // Assert
        Assert.NotNull(next);
        Assert.Equal("Oldest Due", next.Name);
    }

    [Fact]
    public void GetEventById_ReturnsCorrectEvent()
    {
        // Arrange
        var schedule = new EventSchedule
        {
            Events = new List<Event>
            {
                new Event { Id = 1, Name = "Event 1", StartTime = DateTime.UtcNow },
                new Event { Id = 2, Name = "Event 2", StartTime = DateTime.UtcNow },
                new Event { Id = 3, Name = "Event 3", StartTime = DateTime.UtcNow }
            }
        };

        // Act
        var found = schedule.GetEventById(2);

        // Assert
        Assert.NotNull(found);
        Assert.Equal("Event 2", found.Name);
        Assert.Equal(2, found.Id);
    }

    [Fact]
    public void GetEventById_ReturnsNullWhenNotFound()
    {
        // Arrange
        var schedule = new EventSchedule
        {
            Events = new List<Event>
            {
                new Event { Id = 1, Name = "Event 1", StartTime = DateTime.UtcNow }
            }
        };

        // Act
        var found = schedule.GetEventById(999);

        // Assert
        Assert.Null(found);
    }

    [Fact]
    public void ActivateEvent_SetsEventActiveAndDeactivatesOthers()
    {
        // Arrange
        var schedule = new EventSchedule
        {
            Events = new List<Event>
            {
                new Event { Id = 1, Name = "Event 1", StartTime = DateTime.UtcNow, IsActive = true },
                new Event { Id = 2, Name = "Event 2", StartTime = DateTime.UtcNow, IsActive = false },
                new Event { Id = 3, Name = "Event 3", StartTime = DateTime.UtcNow, IsActive = false }
            }
        };

        // Act
        var result = schedule.ActivateEvent(2);

        // Assert
        Assert.True(result);
        Assert.False(schedule.Events[0].IsActive);
        Assert.True(schedule.Events[1].IsActive);
        Assert.False(schedule.Events[2].IsActive);
    }

    [Fact]
    public void ActivateEvent_ReturnsFalseWhenNotFound()
    {
        // Arrange
        var schedule = new EventSchedule
        {
            Events = new List<Event>
            {
                new Event { Id = 1, Name = "Event 1", StartTime = DateTime.UtcNow, IsActive = false }
            }
        };

        // Act
        var result = schedule.ActivateEvent(999);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void DeactivateAllEvents_SetsAllToInactive()
    {
        // Arrange
        var schedule = new EventSchedule
        {
            Events = new List<Event>
            {
                new Event { Id = 1, Name = "Event 1", StartTime = DateTime.UtcNow, IsActive = true },
                new Event { Id = 2, Name = "Event 2", StartTime = DateTime.UtcNow, IsActive = true },
                new Event { Id = 3, Name = "Event 3", StartTime = DateTime.UtcNow, IsActive = false }
            }
        };

        // Act
        schedule.DeactivateAllEvents();

        // Assert
        Assert.All(schedule.Events, e => Assert.False(e.IsActive));
    }

    [Fact]
    public void UpdateEventStartTime_UpdatesTimeAndDeactivates()
    {
        // Arrange
        var originalTime = DateTime.UtcNow;
        var newTime = DateTime.UtcNow.AddHours(2);
        var schedule = new EventSchedule
        {
            Events = new List<Event>
            {
                new Event { Id = 1, Name = "Event 1", StartTime = originalTime, IsActive = true }
            }
        };

        // Act
        var result = schedule.UpdateEventStartTime(1, newTime);

        // Assert
        Assert.True(result);
        Assert.Equal(newTime, schedule.Events[0].StartTime);
        Assert.False(schedule.Events[0].IsActive);
    }

    [Fact]
    public void GetScheduleSummary_ReturnsCorrectCounts()
    {
        // Arrange
        var now = DateTime.UtcNow;
        var schedule = new EventSchedule
        {
            Events = new List<Event>
            {
                new Event { Id = 1, StartTime = now.AddHours(-2), IsActive = true },
                new Event { Id = 2, StartTime = now.AddHours(-1), IsActive = false },
                new Event { Id = 3, StartTime = now.AddHours(1), IsActive = false },
                new Event { Id = 4, StartTime = now.AddHours(2), IsActive = false }
            }
        };

        // Act
        var summary = schedule.GetScheduleSummary();

        // Assert
        Assert.Equal(4, summary.Total);
        Assert.Equal(1, summary.Active);
        Assert.Equal(2, summary.Upcoming);
        Assert.Equal(1, summary.Due);
    }

    [Fact]
    public void AddOrUpdateEvent_AddsNewEvent()
    {
        // Arrange
        var schedule = new EventSchedule { Events = new List<Event>() };
        var newEvent = new Event { Id = 1, Name = "New Event", StartTime = DateTime.UtcNow };

        // Act
        schedule.AddOrUpdateEvent(newEvent);

        // Assert
        Assert.Single(schedule.Events);
        Assert.Equal("New Event", schedule.Events[0].Name);
    }

    [Fact]
    public void AddOrUpdateEvent_UpdatesExistingEvent()
    {
        // Arrange
        var schedule = new EventSchedule
        {
            Events = new List<Event>
            {
                new Event { Id = 1, Name = "Old Name", StartTime = DateTime.UtcNow }
            }
        };
        var updatedEvent = new Event { Id = 1, Name = "New Name", StartTime = DateTime.UtcNow };

        // Act
        schedule.AddOrUpdateEvent(updatedEvent);

        // Assert
        Assert.Single(schedule.Events);
        Assert.Equal("New Name", schedule.Events[0].Name);
    }

    [Fact]
    public void RemoveEvent_RemovesEventSuccessfully()
    {
        // Arrange
        var schedule = new EventSchedule
        {
            Events = new List<Event>
            {
                new Event { Id = 1, Name = "Event 1", StartTime = DateTime.UtcNow },
                new Event { Id = 2, Name = "Event 2", StartTime = DateTime.UtcNow }
            }
        };

        // Act
        var result = schedule.RemoveEvent(1);

        // Assert
        Assert.True(result);
        Assert.Single(schedule.Events);
        Assert.Equal("Event 2", schedule.Events[0].Name);
    }

    [Fact]
    public void RemoveEvent_ReturnsFalseWhenNotFound()
    {
        // Arrange
        var schedule = new EventSchedule
        {
            Events = new List<Event>
            {
                new Event { Id = 1, Name = "Event 1", StartTime = DateTime.UtcNow }
            }
        };

        // Act
        var result = schedule.RemoveEvent(999);

        // Assert
        Assert.False(result);
        Assert.Single(schedule.Events);
    }
}
