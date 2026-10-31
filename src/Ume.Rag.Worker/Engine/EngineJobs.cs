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

    /// <summary>A run's own <c>sync</c> continuation in <paramref name="phase"/>.</summary>
    public static JobRequest Continuation(Guid sourceId, Guid runId, Guid departmentId, string classification, string kind, string trigger, SyncPhase phase,
        bool resumeFromCheckpoint = false) =>
        new(JobClasses.Sync, departmentId, classification, ContractJson.Serialize(new SyncPayload
        {
            SourceId = sourceId,
            RunId = runId,
            Kind = Parse<SyncKind>(kind),
            Trigger = Parse<RunTrigger>(trigger),
            Phase = phase,
            ResumeFromCheckpoint = resumeFromCheckpoint,
        }))
        {
            DedupKey = JobDedupKeys.SyncContinuation(sourceId, runId),
            RunId = runId,
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

    /// <summary>A <c>parse</c> job; <paramref name="slice"/> above 0 is a yielded job's continuation (engine design §9.5).</summary>
    public static JobRequest Parse(SourceContext source, DocumentRow document, Guid executionId, Guid pipelineVersionId, Guid runId, byte[] detectKey,
        int slice = 0) =>
        new(JobClasses.Parse, source.DepartmentId, document.Classification, ContractJson.Serialize(new ParsePayload
        {
            DocumentId = document.Id,
            Level = document.Classification,
            ExecutionId = executionId,
            PipelineVersionId = pipelineVersionId,
            Stage = StageNames.Parse,
            InputKey = StageKeys.ToHex(detectKey),
            Slice = slice,
        }))
        {
            DedupKey = JobDedupKeys.Parse(document.Id, executionId),
            RunId = runId,
        };
}
