using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace WreckfestController.Tests.Api;

/// <summary>
/// Changes a user's concurrency stamp just before the next save, as if someone else
/// had edited the account between the API reading and writing it. Fires once, when armed.
/// </summary>
public sealed class ConcurrentEditInterceptor : SaveChangesInterceptor
{
    private string? _armedFor;

    public void ArmFor(string userId) => _armedFor = userId;

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _armedFor, null) is { } userId && eventData.Context is { } context)
        {
            await context.Database.ExecuteSqlAsync(
                $"UPDATE AspNetUsers SET ConcurrencyStamp = 'edited-elsewhere' WHERE Id = {userId}",
                cancellationToken);
        }

        return result;
    }
}

/// <summary>
/// Holds each request that has just counted the users until a second one has counted
/// too, or until a timeout. Without serialization both see the same count before
/// either deletes; with it, the second cannot count until the first has committed.
/// <paramref name="marker"/> picks the query, such as the users controller's
/// <c>SignInFloorTag</c>, which lock and delete both read.
/// </summary>
public sealed class CountRendezvousInterceptor(string marker = "COUNT(*)") : DbCommandInterceptor
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    private int _arrived;

    // EF reads a count through a data reader; closing it means the count has been read.
    public override async ValueTask<InterceptionResult> DataReaderClosingAsync(
        DbCommand command,
        DataReaderClosingEventData eventData,
        InterceptionResult result)
    {
        if (command.CommandText.Contains(marker, StringComparison.OrdinalIgnoreCase)
            && command.CommandText.Contains("\"AspNetUsers\"", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _arrived);
            var deadline = DateTime.UtcNow + Timeout;
            while (Volatile.Read(ref _arrived) < 2 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }
        }

        return result;
    }
}

/// <summary>
/// Holds the first request that runs a query containing every one of
/// <paramref name="markers"/>, for <paramref name="pause"/> after reading it, so a test
/// can send a competing request into that window. <see cref="Paused"/> completes when
/// the hold begins.
/// </summary>
public sealed class PauseAfterQueryInterceptor(TimeSpan pause, params string[] markers) : DbCommandInterceptor
{
    private readonly TaskCompletionSource _paused = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _fired;

    public Task Paused => _paused.Task;

    public override async ValueTask<InterceptionResult> DataReaderClosingAsync(
        DbCommand command,
        DataReaderClosingEventData eventData,
        InterceptionResult result)
    {
        if (markers.All(marker => command.CommandText.Contains(marker, StringComparison.Ordinal))
            && Interlocked.Exchange(ref _fired, 1) == 0)
        {
            _paused.TrySetResult();
            await Task.Delay(pause);
        }

        return result;
    }
}
