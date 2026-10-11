namespace WreckfestController.Services.ServerControl;

/// <summary>
/// Settles once the startup reattach (<see cref="ServerReattachService"/>) has finished:
/// attached and the roster read, or found nothing to attach to, or failed (#232).
/// </summary>
/// <remarks>
/// The cup scheduler waits for it before its first check. A cup due right after the
/// controller starts would otherwise be activated with no server attached yet - its
/// restart failing, the occurrence recorded as failed for good - or with an empty roster,
/// restarting the server without warning the players on it.
/// </remarks>
public sealed class StartupReattach
{
    private readonly TaskCompletionSource _settled = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsSettled => _settled.Task.IsCompleted;

    public void Settle() => _settled.TrySetResult();

    /// <summary>Waits until settled, or <paramref name="atMost"/>: a reattach that hangs must not hold cups back for ever.</summary>
    public async Task WhenSettledAsync(TimeSpan atMost, TimeProvider time)
    {
        try
        {
            await _settled.Task.WaitAsync(atMost, time);
        }
        catch (TimeoutException)
        {
        }
    }
}
