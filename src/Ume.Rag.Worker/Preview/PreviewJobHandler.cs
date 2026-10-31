using System.Text.Json;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Infrastructure.Config;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Worker.Engine;
using Ume.Rag.Worker.Jobs;
using Ume.Rag.Worker.Processing;

namespace Ume.Rag.Worker.Preview;

/// <summary>
/// <c>preview</c> jobs (engine design §9.1, §11; data-model §4.2: dispatched first, concurrency 2, lease 2 minutes, one
/// attempt): <c>stage-preview</c> and <c>preflight</c>. <c>publish-plan</c> is refused with <c>preview.unsupported</c>
/// until it is built (P1b item 6); an unknown kind or a malformed payload fails at once. A stage's refusal without an
/// attempt (parser or NER busy) requeues the preview, which then reuses the outputs it already wrote.
/// </summary>
public sealed partial class PreviewJobHandler(StagePreview stagePreview, Preflight preflight, ConfigVersionState config, ILogger<PreviewJobHandler> logger)
    : IJobHandler
{
    public const string InvalidPayload = "preview.invalid_payload";
    public const string UnknownKind = "preview.unknown_kind";
    public const string Unsupported = "preview.unsupported";

    public string JobClass => JobClasses.Preview;

    public async Task<JobResult> ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        config.EnsureConfirmed();
        var job = context.Job;
        string? kind;
        try
        {
            using var document = JsonDocument.Parse(job.Payload);
            kind = document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("kind", out var value) &&
                value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
        catch (JsonException)
        {
            return new JobResult.FatalError(InvalidPayload);
        }

        switch (kind)
        {
            case PreviewKinds.StagePreview when SyncJobs.Read<StagePreviewPayload>(job) is { } stage:
                try
                {
                    return await stagePreview.RunAsync(job, stage, cancellationToken);
                }
                catch (StageTransientException transient)
                {
                    // A parser or NER refusal without an attempt (busy, not ready): the preview runs again from its own outputs.
                    return StageResults.From(transient);
                }

            case PreviewKinds.StagePreview:
                return new JobResult.FatalError(InvalidPayload);
            case PreviewKinds.Preflight:
                return SyncJobs.Read<PreflightPayload>(job) is { } check
                    ? await preflight.RunAsync(job, check, cancellationToken)
                    : new JobResult.FatalError(InvalidPayload);
            case PreviewKinds.PublishPlan:
                LogUnsupported(logger, job.Id, kind);
                return new JobResult.FatalError(Unsupported);
            case null:
                return new JobResult.FatalError(InvalidPayload);
            default:
                return new JobResult.FatalError(UnknownKind);
        }
    }

    [LoggerMessage(LogLevel.Warning, "Preview job {JobId}: kind {Kind} is not built yet")]
    private static partial void LogUnsupported(ILogger logger, Guid jobId, string kind);
}
