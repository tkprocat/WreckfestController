using Microsoft.Extensions.Options;
using WreckfestController.Models;

namespace WreckfestController.Tests;

/// <summary>
/// A settings section for a test: <see cref="Value"/> is what the service reads, and
/// <see cref="Set"/> changes it as a save would, telling <see cref="OnChange"/> listeners.
/// </summary>
public sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    where T : class
{
    private readonly List<Action<T, string?>> _listeners = new();

    /// <summary>Mutable, for tests that adjust one field without announcing it.</summary>
    public T Value { get; private set; } = value;

    public T CurrentValue => Value;

    public T Get(string? name) => Value;

    public IDisposable OnChange(Action<T, string?> listener)
    {
        lock (_listeners)
        {
            _listeners.Add(listener);
        }

        return new Registration(() =>
        {
            lock (_listeners)
            {
                _listeners.Remove(listener);
            }
        });
    }

    /// <summary>Replaces the value and tells the listeners, as a saved change would.</summary>
    public void Set(T value)
    {
        Value = value;
        Action<T, string?>[] listeners;
        lock (_listeners)
        {
            listeners = _listeners.ToArray();
        }

        foreach (var listener in listeners)
        {
            listener(value, Options.DefaultName);
        }
    }

    private sealed class Registration(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}

/// <summary>Monitors for the settings sections, with the shipped defaults unless a test says otherwise.</summary>
public static class TestSettings
{
    public static TestOptionsMonitor<WreckfestServerSettings> Server(
        string serverPath = "",
        string workingDirectory = "",
        string serverArguments = "-s server_config=server_config.cfg",
        string logFilePath = "") =>
        new(new WreckfestServerSettings
        {
            ServerPath = serverPath,
            WorkingDirectory = workingDirectory,
            ServerArguments = serverArguments,
            LogFilePath = logFilePath,
        });

    public static TestOptionsMonitor<SteamCmdSettings> SteamCmd(string steamCmdPath = "", string appId = "361580") =>
        new(new SteamCmdSettings { SteamCmdPath = steamCmdPath, WreckfestAppId = appId });
}
