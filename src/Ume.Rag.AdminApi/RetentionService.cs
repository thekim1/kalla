using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ume.Rag.Infrastructure.Persistence;

namespace Ume.Rag.AdminApi;

/// <summary>
/// The nightly retention of the config schema (adapted from the gateway's <c>RetentionService</c>; data-model §8): calls
/// <c>config.rag_retention_config_step</c> (expired admin sessions, outbox rows older than 30 days) until a batch comes back
/// smaller than the batch size. Each batch is one statement under <c>pg_try_advisory_xact_lock</c>, so several instances
/// share or skip the work (repo-and-deployment §3, <c>Admin:Retention</c>). The chat step (<c>chat.rag_retention_chat_step</c>) runs after it under its own lock; the audit step joins it with its
/// tables (P1).
/// </summary>
public sealed partial class RetentionService(IServiceScopeFactory scopes, IOptions<AdminOptions> options, TimeProvider time, ILogger<RetentionService> logger)
    : BackgroundService
{
    /// <summary>Rows per step and table; each call is its own short transaction.</summary>
    public const int BatchSize = 5000;

    /// <summary>Advisory lock key of the config step ("RAG0" + 0x11).</summary>
    public const long ConfigStepLockKey = 0x5241_4730_0011;

    /// <summary>The chat step's advisory lock (chat design §13.1).</summary>
    public const long ChatStepLockKey = 0x5241_4730_0012;

    /// <summary>After the worker's content maintenance (02:00), as the gateway's nightly run.</summary>
    public static readonly TimeOnly RunAt = new(3, 0);

    public static readonly TimeZoneInfo Stockholm = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm");

    private int _running;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Retention)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = time.GetUtcNow();
            await Task.Delay(NextRunAt(now) - now, time, stoppingToken);
            await RunOnceAsync(stoppingToken);
        }
    }

    /// <summary>
    /// The next 03:00 in Stockholm after <paramref name="now"/>; on the spring DST day a missing hour moves it one hour on.
    /// Copied from the worker's <c>MaintenanceService.NextRunAt</c>.
    /// </summary>
    public static DateTimeOffset NextRunAt(DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, Stockholm);
        for (var day = 0; day <= 2; day++)
        {
            var candidate = DateOnly.FromDateTime(local.DateTime).AddDays(day).ToDateTime(RunAt);
            if (Stockholm.IsInvalidTime(candidate))
            {
                candidate = candidate.AddHours(1);
            }

            var at = new DateTimeOffset(candidate, Stockholm.GetUtcOffset(candidate));
            if (at > now)
            {
                return at.ToUniversalTime();
            }
        }

        return now.AddDays(1);
    }

    /// <summary>
    /// One complete run; never throws except on shutdown. Returns the rows removed, or -1 when another instance held the
    /// lock (it does the rest).
    /// </summary>
    public async Task<long> RunOnceAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return 0;
        }

        long removed = 0;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<RagDbContext>();
            long step;
            do
            {
                // -1: another instance holds this step's lock right now.
                step = await db.Database.SqlQuery<long>(
                    $"""SELECT CASE WHEN pg_try_advisory_xact_lock({ConfigStepLockKey}) THEN config.rag_retention_config_step({BatchSize}) ELSE -1 END AS "Value" """)
                    .SingleAsync(cancellationToken);
                removed += Math.Max(step, 0);
            }
            while (step >= BatchSize);

            var skipped = step < 0;

            // Chat metadata (chat design §13.1: expired messages, then empty expired conversations, totals after 3 years).
            do
            {
                step = await db.Database.SqlQuery<long>(
                    $"""SELECT CASE WHEN pg_try_advisory_xact_lock({ChatStepLockKey}) THEN chat.rag_retention_chat_step({BatchSize}) ELSE -1 END AS "Value" """)
                    .SingleAsync(cancellationToken);
                removed += Math.Max(step, 0);
            }
            while (step >= BatchSize);

            skipped &= step < 0;
            LogRun(logger, removed, skipped);
            return skipped && removed == 0 ? -1 : removed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogRunFailed(logger, ex is DbException { SqlState: { } state } ? $"{ex.GetType().Name} (SQL state {state})" : ex.GetType().Name, removed);
            return removed;
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Config retention removed {Removed} rows (another instance held the lock: {Skipped})")]
    private static partial void LogRun(ILogger logger, long removed, bool skipped);

    [LoggerMessage(Level = LogLevel.Error, Message = "Config retention failed ({Error}) after removing {Removed} rows; the next run continues")]
    private static partial void LogRunFailed(ILogger logger, string error, long removed);
}
