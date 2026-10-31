using System.Text.Json;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Infrastructure.Config;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Worker.Engine;
using Ume.Rag.Worker.Jobs;
using Ume.Rag.Worker.Processing;
using Ume.Rag.Worker.Processing.Parse;

namespace Ume.Rag.Worker.Preview;

/// <summary>
/// <c>preview</c> jobs (engine design §9.1, §11; data-model §4.2: dispatched first, concurrency 2, lease 2 minutes, one
/// attempt): <c>stage-preview</c>, <c>preflight</c> and <c>publish-plan</c>; an unknown kind or a malformed payload fails at
/// once. A stage's refusal without an attempt (parser or NER busy) requeues the preview, which then reuses the outputs it
/// already wrote.
/// </summary>
public sealed class PreviewJobHandler(StagePreview stagePreview, Preflight preflight, PublishPlan publishPlan, ConfigVersionState config) : IJobHandler
{
    public const string InvalidPayload = "preview.invalid_payload";
    public const string UnknownKind = "preview.unknown_kind";

    public string JobClass => JobClasses.Preview;

    public async Task<JobResult> ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        config.EnsureConfirmed();
        // Preview first (engine design §9.5): parser calls of this job take the next free parser slot ahead of parse jobs.
        using var previewFirst = ParserSlots.AsPreview();
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
                return SyncJobs.Read<PublishPlanPayload>(job) is { } plan
                    ? await publishPlan.RunAsync(job, plan, cancellationToken)
                    : new JobResult.FatalError(InvalidPayload);
            case null:
                return new JobResult.FatalError(InvalidPayload);
            default:
                return new JobResult.FatalError(UnknownKind);
        }
    }
}
