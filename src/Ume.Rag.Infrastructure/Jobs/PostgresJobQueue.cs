using System.Data.Common;
using Npgsql;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Domain.Ports;

namespace Ume.Rag.Infrastructure.Jobs;

/// <summary>
/// <see cref="IJobQueue"/> over <c>content."Jobs"</c> (data-model §4.1–§4.3): dedup by the partial unique index on
/// <c>DedupKey</c>, one job per dequeue statement under <c>FOR UPDATE SKIP LOCKED</c>, leases and back-off on the
/// database clock. Statements hold a pooled connection only while they run; nothing sets session state. Every change
/// that queues a job (enqueue, a transient failure, a release) notifies <c>rag_jobs</c> at its commit.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "The IJobQueue implementation; the name is fixed by the design (data-model §4).")]
public sealed class PostgresJobQueue(JobDatabase database, JobWakeUps wakeUps) : IJobQueue
{
    private const string EnqueueSql = """
        INSERT INTO content."Jobs" ("Id", "Class", "Priority", "DepartmentId", "TeamId", "Classification", "DedupKey", "Payload",
                                    "AvailableAt", "MaxAttempts", "RunId")
        SELECT x.id, x.class, x.priority, x.department, x.team, x.classification, x.dedup, x.payload::jsonb,
               now() + x.delay, x.max_attempts, x.run
        FROM unnest($1::uuid[], $2::varchar[], $3::smallint[], $4::uuid[], $5::uuid[], $6::varchar[], $7::varchar[], $8::text[],
                    $9::interval[], $10::smallint[], $11::uuid[])
             AS x(id, class, priority, department, team, classification, dedup, payload, delay, max_attempts, run)
        ON CONFLICT ("DedupKey") WHERE "State" IN ('queued','running') DO NOTHING
        RETURNING "Id", "Class"
        """;

    // One pass over the class's queued jobs: the ready ones per department, and how long until the next one that is
    // not ready yet, on the database clock. A department with only later jobs has a null priority.
    private const string ReadyDepartmentsSql = """
        SELECT "DepartmentId",
               max("Priority") FILTER (WHERE "AvailableAt" <= now()),
               min("AvailableAt") FILTER (WHERE "AvailableAt" <= now()),
               min("AvailableAt") FILTER (WHERE "AvailableAt" > now()) - now()
        FROM content."Jobs"
        WHERE "State" = 'queued' AND "Class" = $1
        GROUP BY "DepartmentId"
        """;

    // data-model §4.2, unchanged apart from the explicit column list.
    private const string DequeueSql = """
        WITH next AS (
            SELECT "Id" FROM content."Jobs"
            WHERE "State" = 'queued' AND "Class" = $1 AND "DepartmentId" = $2 AND "AvailableAt" <= now()
            ORDER BY "Priority" DESC, "AvailableAt"
            LIMIT 1
            FOR UPDATE SKIP LOCKED
        )
        UPDATE content."Jobs" j
        SET "State" = 'running', "LeaseOwner" = $3, "LeaseUntil" = now() + $4::interval, "Attempts" = j."Attempts" + 1
        FROM next WHERE j."Id" = next."Id"
        RETURNING j."Id", j."Class", j."Priority", j."DepartmentId", j."TeamId", j."Classification", j."DedupKey", j."Payload"::text,
                  j."Attempts", j."MaxAttempts", j."Reclaims", j."RunId", j."LastError", j."EnqueuedAt", j."LeaseUntil"
        """;

    private const string HeartbeatSql = """
        UPDATE content."Jobs" j SET "LeaseUntil" = now() + l.lease
        FROM unnest($2::uuid[], $3::interval[]) AS l(id, lease)
        WHERE j."Id" = l.id AND j."LeaseOwner" = $1 AND j."State" = 'running'
        RETURNING j."Id"
        """;

    private const string CompleteSql = """
        UPDATE content."Jobs" SET "State" = 'succeeded', "CompletedAt" = now(), "Result" = $3::jsonb
        WHERE "Id" = $1 AND "State" = 'running' AND "LeaseOwner" = $2
        """;

    // Back-off: least(10 s · 2^Attempts, 1 h) ± 20 % (data-model §4.2); Attempts already counts the failed run.
    private const string FailSql = """
        UPDATE content."Jobs" SET
            "State" = CASE WHEN $4 THEN 'failed' WHEN "Attempts" >= "MaxAttempts" THEN 'dead' ELSE 'queued' END,
            "CompletedAt" = CASE WHEN $4 OR "Attempts" >= "MaxAttempts" THEN now() END,
            "AvailableAt" = CASE WHEN $4 OR "Attempts" >= "MaxAttempts" THEN "AvailableAt"
                                 ELSE now() + coalesce($5::interval,
                                     least(interval '10 seconds' * power(2, least("Attempts", 20)), interval '1 hour') * (0.8 + 0.4 * random()))
                            END,
            "LeaseOwner" = CASE WHEN $4 OR "Attempts" >= "MaxAttempts" THEN "LeaseOwner" END,
            "LeaseUntil" = CASE WHEN $4 OR "Attempts" >= "MaxAttempts" THEN "LeaseUntil" END,
            "LastError" = $3
        WHERE "Id" = $1 AND "State" = 'running' AND "LeaseOwner" = $2
        RETURNING "State", "Class"
        """;

    private const string ReleaseSql = """
        UPDATE content."Jobs" SET
            "State" = 'queued', "Attempts" = greatest("Attempts" - 1, 0), "AvailableAt" = now() + $4::interval,
            "LeaseOwner" = NULL, "LeaseUntil" = NULL, "LastError" = $3
        WHERE "Id" = $1 AND "State" = 'running' AND "LeaseOwner" = $2
        RETURNING "State", "Class"
        """;

    private const string CancelRunSql = """
        UPDATE content."Jobs" SET "State" = 'cancelled', "CompletedAt" = now(), "LastError" = $2
        WHERE "RunId" = $1 AND "State" IN ('queued','running') AND "Id" IS DISTINCT FROM $3
        """;

    public async Task<IReadOnlySet<Guid>> EnqueueAsync(IReadOnlyList<JobRequest> jobs, DbTransaction? transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        if (jobs.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var inserted = new HashSet<Guid>();
        var classes = new HashSet<string>(StringComparer.Ordinal);
        if (transaction is not null)
        {
            var callerTransaction = AsNpgsql(transaction);
            await InsertAndNotifyAsync(callerTransaction.Connection!, callerTransaction, jobs, inserted, classes, cancellationToken);
        }
        else
        {
            await using var connection = await database.OpenAsync(cancellationToken);
            await using var ownTransaction = await connection.BeginTransactionAsync(cancellationToken);
            await InsertAndNotifyAsync(connection, ownTransaction, jobs, inserted, classes, cancellationToken);
            await ownTransaction.CommitAsync(cancellationToken);
        }

        // A caller's transaction may still roll back: a wake-up then finds nothing, which costs one probe.
        foreach (var jobClass in classes)
        {
            wakeUps.Signal(jobClass);
        }

        return inserted;
    }

    public async Task<ReadyDepartments> ReadyDepartmentsAsync(string jobClass, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(ReadyDepartmentsSql, connection) { Parameters = { JobDatabase.Parameter(jobClass) } };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var departments = new List<ReadyDepartment>();
        TimeSpan? nextReadyIn = null;
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!reader.IsDBNull(1))
            {
                departments.Add(new ReadyDepartment(reader.GetGuid(0), reader.GetInt16(1), reader.GetFieldValue<DateTimeOffset>(2)));
            }

            if (!reader.IsDBNull(3) && reader.GetFieldValue<TimeSpan>(3) is var readyIn && (nextReadyIn is null || readyIn < nextReadyIn))
            {
                nextReadyIn = readyIn;
            }
        }

        return new ReadyDepartments(departments, nextReadyIn);
    }

    public async Task<LeasedJob?> DequeueAsync(DequeueRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(DequeueSql, connection)
        {
            Parameters =
            {
                JobDatabase.Parameter(request.Class),
                JobDatabase.Parameter(request.DepartmentId),
                JobDatabase.Parameter(request.Owner),
                JobDatabase.Parameter(request.Lease),
            },
        };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new LeasedJob(
            Id: reader.GetGuid(0),
            Class: reader.GetString(1),
            Priority: reader.GetInt16(2),
            DepartmentId: reader.GetGuid(3),
            TeamId: reader.IsDBNull(4) ? null : reader.GetGuid(4),
            Classification: reader.GetString(5),
            DedupKey: reader.IsDBNull(6) ? null : reader.GetString(6),
            Payload: reader.GetString(7),
            Attempts: reader.GetInt16(8),
            MaxAttempts: reader.GetInt16(9),
            Reclaims: reader.GetInt16(10),
            RunId: reader.IsDBNull(11) ? null : reader.GetGuid(11),
            LastError: reader.IsDBNull(12) ? null : reader.GetString(12),
            EnqueuedAt: reader.GetFieldValue<DateTimeOffset>(13),
            LeaseUntil: reader.GetFieldValue<DateTimeOffset>(14));
    }

    public async Task<IReadOnlySet<Guid>> HeartbeatAsync(Guid owner, IReadOnlyList<JobLease> running, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(running);
        var held = new HashSet<Guid>();
        if (running.Count == 0)
        {
            return held;
        }

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(HeartbeatSql, connection)
        {
            Parameters =
            {
                JobDatabase.Parameter(owner),
                JobDatabase.Parameter(running.Select(r => r.JobId).ToArray()),
                JobDatabase.Parameter(running.Select(r => r.Lease).ToArray()),
            },
        };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            held.Add(reader.GetGuid(0));
        }

        return held;
    }

    public async Task<bool> CompleteAsync(Guid jobId, Guid owner, string? resultJson, DbTransaction? transaction, CancellationToken cancellationToken)
    {
        if (transaction is not null)
        {
            var callerTransaction = AsNpgsql(transaction);
            return await ExecuteAsync(callerTransaction.Connection!, callerTransaction, CompleteSql, cancellationToken,
                JobDatabase.Parameter(jobId), JobDatabase.Parameter(owner), JobDatabase.Parameter(resultJson)) == 1;
        }

        await using var connection = await database.OpenAsync(cancellationToken);
        return await ExecuteAsync(connection, null, CompleteSql, cancellationToken,
            JobDatabase.Parameter(jobId), JobDatabase.Parameter(owner), JobDatabase.Parameter(resultJson)) == 1;
    }

    public async Task<string?> FailAsync(Guid jobId, Guid owner, string error, bool fatal, TimeSpan? retryAfter, CancellationToken cancellationToken)
    {
        var row = await FailOrReleaseAsync(FailSql, cancellationToken,
            JobDatabase.Parameter(jobId), JobDatabase.Parameter(owner), JobDatabase.Parameter(Truncate(error)), JobDatabase.Parameter(fatal),
            JobDatabase.Parameter(retryAfter));
        return row?.State;
    }

    public async Task<bool> ReleaseAsync(Guid jobId, Guid owner, string reason, TimeSpan delay, CancellationToken cancellationToken) =>
        await FailOrReleaseAsync(ReleaseSql, cancellationToken,
            JobDatabase.Parameter(jobId), JobDatabase.Parameter(owner), JobDatabase.Parameter(Truncate(reason)), JobDatabase.Parameter(delay)) is not null;

    public async Task<int> CancelRunAsync(Guid runId, string reason, Guid? exceptJobId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        return await ExecuteAsync(connection, null, CancelRunSql, cancellationToken,
            JobDatabase.Parameter(runId), JobDatabase.Parameter(Truncate(reason)), JobDatabase.Parameter(exceptJobId));
    }

    private static async Task InsertAndNotifyAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, IReadOnlyList<JobRequest> jobs,
        HashSet<Guid> inserted, HashSet<string> classes, CancellationToken cancellationToken)
    {
        await using (var insert = new NpgsqlCommand(EnqueueSql, connection, transaction)
        {
            Parameters =
            {
                JobDatabase.Parameter(jobs.Select(j => j.Id).ToArray()),
                JobDatabase.Parameter(jobs.Select(j => j.Class).ToArray()),
                JobDatabase.Parameter(jobs.Select(j => j.Priority).ToArray()),
                JobDatabase.Parameter(jobs.Select(j => j.DepartmentId).ToArray()),
                JobDatabase.Parameter(jobs.Select(j => j.TeamId).ToArray()),
                JobDatabase.Parameter(jobs.Select(j => j.Classification).ToArray()),
                JobDatabase.Parameter(jobs.Select(j => j.DedupKey).ToArray()),
                JobDatabase.Parameter(jobs.Select(j => j.Payload).ToArray()),
                JobDatabase.Parameter(jobs.Select(j => j.Delay).ToArray()),
                JobDatabase.Parameter(jobs.Select(j => j.MaxAttempts ?? JobClassPolicy.For(j.Class).MaxAttempts).ToArray()),
                JobDatabase.Parameter(jobs.Select(j => j.RunId).ToArray()),
            },
        })
        {
            await using var reader = await insert.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                inserted.Add(reader.GetGuid(0));
                classes.Add(reader.GetString(1));
            }
        }

        await JobDatabase.NotifyAsync(connection, transaction, classes, cancellationToken);
    }

    /// <summary>
    /// Runs a <see cref="FailSql"/> or <see cref="ReleaseSql"/> update; a job back in the queue wakes its class here and,
    /// at the commit, in every worker: the idle lanes may sleep past its new <c>AvailableAt</c> otherwise. Null when the
    /// lease was lost.
    /// </summary>
    private async Task<(string State, string Class)?> FailOrReleaseAsync(string sql, CancellationToken cancellationToken, params NpgsqlParameter[] parameters)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        (string State, string Class)? row = null;
        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            command.Parameters.AddRange(parameters);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                row = (reader.GetString(0), reader.GetString(1));
            }
        }

        var requeued = row is { State: JobStates.Queued } ? row.Value.Class : null;
        if (requeued is not null)
        {
            await JobDatabase.NotifyAsync(connection, transaction, [requeued], cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        if (requeued is not null)
        {
            wakeUps.Signal(requeued);
        }

        return row;
    }

    private static async Task<int> ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql,
        CancellationToken cancellationToken, params NpgsqlParameter[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddRange(parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static NpgsqlTransaction AsNpgsql(DbTransaction transaction) =>
        transaction as NpgsqlTransaction ?? throw new ArgumentException("The job queue joins Npgsql transactions only.", nameof(transaction));

    /// <summary><c>LastError</c> is <c>varchar(500)</c>.</summary>
    private static string Truncate(string value) => value.Length <= 500 ? value : value[..500];
}
