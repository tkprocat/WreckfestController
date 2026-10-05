using WreckfestController.Services.Cups;

namespace WreckfestController.Tests.Services.Cups;

/// <summary>
/// A cup's warmup and end as clock times around its start (#204): resolved per occurrence in
/// the cup's zone, across midnight and daylight saving.
/// </summary>
public class CupWindowTests
{
    private static readonly TimeZoneInfo Copenhagen = CupRecurrence.FindZone("Europe/Copenhagen")!;

    private static DateTime Utc(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    [Fact]
    public void WarmupAndEnd_OnTheSameEvening()
    {
        // 20:00 in Copenhagen in October is 18:00 UTC.
        var start = Utc(2026, 10, 9, 18, 0);

        var (warmup, end) = CupRecurrence.Window(start, new TimeOnly(19, 30), new TimeOnly(21, 30), Copenhagen);

        Assert.Equal(Utc(2026, 10, 9, 17, 30), warmup);
        Assert.Equal(Utc(2026, 10, 9, 19, 30), end);
    }

    [Fact]
    public void AWarmupLaterOnTheClock_IsTheDayBefore()
    {
        var start = Utc(2026, 10, 9, 0, 10);

        var (warmup, _) = CupRecurrence.Window(start, new TimeOnly(23, 50), null, TimeZoneInfo.Utc);

        Assert.Equal(Utc(2026, 10, 8, 23, 50), warmup);
    }

    [Fact]
    public void AnEndEarlierOnTheClock_IsTheNextDay()
    {
        var start = Utc(2026, 10, 9, 22, 0);

        var (_, end) = CupRecurrence.Window(start, null, new TimeOnly(1, 30), TimeZoneInfo.Utc);

        Assert.Equal(Utc(2026, 10, 10, 1, 30), end);
    }

    [Fact]
    public void WithoutWarmupOrEnd_TheWarmupIsTheStart_AndThereIsNoEnd()
    {
        var start = Utc(2026, 10, 9, 18, 0);

        var (warmup, end) = CupRecurrence.Window(start, null, null, Copenhagen);

        Assert.Equal(start, warmup);
        Assert.Null(end);
    }

    [Fact]
    public void AcrossTheClocksGoingBack_TheWallClockTimesHold()
    {
        // 25 October 2026: Copenhagen goes from UTC+2 to UTC+1 at 03:00. A cup at 03:30 local
        // (02:30 UTC) warming up from 01:30 local, which is still UTC+2 (23:30 UTC the day before).
        var start = Utc(2026, 10, 25, 2, 30);

        var (warmup, end) = CupRecurrence.Window(start, new TimeOnly(1, 30), new TimeOnly(5, 0), Copenhagen);

        Assert.Equal(Utc(2026, 10, 24, 23, 30), warmup);
        Assert.Equal(Utc(2026, 10, 25, 4, 0), end);
    }

    [Fact]
    public void AWarmupInTheSpringGap_IsMovedOnByTheGap_AndNeverPastTheStart()
    {
        // 29 March 2026: 02:00-03:00 does not exist in Copenhagen. 02:30 becomes 03:30
        // (01:30 UTC). With the start at 03:15 local (01:15 UTC), the warmup cannot follow it.
        var start = Utc(2026, 3, 29, 1, 15);

        var (warmup, _) = CupRecurrence.Window(start, new TimeOnly(2, 30), null, Copenhagen);

        Assert.Equal(start, warmup);
    }

    [Fact]
    public void AFirstOccurrenceOffTheRepeatsClock_GetsNoWarmup_RatherThanOneTheEveningBefore()
    {
        // Repeat at 20:00 warming up from 19:30, but a first start set for 18:00.
        var start = Utc(2026, 10, 9, 18, 0);

        var (warmup, _) = CupRecurrence.Window(start, new TimeOnly(19, 30), null, TimeZoneInfo.Utc);

        Assert.Equal(start, warmup);
    }

    [Theory]
    [InlineData("20:00", "19:30", "21:30", 30, 90)]
    [InlineData("00:10", "23:50", "02:00", 20, 110)]
    [InlineData("20:00", null, null, 0, null)]
    [InlineData("20:00", "20:00", "20:00", 0, 0)]
    public void ClockSpans_AreForwardAroundTheClock(string start, string? warmup, string? end, int warmupMinutes, int? durationMinutes)
    {
        var (warmupSpan, duration) = CupRecurrence.ClockSpans(
            TimeOnly.Parse(start),
            warmup is null ? null : TimeOnly.Parse(warmup),
            end is null ? null : TimeOnly.Parse(end));

        Assert.Equal(TimeSpan.FromMinutes(warmupMinutes), warmupSpan);
        Assert.Equal(durationMinutes is { } m ? TimeSpan.FromMinutes(m) : null, duration);
    }
}
