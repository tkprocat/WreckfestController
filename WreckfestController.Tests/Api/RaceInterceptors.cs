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
/// </summary>
public sealed class CountRendezvousInterceptor : DbCommandInterceptor
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    private int _arrived;

    // EF reads a count through a data reader; closing it means the count has been read.
    public override async ValueTask<InterceptionResult> DataReaderClosingAsync(
        DbCommand command,
        DataReaderClosingEventData eventData,
        InterceptionResult result)
    {
        if (command.CommandText.Contains("COUNT(*)", StringComparison.OrdinalIgnoreCase)
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
