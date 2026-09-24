namespace WreckfestController.Data;

/// <summary>
/// Whether the controller's database is usable. Until <see cref="DatabaseBootstrapper"/>
/// has run successfully, the app is in recovery mode: the WPF window shows why, the
/// scheduler waits and the API answers only <c>/api/auth/state</c>.
/// </summary>
public sealed class DatabaseState
{
    private readonly object _lock = new();

    public DatabaseState(string databasePath)
    {
        DatabasePath = databasePath;
    }

    public string DatabasePath { get; }

    public bool IsReady { get; private set; }

    /// <summary>Why the last startup attempt failed, or null when it has not failed.</summary>
    public string? Error { get; private set; }

    /// <summary>The backup made before the last migration attempt, if one was needed.</summary>
    public string? BackupPath { get; private set; }

    /// <summary>Raised after every bootstrap attempt, on the thread that made it.</summary>
    public event Action? Changed;

    public void MarkReady(string? backupPath)
    {
        lock (_lock)
        {
            IsReady = true;
            Error = null;
            BackupPath = backupPath;
        }

        Changed?.Invoke();
    }

    public void MarkFailed(string error, string? backupPath)
    {
        lock (_lock)
        {
            IsReady = false;
            Error = error;
            BackupPath = backupPath;
        }

        Changed?.Invoke();
    }
}
