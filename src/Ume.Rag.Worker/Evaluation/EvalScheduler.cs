using Cronos;
using Microsoft.Extensions.Options;
using Npgsql;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Infrastructure.Jobs;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Pipelines.Json;
using Ume.Rag.Worker.Connectors.Persistence;
using Ume.Rag.Worker.Engine;
using Ume.Rag.Worker.Jobs;

namespace Ume.Rag.Worker.Evaluation;

/// <summary>
/// The nightly evaluation schedule (processing design §14.8, engine design §9.6 "the scheduler also enqueues the nightly
/// evaluation runs"): every <c>Worker:SchedulerSeconds</c> the instance holding the advisory lock takes the latest
/// occurrence of <see cref="Cron"/> (Europe/Stockholm, after the nightly syncs and the drift sweep) that has passed, and
/// enqueues one <c>nightly</c> job with <c>ScheduledFor</c> = that occurrence for every published collection with an
/// active question and no <c>nightly</c> job enqueued since it (dedup <c>eval-nightly:{collectionId}</c>). Missed
/// occurrences after an outage collapse into one run; a job that failed is not enqueued again before the next night.
/// </summary>
public sealed partial class EvalScheduler(JobDatabase database, IJobQueue queue, IOptions<WorkerOptions> options, TimeProvider time, ILogger<EvalScheduler> logger)
    : BackgroundService
{
    /// <summary>03:30 every night.</summary>
    public const string Cron = "30 3 * * *";

    private const long LockKey = 0x5241_4731_0005;

    private static readonly CronExpression Expression = CronExpression.Parse(Cron);

    private const string DueSql = $"""
        SELECT c."Id", c."DepartmentId", c."Classification"
        FROM config."Collections" c
        WHERE c."PublishedAt" IS NOT NULL AND c."DeletedAt" IS NULL
          AND EXISTS (SELECT 1 FROM content."EvaluationSets" s JOIN content."EvaluationQuestions" q ON q."SetId" = s."Id"
                      WHERE s."CollectionId" = c."Id" AND q."State" = '{EvaluationQuestionStates.Active}')
          AND NOT EXISTS (SELECT 1 FROM content."Jobs" j
                          WHERE j."Class" = '{JobClasses.Eval}' AND j."DedupKey" = 'eval-nightly:' || c."Id"::text AND j."EnqueuedAt" >= $1)
        ORDER BY c."Id"
        """;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.SchedulerSeconds), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                LogFailed(logger, ex);
            }
        }
    }

    /// <summary>One pass under the advisory lock; returns the nightly jobs enqueued.</summary>
    public async Task<int> TickAsync(CancellationToken cancellationToken)
    {
        if (LatestOccurrence(time.GetUtcNow()) is not { } occurrence)
        {
            return 0;
        }

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock($1)", connection, transaction) { Parameters = { Db.P(LockKey) } })
        {
            if (await lockCommand.ExecuteScalarAsync(cancellationToken) is not true)
            {
                return 0;
            }
        }

        var jobs = new List<JobRequest>();
        await using (var command = new NpgsqlCommand(DueSql, connection, transaction) { Parameters = { Db.P(occurrence.ToUniversalTime()) } })
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var collectionId = reader.GetGuid(0);
                var payload = new EvalNightlyPayload { CollectionId = collectionId, ScheduledFor = occurrence };
                jobs.Add(new JobRequest(JobClasses.Eval, reader.GetGuid(1), reader.GetString(2), ContractJson.Serialize(payload))
                {
                    DedupKey = JobDedupKeys.EvalNightly(collectionId),
                });
            }
        }

        var enqueued = jobs.Count == 0 ? 0 : (await queue.EnqueueAsync(jobs, transaction, cancellationToken)).Count;
        await transaction.CommitAsync(cancellationToken);
        if (enqueued > 0)
        {
            LogEnqueued(logger, enqueued, occurrence);
        }

        return enqueued;
    }

    /// <summary>The latest occurrence of <see cref="Cron"/> at or before <paramref name="now"/>.</summary>
    public static DateTimeOffset? LatestOccurrence(DateTimeOffset now)
    {
        DateTimeOffset? latest = null;
        var cursor = now.AddDays(-2);
        while (Expression.GetNextOccurrence(cursor, SyncScheduler.Stockholm) is { } next && next <= now)
        {
            latest = next;
            cursor = next;
        }

        return latest;
    }

    [LoggerMessage(LogLevel.Information, "Enqueued {Count} nightly evaluation runs for {ScheduledFor}")]
    private static partial void LogEnqueued(ILogger logger, int count, DateTimeOffset scheduledFor);

    [LoggerMessage(LogLevel.Warning, "The evaluation scheduler pass failed; it runs again at the next tick")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
