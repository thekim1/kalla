using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ume.Rag.Infrastructure.Persistence;

namespace Ume.Rag.AdminApi;

/// <summary>
/// Keeps the publish report of a pipeline version past job retention (#129 request 2; data-model §2.1
/// <c>PipelineVersions.EvaluationRunId</c>): every few minutes, a version without one takes the <c>EvaluationRunId</c> of the
/// newest succeeded <c>eval</c> <c>publish-report</c> job for it (payload <c>pipelineVersionId</c>, result
/// <c>evaluationRunId</c>). One statement, idempotent (only rows still null), so several instances may run it. Derived from the
/// worker's result, so it is neither a user change nor audited; the config version moves as for any config row.
/// </summary>
public sealed partial class PublishReportLinker(IServiceScopeFactory scopes, IOptions<AdminOptions> options, TimeProvider time, ILogger<PublishReportLinker> logger)
    : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);

    /// <summary>Only jobs finished this recently are looked at; a version missed longer than this keeps null.</summary>
    public static readonly TimeSpan Lookback = TimeSpan.FromDays(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.PublishReportLinker)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(Interval, time, stoppingToken);
            try
            {
                await LinkAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LogFailed(logger, e);
            }
        }
    }

    /// <summary>Links what can be linked now; returns the number of versions updated.</summary>
    public async Task<int> LinkAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RagDbContext>();
        var since = time.GetUtcNow() - Lookback;
        var linked = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE config."PipelineVersions" v SET "EvaluationRunId" = r.run
            FROM (
              SELECT DISTINCT ON ((j."Payload"::jsonb ->> 'pipelineVersionId')::uuid)
                     (j."Payload"::jsonb ->> 'pipelineVersionId')::uuid AS version, (j."Result"::jsonb ->> 'evaluationRunId')::uuid AS run
              FROM content."Jobs" j
              WHERE j."Class" = 'eval' AND j."State" = 'succeeded' AND j."CompletedAt" > {since}
                AND j."Payload"::jsonb ->> 'kind' = 'publish-report' AND j."Result" IS NOT NULL
              ORDER BY (j."Payload"::jsonb ->> 'pipelineVersionId')::uuid, j."CompletedAt" DESC
            ) r
            WHERE v."Id" = r.version AND v."EvaluationRunId" IS NULL AND r.run IS NOT NULL
            """, ct);
        if (linked > 0)
        {
            LogLinked(logger, linked);
        }

        return linked;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Linked {Count} pipeline versions to their publish report")]
    private static partial void LogLinked(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Linking publish reports to pipeline versions failed")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
