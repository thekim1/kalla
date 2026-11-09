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
/// the <c>fetch</c> job scans and sniffs the slot's blob, which is the document's raw content, and marks a clean file
/// <c>clean</c> before its processing. The slot's final state follows the run item that ends the processing of its bytes
/// (<see cref="EngineCommit.AddRunItemAsync"/>: <c>indexed</c>, <c>refused</c> or <c>malware</c>, carried by
/// <see cref="FetchCase.ContentHash"/> at the fetch). Deleted slots become <c>upload.deleted</c> candidates in the reconcile phase.
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
        if (outcome is FetchOutcome.Content)
        {
            await writer.SetStateAsync(slot.Id, UploadState.Clean, null, null, null, cancellationToken);
        }

        var fetch = new FetchCase(source, run, pipeline, job, IdentityKinds.Upload, slot.Identity, null)
        {
            DisplayName = slot.FileName,
            ContentHash = Convert.FromHexString(slot.Sha256),
        };
        return await ingest.HandleAsync(fetch, outcome, cancellationToken);
    }
}
