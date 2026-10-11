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

    /// <summary>
    /// Waits until settled, <paramref name="atMost"/> has passed - a reattach that hangs
    /// must not hold cups back for ever - or <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    public async Task WhenSettledAsync(TimeSpan atMost, TimeProvider time, CancellationToken cancellationToken = default)
    {
        try
        {
            await _settled.Task.WaitAsync(atMost, time, cancellationToken);
        }
        catch (TimeoutException)
        {
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
