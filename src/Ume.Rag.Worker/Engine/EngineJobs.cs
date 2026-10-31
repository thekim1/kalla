using System.Text.Json;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Pipelines.Definitions;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Pipelines.Json;

namespace Ume.Rag.Worker.Engine;

/// <summary>The engine's job requests and payload reading (engine design §7.4, §9.1; dedup keys as <see cref="JobDedupKeys"/>).</summary>
public static class SyncJobs
{
    /// <summary>Reads a payload tolerantly; null when it is not the class's payload (the job fails at once).</summary>
    public static T? Read<T>(LeasedJob job) where T : class
    {
        ArgumentNullException.ThrowIfNull(job);
        try
        {
            using var document = JsonDocument.Parse(job.Payload);
            return ContractJson.Deserialize<T>(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary><c>Runs.Kind</c> or <c>Runs.Trigger</c> text back to its enum.</summary>
    public static TEnum Parse<TEnum>(string text) where TEnum : struct, Enum =>
        JsonSerializer.Deserialize<TEnum>(JsonSerializer.Serialize(text), ContractJson.Options);

    /// <summary>A run's own <c>sync</c> continuation in <paramref name="phase"/>; a forced <c>reprocess</c> carries its bypass on (<paramref name="bypassFrom"/>).</summary>
    public static JobRequest Continuation(Guid sourceId, Guid runId, Guid departmentId, string classification, string kind, string trigger, SyncPhase phase,
        bool resumeFromCheckpoint = false, string? bypassFrom = null) =>
        new(JobClasses.Sync, departmentId, classification, ContractJson.Serialize(new SyncPayload
        {
            SourceId = sourceId,
            RunId = runId,
            Kind = Parse<SyncKind>(kind),
            Trigger = Parse<RunTrigger>(trigger),
            Phase = phase,
            ResumeFromCheckpoint = resumeFromCheckpoint,
            Force = bypassFrom is not null,
            FromStage = bypassFrom,
        }))
        {
            DedupKey = JobDedupKeys.SyncContinuation(sourceId, runId),
            RunId = runId,
        };

    /// <summary>
    /// The <c>eval</c> <c>publish-report</c> of a finished <c>reprocess</c> run of a publish or rollback (engine design §10.4,
    /// §12.1 step 6), on the source's collection at its level; one per run (dedup <c>eval-report:{runId}</c>).
    /// </summary>
    public static JobRequest PublishReport(Guid collectionId, string collectionClassification, Guid departmentId, Guid runId, Guid pipelineVersionId) =>
        new(JobClasses.Eval, departmentId, collectionClassification, ContractJson.Serialize(new EvalPublishReportPayload
        {
            CollectionId = collectionId,
            RunId = runId,
            PipelineVersionId = pipelineVersionId,
        }))
        {
            DedupKey = JobDedupKeys.EvalReport(runId),
        };

    public static JobRequest FetchUrl(SourceContext source, Guid runId, string urlHash, Guid? executionId = null) =>
        new(JobClasses.Fetch, source.DepartmentId, source.Classification,
            ContractJson.Serialize(new FetchPayload { SourceId = source.SourceId, RunId = runId, UrlHash = urlHash, ExecutionId = executionId }))
        {
            DedupKey = JobDedupKeys.FetchUrl(source.SourceId, urlHash),
            RunId = runId,
        };

    public static JobRequest FetchUpload(SourceContext source, Guid runId, Guid uploadId, Guid? executionId = null) =>
        new(JobClasses.Fetch, source.DepartmentId, source.Classification,
            ContractJson.Serialize(new FetchPayload { SourceId = source.SourceId, RunId = runId, UploadId = uploadId, ExecutionId = executionId }))
        {
            DedupKey = JobDedupKeys.FetchUpload(source.SourceId, uploadId),
            RunId = runId,
        };

    public static JobRequest Embed(SourceContext source, DocumentRow document, Guid executionId, Guid pipelineVersionId, Guid runId, byte[] inputKey) =>
        new(JobClasses.Embed, source.DepartmentId, document.Classification, ContractJson.Serialize(new EmbedPayload
        {
            DocumentId = document.Id,
            Level = document.Classification,
            ExecutionId = executionId,
            PipelineVersionId = pipelineVersionId,
            RunId = runId,
            InputKey = StageKeys.ToHex(inputKey),
        }))
        {
            DedupKey = JobDedupKeys.Embed(document.Id, executionId, pipelineVersionId),
            RunId = runId,
        };

    /// <summary>
    /// A <c>parse</c> job; <paramref name="slice"/> above 0 is a yielded job's continuation (engine design §9.5);
    /// <paramref name="bypassFrom"/> is a forced reprocess's bypass (<see cref="StageBypass"/>).
    /// </summary>
    public static JobRequest Parse(SourceContext source, DocumentRow document, Guid executionId, Guid pipelineVersionId, Guid runId, byte[] detectKey,
        int slice = 0, string? bypassFrom = null) =>
        new(JobClasses.Parse, source.DepartmentId, document.Classification, ContractJson.Serialize(new ParsePayload
        {
            DocumentId = document.Id,
            Level = document.Classification,
            ExecutionId = executionId,
            PipelineVersionId = pipelineVersionId,
            Stage = StageNames.Parse,
            InputKey = StageKeys.ToHex(detectKey),
            Slice = slice,
            BypassFrom = bypassFrom,
        }))
        {
            DedupKey = JobDedupKeys.Parse(document.Id, executionId),
            RunId = runId,
        };
}
