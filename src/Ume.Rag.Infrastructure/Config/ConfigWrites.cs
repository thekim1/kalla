using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ume.Rag.Infrastructure.Persistence;

namespace Ume.Rag.Infrastructure.Config;

/// <summary>
/// Serialised config writes (data-model §5): every admin config transaction first locks the version row
/// (<c>SELECT … FOR UPDATE</c>), then runs the cross-rules and writes, so two concurrent saves cannot each pass a
/// cross-rule (write skew), and versions are monotonic in commit order.
/// </summary>
public static class ConfigWrites
{
    /// <summary>How long a config write waits for the version lock before it answers <c>config_busy</c> (admin design §4.4).</summary>
    public static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);

    private const string SetLockTimeout = "SET LOCAL lock_timeout = '5s'";

    /// <summary>
    /// Runs <paramref name="work"/> in one transaction that holds the config version lock, through the context's execution
    /// strategy, and commits. <paramref name="work"/> gets the version before the change; it runs the cross-rules, saves
    /// its changes and returns a result. Throwing rolls everything back.
    /// </summary>
    /// <remarks>
    /// A transient failure retries the whole attempt on the same context (Aspire's Npgsql integration enables retries).
    /// Each attempt starts with an empty change tracker, so <paramref name="work"/> must load and build every change
    /// inside the delegate: entities loaded or added before the call are detached and would not be saved. A failure while
    /// committing whose outcome is unknown (the connection drops during <c>COMMIT</c>) can still apply a change twice; a
    /// caller that cannot tolerate that uses EF's <c>ExecuteInTransactionAsync</c> overload with a <c>verifySucceeded</c>
    /// check instead.
    /// <paramref name="lockMetrics"/>, when given, records each attempt's wait for the lock (<c>rag.admin.config_lock.wait</c>),
    /// a busy one too.
    /// </remarks>
    public static Task<T> ExecuteConfigWriteAsync<T>(this RagDbContext db, Func<long, CancellationToken, Task<T>> work, CancellationToken cancellationToken,
        ConfigLockMetrics? lockMetrics = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(work);
        var strategy = db.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(async ct =>
        {
            // A failed attempt leaves its entities tracked (added, or unchanged after a save that was rolled back).
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlRawAsync(SetLockTimeout, ct);
            long version;
            var lockStart = Stopwatch.GetTimestamp();
            try
            {
                version = await db.Database
                    .SqlQueryRaw<long>("""SELECT "Version" AS "Value" FROM config."ConfigVersion" WHERE "Id" = 1 FOR UPDATE""")
                    .SingleAsync(ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
            {
                // Not retried: the caller answers config_busy at once instead of queueing behind the lock again.
                throw new ConfigBusyException(ex);
            }
            finally
            {
                lockMetrics?.Record(lockStart);
            }

            var result = await work(version, ct);
            await transaction.CommitAsync(ct);
            return result;
        }, cancellationToken);
    }
}

/// <summary>The config version lock was not granted within <see cref="ConfigWrites.LockTimeout"/>; the caller may retry.</summary>
public sealed class ConfigBusyException(Exception inner) : Exception("Another config change holds the version lock.", inner);
