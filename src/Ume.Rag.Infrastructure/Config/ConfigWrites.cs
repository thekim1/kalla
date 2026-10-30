using Microsoft.EntityFrameworkCore;
using Ume.Rag.Infrastructure.Persistence;

namespace Ume.Rag.Infrastructure.Config;

/// <summary>
/// Serialised config writes (data-model §5): every admin config transaction first locks the version row
/// (<c>SELECT … FOR UPDATE</c>), then runs the cross-rules and writes, so two concurrent saves cannot each pass a
/// cross-rule (write skew), and versions are monotonic in commit order.
/// </summary>
public static class ConfigWrites
{
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
    /// </remarks>
    public static Task<T> ExecuteConfigWriteAsync<T>(this RagDbContext db, Func<long, CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(work);
        var strategy = db.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(async ct =>
        {
            // A failed attempt leaves its entities tracked (added, or unchanged after a save that was rolled back).
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var version = await db.Database
                .SqlQueryRaw<long>("""SELECT "Version" AS "Value" FROM config."ConfigVersion" WHERE "Id" = 1 FOR UPDATE""")
                .SingleAsync(ct);
            var result = await work(version, ct);
            await transaction.CommitAsync(ct);
            return result;
        }, cancellationToken);
    }
}
