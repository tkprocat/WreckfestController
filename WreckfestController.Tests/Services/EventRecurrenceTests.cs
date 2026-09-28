using WreckfestController.Models;
using WreckfestController.Services;

namespace WreckfestController.Tests.Services;

public class EventRecurrenceTests
{
    private static readonly TimeZoneInfo Copenhagen = EventRecurrence.FindZone("Europe/Copenhagen")!;

    private static DateTime Utc(int year, int month, int day, int hour, int minute = 0) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    private static RepeatSchedule Daily(string time) => new() { Frequency = "daily", Time = time };

    private static RepeatSchedule Weekly(string time, params DayOfWeek[] days) =>
        new() { Frequency = "weekly", Time = time, Days = days.Select(d => (int)d).ToList() };

    [Fact]
    public void Daily_LaterToday()
    {
        var next = EventRecurrence.NextAfter(Daily("20:00"), TimeZoneInfo.Utc, Utc(2026, 10, 1, 10));

        Assert.Equal(Utc(2026, 10, 1, 20), next);
    }

    [Fact]
    public void Daily_AtOrAfterTodaysTime_IsTomorrow()
    {
        Assert.Equal(Utc(2026, 10, 2, 20), EventRecurrence.NextAfter(Daily("20:00"), TimeZoneInfo.Utc, Utc(2026, 10, 1, 20)));
        Assert.Equal(Utc(2026, 10, 2, 20), EventRecurrence.NextAfter(Daily("20:00"), TimeZoneInfo.Utc, Utc(2026, 10, 1, 21)));
    }

    [Fact]
    public void Weekly_PicksTheNextListedDay()
    {
        // 2026-10-01 is a Thursday.
        var repeat = Weekly("20:00", DayOfWeek.Tuesday, DayOfWeek.Saturday);

        Assert.Equal(Utc(2026, 10, 3, 20), EventRecurrence.NextAfter(repeat, TimeZoneInfo.Utc, Utc(2026, 10, 1, 12)));
        Assert.Equal(Utc(2026, 10, 6, 20), EventRecurrence.NextAfter(repeat, TimeZoneInfo.Utc, Utc(2026, 10, 3, 20)));
    }

    [Fact]
    public void Weekly_SameDay_BeforeAndAfterTheTime()
    {
        var repeat = Weekly("20:00", DayOfWeek.Thursday);

        Assert.Equal(Utc(2026, 10, 1, 20), EventRecurrence.NextAfter(repeat, TimeZoneInfo.Utc, Utc(2026, 10, 1, 19)));
        Assert.Equal(Utc(2026, 10, 8, 20), EventRecurrence.NextAfter(repeat, TimeZoneInfo.Utc, Utc(2026, 10, 1, 20, 1)));
    }

    [Fact]
    public void Weekly_WithNoDays_NeverOccurs()
    {
        Assert.Null(EventRecurrence.NextAfter(Weekly("20:00"), TimeZoneInfo.Utc, Utc(2026, 10, 1, 12)));
    }

    [Fact]
    public void Weekly_KeepsItsLocalTimeAcrossTheEndOfDaylightSaving()
    {
        // Sundays 20:00 in Copenhagen: CEST (+2) until 2026-10-25 03:00, CET (+1) after.
        var repeat = Weekly("20:00", DayOfWeek.Sunday);

        var before = EventRecurrence.NextAfter(repeat, Copenhagen, Utc(2026, 10, 18, 12));
        var after = EventRecurrence.NextAfter(repeat, Copenhagen, before!.Value);

        Assert.Equal(Utc(2026, 10, 18, 18), before);
        Assert.Equal(Utc(2026, 10, 25, 19), after);
    }

    [Fact]
    public void ATimeSkippedByTheClocksGoingForward_MovesForwardByTheGap()
    {
        // 2026-03-29: Copenhagen goes from 02:00 CET straight to 03:00 CEST.
        var next = EventRecurrence.NextAfter(Daily("02:30"), Copenhagen, Utc(2026, 3, 28, 12));

        Assert.Equal(Utc(2026, 3, 29, 1, 30), next); // 03:30 CEST
    }

    [Fact]
    public void ATimeThatHappensTwice_IsTheFirstOfThem()
    {
        // 2026-10-25: 02:30 happens at 00:30 UTC (CEST) and again at 01:30 UTC (CET).
        var next = EventRecurrence.NextAfter(Daily("02:30"), Copenhagen, Utc(2026, 10, 24, 12));

        Assert.Equal(Utc(2026, 10, 25, 0, 30), next);
    }

    [Fact]
    public void FirstOccurrence_IsAFutureStart_EvenOffThePattern()
    {
        var start = Utc(2026, 10, 2, 9);

        Assert.Equal(start, EventRecurrence.FirstOccurrence(start, Daily("20:00"), TimeZoneInfo.Utc, Utc(2026, 10, 1, 12)));
        Assert.Equal(start, EventRecurrence.FirstOccurrence(start, null, TimeZoneInfo.Utc, Utc(2026, 10, 1, 12)));
    }

    [Fact]
    public void FirstOccurrence_OfAPastStart_IsTheStartForOneOffs_AndTheNextRepeatOtherwise()
    {
        var start = Utc(2026, 9, 1, 20);
        var now = Utc(2026, 10, 1, 12);

        Assert.Equal(start, EventRecurrence.FirstOccurrence(start, null, TimeZoneInfo.Utc, now));
        Assert.Equal(Utc(2026, 10, 1, 20), EventRecurrence.FirstOccurrence(start, Daily("20:00"), TimeZoneInfo.Utc, now));
    }

    [Fact]
    public void After_AOneOffEvent_IsNothing()
    {
        Assert.Null(EventRecurrence.After(Utc(2026, 10, 1, 20), null, TimeZoneInfo.Utc, Utc(2026, 10, 1, 20)));
    }

    [Fact]
    public void After_AnOccurrenceActivatedEarly_IsTheOneAfterIt()
    {
        // Activated during the lead-in, before 20:00: the next is tomorrow, not 20:00 today.
        var next = EventRecurrence.After(Utc(2026, 10, 1, 20), Daily("20:00"), TimeZoneInfo.Utc, Utc(2026, 10, 1, 19, 56));

        Assert.Equal(Utc(2026, 10, 2, 20), next);
    }

    [Fact]
    public void After_AMissedOccurrence_SkipsEveryOneAlreadyPast()
    {
        var next = EventRecurrence.After(Utc(2026, 9, 1, 20), Daily("20:00"), TimeZoneInfo.Utc, Utc(2026, 10, 1, 21));

        Assert.Equal(Utc(2026, 10, 2, 20), next);
    }

    [Fact]
    public void Describe()
    {
        Assert.Equal("One-time", EventRecurrence.Describe(null));
        Assert.Equal("Daily at 20:00", EventRecurrence.Describe(Daily("20:00")));
        Assert.Equal("Weekly on Sun, Fri at 19:30", EventRecurrence.Describe(Weekly("19:30", DayOfWeek.Friday, DayOfWeek.Sunday)));
    }
}
