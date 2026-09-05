namespace WreckfestController.Services;

/// <summary>
/// Retains whole log entries on the producer side so a busy UI cannot accumulate
/// an unbounded backlog. The UI takes a snapshot only when the buffer changes.
/// </summary>
public sealed class ControllerLogBuffer : IDisposable
{
    private readonly object _sync = new();
    private readonly Queue<string> _entries = new();
    private readonly int _capacity;
    private bool _dirty;
    private bool _disposed;

    public ControllerLogBuffer(int capacity = 500)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
    }

    public void Add(string entry)
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            if (_entries.Count == _capacity)
                _entries.Dequeue();
            _entries.Enqueue(entry);
            _dirty = true;
        }
    }

    public string[]? TakeSnapshot()
    {
        lock (_sync)
        {
            if (!_dirty || _disposed)
                return null;
            _dirty = false;
            return _entries.ToArray();
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
            _dirty = !_disposed;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _entries.Clear();
            _dirty = false;
        }
    }
}
