using Ume.Rag.Domain.Entities;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Infrastructure.Jobs;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Worker.Connectors;
using Ume.Rag.Worker.Connectors.Persistence;
using Ume.Rag.Worker.Connectors.Uploads;
using Ume.Rag.Worker.Jobs;
using Outcome = Ume.Rag.Pipelines.Jobs.RunItemOutcome;

namespace Ume.Rag.Worker.Engine;

/// <summary>
/// The engine's part of an <c>upload</c> source (connectors design §9): the commit's <c>sync</c> plans the batch (slots
/// written, skips as run items, one <c>fetch</c> job per changed slot, dedup <c>fetch:{sourceId}:upload:{uploadId}</c>);
/// the <c>fetch</c> job scans and sniffs the slot's blob, which is the document's raw content, and records the slot's state.
/// Deleted slots become <c>upload.deleted</c> candidates in the reconcile phase.
/// </summary>
public sealed class UploadSource(
    UploadConnector connector,
    IUploadSlotReader slots,
    IUploadSlotWriter writer,
    RunStore runs,
    DocumentIngest ingest,
    IJobQueue queue,
    JobDatabase database)
{
    public async Task<JobResult> ListAsync(SourceContext source, RunRow run, SyncPayload payload, CancellationToken cancellationToken)
    {
        var context = new ListingContext(source.SourceId, source.Classification, run.Id, source.Settings) { BatchId = payload.BatchId };
        var plan = await connector.PlanAsync(context, cancellationToken);
        await using (var commit = await EngineCommit.BeginAsync(database, cancellationToken))
        {
            if (plan is null)
            {
                await commit.AddRunItemAsync(new RunItemRow(run.Id, source.Classification, null, payload.BatchId?.ToString("D") ?? "batch", Outcome.Failed)
                {
                    Reason = UploadConnector.ManifestMissing,
                }, cancellationToken);
            }
            else
            {
                foreach (var skip in plan.Skipped)
                {
                    await commit.AddRunItemAsync(new RunItemRow(run.Id, source.Classification, null, UploadConnector.IdentityOf(skip.UploadId), Outcome.Skipped)
                    {
                        Reason = skip.Reason,
                    }, cancellationToken);
                }
            }

            await commit.CommitAsync(cancellationToken);
        }

        if (plan is not null)
        {
            await writer.WriteAsync(plan.Writes, cancellationToken);
            var jobs = plan.Fetches.Select(f => f.Hints.Upload!.UploadId).Distinct().Select(id => SyncJobs.FetchUpload(source, run.Id, id)).ToList();
            if (jobs.Count > 0)
            {
                await queue.EnqueueAsync(jobs, null, cancellationToken);
            }
        }

        await runs.SetPhaseAsync(run.Id, RunPhases.Fetch, null, cancellationToken);
        return new JobResult.Success();
    }

    public async Task<JobResult> FetchAsync(SourceContext source, RunRow run, PipelineRun pipeline, FetchPayload payload, LeasedJob job, CancellationToken cancellationToken)
    {
        var slot = (await slots.ListAsync(source.SourceId, cancellationToken)).FirstOrDefault(s => s.Id == payload.UploadId);
        if (slot is null || slot.DeletedAt is not null)
        {
            return new JobResult.Success();
        }

        var item = new WorkItem(IdentityKind.Upload, slot.Identity, null, 0,
            new WorkHints(Upload: new UploadHint(slot.Id, slot.Sha256, slot.FileName, slot.DeclaredMediaType)));
        var outcome = await connector.FetchAsync(new FetchContext(source.SourceId, source.Classification, run.Id, source.Settings, false), item, cancellationToken);
        var fetch = new FetchCase(source, run, pipeline, job, IdentityKinds.Upload, slot.Identity, null) { DisplayName = slot.FileName };
        var result = await ingest.HandleAsync(fetch, outcome, cancellationToken);
        var state = outcome switch
        {
            FetchOutcome.Content => UploadState.Clean,
            FetchOutcome.Quarantined => UploadState.Malware,
            FetchOutcome.Refused or FetchOutcome.Failed or FetchOutcome.Skipped => UploadState.Refused,
            _ => (UploadState?)null,
        };
        if (state is { } value && result is JobResult.Committed)
        {
            var reason = outcome switch
            {
                FetchOutcome.Quarantined q => q.Reason,
                FetchOutcome.Refused r => r.Reason,
                FetchOutcome.Failed f => f.Reason,
                FetchOutcome.Skipped s => s.Reason,
                _ => null,
            };
            await writer.SetStateAsync(slot.Id, value, null, reason, null, cancellationToken);
        }

        return result;
    }
}
