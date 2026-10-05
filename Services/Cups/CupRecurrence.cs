using WreckfestController.Models;

namespace WreckfestController.Services.Cups;

/// <summary>
/// Occurrence arithmetic for recurring cups. Pure: every method takes the time it
/// works from, so the scheduler, the API and tests agree on the answer.
/// </summary>
/// <remarks>
/// A repeat's time and days are wall-clock values in the cup's time zone. The
/// arithmetic is done in that zone and converted to UTC per occurrence, so a weekly
/// 20:00 cup stays at 20:00 local when daylight saving starts or ends. 1.x did it in
/// UTC, which moved such a cup by an hour twice a year.
/// </remarks>
public static class CupRecurrence
{
    /// <summary>
    /// The first occurrence strictly after <paramref name="afterUtc"/>, or null when the
    /// repeat can never occur (weekly with no days).
    /// </summary>
    public static DateTime? NextAfter(RepeatSchedule repeat, TimeZoneInfo zone, DateTime afterUtc)
    {
        afterUtc = AsUtc(afterUtc);
        var days = repeat.IsWeekly ? repeat.Days ?? [] : null;
        if (days is { Count: 0 })
        {
            return null;
        }

        var localDate = TimeZoneInfo.ConvertTimeFromUtc(afterUtc, zone).Date;
        var time = repeat.TimeAsTimeSpan;

        // Eight days: the rest of today, and every weekday of the week after it.
        for (var offset = 0; offset <= 7; offset++)
        {
            var date = localDate.AddDays(offset);
            if (days is not null && !days.Contains((int)date.DayOfWeek))
            {
                continue;
            }

            var candidate = ToUtc(date + time, zone);
            if (candidate > afterUtc)
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// The occurrence a new or rescheduled cup waits for. A start time still ahead is
    /// the first occurrence, whether or not it fits the repeat. A past one is the start
    /// of a one-off cup, which the scheduler will skip as missed, or the anchor of a
    /// recurring one, which then waits for its next occurrence after now.
    /// </summary>
    public static DateTime? FirstOccurrence(DateTime startTimeUtc, RepeatSchedule? repeat, TimeZoneInfo zone, DateTime nowUtc)
    {
        startTimeUtc = AsUtc(startTimeUtc);
        if (repeat is null || startTimeUtc > nowUtc)
        {
            return startTimeUtc;
        }

        return NextAfter(repeat, zone, nowUtc);
    }

    /// <summary>
    /// What follows <paramref name="occurrenceUtc"/> once it has been dealt with: nothing
    /// for a one-off cup, and otherwise the next occurrence after both it and now, so
    /// an occurrence activated early is not repeated and missed ones are not replayed.
    /// </summary>
    public static DateTime? After(DateTime occurrenceUtc, RepeatSchedule? repeat, TimeZoneInfo zone, DateTime nowUtc)
    {
        if (repeat is null)
        {
            return null;
        }

        occurrenceUtc = AsUtc(occurrenceUtc);
        return NextAfter(repeat, zone, occurrenceUtc > nowUtc ? occurrenceUtc : nowUtc);
    }

    /// <summary>How far before its start a warmup may begin.</summary>
    public static readonly TimeSpan MaxWarmup = TimeSpan.FromHours(12);

    /// <summary>
    /// The warmup and end of the occurrence that starts at <paramref name="startUtc"/>, from
    /// the cup's wall-clock times in <paramref name="zone"/>. The warmup is the latest time at
    /// or before the start with that clock time - the day before when it is later on the
    /// clock, so 23:50 for a 00:10 start. The end is the first time after the start with its
    /// clock time - the next day when it is earlier on the clock, for a cup running past
    /// midnight. Without a warmup it is the start; without an end, null.
    /// A warmup more than <see cref="MaxWarmup"/> before the start counts as none: that
    /// happens only to a first occurrence set off the repeat's clock (a future start time at
    /// 18:00 for a repeat at 20:00, warming up from 19:30), which would otherwise restart the
    /// server the evening before.
    /// </summary>
    public static (DateTime Warmup, DateTime? End) Window(
        DateTime startUtc,
        TimeOnly? warmup,
        TimeOnly? end,
        TimeZoneInfo zone)
    {
        startUtc = AsUtc(startUtc);
        var localStart = TimeZoneInfo.ConvertTimeFromUtc(startUtc, zone);

        var warmupUtc = startUtc;
        if (warmup is { } warmupClock)
        {
            var wall = localStart.Date + warmupClock.ToTimeSpan();
            if (wall > localStart)
            {
                wall = wall.AddDays(-1);
            }

            // A clock change between the two can push it past the start; never after it.
            var resolved = ToUtc(wall, zone);
            warmupUtc = resolved < startUtc && startUtc - resolved <= MaxWarmup ? resolved : startUtc;
        }

        DateTime? endUtc = null;
        if (end is { } endClock)
        {
            var wall = localStart.Date + endClock.ToTimeSpan();
            if (wall <= localStart)
            {
                wall = wall.AddDays(1);
            }

            var resolved = ToUtc(wall, zone);
            endUtc = resolved > startUtc ? resolved : ToUtc(wall.AddDays(1), zone);
        }

        return (warmupUtc, endUtc);
    }

    /// <summary>
    /// How long before the start the warmup begins, and how long the cup lasts, worked out on
    /// the clock alone (no zone, no date): what <see cref="CupRules"/> checks.
    /// </summary>
    public static (TimeSpan Warmup, TimeSpan? Duration) ClockSpans(TimeOnly start, TimeOnly? warmup, TimeOnly? end)
    {
        static TimeSpan Forward(TimeOnly from, TimeOnly to)
        {
            var span = to.ToTimeSpan() - from.ToTimeSpan();
            return span < TimeSpan.Zero ? span + TimeSpan.FromDays(1) : span;
        }

        return (warmup is { } w ? Forward(w, start) : TimeSpan.Zero, end is { } e ? Forward(start, e) : null);
    }

    /// <summary>The zone for <paramref name="id"/>, an IANA or Windows id; null when unknown.</summary>
    public static TimeZoneInfo? FindZone(string? id) =>
        !string.IsNullOrWhiteSpace(id) && TimeZoneInfo.TryFindSystemTimeZoneById(id.Trim(), out var zone)
            ? zone
            : null;

    public static string Describe(RepeatSchedule? repeat)
    {
        if (repeat is null)
        {
            return "One-time";
        }

        if (repeat.IsDaily)
        {
            return $"Daily at {repeat.Time}";
        }

        var days = (repeat.Days ?? []).Order().Select(d => ((DayOfWeek)d).ToString()[..3]);
        return $"Weekly on {string.Join(", ", days)} at {repeat.Time}";
    }

    /// <summary>
    /// Converts a wall-clock time in <paramref name="zone"/> to UTC. A time skipped when
    /// the clocks go forward is moved forward by the gap (02:30 becomes 03:30), and a
    /// time that happens twice when they go back is the first of the two.
    /// </summary>
    private static DateTime ToUtc(DateTime wall, TimeZoneInfo zone)
    {
        wall = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);

        if (zone.IsInvalidTime(wall))
        {
            // The offset in force before the gap; transitions are never a day apart.
            var before = zone.GetUtcOffset(wall.AddDays(-1));
            return DateTime.SpecifyKind(wall - before, DateTimeKind.Utc);
        }

        if (zone.IsAmbiguousTime(wall))
        {
            var earlier = zone.GetAmbiguousTimeOffsets(wall).Max();
            return DateTime.SpecifyKind(wall - earlier, DateTimeKind.Utc);
        }

        return TimeZoneInfo.ConvertTimeToUtc(wall, zone);
    }

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
