namespace WreckfestController.Services.Assistant;

/// <summary>
/// How many questions the AI assistant has taken today (UTC), across every visitor, so
/// <see cref="Models.AssistantSettings.DailyRequestLimit"/> caps the bill. In memory only:
/// a restart starts the day's count again.
/// </summary>
public sealed class AssistantUsage
{
    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private DateOnly _day;
    private int _count;

    public AssistantUsage(TimeProvider time)
    {
        _time = time;
    }

    /// <summary>Counts one question, unless today's <paramref name="limit"/> is used up.</summary>
    public bool TryTake(int limit)
    {
        lock (_gate)
        {
            var today = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);
            if (today != _day)
            {
                _day = today;
                _count = 0;
            }

            if (_count >= limit)
            {
                return false;
            }

            _count++;
            return true;
        }
    }
}
