using Cronos;
using Microsoft.Extensions.Options;
using Npgsql;
using Kalla.Domain;
using Kalla.Domain.Entities;
using Kalla.Domain.Ports;
using Kalla.Infrastructure.Jobs;
using Kalla.Pipelines.Jobs;
using Kalla.Pipelines.Json;
using Kalla.Worker.Connectors.Persistence;
using Kalla.Worker.Jobs;

namespace Kalla.Worker.Evaluation;

/// <summary>
/// The evaluation schedule (engine design §9.6 "the scheduler also enqueues the nightly evaluation runs"): every
/// <c>Worker:SchedulerSeconds</c> the instance holding the advisory lock enqueues, in one transaction,
/// <list type="bullet">
/// <item>the nightly runs (processing design §14.8): it takes the latest occurrence of <see cref="Cron"/> (Europe/Stockholm,
/// after the nightly syncs and the drift sweep) that has passed and enqueues one <c>nightly</c> job with
/// <c>ScheduledFor</c> = that occurrence for every published collection with a question a run reads (an active one, or a
/// <c>stale</c> <c>human</c> one the run makes active again once its documents are back) and no <c>nightly</c> job
/// enqueued since it (dedup <c>eval-nightly:{collectionId}</c>). Missed occurrences after an outage collapse into one run;
/// a job that failed is not enqueued again before the next night;</item>
/// <item>the set jobs of <see cref="EvalSetTriggers"/> (processing design §13.1, §13.3): <c>generate-set</c> after a
/// collection's first syncs, <c>top-up-set</c> once per occurrence of <see cref="WeeklyCron"/> and when <c>D</c> grew by
/// 25 % (dedup <c>eval-generate:{collectionId}</c>).</item>
/// </list>
/// </summary>
public sealed partial class EvalScheduler(JobDatabase database, IJobQueue queue, IOptions<WorkerOptions> options, TimeProvider time, ILogger<EvalScheduler> logger)
    : BackgroundService
{
    /// <summary>03:30 every night.</summary>
    public const string Cron = "30 3 * * *";

    /// <summary>The weekly top-up check: Sunday 04:30, after Saturday's crawls and the night's runs.</summary>
    public const string WeeklyCron = "30 4 * * 0";

    private const long LockKey = 0x5241_4731_0005;

    private static readonly CronExpression Expression = CronExpression.Parse(Cron);
    private static readonly CronExpression WeeklyExpression = CronExpression.Parse(WeeklyCron);

    private const string DueSql = $"""
        SELECT c."Id", c."DepartmentId", c."Classification"
        FROM config."Collections" c
        WHERE c."PublishedAt" IS NOT NULL AND c."DeletedAt" IS NULL
          AND EXISTS (SELECT 1 FROM content."EvaluationSets" s JOIN content."EvaluationQuestions" q ON q."SetId" = s."Id"
                      WHERE s."CollectionId" = c."Id" AND {EvaluationStore.RunQuestionFilter})
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

    /// <summary>One pass under the advisory lock; returns the jobs enqueued (nightly runs and set jobs).</summary>
    public async Task<int> TickAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock($1)", connection, transaction) { Parameters = { Db.P(LockKey) } })
        {
            if (await lockCommand.ExecuteScalarAsync(cancellationToken) is not true)
            {
                return 0;
            }
        }

        var nightly = new List<JobRequest>();
        var occurrence = LatestOccurrence(now);
        if (occurrence is { } night)
        {
            await using var command = new NpgsqlCommand(DueSql, connection, transaction) { Parameters = { Db.P(night.ToUniversalTime()) } };
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var collectionId = reader.GetGuid(0);
                var payload = new EvalNightlyPayload { CollectionId = collectionId, ScheduledFor = night };
                nightly.Add(new JobRequest(JobClasses.Eval, reader.GetGuid(1), reader.GetString(2), ContractJson.Serialize(payload))
                {
                    DedupKey = JobDedupKeys.EvalNightly(collectionId),
                });
            }
        }

        var sets = new List<(JobRequest Job, Guid CollectionId, string Kind, EvalSetTrigger Trigger)>();
        foreach (var (collection, trigger) in await EvalSetTriggers.DueAsync(connection, transaction, now, LatestWeeklyOccurrence(now), cancellationToken))
        {
            var (kind, json) = trigger == EvalSetTrigger.FirstSyncs
                ? (EvalKinds.GenerateSet, ContractJson.Serialize(new EvalGenerateSetPayload { CollectionId = collection.CollectionId }))
                : (EvalKinds.TopUpSet, ContractJson.Serialize(new EvalTopUpSetPayload { CollectionId = collection.CollectionId }));
            sets.Add((new JobRequest(JobClasses.Eval, collection.DepartmentId, collection.Classification, json)
            {
                DedupKey = JobDedupKeys.EvalGenerate(collection.CollectionId),
            }, collection.CollectionId, kind, trigger));
        }

        List<JobRequest> jobs = [.. nightly, .. sets.Select(s => s.Job)];
        IReadOnlySet<Guid> enqueued = jobs.Count == 0 ? new HashSet<Guid>() : await queue.EnqueueAsync(jobs, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var nights = nightly.Count(j => enqueued.Contains(j.Id));
        if (nights > 0)
        {
            LogEnqueued(logger, nights, occurrence!.Value);
        }

        foreach (var set in sets.Where(s => enqueued.Contains(s.Job.Id)))
        {
            LogSetEnqueued(logger, set.CollectionId, set.Kind, set.Trigger);
        }

        return enqueued.Count;
    }

    /// <summary>The latest occurrence of <see cref="Cron"/> at or before <paramref name="now"/>.</summary>
    public static DateTimeOffset? LatestOccurrence(DateTimeOffset now) => Latest(Expression, now, TimeSpan.FromDays(2));

    /// <summary>The latest occurrence of <see cref="WeeklyCron"/> at or before <paramref name="now"/>.</summary>
    public static DateTimeOffset? LatestWeeklyOccurrence(DateTimeOffset now) => Latest(WeeklyExpression, now, TimeSpan.FromDays(8));

    private static DateTimeOffset? Latest(CronExpression expression, DateTimeOffset now, TimeSpan lookBack)
    {
        DateTimeOffset? latest = null;
        var cursor = now - lookBack;
        while (expression.GetNextOccurrence(cursor, StockholmTime.Zone) is { } next && next <= now)
        {
            latest = next;
            cursor = next;
        }

        return latest;
    }

    [LoggerMessage(LogLevel.Information, "Enqueued {Count} nightly evaluation runs for {ScheduledFor}")]
    private static partial void LogEnqueued(ILogger logger, int count, DateTimeOffset scheduledFor);

    [LoggerMessage(LogLevel.Information, "Collection {CollectionId}: {Kind} enqueued ({Trigger})")]
    private static partial void LogSetEnqueued(ILogger logger, Guid collectionId, string kind, EvalSetTrigger trigger);

    [LoggerMessage(LogLevel.Warning, "The evaluation scheduler pass failed; it runs again at the next tick")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
