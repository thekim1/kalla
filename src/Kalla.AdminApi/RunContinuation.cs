using Microsoft.EntityFrameworkCore;
using Kalla.Domain.Admin;
using Kalla.Domain.Entities;
using Kalla.Domain.Ports;
using Kalla.Pipelines.Jobs;
using Kalla.Pipelines.Json;

namespace Kalla.AdminApi;

/// <summary>
/// Continue on a crawl run (#804; the user's answers of 2026-11-17): a <c>continue</c> run that takes over what the run left,
/// its open frontier rows and its failed pages, with link discovery (the worker's <c>SourceReruns.ContinueAsync</c>). Only the
/// source's latest run can be continued, once it ended (not while it waits for a deletion decision), and only when it left
/// something to continue: else the editor gets Sync now only.
/// </summary>
public static class RunContinuation
{
    private const string Failed = "failed";
    private const string Superseded = "superseded";

    /// <summary>The run can be continued now (<see cref="RunDto.CanContinue"/>).</summary>
    public static async Task<bool> CanContinueAsync(AdminContext admin, Run run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(admin);
        ArgumentNullException.ThrowIfNull(run);
        if (run.State is RunStates.Running or RunStates.AwaitingDeletionConfirmation)
        {
            return false;
        }

        var source = await admin.Db.Sources.AsNoTracking().Where(s => s.Id == run.SourceId).Select(s => new { s.Type, s.DeletedAt }).SingleOrDefaultAsync(ct);
        if (source is not { Type: SourceTypes.Crawl, DeletedAt: null })
        {
            return false;
        }

        var newer = await admin.Db.Runs.AsNoTracking()
            .AnyAsync(r => r.SourceId == run.SourceId && r.Id != run.Id && (r.StartedAt > run.StartedAt || (r.StartedAt == run.StartedAt && r.Id.CompareTo(run.Id) > 0)), ct);
        return !newer && await LeftSomethingAsync(admin, run, ct);
    }

    /// <summary>
    /// The run's frontier rows still open, or a page whose latest item in the run failed and whose row the run still holds:
    /// what the worker takes over. RunItems is partitioned by day, so the scan is bounded to the run's days.
    /// </summary>
    private static async Task<bool> LeftSomethingAsync(AdminContext admin, Run run, CancellationToken ct)
    {
        var frontier = admin.Db.CrawlFrontier.AsNoTracking().Where(f => f.Classification == run.Classification && f.SourceId == run.SourceId && f.RunId == run.Id);
        if (await frontier.AnyAsync(f => f.State != CrawlFrontierStates.Idle, ct))
        {
            return true;
        }

        var from = run.StartedAt.UtcDateTime.Date;
        var to = (run.FinishedAt ?? DateTimeOffset.UtcNow).UtcDateTime.Date.AddDays(1);
        var items = admin.Db.RunItems.AsNoTracking().Where(i => i.RunId == run.Id && i.CreatedAt >= from && i.CreatedAt < to);
        return await items.AnyAsync(i => i.Outcome == Failed
            && frontier.Any(f => f.Url == i.Identity)
            && !items.Any(later => later.Identity == i.Identity && later.Outcome != Failed && later.Outcome != Superseded
                && later.CreatedAt > i.CreatedAt), ct);
    }

    /// <summary>
    /// <c>POST /api/runs/{id}/continue</c>: a <c>sync</c> job of kind <c>continue</c> with <c>continuesRunId</c>, trigger
    /// <c>manual</c>, priority 10 and the source's sync dedup key, as sync now. A sync already queued or a run already running
    /// is answered instead (<see cref="SyncStarted.AlreadyRunning"/>).
    /// </summary>
    public static async Task<SyncStarted> ContinueAsync(Guid id, AdminContext admin, CancellationToken ct) =>
        await admin.WriteAsync(async attempt =>
        {
            var sources = SourceEndpoints.Scoped(admin).Select(s => s.Id);
            var run = await admin.Db.Runs.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id && sources.Contains(r.SourceId), attempt)
                ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
            var source = await admin.Db.Sources.AsNoTracking().SingleAsync(s => s.Id == run.SourceId, attempt);
            if (source.State == SourceStates.Paused)
            {
                throw AdminFaults.Of(AdminErrorCodes.SourcePaused);
            }

            var collection = await admin.Db.Collections.AsNoTracking().SingleAsync(c => c.Id == source.CollectionId, attempt);
            var queued = await SourceEndpoints.QueuedSyncAsync(admin, source.Id, attempt);
            if (queued is not null)
            {
                return new AdminChange<SyncStarted>(queued, "continue", AuditEntityTypes.Run, run.Id, null, new { queued.JobId, queued.RunId })
                {
                    DepartmentId = collection.DepartmentId, Classification = source.Classification,
                };
            }

            if (!await CanContinueAsync(admin, run, attempt))
            {
                throw AdminFaults.Of(AdminErrorCodes.RunNotContinuable);
            }

            var job = new JobRequest(JobClasses.Sync, collection.DepartmentId, source.Classification, ContractJson.Serialize(new SyncPayload
            {
                SourceId = source.Id, Kind = SyncKind.Continue, Trigger = RunTrigger.Manual, ContinuesRunId = run.Id,
            }))
            {
                Priority = 10,
                DedupKey = JobDedupKeys.Sync(source.Id),
                TeamId = collection.OwnerTeamId,
            };
            return new AdminChange<SyncStarted>(new SyncStarted(job.Id, null, false, "continue"), "continue", AuditEntityTypes.Run, run.Id, null,
                new { jobId = job.Id, sourceId = source.Id })
            {
                DepartmentId = collection.DepartmentId, Classification = source.Classification, Jobs = [job],
            };
        }, ct);
}
