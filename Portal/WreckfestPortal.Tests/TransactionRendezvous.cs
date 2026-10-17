using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace WreckfestPortal.Tests;

/// <summary>
/// Once <see cref="Arm"/>ed, holds each request that is about to begin a transaction until a
/// second one is about to as well, or until a timeout. Both are then past signing in, and they
/// race for SQLite's write lock: the account changes that guard the last admin must still
/// leave one. Armed only after setup, whose own saves begin transactions too.
/// </summary>
internal sealed class TransactionRendezvous : DbTransactionInterceptor
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private int _armed;
    private int _arrived;
    private int _timedOut;

    public void Arm() => Volatile.Write(ref _armed, 1);

    public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
        DbConnection connection,
        TransactionStartingEventData eventData,
        InterceptionResult<DbTransaction> result,
        CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _armed) == 1 && Interlocked.Increment(ref _arrived) <= 2)
        {
            var deadline = DateTime.UtcNow + Timeout;
            while (Volatile.Read(ref _arrived) < 2 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20, cancellationToken);
            }

            // One that gave up went on alone: the second arriving later is no race.
            if (Volatile.Read(ref _arrived) < 2)
            {
                Volatile.Write(ref _timedOut, 1);
            }
        }

        return result;
    }

    /// <summary>Whether two requests met before either went on, so the test really raced them.</summary>
    public bool Met => Volatile.Read(ref _arrived) >= 2 && Volatile.Read(ref _timedOut) == 0;
}
