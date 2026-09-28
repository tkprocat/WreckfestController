using WreckfestController.Models;

namespace WreckfestController.Services;

/// <summary>
/// Occurrence arithmetic for recurring events. Pure: every method takes the time it
/// works from, so the scheduler, the API and tests agree on the answer.
/// </summary>
/// <remarks>
/// A repeat's time and days are wall-clock values in the event's time zone. The
/// arithmetic is done in that zone and converted to UTC per occurrence, so a weekly
/// 20:00 event stays at 20:00 local when daylight saving starts or ends. 1.x did it in
/// UTC, which moved such an event by an hour twice a year.
/// </remarks>
public static class EventRecurrence
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
    /// The occurrence a new or rescheduled event waits for. A start time still ahead is
    /// the first occurrence, whether or not it fits the repeat. A past one is the start
    /// of a one-off event, which the scheduler will skip as missed, or the anchor of a
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
    /// for a one-off event, and otherwise the next occurrence after both it and now, so
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
