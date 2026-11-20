using System.Text.Json;
using System.Text.Json.Nodes;
using Kalla.Domain.Ports;
using Kalla.Infrastructure.Jobs;
using Kalla.Pipelines.Jobs;
using Kalla.Pipelines.Json;
using Kalla.Worker.Connectors.Deletion;
using Kalla.Worker.Connectors.Persistence;
using Kalla.Worker.Engine;
using Kalla.Worker.Jobs;

namespace Kalla.Worker.Commands;

/// <summary>
/// <c>check-hnsw-index</c> (knowledge design §6.4): <see cref="HnswIndexHealth"/> measures the profile's index and
/// rebuilds it below the floor. A rebuild is audited as a system action (<c>audit."AuditLog"</c>, actor
/// <c>system:worker</c>, action <c>hnsw-reindex</c>, entity the embedding profile, both recalls in the details), committed
/// with the job. A profile without an index has nothing to measure. Result <see cref="CheckHnswIndexResult"/>.
/// </summary>
public sealed class CheckHnswIndexHandler(JobDatabase database, IJobQueue queue, WorkerIdentity identity, TimeProvider time) : ICommandHandler
{
    public const string ProfileUnknown = "embedding_profile.unknown";
    public const string AuditAction = "hnsw-reindex";
    public const string AuditEntityType = "embeddingprofile";

    private const string AuditSql = """
        INSERT INTO audit."AuditLog" ("Timestamp", "Actor", "Action", "EntityType", "EntityId", "Details", "Service")
        VALUES ($1, $2, $3, $4, $5, $6::jsonb, $7)
        """;

    public string Kind => CommandKinds.CheckHnswIndex;

    public async Task<JobResult> ExecuteAsync(JobContext context, JsonElement payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (CommandPayload.Read<CheckHnswIndexCommand>(payload) is not { } check)
        {
            return new JobResult.FatalError(CommandJobHandler.InvalidPayload);
        }

        var health = context.Services.GetRequiredService<HnswIndexHealth>();
        if (await health.DimensionsAsync(check.EmbeddingProfileId, cancellationToken) is not { } dimensions)
        {
            return new JobResult.FatalError(ProfileUnknown);
        }

        var settings = await health.SettingsAsync(cancellationToken);
        var outcome = await health.CheckAsync(check.EmbeddingProfileId, dimensions, settings, cancellationToken);
        await using var commit = await EngineCommit.BeginAsync(database, cancellationToken);
        if (outcome is { After: { } after })
        {
            var details = new JsonObject
            {
                ["index"] = outcome.IndexName,
                ["trigger"] = ContractJson.Serialize(check.Trigger).Trim('"'),
                ["floor"] = settings.RecallFloor,
                ["recallBefore"] = outcome.Before.Recall,
                ["recallAfter"] = after.Recall,
            }.ToJsonString();
            await CommandCommit.ExecuteAsync(commit, AuditSql, cancellationToken,
                Db.P(time.GetUtcNow().ToUniversalTime()), Db.P(DeletionProposalLifecycle.System), Db.P(AuditAction), Db.P(AuditEntityType),
                Db.P(check.EmbeddingProfileId.ToString("D")), Db.P(details), Db.P(ParseRuleRows.AuditService));
        }

        var result = new CheckHnswIndexResult
        {
            Scopes = outcome?.Before.Scopes ?? 0,
            Queries = outcome?.Before.Queries ?? 0,
            Recall = outcome?.Before.Recall,
            Rebuilt = outcome?.Rebuilt ?? false,
            RecallAfter = outcome?.After?.Recall,
        };
        return await CommandCommit.CompleteAsync(commit, queue, context.Job, identity, result, cancellationToken);
    }
}
