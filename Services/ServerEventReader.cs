namespace WreckfestController.Services;

/// <summary>
/// One entry from Wreckfest's server-event ring buffer.
/// </summary>
public sealed record ServerEvent(int Id, string Name, IReadOnlyList<int> Args)
{
    public const int QuitNormal = 0x00;
    public const int QuitTimeout = 0x01;
    public const int QuitKicked = 0x02;
    public const int QuitIdleKick = 0x03;
    public const int QuitBanned = 0x04;
    public const int QuitInvalid = 0x05;
    public const int QuitBot = 0x06;
    public const int PlayerHasJoined = 0x07;
    public const int NewModerator = 0x15;
    public const int NewAdmin = 0x16;
    public const int Demoted = 0x17;
    public const int RotationOn = 0x18;
    public const int RotationOff = 0x19;

    public bool IsQuit => Id is >= QuitNormal and <= QuitBot;

    /// <summary>Why the player left, for logging and webhooks.</summary>
    public string QuitReason => Id switch
    {
        QuitNormal => "normal",
        QuitTimeout => "timeout",
        QuitKicked => "kicked",
        QuitIdleKick => "idle kick",
        QuitBanned => "banned",
        QuitInvalid => "invalid",
        QuitBot => "bot removed",
        _ => "unknown"
    };
}

/// <summary>
/// Reads Wreckfest's server-event ring buffer rather than parsing console text.
/// The game serialises every server event - joins, quits with their reason,
/// privilege changes, rotation toggles - into a 4 KB ring before formatting it for
/// display, so this is the same data the console line is built from, minus the
/// text parsing.
/// </summary>
/// <remarks>
/// Wire format, from the emitter at RVA 0x00391760:
///   0x12, (0x20 + eventId), [argCount bytes of (arg + 0x20)], name..., 0x13, '\n'
/// The per-event argument count comes from a table the emitter indexes at
/// (eventId * 6); it is read once and cached, because arguments sit between the
/// type byte and the name.
/// </remarks>
public class ServerEventReader
{
    // Module-relative; see docs/finding-rvas.md for how these were derived.
    private const uint RvaRingCursor = 0x192CB28;   // int64, monotonic byte count
    private const uint RvaRingBuffer = 0x192CB30;
    private const uint RvaArgCountTable = 0xFDCCB2; // short per event, stride 6

    private const int RingSize = 0x1000;
    private const int MaxEventId = 0x20;
    private const int ArgTableStride = 6;
    private const byte EntryMarker = 0x12;
    private const byte EntryTerminator = 0x13;
    private const int EventIdBias = 0x20;

    // The hook caps a single read; larger spans are fetched in chunks.
    private const int MaxReadChunk = 1024;

    private readonly Func<uint, int, Task<byte[]?>> _read;
    private readonly ILogger<ServerEventReader> _logger;

    private short[]? _argCounts;
    private long _lastCursor = -1;

    /// <summary>
    /// True once a cursor has been read successfully. The first poll only adopts the
    /// position, so callers use this to know when to seed state from a snapshot.
    /// </summary>
    public bool HasSynced => _lastCursor >= 0;

    public ServerEventReader(Func<uint, int, Task<byte[]?>> read, ILogger<ServerEventReader> logger)
    {
        _read = read;
        _logger = logger;
    }

    /// <summary>
    /// Forgets the read position, so the next poll re-syncs to the current cursor
    /// without replaying history. Call after re-injecting into a new process.
    /// </summary>
    public void Reset()
    {
        _lastCursor = -1;
        _argCounts = null;
    }

    /// <summary>
    /// Returns events written since the previous poll. <c>Overflowed</c> is true when
    /// more than a ring's worth was written in between, meaning events were lost and
    /// the caller should fall back to a full player snapshot.
    /// </summary>
    public async Task<(IReadOnlyList<ServerEvent> Events, bool Overflowed)> PollAsync()
    {
        var cursorBytes = await _read(RvaRingCursor, 8);
        if (cursorBytes is not { Length: 8 })
        {
            return ([], false);
        }

        var cursor = BitConverter.ToInt64(cursorBytes);
        if (cursor < 0)
        {
            return ([], false);
        }

        // First poll: adopt the current position rather than replaying the ring, which
        // would re-announce joins that happened before we attached.
        if (_lastCursor < 0)
        {
            _lastCursor = cursor;
            return ([], false);
        }

        if (cursor == _lastCursor)
        {
            return ([], false);
        }

        if (cursor < _lastCursor)
        {
            // The server restarted underneath us.
            _lastCursor = cursor;
            return ([], true);
        }

        var pending = cursor - _lastCursor;
        var overflowed = pending > RingSize;
        if (overflowed)
        {
            _logger.LogWarning(
                "Server event ring overflowed: {Pending} bytes pending, ring holds {RingSize}. Events were lost.",
                pending, RingSize);
            pending = RingSize;
        }

        var span = await ReadRingAsync(_lastCursor, (int)pending);
        _lastCursor = cursor;

        if (span is null)
        {
            return ([], overflowed);
        }

        if (_argCounts is null)
        {
            _argCounts = await ReadArgCountsAsync();
        }

        return (Parse(span, _argCounts), overflowed);
    }

    /// <summary>
    /// Reads <paramref name="length"/> bytes ending at <paramref name="cursorEnd"/>,
    /// handling both the ring wrapping and the hook's per-read size cap.
    /// </summary>
    private async Task<byte[]?> ReadRingAsync(long startCursor, int length)
    {
        var result = new byte[length];
        var written = 0;

        while (written < length)
        {
            var offset = (int)((startCursor + written) % RingSize);
            var chunk = Math.Min(Math.Min(MaxReadChunk, length - written), RingSize - offset);

            var bytes = await _read(RvaRingBuffer + (uint)offset, chunk);
            if (bytes is not { Length: > 0 })
            {
                return null;
            }

            Array.Copy(bytes, 0, result, written, bytes.Length);
            written += bytes.Length;
        }

        return result;
    }

    private async Task<short[]?> ReadArgCountsAsync()
    {
        var bytes = await _read(RvaArgCountTable, (MaxEventId + 1) * ArgTableStride);
        if (bytes is null || bytes.Length < (MaxEventId + 1) * ArgTableStride)
        {
            return null;
        }

        var counts = new short[MaxEventId + 1];
        for (var i = 0; i <= MaxEventId; i++)
        {
            var value = BitConverter.ToInt16(bytes, i * ArgTableStride);
            // Reject an implausible table rather than desynchronising the parser.
            counts[i] = value is >= 0 and <= 3 ? value : (short)0;
        }

        return counts;
    }

    /// <summary>
    /// Parses entries out of a raw ring span. Anything that does not look like a
    /// well-formed entry is skipped rather than guessed at: the span may begin
    /// mid-entry, and a wrong offset must not be allowed to invent events.
    /// </summary>
    public static List<ServerEvent> Parse(byte[] span, short[]? argCounts)
    {
        var events = new List<ServerEvent>();

        for (var i = 0; i < span.Length; i++)
        {
            if (span[i] != EntryMarker || i + 1 >= span.Length)
            {
                continue;
            }

            var id = span[i + 1] - EventIdBias;
            if (id is < 0 or > MaxEventId)
            {
                continue;
            }

            var argCount = argCounts is not null && id < argCounts.Length ? argCounts[id] : 0;
            var nameStart = i + 2 + argCount;
            if (nameStart > span.Length)
            {
                continue;
            }

            var end = Array.IndexOf(span, EntryTerminator, nameStart);
            if (end < 0)
            {
                // Truncated tail: the rest arrives on the next poll.
                break;
            }

            var args = new int[argCount];
            for (var a = 0; a < argCount; a++)
            {
                args[a] = span[i + 2 + a] - 0x20;
            }

            var name = System.Text.Encoding.Latin1.GetString(span, nameStart, end - nameStart);
            events.Add(new ServerEvent(id, InjectedHookOutputReader.NormalizeLine(name), args));

            i = end;
        }

        return events;
    }
}
