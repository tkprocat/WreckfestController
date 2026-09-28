using Microsoft.Extensions.Options;

namespace WreckfestController.Services.Config;

/// <summary>
/// One settings section as <see cref="IOptionsMonitor{TOptions}"/>, the standard .NET
/// shape for settings that change at runtime. Consumers read <see cref="CurrentValue"/>
/// and never depend on the database; tests hand them a fake monitor.
/// </summary>
/// <remarks>Named options are not used: every name reads the one section.</remarks>
public sealed class SettingsStoreOptionsMonitor<T> : IOptionsMonitor<T>, IDisposable
    where T : class
{
    private readonly ISettingsStore _store;
    private readonly object _lock = new();
    private readonly List<Action<T, string?>> _listeners = new();

    public SettingsStoreOptionsMonitor(ISettingsStore store)
    {
        _store = store;
        _store.Changed += OnStoreChanged;
    }

    public T CurrentValue => _store.Get<T>();

    public T Get(string? name) => CurrentValue;

    public IDisposable OnChange(Action<T, string?> listener)
    {
        lock (_lock)
        {
            _listeners.Add(listener);
        }

        return new Registration(this, listener);
    }

    public void Dispose() => _store.Changed -= OnStoreChanged;

    private void OnStoreChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.Section != typeof(T))
        {
            return;
        }

        Action<T, string?>[] listeners;
        lock (_lock)
        {
            listeners = _listeners.ToArray();
        }

        if (listeners.Length == 0)
        {
            return;
        }

        var value = CurrentValue;
        List<Exception>? failures = null;
        foreach (var listener in listeners)
        {
            try
            {
                listener(value, Options.DefaultName);
            }
            catch (Exception ex)
            {
                // Every listener hears about the change; the store logs the failures.
                (failures ??= new()).Add(ex);
            }
        }

        if (failures is not null)
        {
            throw new AggregateException(failures);
        }
    }

    private sealed class Registration(SettingsStoreOptionsMonitor<T> monitor, Action<T, string?> listener) : IDisposable
    {
        public void Dispose()
        {
            lock (monitor._lock)
            {
                monitor._listeners.Remove(listener);
            }
        }
    }
}
