using System.Text.Json;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Infrastructure.Config;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Worker.Commands;
using Ume.Rag.Worker.Jobs;

namespace Ume.Rag.Worker.Evaluation;

/// <summary>
/// The <c>eval</c> kinds that are not retrieval runs (<c>generate-set</c>, <c>top-up-set</c>, processing design §13.1,
/// §13.3). Registered in the job's services, it receives every kind <see cref="EvalJobHandler"/> does not run itself; it
/// reads its payload from <see cref="JobContext.Job"/> and answers an unknown kind itself.
/// </summary>
public interface IEvalSetHandler
{
    Task<JobResult> ExecuteAsync(JobContext context, string kind, CancellationToken cancellationToken);
}

/// <summary>
/// <c>eval</c> jobs (engine design §9.1: concurrency 1, lease 15 minutes, two attempts): <c>on-demand</c>,
/// <c>publish-report</c> and <c>nightly</c> run here (<see cref="EvaluationRunner"/>); any other kind goes to the
/// <see cref="IEvalSetHandler"/> when one is registered, and is refused with <see cref="Unsupported"/> otherwise. A
/// payload that is not valid against its schema fails at once.
/// </summary>
public sealed partial class EvalJobHandler(EvaluationRunner runner, ConfigVersionState config, ILogger<EvalJobHandler> logger) : IJobHandler
{
    public const string InvalidPayload = "eval.invalid_payload";
    public const string Unsupported = "eval.unsupported";

    public string JobClass => JobClasses.Eval;

    public async Task<JobResult> ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        config.EnsureConfirmed();
        var job = context.Job;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(job.Payload);
        }
        catch (JsonException)
        {
            return new JobResult.FatalError(InvalidPayload);
        }

        using (document)
        {
            var payload = document.RootElement;
            var kind = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("kind", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
            switch (kind)
            {
                case null:
                    return new JobResult.FatalError(InvalidPayload);
                case EvalKinds.OnDemand:
                    return CommandPayload.Read<EvalOnDemandPayload>(payload) is { } onDemand
                        ? await runner.OnDemandAsync(job, onDemand, cancellationToken)
                        : new JobResult.FatalError(InvalidPayload);
                case EvalKinds.PublishReport:
                    return CommandPayload.Read<EvalPublishReportPayload>(payload) is { } report
                        ? await runner.PublishReportAsync(job, report, cancellationToken)
                        : new JobResult.FatalError(InvalidPayload);
                case EvalKinds.Nightly:
                    return CommandPayload.Read<EvalNightlyPayload>(payload) is { } nightly
                        ? await runner.NightlyAsync(job, nightly, cancellationToken)
                        : new JobResult.FatalError(InvalidPayload);
                default:
                    if (context.Services.GetService<IEvalSetHandler>() is { } sets)
                    {
                        return await sets.ExecuteAsync(context, kind, cancellationToken);
                    }

                    LogUnsupported(logger, job.Id, kind);
                    return new JobResult.FatalError(Unsupported);
            }
        }
    }

    [LoggerMessage(LogLevel.Warning, "Eval job {JobId}: kind {Kind} has no handler in this build")]
    private static partial void LogUnsupported(ILogger logger, Guid jobId, string kind);
}
