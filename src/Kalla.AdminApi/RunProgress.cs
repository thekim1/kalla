using Microsoft.EntityFrameworkCore;
using Kalla.Domain.Entities;

namespace Kalla.AdminApi;

/// <summary>
/// How far a running run has got (#803; admin design §5.4 step 6 "live counters", connectors design §5.5).
/// <see cref="Processed"/> counts the documents the run has an outcome for so far, plus the fetch, parse and embed jobs
/// that failed. A crawl run also has the pages admitted to its frontier (<see cref="Admitted"/>, at most
/// <see cref="MaxPages"/>), the admitted pages not yet handled (<see cref="Waiting"/>), its start addresses and those
/// that did not answer, and whether the page limit was hit. Those crawl members are null for a run of another source type.
/// </summary>
public sealed record RunProgress(
    int Processed, int? Admitted, int? MaxPages, int? Waiting, int? Seeds, int? SeedsFailed, bool? LimitHit);

/// <summary>
/// The counters of a running run. A run's <c>Runs</c> counters are written only when it finishes, from its items (engine
/// design §10.4: derived, never incremented). While it runs they are derived on read, with the finalizer's buckets
/// (<c>RunFinalizer.CountersSql</c> in the worker), so the page shows what the finished run will show.
/// </summary>
public static class RunLive
{
    /// <summary>
    /// <paramref name="summary"/> with live counters and <see cref="RunSummary.Progress"/> when the run is
    /// <c>running</c>. Any other run is returned unchanged.
    /// </summary>
    public static async Task<RunSummary> WithProgressAsync(AdminContext admin, RunSummary summary, string classification, Guid sourceId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(admin);
        ArgumentNullException.ThrowIfNull(summary);
        if (summary.State != RunStates.Running)
        {
            return summary;
        }

        var id = summary.Id;
        // RunItems is partitioned by day: bound the scan to the run's days, as the items endpoint does.
        var from = summary.StartedAt.UtcDateTime.Date;
        var counts = (await admin.Db.Database.SqlQuery<LiveCounts>($"""
            WITH latest AS (
                SELECT DISTINCT ON (coalesce("DocumentId"::text, "Identity")) "Outcome"
                FROM content."RunItems"
                WHERE "RunId" = {id} AND "CreatedAt" >= {from} AND "Outcome" <> 'superseded'
                ORDER BY coalesce("DocumentId"::text, "Identity"), "CreatedAt" DESC, "Id" DESC),
            failed_jobs AS (
                SELECT count(*)::int AS n FROM content."Jobs"
                WHERE "RunId" = {id} AND "Class" IN ('fetch', 'parse', 'embed') AND "State" IN ('failed', 'dead'))
            SELECT (count(*) FILTER (WHERE "Outcome" = 'added'))::int AS "Added",
                   (count(*) FILTER (WHERE "Outcome" IN ('updated', 'degraded')))::int AS "Updated",
                   (count(*) FILTER (WHERE "Outcome" IN ('unchanged', 'skipped')))::int AS "Unchanged",
                   (count(*) FILTER (WHERE "Outcome" = 'deleted'))::int AS "Deleted",
                   (count(*) FILTER (WHERE "Outcome" IN ('unsupported', 'too-large', 'encrypted', 'malware', 'blocked', 'failed')))::int
                       + (SELECT n FROM failed_jobs) AS "Failed",
                   count(*)::int + (SELECT n FROM failed_jobs) AS "Processed",
                   (SELECT coalesce(sum("CostUsd"), 0) FROM content."RunItems" WHERE "RunId" = {id} AND "CreatedAt" >= {from}) AS "CostUsd"
            FROM latest
            """).ToListAsync(ct)).Single();

        var crawl = await admin.Db.CrawlRuns.AsNoTracking().Where(c => c.RunId == id)
            .Select(c => new { c.Admitted, c.MaxPages, c.Seeds, c.SeedsFailed, c.LimitHit }).SingleOrDefaultAsync(ct);
        int? waiting = null;
        if (crawl is not null)
        {
            // The finalizer's "open frontier" (RunFinalizer.IdleRunsSql): rows of this run not yet back to idle.
            waiting = await admin.Db.CrawlFrontier.AsNoTracking()
                .CountAsync(f => f.Classification == classification && f.SourceId == sourceId && f.RunId == id && f.State != CrawlFrontierStates.Idle, ct);
        }

        return summary with
        {
            Added = counts.Added, Updated = counts.Updated, Unchanged = counts.Unchanged, Deleted = counts.Deleted, Failed = counts.Failed,
            CostUsd = counts.CostUsd,
            Progress = new RunProgress(counts.Processed, crawl?.Admitted, crawl?.MaxPages, waiting, crawl?.Seeds, crawl?.SeedsFailed, crawl?.LimitHit),
        };
    }

    internal sealed record LiveCounts(int Added, int Updated, int Unchanged, int Deleted, int Failed, int Processed, decimal CostUsd);
}
