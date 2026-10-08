namespace WreckfestController.Services.ServerControl;

/// <summary>The game's event loop: on or off, the entry it is at, and how many there are.</summary>
public sealed record EventLoopState(bool Enabled, int Index, int Count);

/// <summary>
/// Reads and switches the server's event loop through the hook. Used by the <c>!eventloop</c>
/// chat command and by a cup going back to the start of its rotation (#204). Sends no chat:
/// what to tell players is the caller's business.
/// </summary>
/// <remarks>
/// <para>
/// The globals are module-relative, found by decoding the RIP-relative operands inside the
/// game's own "is the event loop enabled" getter (FUN_1402dd490 at RVA 0x002DD490), then
/// confirmed live by toggling <c>/eventloop</c> and watching them move. Build-specific: a
/// Wreckfest patch will shift them, which is why every read is sanity-checked and falls open.
/// See docs/finding-rvas.md.
/// </para>
/// <para>
/// Confirmed live on build 1.308438: turning the loop off sets the index to -1; turning it on
/// sets it to 0 without loading anything, so the next event is the second entry.
/// <c>/rotate</c> loads the next event at once.
/// </para>
/// </remarks>
public class EventLoopControl
{
    public const uint RvaEventLoopCount = 0x1857630;   // int32: number of el_add entries
    public const uint RvaEventLoopIndex = 0x122B270;   // int32: current entry, -1 when off

    private readonly ServerManager _serverManager;

    public EventLoopControl(ServerManager serverManager)
    {
        _serverManager = serverManager;
    }

    /// <summary>How long a toggle is given to show, polled in quarters of a second.</summary>
    protected virtual TimeSpan PollInterval => TimeSpan.FromMilliseconds(250);

    private const int PollAttempts = 8;

    /// <summary>The loop as it is now, or null when it cannot be read or reads as nonsense.</summary>
    public virtual async Task<EventLoopState?> ReadAsync()
    {
        var countBytes = await _serverManager.ReadHookMemoryAsync(RvaEventLoopCount, 4);
        var indexBytes = await _serverManager.ReadHookMemoryAsync(RvaEventLoopIndex, 4);
        if (countBytes?.Length != 4 || indexBytes?.Length != 4)
        {
            return null;
        }

        var count = BitConverter.ToInt32(countBytes);
        var index = BitConverter.ToInt32(indexBytes);

        // Reject implausible values rather than trusting a stale offset after a game patch:
        // an entry count outside 0..256, or an index that is neither -1 nor a valid
        // position, means we are not reading what we think.
        return count >= 0 && count <= 256 && index >= -1 && index < Math.Max(count, 1)
            ? new EventLoopState(count > 0 && index > -1, index, count)
            : null;
    }

    /// <summary>
    /// Polls until the loop reads as <paramref name="enabled"/>, briefly. The game does not
    /// apply <c>/eventloop</c> synchronously, so a single immediate read sees the old value.
    /// Returns the last state read, which may still differ.
    /// </summary>
    public async Task<EventLoopState?> WaitForStateAsync(bool enabled)
    {
        EventLoopState? latest = null;
        for (var attempt = 0; attempt < PollAttempts; attempt++)
        {
            await Task.Delay(PollInterval);
            var loop = await ReadAsync();
            latest = loop ?? latest;
            if (loop?.Enabled == enabled)
            {
                return loop;
            }
        }

        return latest;
    }

    /// <summary>
    /// Turns the loop on or off. <c>/eventloop</c> is a plain toggle, so it is sent only when
    /// the loop is known to be the other way. Returns the state afterwards; null when it
    /// cannot be read or the command could not be sent. The toggle goes to
    /// <paramref name="session"/> only (#40).
    /// </summary>
    public async Task<EventLoopState?> SetAsync(AttachmentSession? session, bool enabled)
    {
        var loop = await ReadAsync();
        if (loop is null || loop.Enabled == enabled)
        {
            return loop;
        }

        var sent = await _serverManager.SendCommandAsync(session, "/eventloop");
        return sent.Success ? await WaitForStateAsync(enabled) : null;
    }

    /// <summary>
    /// Back to the beginning of the rotation: off, then on, which sets the position to the
    /// first entry. The loop must never be left off, so turning it back on is tried twice.
    /// Returns the state at the end; on means it worked.
    /// </summary>
    public virtual async Task<EventLoopState?> RestartAsync(AttachmentSession? session)
    {
        var off = await SetAsync(session, false);
        if (off is not { Enabled: false })
        {
            // Could not read it or turn it off: nothing was changed, or it is still on.
            return off;
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var on = await SetAsync(session, true);
            if (on is { Enabled: true })
            {
                return on;
            }
        }

        return await ReadAsync();
    }
}
