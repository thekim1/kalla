using Npgsql;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Worker.Connectors.Persistence;
using Ume.Rag.Worker.Evaluation.Generation;

namespace Ume.Rag.Worker.Evaluation;

/// <summary>Why the scheduler enqueues a set job for a collection.</summary>
public enum EvalSetTrigger
{
    None,

    /// <summary><c>generate-set</c>: the collection's first syncs finished and it has no generated layer.</summary>
    FirstSyncs,

    /// <summary><c>top-up-set</c>: no set job of the collection since the latest weekly occurrence.</summary>
    Weekly,

    /// <summary><c>top-up-set</c>: <c>D</c> grew by at least <see cref="EvalSetTriggers.GrowthPercent"/> since the last round.</summary>
    Growth,
}

/// <summary>
/// What the scheduler knows of a collection for its set triggers: whether it has a generated layer, <c>D</c> at the
/// layer's last round (<c>Origin.documents</c>, null for questions written before it was recorded), the active generated
/// questions, <c>D</c> now (documents with a live set), whether its first syncs finished (every active source has a
/// finished run and none is running) and when its last set job was enqueued (any state; terminal jobs are deleted after
/// 7 days, engine design §9.4).
/// </summary>
public sealed record EvalSetCandidate(Guid CollectionId, Guid DepartmentId, string Classification, bool HasGeneratedLayer, int? RoundDocuments,
    int ActiveGenerated, int LiveDocuments, bool FirstSyncsFinished, DateTimeOffset? LastEnqueuedAt);

/// <summary>
/// The set triggers of processing design §13.1 and §13.3, read by <see cref="EvalScheduler"/> in its tick: a
/// <c>generate-set</c> after a collection's first syncs finish, the weekly <c>top-up-set</c> check (engine design §9.6)
/// and a <c>top-up-set</c> when <c>D</c> grew by ≥ 25 % since the last round. Every job uses the dedup key
/// <see cref="JobDedupKeys.EvalGenerate"/>, and a trigger never fires while a set job of the collection exists that it
/// would repeat, so a second tick enqueues nothing. Nothing is enqueued without <c>Settings.EvaluationModel</c>: the job
/// would refuse it.
/// </summary>
public static class EvalSetTriggers
{
    /// <summary>A top-up when <c>D</c> grew by at least this share since the last round (processing design §13.3).</summary>
    public const int GrowthPercent = 25;

    /// <summary>
    /// The least time between set jobs for a growth top-up, so a top-up that could not raise the layer (every candidate
    /// filtered out, so <c>D</c> at the last round stays as it was) is repeated at most daily.
    /// </summary>
    public static readonly TimeSpan GrowthCooldown = TimeSpan.FromDays(1);

    private const string SettingsSql = """
        SELECT "EvaluationModel", "EvaluationMaxQuestions" FROM config."Settings" ORDER BY "Id" LIMIT 1
        """;

    private const string FinishedRunStates = $"'{RunStates.Succeeded}', '{RunStates.Degraded}', '{RunStates.AwaitingDeletionConfirmation}'";

    private const string ActiveSource = $"""s."CollectionId" = c."Id" AND s."State" = '{SourceStates.Active}' AND s."DeletedAt" IS NULL""";

    private const string CandidatesSql = $"""
        SELECT c."Id", c."DepartmentId", c."Classification", g."Questions" IS NOT NULL, g."Documents", coalesce(g."Active", 0),
               (SELECT count(*) FROM content."Documents" d JOIN content."ChunkSets" cs ON cs."Id" = d."LiveChunkSetId"
                WHERE cs."CollectionId" = c."Id" AND cs."State" = 'live' AND d."DeletedAt" IS NULL),
               EXISTS (SELECT 1 FROM config."Sources" s WHERE {ActiveSource})
               AND NOT EXISTS (SELECT 1 FROM config."Sources" s
                               WHERE {ActiveSource}
                                 AND (NOT EXISTS (SELECT 1 FROM content."Runs" r WHERE r."SourceId" = s."Id" AND r."State" IN ({FinishedRunStates}))
                                      OR EXISTS (SELECT 1 FROM content."Runs" r WHERE r."SourceId" = s."Id" AND r."State" = '{RunStates.Running}'))),
               (SELECT max(j."EnqueuedAt") FROM content."Jobs" j WHERE j."Class" = '{JobClasses.Eval}' AND j."DedupKey" = 'eval-generate:' || c."Id"::text)
        FROM config."Collections" c
        LEFT JOIN LATERAL (
            SELECT count(*) AS "Questions",
                   count(*) FILTER (WHERE q."State" = '{EvaluationQuestionStates.Active}' AND q."RetiredAt" IS NULL) AS "Active",
                   (array_agg((q."Origin" ->> 'documents')::int ORDER BY (q."Origin" ->> 'round')::int DESC NULLS LAST))[1] AS "Documents"
            FROM content."EvaluationSets" es JOIN content."EvaluationQuestions" q ON q."SetId" = es."Id"
            WHERE es."CollectionId" = c."Id" AND q."Layer" = '{EvaluationLayers.Generated}'
            HAVING count(*) > 0
        ) g ON true
        WHERE c."DeletedAt" IS NULL
        ORDER BY c."Id"
        """;

    /// <summary>The set jobs due now, under the caller's transaction (the scheduler's advisory lock).</summary>
    public static async Task<IReadOnlyList<(EvalSetCandidate Collection, EvalSetTrigger Trigger)>> DueAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, DateTimeOffset now, DateTimeOffset? weekly, CancellationToken cancellationToken)
    {
        int maxQuestions;
        await using (var command = new NpgsqlCommand(SettingsSql, connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken) || string.IsNullOrWhiteSpace(Db.String(reader, 0)))
            {
                return [];
            }

            maxQuestions = reader.GetInt32(1);
        }

        var due = new List<(EvalSetCandidate, EvalSetTrigger)>();
        await using (var command = new NpgsqlCommand(CandidatesSql, connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var candidate = new EvalSetCandidate(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetBoolean(3), Db.Nullable<int>(reader, 4),
                    (int)reader.GetInt64(5), (int)reader.GetInt64(6), reader.GetBoolean(7), Db.Nullable<DateTimeOffset>(reader, 8));
                if (Due(candidate, now, weekly, maxQuestions) is not EvalSetTrigger.None and var trigger)
                {
                    due.Add((candidate, trigger));
                }
            }
        }

        return due;
    }

    /// <summary>
    /// The trigger due for <paramref name="collection"/>: without a generated layer, <see cref="EvalSetTrigger.FirstSyncs"/>
    /// once its first syncs finished with a live document and no set job exists; with one, <see cref="EvalSetTrigger.Weekly"/>
    /// when no set job was enqueued since <paramref name="weekly"/>, else <see cref="EvalSetTrigger.Growth"/> when <c>D</c>
    /// grew by ≥ <see cref="GrowthPercent"/> % since the last round, the layer is below its target for the new <c>D</c>
    /// (<c>min(max, 3 × D)</c>) and the last set job is at least <see cref="GrowthCooldown"/> old.
    /// </summary>
    public static EvalSetTrigger Due(EvalSetCandidate collection, DateTimeOffset now, DateTimeOffset? weekly, int maxQuestions)
    {
        ArgumentNullException.ThrowIfNull(collection);
        if (!collection.HasGeneratedLayer)
        {
            return collection.FirstSyncsFinished && collection.LiveDocuments > 0 && collection.LastEnqueuedAt is null ? EvalSetTrigger.FirstSyncs : EvalSetTrigger.None;
        }

        if (weekly is { } occurrence && (collection.LastEnqueuedAt is not { } enqueued || enqueued < occurrence))
        {
            return EvalSetTrigger.Weekly;
        }

        var grew = collection.RoundDocuments is int before && before > 0 && 100L * collection.LiveDocuments >= (100L + GrowthPercent) * before;
        var belowTarget = collection.ActiveGenerated < ChunkSampler.Target(maxQuestions, collection.LiveDocuments, int.MaxValue);
        var rested = collection.LastEnqueuedAt is not { } last || last <= now - GrowthCooldown;
        return grew && belowTarget && rested ? EvalSetTrigger.Growth : EvalSetTrigger.None;
    }
}
