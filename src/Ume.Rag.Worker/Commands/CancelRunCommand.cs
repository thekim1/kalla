using System.Text.Json;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Worker.Engine;
using Ume.Rag.Worker.Jobs;

namespace Ume.Rag.Worker.Commands;

/// <summary>
/// <c>cancel-run</c> (admin design §4.7, engine design §10.1): <c>{"kind":"cancel-run","runId":"…"}</c>, dedup key
/// <c>cancel:{runId}</c>. Cancels the run's queued and running jobs; a running one stops at its owner's next heartbeat
/// without committing; the run ends <c>cancelled</c>. Result: <c>{"cancelledJobs":n}</c>.
/// </summary>
public sealed class CancelRunCommand(IJobQueue queue, RunStore runs) : ICommandHandler
{
    public const string KindName = "cancel-run";

    /// <summary><c>LastError</c> of the cancelled jobs.</summary>
    public const string CancelledReason = "run.cancelled";

    public string Kind => KindName;

    public async Task<JobResult> ExecuteAsync(JobContext context, JsonElement payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!payload.TryGetProperty("runId", out var runIdElement) || runIdElement.ValueKind != JsonValueKind.String ||
            !runIdElement.TryGetGuid(out var runId))
        {
            return new JobResult.FatalError(CommandJobHandler.InvalidPayload);
        }

        var cancelled = await queue.CancelRunAsync(runId, CancelledReason, context.Job.Id, cancellationToken);
        await runs.CancelAsync(runId, cancellationToken);
        return new JobResult.Success(JsonSerializer.Serialize(new CancelRunResult(cancelled), CancelRunResult.JsonOptions));
    }

    private sealed record CancelRunResult(int CancelledJobs)
    {
        public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    }
}
