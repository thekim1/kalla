using Npgsql;
using Kalla.Domain.Ports;
using Kalla.Infrastructure.Jobs;
using Kalla.Pipelines.Jobs;
using Kalla.Pipelines.Json;
using Kalla.Worker.Connectors.Persistence;
using Kalla.Worker.Jobs;

namespace Kalla.Worker.Preview;

/// <summary>
/// <c>publish-plan</c> (engine design §12.1 step 2, §12.2; admin design §6.6 step 2): the re-processing plan of publishing,
/// or rolling back to, a version of the pipeline. P1 (weakness review §1) re-processes <b>every live document</b> of the
/// sources that use the pipeline, so the plan is their count per source with a conservative forecast for the full
/// pipeline from <c>parse</c>: the parser pages of parser-bound documents, the embedding tokens of the live chunks, and
/// the time those take at the rates below. Stages whose key did not change are served from the caches at run time, so
/// the run costs at most this. The cost stays 0: the worker reads no price (<c>config."GatewayModels"</c> is not in its
/// grants, data-model §1.1). The plan by effect tags is P2. Read-only.
/// </summary>
public sealed partial class PublishPlan(JobDatabase database, ILogger<PublishPlan> logger)
{
    public const string VersionUnknown = "plan.version_unknown";

    /// <summary>Worker stages from <c>parse</c> per document: the R1 target's 10,000 pages per hour (engine design §18), rounded up.</summary>
    public const double SecondsPerDocument = 0.5;

    /// <summary>One parser page on one slot with OCR, the slow case (an assumption until per-class throughput is measured, P2).</summary>
    public const double SecondsPerParserPage = 3;

    /// <summary>A quarter of the 10,000 tokens/s planning figure of the on-prem embedding model (engine design §18).</summary>
    public const double EmbeddingTokensPerSecond = 2_500;

    /// <summary>The <c>ParseStrategy</c> prefix of parser-bound documents (<c>StrategyString.Paged</c>, <c>Whole</c>).</summary>
    private const string ParserStrategyPrefix = "parse=auto";

    private const string VersionSql = """SELECT 1 FROM config."PipelineVersions" WHERE "Id" = $1 AND "PipelineId" = $2""";

    // Live documents as a reprocess run iterates them (not soft-deleted, with content), per source of the pipeline.
    private const string PlanSql = $"""
        SELECT s."Id", count(d."Id"),
               coalesce(sum(coalesce(d."PageCount", 1)) FILTER (WHERE d."ParseStrategy" LIKE '{ParserStrategyPrefix}%'), 0)::bigint,
               coalesce(sum(t.tokens), 0)::bigint
        FROM config."Sources" s
        LEFT JOIN content."Documents" d ON d."SourceId" = s."Id" AND d."DeletedAt" IS NULL AND d."ContentHash" IS NOT NULL
        LEFT JOIN LATERAL (SELECT sum(c."TokenCount") AS tokens FROM content."Chunks" c WHERE c."ChunkSetId" = d."LiveChunkSetId") t ON true
        WHERE s."PipelineId" = $1 AND s."DeletedAt" IS NULL
        GROUP BY s."Id"
        ORDER BY s."Id"
        """;

    public async Task<JobResult> RunAsync(LeasedJob job, PublishPlanPayload payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(payload);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using (var version = new NpgsqlCommand(VersionSql, connection) { Parameters = { Db.P(payload.PipelineVersionId), Db.P(payload.PipelineId) } })
        {
            if (await version.ExecuteScalarAsync(cancellationToken) is null)
            {
                return new JobResult.FatalError(VersionUnknown);
            }
        }

        var sources = new List<PublishPlanSource>();
        long documents = 0, pages = 0, tokens = 0;
        await using (var plan = new NpgsqlCommand(PlanSql, connection) { Parameters = { Db.P(payload.PipelineId) } })
        await using (var reader = await plan.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var count = reader.GetInt64(1);
                sources.Add(new PublishPlanSource { SourceId = reader.GetGuid(0), Documents = Clamp(count) });
                documents += count;
                pages += reader.GetInt64(2);
                tokens += reader.GetInt64(3);
            }
        }

        var seconds = documents * SecondsPerDocument + pages * SecondsPerParserPage + tokens / EmbeddingTokensPerSecond;
        var result = new PublishPlanResult
        {
            PipelineVersionId = payload.PipelineVersionId,
            Documents = Clamp(documents),
            Sources = sources,
            ParserPages = Clamp(pages),
            EmbeddingTokens = Clamp(tokens),
            EstimatedMinutes = Clamp((long)Math.Ceiling(seconds / 60)),
        };
        LogPlanned(logger, job.Id, payload.PipelineVersionId, result.Documents, sources.Count, result.EstimatedMinutes);
        return new JobResult.Success(ContractJson.Serialize(result));
    }

    private static int Clamp(long value) => (int)Math.Clamp(value, 0, int.MaxValue);

    [LoggerMessage(LogLevel.Information, "Publish plan {JobId} for version {PipelineVersionId}: {Documents} documents in {Sources} sources, about {Minutes} min")]
    private static partial void LogPlanned(ILogger logger, Guid jobId, Guid pipelineVersionId, int documents, int sources, int minutes);
}
