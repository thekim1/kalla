using Npgsql;
using Kalla.Domain;
using Kalla.Domain.Entities;
using Kalla.Domain.Ports;
using Kalla.Infrastructure.Jobs;
using Kalla.TestKit;

namespace Kalla.Worker.Tests;

/// <summary>The Postgres <see cref="IJobQueue"/> against the real schema and roles (data-model §4.2, §4.3).</summary>
[Collection(RagDatabaseGroup3.Name)]
public sealed class JobQueueTests(RagDatabaseFixture database) : IAsyncLifetime
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(1);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await database.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_dedup_key_held_by_an_open_job_skips_the_enqueue_until_that_job_ends()
    {
        var admin = database.Queue(RagDatabase.Admin);
        var worker = database.Queue(RagDatabase.Worker);
        var first = Job(dedupKey: "cancel:run-1");

        (await admin.EnqueueAsync([first], null, Ct)).ShouldBe([first.Id]);
        (await admin.EnqueueAsync([Job(dedupKey: "cancel:run-1")], null, Ct)).ShouldBeEmpty();

        // Still held while running.
        var owner = Guid.NewGuid();
        var leased = await worker.DequeueAsync(new DequeueRequest(JobClasses.Sync, first.DepartmentId, owner, Lease), Ct);
        leased.ShouldNotBeNull().Id.ShouldBe(first.Id);
        (await admin.EnqueueAsync([Job(dedupKey: "cancel:run-1")], null, Ct)).ShouldBeEmpty();

        // Released once it is terminal.
        (await worker.CompleteAsync(first.Id, owner, null, null, Ct)).ShouldBeTrue();
        var again = Job(dedupKey: "cancel:run-1");
        (await admin.EnqueueAsync([again], null, Ct)).ShouldBe([again.Id]);
    }

    [Fact]
    public async Task Concurrent_500_row_batches_with_overlapping_and_repeated_keys_insert_each_key_once()
    {
        // to-verify #10: ON CONFLICT inference against the partial unique index under 500-row batch enqueues.
        var department = Guid.NewGuid();
        JobRequest[] Batch(int from) => [.. Enumerable.Range(from, 500).Select(i => Job(department, dedupKey: $"fetch:internal:{i % 400 + from}"))];
        var queue = database.Queue(RagDatabase.Worker);

        var results = await Task.WhenAll(
            queue.EnqueueAsync(Batch(0), null, Ct),
            database.Queue(RagDatabase.Admin).EnqueueAsync(Batch(200), null, Ct),
            queue.EnqueueAsync(Batch(0), null, Ct));

        // Keys 0..399 and 200..599: 600 distinct, each inserted by exactly one batch.
        results.Sum(r => r.Count).ShouldBe(600);
        (await Sql.ScalarAsync<long>(database.Connection(null), """SELECT count(*) FROM content."Jobs" WHERE "DepartmentId" = $1""", department)).ShouldBe(600);
        (await Sql.ScalarAsync<long>(database.Connection(null),
            """SELECT count(DISTINCT "DedupKey") FROM content."Jobs" WHERE "DepartmentId" = $1""", department)).ShouldBe(600);
    }

    [Fact]
    public async Task Enqueue_joins_the_callers_transaction_and_notifies_rag_jobs_at_commit()
    {
        await using var listener = new NpgsqlConnection(database.Connection(null));
        await listener.OpenAsync(Ct);
        var notified = new List<string>();
        listener.Notification += (_, e) => notified.Add(e.Payload);
        await using (var listen = new NpgsqlCommand("LISTEN rag_jobs", listener))
        {
            await listen.ExecuteNonQueryAsync(Ct);
        }

        var job = Job();
        await using (var connection = new NpgsqlConnection(database.Connection(RagDatabase.Admin)))
        {
            await connection.OpenAsync(Ct);
            await using var transaction = await connection.BeginTransactionAsync(Ct);
            (await database.Queue(RagDatabase.Admin).EnqueueAsync([job], transaction, Ct)).ShouldBe([job.Id]);
            await transaction.RollbackAsync(Ct);
        }

        (await Sql.ScalarAsync<long>(database.Connection(null), """SELECT count(*) FROM content."Jobs" WHERE "Id" = $1""", job.Id)).ShouldBe(0);
        (await listener.WaitAsync(TimeSpan.FromMilliseconds(300), Ct)).ShouldBeFalse();

        await database.Queue(RagDatabase.Admin).EnqueueAsync([job], null, Ct);
        (await listener.WaitAsync(TimeSpan.FromSeconds(5), Ct)).ShouldBeTrue();
        notified.ShouldBe([JobClasses.Sync]);
    }

    [Fact]
    public async Task Dequeue_takes_one_job_of_the_class_and_department_by_priority_then_age()
    {
        var department = Guid.NewGuid();
        var queue = database.Queue(RagDatabase.Worker);
        var old = Job(department);
        var urgent = Job(department) with { Priority = 10 };
        var otherDepartment = Job();
        await queue.EnqueueAsync([old], null, Ct);
        await queue.EnqueueAsync([urgent, otherDepartment], null, Ct);
        var owner = Guid.NewGuid();

        var first = await queue.DequeueAsync(new DequeueRequest(JobClasses.Sync, department, owner, Lease), Ct);
        var second = await queue.DequeueAsync(new DequeueRequest(JobClasses.Sync, department, owner, Lease), Ct);
        var none = await queue.DequeueAsync(new DequeueRequest(JobClasses.Sync, department, owner, Lease), Ct);

        first.ShouldNotBeNull().Id.ShouldBe(urgent.Id);
        first.Attempts.ShouldBe((short)1);
        first.MaxAttempts.ShouldBe(JobClassPolicy.For(JobClasses.Sync).MaxAttempts);
        second.ShouldNotBeNull().Id.ShouldBe(old.Id);
        none.ShouldBeNull();
        var row = await Sql.JobAsync(database.Connection(null), urgent.Id);
        row.State.ShouldBe(JobStates.Running);
        row.LeaseOwner.ShouldBe(owner);
    }

    [Fact]
    public async Task Eight_parallel_consumers_never_lease_the_same_job()
    {
        var department = Guid.NewGuid();
        var queue = database.Queue(RagDatabase.Worker);
        await queue.EnqueueAsync([.. Enumerable.Range(0, 40).Select(_ => Job(department))], null, Ct);

        var leased = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            var mine = new List<Guid>();
            var owner = Guid.NewGuid();
            while (await queue.DequeueAsync(new DequeueRequest(JobClasses.Sync, department, owner, Lease), Ct) is { } job)
            {
                mine.Add(job.Id);
            }

            return mine;
        }));

        leased.SelectMany(l => l).Count().ShouldBe(40);
        leased.SelectMany(l => l).Distinct().Count().ShouldBe(40);
    }

    [Fact]
    public async Task A_transient_failure_backs_off_exponentially_with_jitter_and_dies_when_attempts_run_out()
    {
        var (queue, owner, job) = await LeasedAsync();

        // Attempts = 1: 10 s · 2^1 = 20 s ± 20 %.
        (await queue.FailAsync(job.Id, owner, "gateway.timeout", fatal: false, retryAfter: null, Ct)).ShouldBe(JobStates.Queued);
        var row = await Sql.JobAsync(database.Connection(null), job.Id);
        row.AvailableIn.TotalSeconds.ShouldBeInRange(16 - 1, 24 + 1);
        row.LastError.ShouldBe("gateway.timeout");
        row.LeaseOwner.ShouldBeNull();

        // Attempts = 12: capped at 1 h ± 20 %.
        await Sql.ExecuteAsync(database.Connection(null),
            """UPDATE content."Jobs" SET "State" = 'running', "LeaseOwner" = $2, "Attempts" = 12, "MaxAttempts" = 20 WHERE "Id" = $1""", job.Id, owner);
        await queue.FailAsync(job.Id, owner, "gateway.timeout", fatal: false, retryAfter: null, Ct);
        (await Sql.JobAsync(database.Connection(null), job.Id)).AvailableIn.TotalSeconds.ShouldBeInRange(2880 - 1, 4320 + 1);

        // Retry-After wins over the back-off.
        await Sql.ExecuteAsync(database.Connection(null),
            """UPDATE content."Jobs" SET "State" = 'running', "LeaseOwner" = $2 WHERE "Id" = $1""", job.Id, owner);
        await queue.FailAsync(job.Id, owner, "gateway.budget_exhausted", fatal: false, TimeSpan.FromMinutes(7), Ct);
        (await Sql.JobAsync(database.Connection(null), job.Id)).AvailableIn.TotalSeconds.ShouldBeInRange(7 * 60 - 2, 7 * 60 + 1);

        // Attempts used up: dead.
        await Sql.ExecuteAsync(database.Connection(null),
            """UPDATE content."Jobs" SET "State" = 'running', "LeaseOwner" = $2, "Attempts" = "MaxAttempts" WHERE "Id" = $1""", job.Id, owner);
        (await queue.FailAsync(job.Id, owner, "gateway.timeout", fatal: false, retryAfter: null, Ct)).ShouldBe(JobStates.Dead);
        row = await Sql.JobAsync(database.Connection(null), job.Id);
        row.Completed.ShouldBeTrue();
        row.LeaseOwner.ShouldBe(owner);
    }

    [Fact]
    public async Task A_fatal_failure_ends_the_job_at_once()
    {
        var (queue, owner, job) = await LeasedAsync();

        (await queue.FailAsync(job.Id, owner, "command.unknown_kind", fatal: true, retryAfter: null, Ct)).ShouldBe(JobStates.Failed);

        var row = await Sql.JobAsync(database.Connection(null), job.Id);
        row.Completed.ShouldBeTrue();
        row.Attempts.ShouldBe((short)1);
    }

    [Fact]
    public async Task A_capacity_refusal_requeues_without_using_an_attempt()
    {
        var (queue, owner, job) = await LeasedAsync();
        job.Attempts.ShouldBe((short)1);

        (await queue.ReleaseAsync(job.Id, owner, "busy", TimeSpan.FromSeconds(30), Ct)).ShouldBeTrue();

        var row = await Sql.JobAsync(database.Connection(null), job.Id);
        row.State.ShouldBe(JobStates.Queued);
        row.Attempts.ShouldBe((short)0);
        row.LastError.ShouldBe("busy");
        row.LeaseOwner.ShouldBeNull();
        row.AvailableIn.TotalSeconds.ShouldBeInRange(28, 31);
        (await queue.DequeueAsync(new DequeueRequest(JobClasses.Sync, job.DepartmentId, owner, Lease), Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Ready_departments_leave_out_later_jobs_and_say_when_the_next_one_is_due()
    {
        var queue = database.Queue(RagDatabase.Worker);
        var ready = Guid.NewGuid();
        var later = Guid.NewGuid();
        await queue.EnqueueAsync([Job(ready) with { Priority = 3 }, Job(ready) with { Delay = TimeSpan.FromMinutes(5) },
            Job(later) with { Delay = TimeSpan.FromMinutes(2) }, Job(later) with { Delay = TimeSpan.FromMinutes(10) }], null, Ct);

        var result = await queue.ReadyDepartmentsAsync(JobClasses.Sync, Ct);

        result.Departments.ShouldHaveSingleItem().DepartmentId.ShouldBe(ready);
        result.Departments[0].MaxPriority.ShouldBe((short)3);
        result.NextReadyIn.ShouldNotBeNull().TotalSeconds.ShouldBeInRange(115, 120);
        var none = await queue.ReadyDepartmentsAsync(JobClasses.Fetch, Ct);
        none.Departments.ShouldBeEmpty();
        none.NextReadyIn.ShouldBeNull();
    }

    [Fact]
    public async Task A_requeue_notifies_rag_jobs_and_a_terminal_failure_does_not()
    {
        // Another worker's idle lane may be waiting past the job's new AvailableAt; the notification makes it look again.
        var (queue, owner, job) = await LeasedAsync();
        await using var listener = await RagJobsListener.StartAsync(database.Connection(null));

        (await queue.ReleaseAsync(job.Id, owner, "busy", TimeSpan.FromSeconds(5), Ct)).ShouldBeTrue();
        (await listener.NextAsync(TimeSpan.FromSeconds(5))).ShouldBe(JobClasses.Sync);

        await Sql.ExecuteAsync(database.Connection(null), """UPDATE content."Jobs" SET "State" = 'running', "LeaseOwner" = $2 WHERE "Id" = $1""", job.Id, owner);
        (await queue.FailAsync(job.Id, owner, "gateway.timeout", fatal: false, retryAfter: null, Ct)).ShouldBe(JobStates.Queued);
        (await listener.NextAsync(TimeSpan.FromSeconds(5))).ShouldBe(JobClasses.Sync);

        await Sql.ExecuteAsync(database.Connection(null), """UPDATE content."Jobs" SET "State" = 'running', "LeaseOwner" = $2 WHERE "Id" = $1""", job.Id, owner);
        (await queue.FailAsync(job.Id, owner, "command.unknown_kind", fatal: true, retryAfter: null, Ct)).ShouldBe(JobStates.Failed);
        (await listener.NextAsync(TimeSpan.FromMilliseconds(500))).ShouldBeNull();
    }

    [Fact]
    public async Task The_lease_heartbeat_renews_held_jobs_and_reports_lost_ones()
    {
        var (queue, owner, job) = await LeasedAsync();
        var (_, _, other) = await LeasedAsync(owner);

        var held = await queue.HeartbeatAsync(owner, [new JobLease(job.Id, TimeSpan.FromMinutes(15)), new JobLease(other.Id, TimeSpan.FromMinutes(15))], Ct);
        held.ShouldBe([job.Id, other.Id], ignoreOrder: true);
        (await Sql.ScalarAsync<double>(database.Connection(null),
            """SELECT extract(epoch FROM "LeaseUntil" - now())::float8 FROM content."Jobs" WHERE "Id" = $1""", job.Id)).ShouldBeInRange(14 * 60, 15 * 60 + 1);

        // Another instance's reclaim or a cancel takes the job away: the heartbeat no longer returns it, and its outcome is refused.
        await Sql.ExecuteAsync(database.Connection(null), """UPDATE content."Jobs" SET "State" = 'queued', "LeaseOwner" = NULL WHERE "Id" = $1""", other.Id);
        (await queue.HeartbeatAsync(owner, [new JobLease(job.Id, Lease), new JobLease(other.Id, Lease)], Ct)).ShouldBe([job.Id]);
        (await queue.CompleteAsync(other.Id, owner, null, null, Ct)).ShouldBeFalse();
        (await queue.FailAsync(other.Id, owner, "x", fatal: true, null, Ct)).ShouldBeNull();
        (await queue.ReleaseAsync(other.Id, owner, "busy", TimeSpan.Zero, Ct)).ShouldBeFalse();
        (await queue.CompleteAsync(job.Id, Guid.NewGuid(), null, null, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Completion_joins_the_callers_transaction()
    {
        var (queue, owner, job) = await LeasedAsync();
        await using var connection = new NpgsqlConnection(database.Connection(RagDatabase.Worker));
        await connection.OpenAsync(Ct);

        await using (var rolledBack = await connection.BeginTransactionAsync(Ct))
        {
            (await queue.CompleteAsync(job.Id, owner, """{"applied":true}""", rolledBack, Ct)).ShouldBeTrue();
            await rolledBack.RollbackAsync(Ct);
        }

        (await Sql.JobAsync(database.Connection(null), job.Id)).State.ShouldBe(JobStates.Running);
        await using (var committed = await connection.BeginTransactionAsync(Ct))
        {
            (await queue.CompleteAsync(job.Id, owner, """{"applied":true}""", committed, Ct)).ShouldBeTrue();
            await committed.CommitAsync(Ct);
        }

        var row = await Sql.JobAsync(database.Connection(null), job.Id);
        row.State.ShouldBe(JobStates.Succeeded);
        row.Result.ShouldBe("""{"applied": true}""");
    }

    [Fact]
    public async Task Cancelling_a_run_ends_its_open_jobs_except_the_command_itself()
    {
        var queue = database.Queue(RagDatabase.Worker);
        var run = Guid.NewGuid();
        var department = Guid.NewGuid();
        var queued = Job(department) with { RunId = run };
        var running = Job(department) with { RunId = run, Priority = 5 };
        var done = Job(department) with { RunId = run };
        var self = Job(department) with { RunId = run };
        var otherRun = Job(department) with { RunId = Guid.NewGuid() };
        await queue.EnqueueAsync([queued, running, done, self, otherRun], null, Ct);
        var owner = Guid.NewGuid();
        await Sql.ExecuteAsync(database.Connection(null),
            """UPDATE content."Jobs" SET "State" = 'running', "LeaseOwner" = $2 WHERE "Id" = $1""", running.Id, owner);
        await Sql.ExecuteAsync(database.Connection(null),
            """UPDATE content."Jobs" SET "State" = 'succeeded', "CompletedAt" = now() WHERE "Id" = $1""", done.Id);

        (await queue.CancelRunAsync(run, "run.cancelled", self.Id, Ct)).ShouldBe(2);

        (await Sql.JobAsync(database.Connection(null), queued.Id)).State.ShouldBe(JobStates.Cancelled);
        (await Sql.JobAsync(database.Connection(null), running.Id)).LastError.ShouldBe("run.cancelled");
        (await Sql.JobAsync(database.Connection(null), done.Id)).State.ShouldBe(JobStates.Succeeded);
        (await Sql.JobAsync(database.Connection(null), self.Id)).State.ShouldBe(JobStates.Queued);
        (await Sql.JobAsync(database.Connection(null), otherRun.Id)).State.ShouldBe(JobStates.Queued);
        (await queue.HeartbeatAsync(owner, [new JobLease(running.Id, Lease)], Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_admin_role_can_enqueue_but_not_lease()
    {
        var job = Job();
        await database.Queue(RagDatabase.Admin).EnqueueAsync([job], null, Ct);

        var denied = await Should.ThrowAsync<PostgresException>(() =>
            database.Queue(RagDatabase.Admin).DequeueAsync(new DequeueRequest(JobClasses.Sync, job.DepartmentId, Guid.NewGuid(), Lease), Ct));
        denied.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    private async Task<(PostgresJobQueue Queue, Guid Owner, LeasedJob Job)> LeasedAsync(Guid? owner = null)
    {
        var queue = database.Queue(RagDatabase.Worker);
        var job = Job();
        await queue.EnqueueAsync([job], null, Ct);
        var leaseOwner = owner ?? Guid.NewGuid();
        var leased = await queue.DequeueAsync(new DequeueRequest(JobClasses.Sync, job.DepartmentId, leaseOwner, Lease), Ct);
        return (queue, leaseOwner, leased.ShouldNotBeNull());
    }

    /// <summary>A <c>sync</c> job: no test worker host handles that class, so nothing else picks it up.</summary>
    private static JobRequest Job(Guid? departmentId = null, string? dedupKey = null) =>
        new(JobClasses.Sync, departmentId ?? Guid.NewGuid(), Classification.Internal, """{"sourceId":"00000000-0000-0000-0000-000000000001"}""")
        {
            DedupKey = dedupKey,
        };
}
