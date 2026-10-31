using System.Text.Json;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Infrastructure.Config;
using Ume.Rag.Infrastructure.Jobs;
using Ume.Rag.Pipelines.Definitions;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Pipelines.Json;
using Ume.Rag.Worker.Connectors.Persistence;
using Ume.Rag.Worker.Engine;
using Ume.Rag.Worker.Jobs;
using Ume.Rag.Worker.Processing;
using Outcome = Ume.Rag.Pipelines.Jobs.RunItemOutcome;

namespace Ume.Rag.Worker.Commands;

/// <summary>
/// <c>reprocess-document</c> (admin design §4.7; engine design §7.5): one document re-run from its stored raw content in a
/// <c>reprocess</c> run of its source, under a new execution id that supersedes any work in flight for it. The run (keyed by
/// the job's id, so a retry finds it) and the new execution commit first; then the processing segment runs as a fetch of
/// stored content does (the host's applied site profile for HTML) and its outputs, the document facts, the continuation
/// (<c>embed</c>, or <c>parse</c> for parser-bound documents), and the job's completion with its result commit in one
/// fenced transaction. <c>fromStage</c> <c>fetch</c> is refused: this path never fetches. The engine's parts come from the
/// job's scope (they are registered by <c>AddRagEngine</c>). Result <see cref="ReprocessDocumentResult"/>.
/// </summary>
public sealed class ReprocessDocumentHandler(JobDatabase database, IJobQueue queue, WorkerIdentity identity) : ICommandHandler
{
    public const string FetchUnsupported = "reprocess.fetch_unsupported";
    public const string DocumentUnknown = "document.unknown";
    public const string DocumentDeleted = "document.deleted";
    public const string NoContent = "document.no_content";
    public const string SourceUnknown = "source.unknown";
    public const string SourcePaused = "source.paused";
    public const string Unpublished = "pipeline.unpublished";

    private const string OpenSql = """
        INSERT INTO content."Runs" ("Id", "SourceId", "Classification", "Kind", "Trigger", "PipelineVersionId", "Phase", "State",
                                   "Added", "Updated", "Unchanged", "Deleted", "Failed", "ProposedDeletions", "CostUsd")
        VALUES ($1, $2, $3, $4, $5, $6, 'list', 'running', 0, 0, 0, 0, 0, 0, 0)
        ON CONFLICT ("Id") DO NOTHING
        """;

    private const string ExecutionSql = """UPDATE content."Documents" SET "CurrentExecutionId" = $2 WHERE "Id" = $1""";

    private const string RunStateSql = """SELECT "State" FROM content."Runs" WHERE "Id" = $1""";

    public string Kind => CommandKinds.ReprocessDocument;

    public async Task<JobResult> ExecuteAsync(JobContext context, JsonElement payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (CommandPayload.Read<ReprocessDocumentCommand>(payload) is not { } reprocess)
        {
            return new JobResult.FatalError(CommandJobHandler.InvalidPayload);
        }

        if (reprocess.FromStage == StageNames.Fetch)
        {
            return new JobResult.FatalError(FetchUnsupported);
        }

        context.Services.GetRequiredService<ConfigVersionState>().EnsureConfirmed();
        var documents = context.Services.GetRequiredService<DocumentStore>();
        var sources = context.Services.GetRequiredService<SourceContexts>();
        if (await documents.GetAsync(reprocess.DocumentId, cancellationToken) is not { } document)
        {
            return new JobResult.FatalError(DocumentUnknown);
        }

        if (document.DeletedAt is not null)
        {
            return new JobResult.FatalError(DocumentDeleted);
        }

        if (document.ContentHash is null)
        {
            return new JobResult.FatalError(NoContent);
        }

        if (await sources.GetAsync(document.SourceId, cancellationToken) is not { } source)
        {
            return new JobResult.FatalError(SourceUnknown);
        }

        if (!source.Active)
        {
            return new JobResult.FatalError(SourcePaused);
        }

        if (source.PublishedVersionId is not { } versionId)
        {
            return new JobResult.FatalError(Unpublished);
        }

        try
        {
            var pipeline = await sources.VersionAsync(versionId, source.Type, cancellationToken);
            var runId = context.Job.Id;
            if (!await OpenAsync(source, document, versionId, runId, cancellationToken))
            {
                // The run was cancelled before this attempt: nothing more to do for it.
                return new JobResult.Success(ContractJson.Serialize(new ReprocessDocumentResult { RunId = runId, ExecutionId = document.CurrentExecutionId ?? Guid.Empty }));
            }

            document = await documents.GetAsync(document.Id, cancellationToken) ?? throw new InvalidOperationException("The document vanished.");
            return await ProcessAsync(context, source, pipeline, document, runId, cancellationToken);
        }
        catch (PipelineRefusedException refused)
        {
            return new JobResult.FatalError(refused.Reason);
        }
        catch (StageTransientException transient)
        {
            return StageResults.From(transient);
        }
    }

    /// <summary>Opens the run and sets the new execution in one transaction; a retry finds the run and keeps its execution. False when the run is no longer running.</summary>
    private async Task<bool> OpenAsync(SourceContext source, DocumentRow document, Guid versionId, Guid runId, CancellationToken cancellationToken)
    {
        await using var commit = await EngineCommit.BeginAsync(database, cancellationToken);
        if (await CommandCommit.ExecuteAsync(commit, OpenSql, cancellationToken, Db.P(runId), Db.P(source.SourceId), Db.P(source.Classification),
                Db.P(RunStore.Text(SyncKind.Reprocess)), Db.P(RunStore.Text(RunTrigger.Manual)), Db.P(versionId)) == 1)
        {
            await CommandCommit.ExecuteAsync(commit, ExecutionSql, cancellationToken, Db.P(document.Id), Db.P(Guid.CreateVersion7()));
        }

        var state = await CommandCommit.ScalarAsync(commit, RunStateSql, cancellationToken, Db.P(runId)) as string;
        await commit.CommitAsync(cancellationToken);
        return state == RunStates.Running;
    }

    /// <summary>
    /// The segment and its fenced commit, as <see cref="DocumentIngest.CommitSegmentAsync"/> does for a fetch, plus the job's
    /// result; a superseded execution ends with a <c>superseded</c> run item.
    /// </summary>
    private async Task<JobResult> ProcessAsync(JobContext context, SourceContext source, PipelineRun pipeline, DocumentRow document, Guid runId,
        CancellationToken cancellationToken)
    {
        var segment = context.Services.GetRequiredService<ProcessingSegment>();
        var siteProfiles = context.Services.GetRequiredService<SiteProfiles>();
        var executionId = document.CurrentExecutionId ?? throw new InvalidOperationException("A reprocessed document has an execution id.");
        var profile = document.IdentityKind == IdentityKinds.Url ? await siteProfiles.AppliedAsync(document.Classification, document.Url, cancellationToken) : null;
        var input = new SegmentInput(document, pipeline, source.CollectionClassification, RawContentKeys.Of(document))
        {
            DeclaredMediaType = document.MediaType,
            SiteProfile = profile,
        };
        var result = await segment.RunAsync(input, JobClasses.Fetch, null, cancellationToken);
        var answer = new ReprocessDocumentResult { RunId = runId, ExecutionId = executionId };
        await using var commit = await EngineCommit.BeginAsync(database, cancellationToken);
        if (!await commit.FenceAsync(document.Id, document.Classification, executionId, document.ContentHash!, cancellationToken))
        {
            await commit.AddRunItemAsync(Item(runId, document, pipeline.VersionId, Outcome.Superseded, "execution.superseded"), cancellationToken);
            return await CommandCommit.CompleteAsync(commit, queue, context.Job, identity, answer, cancellationToken);
        }

        await commit.AddOutputsAsync(result.Outputs, cancellationToken);
        switch (result)
        {
            case SegmentResult.Embed embed:
                await commit.SetFactsAsync(document.Id, embed.Facts, cancellationToken);
                await commit.EnqueueAsync(queue, [SyncJobs.Embed(source, document, executionId, pipeline.VersionId, runId, embed.PersonalDataKey)], cancellationToken);
                break;
            case SegmentResult.Parse parse:
                await commit.SetFactsAsync(document.Id, parse.Facts, cancellationToken);
                await commit.EnqueueAsync(queue, [SyncJobs.Parse(source, document, executionId, pipeline.VersionId, runId, parse.DetectKey)], cancellationToken);
                break;
            case SegmentResult.Ended ended:
                await commit.SetParseOutcomeAsync(document.Id, EngineText.Outcome(ended.Outcome), cancellationToken);
                await commit.AddRunItemAsync(Item(runId, document, pipeline.VersionId, ended.Outcome, ended.Reason), cancellationToken);
                break;
        }

        return await CommandCommit.CompleteAsync(commit, queue, context.Job, identity, answer, cancellationToken);
    }

    private static RunItemRow Item(Guid runId, DocumentRow document, Guid pipelineVersionId, Outcome outcome, string reason) =>
        new(runId, document.Classification, document.Id, document.CanonicalIdentity, outcome)
        {
            Reason = reason,
            Strategy = document.ParseStrategy,
            PipelineVersionId = pipelineVersionId,
            ExecutionId = document.CurrentExecutionId,
        };
}
