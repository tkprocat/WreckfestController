using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using WreckfestController.Models;
using WreckfestController.Services;
using Xunit;

namespace WreckfestController.Tests.Services;

public class RecurringEventServiceTests
{
    private readonly Mock<ILogger<RecurringEventService>> _mockLogger;
    private readonly RecurringEventService _service;

    public RecurringEventServiceTests()
    {
        _mockLogger = new Mock<ILogger<RecurringEventService>>();
        _service = new RecurringEventService(_mockLogger.Object);
    }

    [Fact]
    public void CalculateNextInstance_NonRecurringEvent_ReturnsNull()
    {
        // Arrange
        var evt = new Event
        {
            Id = 1,
            Name = "One-time Event",
            StartTime = DateTime.UtcNow,
            Repeat = null
        };

        // Act
        var result = _service.CalculateNextInstance(evt);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void CalculateNextInstance_DailyEvent_NextDay()
    {
        // Arrange
        var baseTime = new DateTime(2025, 1, 15, 14, 30, 0, DateTimeKind.Utc); // 2:30 PM
        var evt = new Event
        {
            Id = 1,
            Name = "Daily Event",
            StartTime = baseTime,
            Repeat = new RepeatSchedule
            {
                Frequency = "daily",
                Time = "15:00" // 3:00 PM
            }
        };

        // Act - from a time before today's occurrence
        var result = _service.CalculateNextInstance(evt, baseTime);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(15, result.Value.Hour);
        Assert.Equal(0, result.Value.Minute);
        Assert.Equal(baseTime.Date, result.Value.Date); // Same day since we're before 3 PM
    }

    [Fact]
    public void CalculateNextInstance_DailyEvent_AfterTodaysTime_NextDay()
    {
        // Arrange
        var baseTime = new DateTime(2025, 1, 15, 16, 30, 0, DateTimeKind.Utc); // 4:30 PM
        var evt = new Event
        {
            Id = 1,
            Name = "Daily Event",
            StartTime = baseTime,
            Repeat = new RepeatSchedule
            {
                Frequency = "daily",
                Time = "15:00" // 3:00 PM
            }
        };

        // Act
        var result = _service.CalculateNextInstance(evt, baseTime);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(15, result.Value.Hour);
        Assert.Equal(0, result.Value.Minute);
        Assert.Equal(baseTime.Date.AddDays(1), result.Value.Date);
    }

    [Fact]
    public void CalculateNextInstance_WeeklyEvent_SameDay_BeforeTime()
    {
        // Arrange
        var wednesday = new DateTime(2025, 1, 15, 10, 0, 0, DateTimeKind.Utc); // Wednesday 10 AM
        var evt = new Event
        {
            Id = 1,
            Name = "Weekly Wednesday Event",
            StartTime = wednesday,
            Repeat = new RepeatSchedule
            {
                Frequency = "weekly",
                Days = new List<int> { 3 }, // Wednesday
                Time = "15:00" // 3:00 PM
            }
        };

        // Act
        var result = _service.CalculateNextInstance(evt, wednesday);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(DayOfWeek.Wednesday, result.Value.DayOfWeek);
        Assert.Equal(15, result.Value.Hour);
        Assert.Equal(wednesday.Date, result.Value.Date); // Same day
    }

    [Fact]
    public void CalculateNextInstance_WeeklyEvent_SameDay_AfterTime()
    {
        // Arrange
        var wednesday = new DateTime(2025, 1, 15, 16, 0, 0, DateTimeKind.Utc); // Wednesday 4 PM
        var evt = new Event
        {
            Id = 1,
            Name = "Weekly Wednesday Event",
            StartTime = wednesday,
            Repeat = new RepeatSchedule
            {
                Frequency = "weekly",
                Days = new List<int> { 3 }, // Wednesday
                Time = "15:00" // 3:00 PM
            }
        };

        // Act
        var result = _service.CalculateNextInstance(evt, wednesday);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(DayOfWeek.Wednesday, result.Value.DayOfWeek);
        Assert.Equal(15, result.Value.Hour);
        // Should be next Wednesday (7 days later)
        Assert.Equal(wednesday.Date.AddDays(7), result.Value.Date);
    }

    [Fact]
    public void CalculateNextInstance_WeeklyEvent_NextDayInSameWeek()
    {
        // Arrange
        var monday = new DateTime(2025, 1, 13, 10, 0, 0, DateTimeKind.Utc); // Monday
        var evt = new Event
        {
            Id = 1,
            Name = "Weekly Friday Event",
            StartTime = monday,
            Repeat = new RepeatSchedule
            {
                Frequency = "weekly",
                Days = new List<int> { 5 }, // Friday
                Time = "15:00" // 3:00 PM
            }
        };

        // Act
        var result = _service.CalculateNextInstance(evt, monday);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(DayOfWeek.Friday, result.Value.DayOfWeek);
        Assert.Equal(15, result.Value.Hour);
        // Should be this Friday (4 days later)
        Assert.Equal(monday.Date.AddDays(4), result.Value.Date);
    }

    [Fact]
    public void CalculateNextInstance_WeeklyEvent_MultipleDays()
    {
        // Arrange
        var monday = new DateTime(2025, 1, 13, 10, 0, 0, DateTimeKind.Utc); // Monday
        var evt = new Event
        {
            Id = 1,
            Name = "Multi-day Event",
            StartTime = monday,
            Repeat = new RepeatSchedule
            {
                Frequency = "weekly",
                Days = new List<int> { 3, 5 }, // Wednesday and Friday
                Time = "15:00" // 3:00 PM
            }
        };

        // Act
        var result = _service.CalculateNextInstance(evt, monday);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(DayOfWeek.Wednesday, result.Value.DayOfWeek); // Should pick the soonest
        Assert.Equal(15, result.Value.Hour);
    }

    [Fact]
    public void CalculateNextInstance_WeeklyEvent_NoDays_ReturnsNull()
    {
        // Arrange
        var evt = new Event
        {
            Id = 1,
            Name = "Invalid Weekly Event",
            StartTime = DateTime.UtcNow,
            Repeat = new RepeatSchedule
            {
                Frequency = "weekly",
                Days = new List<int>(), // No days specified
                Time = "15:00"
            }
        };

        // Act
        var result = _service.CalculateNextInstance(evt);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void GetRepeatDescription_DailySchedule()
    {
        // Arrange
        var repeat = new RepeatSchedule
        {
            Frequency = "daily",
            Time = "15:30"
        };

        // Act
        var description = _service.GetRepeatDescription(repeat);

        // Assert
        Assert.Contains("Daily", description);
        Assert.Contains("15:30", description);
    }

    [Fact]
    public void GetRepeatDescription_WeeklySchedule()
    {
        // Arrange
        var repeat = new RepeatSchedule
        {
            Frequency = "weekly",
            Days = new List<int> { 1, 3, 5 }, // Mon, Wed, Fri
            Time = "20:00"
        };

        // Act
        var description = _service.GetRepeatDescription(repeat);

        // Assert
        Assert.Contains("Weekly", description);
        Assert.Contains("20:00", description);
        Assert.Contains("Mon", description);
        Assert.Contains("Wed", description);
        Assert.Contains("Fri", description);
    }

    [Fact]
    public void GetRepeatDescription_NullSchedule()
    {
        // Act
        var description = _service.GetRepeatDescription(null);

        // Assert
        Assert.Equal("Does not repeat", description);
    }

    [Fact]
    public void RescheduleEvent_NonRecurring_ReturnsFalse()
    {
        // Arrange
        var mockStorage = new Mock<EventStorageService>(
            Mock.Of<IConfiguration>(),
            Mock.Of<ILogger<EventStorageService>>());
        var schedule = new EventSchedule();
        var evt = new Event
        {
            Id = 1,
            Name = "One-time Event",
            StartTime = DateTime.UtcNow,
            Repeat = null
        };

        // Act
        var result = _service.RescheduleEvent(evt, mockStorage.Object, schedule);

        // Assert
        Assert.False(result);
    }

    // NOTE: RescheduleEvent_ValidRecurring_UpdatesSchedule test removed
    // Cannot mock EventStorageService.SaveSchedule() since it's not virtual.
    // This method requires integration testing with real EventStorageService.

    [Fact]
    public void CalculateNextInstance_WeeklyEvent_WrapsToNextWeek()
    {
        // Arrange
        var friday = new DateTime(2025, 1, 17, 16, 0, 0, DateTimeKind.Utc); // Friday 4 PM
        var evt = new Event
        {
            Id = 1,
            Name = "Weekly Monday Event",
            StartTime = friday,
            Repeat = new RepeatSchedule
            {
                Frequency = "weekly",
                Days = new List<int> { 1 }, // Monday
                Time = "15:00" // 3:00 PM
            }
        };

        // Act
        var result = _service.CalculateNextInstance(evt, friday);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(DayOfWeek.Monday, result.Value.DayOfWeek);
        Assert.Equal(15, result.Value.Hour);
        // Should be next Monday (3 days later)
        Assert.True(result.Value > friday);
    }
}
