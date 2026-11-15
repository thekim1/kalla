using System.Collections.Concurrent;
using System.Data.Common;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Infrastructure.Config;
using Ume.Rag.Infrastructure.Jobs;
using Ume.Rag.Worker.Commands;
using Ume.Rag.Worker.Jobs;
using static Ume.Rag.Worker.Tests.Commands;

namespace Ume.Rag.Worker.Tests;

/// <summary>
/// Idle cost of the dispatcher (#505): an idle lane waits for a wake-up of its class, its next due job or
/// <c>Worker:IdleWaitSeconds</c>, instead of probing the queue every 2 s. A lane for every job class, with
/// <see cref="CountingJobQueue"/> counting their <c>ReadyDepartmentsAsync</c> probes.
/// </summary>
[Collection(RagDatabaseGroup4.Name)]
public sealed class IdleDispatchTests(RagDatabaseFixture database) : IAsyncLifetime
{
    /// <summary>Longer than every wait below, so only a wake-up or a due job can end a lane's wait.</summary>
    private const int LongIdleWaitSeconds = 120;

    private static readonly TimeSpan IdleWindow = TimeSpan.FromSeconds(20);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await database.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task An_idle_worker_issues_no_lane_query_between_notifications()
    {
        var handler = new BlockingCommand(released: true);
        await using var worker = await StartAsync(handler, LongIdleWaitSeconds);
        var probes = Probes(worker);
        var lanes = JobClassPolicy.All.Count;

        var before = probes.Total;
        await Task.Delay(IdleWindow, Ct);
        var idle = probes.Total - before;
        Log($"{lanes} lanes, {idle} queue probes in {IdleWindow.TotalSeconds:F0} s idle (the 2 s poll made {lanes * IdleWindow.TotalSeconds / 2:F0})");
        idle.ShouldBe(0);

        // A job enqueued by another process wakes its own lane through rag_jobs, and no other.
        var others = probes.Snapshot();
        var enqueuedAt = handler.Now;
        var job = await EnqueueAsync(database, Command(BlockingCommand.KindName));
        var latency = await handler.StartedAsync(job, TimeSpan.FromSeconds(60)) - enqueuedAt;
        await WaitForStateAsync(database, job, JobStates.Succeeded);
        Log($"started {latency.TotalMilliseconds:F0} ms after the enqueue");
        latency.ShouldBeLessThan(TimeSpan.FromSeconds(LongIdleWaitSeconds / 4), "woken by the notification, not the idle wait");
        foreach (var policy in JobClassPolicy.All.Where(p => p.Class != JobClasses.Command))
        {
            probes.Of(policy.Class).ShouldBe(others.GetValueOrDefault(policy.Class), $"the {policy.Class} lane was not woken");
        }
    }

    [Fact]
    public async Task An_idle_lane_takes_a_delayed_job_when_it_is_due()
    {
        var handler = new BlockingCommand(released: true);
        await using var worker = await StartAsync(handler, LongIdleWaitSeconds);
        var probes = Probes(worker);
        var delay = TimeSpan.FromSeconds(3);

        var before = probes.Of(JobClasses.Command);
        var enqueuedAt = handler.Now;
        var job = await EnqueueAsync(database, Command(BlockingCommand.KindName) with { Delay = delay });
        var startedAfter = await handler.StartedAsync(job, TimeSpan.FromSeconds(60)) - enqueuedAt;
        await WaitForStateAsync(database, job, JobStates.Succeeded);

        var used = probes.Of(JobClasses.Command) - before;
        Log($"a job due in {delay.TotalSeconds:F0} s started after {startedAfter.TotalMilliseconds:F0} ms, {used} probes");
        startedAfter.ShouldBeGreaterThan(delay - TimeSpan.FromMilliseconds(200), "not before its AvailableAt");
        startedAfter.ShouldBeLessThan(TimeSpan.FromSeconds(LongIdleWaitSeconds / 4), "when due, not at the idle wait");
        // The notification, the due time, then the free slot after the job: no poll in between.
        used.ShouldBeLessThanOrEqualTo(4);
    }

    [Fact]
    public async Task A_job_released_with_a_delay_runs_again_when_due()
    {
        // The release wakes the lane, which then waits for the job's new AvailableAt (2 s, then 5 s for a second refusal).
        var refuseOnce = new RefuseOnceCommand();
        await using var worker = await StartAsync(refuseOnce, LongIdleWaitSeconds);

        var job = await EnqueueAsync(database, Command(RefuseOnceCommand.KindName));
        await WaitForStateAsync(database, job, JobStates.Succeeded, TimeSpan.FromSeconds(LongIdleWaitSeconds / 4));

        refuseOnce.Runs(job).ShouldBe(2);
    }

    [Fact]
    public async Task A_job_queued_without_a_notification_is_found_at_the_idle_wait()
    {
        // As after a lost notification (the listening connection was down): nothing wakes the lane, its idle wait ends.
        var handler = new BlockingCommand(released: true);
        await using var worker = await StartAsync(handler, idleWaitSeconds: 2);

        var job = Guid.NewGuid();
        await Sql.ExecuteAsync(database.Connection(null), """
            INSERT INTO content."Jobs" ("Id", "Class", "DepartmentId", "Classification", "Payload", "MaxAttempts")
            VALUES ($1, $2, $3, $4, $5::jsonb, 3)
            """, job, JobClasses.Command, Guid.NewGuid(), Classification.Internal, $$"""{"kind":"{{BlockingCommand.KindName}}"}""");

        await handler.StartedAsync(job, TimeSpan.FromSeconds(60));
        await WaitForStateAsync(database, job, JobStates.Succeeded);
    }

    [Fact]
    public async Task A_busy_lane_reads_the_ready_departments_once_per_lifetime_not_once_per_job()
    {
        // #764: the ready list is a pass over the class's whole queue; before, every claim made one.
        const int Jobs = 300;
        var handler = new BlockingCommand(released: true);
        Guid[] departments = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        JobRequest[] jobs = [.. Enumerable.Range(0, Jobs).Select(i => Command(BlockingCommand.KindName, departments[i % 3]))];
        await using var worker = await StartAsync(handler, LongIdleWaitSeconds);
        var probes = Probes(worker);
        var before = probes.Of(JobClasses.Command);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        (await database.Queue(Ume.Rag.TestKit.RagDatabase.Admin).EnqueueAsync(jobs, null, Ct)).Count.ShouldBe(Jobs);
        await WaitUntilAsync(async () => await Sql.ScalarAsync<long>(database.Connection(null),
            """SELECT count(*) FROM content."Jobs" WHERE "Class" = 'command' AND "State" = 'succeeded'""") == Jobs, TimeSpan.FromSeconds(120));
        var seconds = clock.Elapsed.TotalSeconds;

        var used = probes.Of(JobClasses.Command) - before;
        Log($"{Jobs} jobs of 3 departments in {seconds:F1} s: {used} ready-department probes");
        // One per lifetime while busy, one for the wake-up and one to find the lane idle; far from one per job.
        used.ShouldBeLessThanOrEqualTo((int)Math.Ceiling(seconds / JobDispatcher.ReadyListLifetime.TotalSeconds) + 4);
        handler.StartedCount.ShouldBe(Jobs);
    }

    [Fact]
    public async Task A_department_that_becomes_ready_while_the_lane_is_busy_is_served_within_the_ready_lists_lifetime()
    {
        // The lane (two slots) works through a long queue of one department from its kept list. Another department's job
        // enqueued meanwhile is not in that list; once the list is read again it runs fewer jobs here than the busy one,
        // so the next free slot is its (engine design §9.3), and it must not wait for the queue to drain.
        const int Jobs = 200;
        var handler = new SlowCommand(TimeSpan.FromMilliseconds(40)); // 4 s of work on two slots
        var busy = Guid.NewGuid();
        await using var worker = await WorkerHost.StartAsync(database, s => s.AddSingleton<ICommandHandler>(handler),
            new Dictionary<string, string?> { ["Worker:Concurrency:command"] = "2" });
        await database.Queue(Ume.Rag.TestKit.RagDatabase.Admin).EnqueueAsync([.. Enumerable.Range(0, Jobs).Select(_ => Command(SlowCommand.KindName, busy))], null, Ct);
        await WaitUntilAsync(() => Task.FromResult(handler.Started >= 5));

        var enqueuedAt = handler.Now;
        var late = await EnqueueAsync(database, Command(SlowCommand.KindName, Guid.NewGuid()));
        var waited = await handler.StartedAsync(late, TimeSpan.FromSeconds(60)) - enqueuedAt;

        Log($"the other department's job started {waited.TotalMilliseconds:F0} ms after its enqueue, as job {handler.Started} of {Jobs + 1}");
        waited.ShouldBeLessThan(JobDispatcher.ReadyListLifetime + TimeSpan.FromSeconds(1.5));
        handler.Started.ShouldBeLessThan(Jobs, "before the busy department's queue drained");
    }

    /// <summary><c>test-slow</c>: each job takes a fixed time; records when each started.</summary>
    private sealed class SlowCommand(TimeSpan work) : ICommandHandler
    {
        public const string KindName = "test-slow";

        private readonly ConcurrentDictionary<Guid, TaskCompletionSource<TimeSpan>> _started = new();
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private int _count;

        public string Kind => KindName;

        public int Started => Volatile.Read(ref _count);

        public TimeSpan Now => _clock.Elapsed;

        public Task<TimeSpan> StartedAsync(Guid jobId, TimeSpan timeout) => Of(jobId).Task.WaitAsync(timeout, Ct);

        public async Task<JobResult> ExecuteAsync(JobContext context, System.Text.Json.JsonElement payload, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            Of(context.Job.Id).TrySetResult(_clock.Elapsed);
            await Task.Delay(work, cancellationToken);
            return new JobResult.Success();
        }

        private TaskCompletionSource<TimeSpan> Of(Guid jobId) =>
            _started.GetOrAdd(jobId, _ => new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    [Theory]
    [InlineData(null, 30_000d, 30_000d)]
    [InlineData(2_000.4, 30_000d, 2_001d)]
    [InlineData(0.0001, 30_000d, 1d)]
    [InlineData(45_000d, 30_000d, 30_000d)]
    public void The_idle_wait_is_the_time_to_the_next_due_job_within_the_cap(double? nextReadyInMs, double capMs, double expectedMs) =>
        JobDispatcher.IdleWait(nextReadyInMs is { } next ? TimeSpan.FromMilliseconds(next) : null, TimeSpan.FromMilliseconds(capMs))
            .ShouldBe(TimeSpan.FromMilliseconds(expectedMs));

    /// <summary>
    /// A worker with a lane for every job class (stub handlers besides <c>command</c>), its queue counted, once every lane
    /// has made its first probe and the config monitor listens.
    /// </summary>
    private async Task<WorkerHost> StartAsync(ICommandHandler handler, int idleWaitSeconds)
    {
        var worker = await WorkerHost.StartAsync(database, s =>
        {
            s.AddSingleton(handler);
            foreach (var policy in JobClassPolicy.All.Where(p => p.Class != JobClasses.Command))
            {
                s.AddSingleton<IJobHandler>(new SucceedingHandler(policy.Class));
            }

            s.AddSingleton<IJobQueue>(sp => new CountingJobQueue(new PostgresJobQueue(sp.GetRequiredService<JobDatabase>(), sp.GetRequiredService<JobWakeUps>())));
        }, new Dictionary<string, string?> { ["Worker:IdleWaitSeconds"] = idleWaitSeconds.ToString(CultureInfo.InvariantCulture) });
        var probes = Probes(worker);
        var config = worker.Services.GetRequiredService<ConfigVersionState>();
        await WaitUntilAsync(() => Task.FromResult(config.LastConfirmedAt is not null && JobClassPolicy.All.All(p => probes.Of(p.Class) > 0)),
            TimeSpan.FromSeconds(60));
        return worker;
    }

    private static CountingJobQueue Probes(WorkerHost worker) => (CountingJobQueue)worker.Services.GetRequiredService<IJobQueue>();

    private static void Log(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    private sealed class SucceedingHandler(string jobClass) : IJobHandler
    {
        public string JobClass => jobClass;

        public Task<JobResult> ExecuteAsync(JobContext context, CancellationToken cancellationToken) => Task.FromResult<JobResult>(new JobResult.Success());
    }
}

/// <summary>The Postgres queue with its <see cref="IJobQueue.ReadyDepartmentsAsync"/> probes counted per class.</summary>
internal sealed class CountingJobQueue(IJobQueue inner) : IJobQueue
{
    private readonly ConcurrentDictionary<string, int> _probes = new(StringComparer.Ordinal);

    public int Total => _probes.Values.Sum();

    public int Of(string jobClass) => _probes.GetValueOrDefault(jobClass);

    public Dictionary<string, int> Snapshot() => new(_probes, StringComparer.Ordinal);

    public Task<ReadyDepartments> ReadyDepartmentsAsync(string jobClass, CancellationToken cancellationToken)
    {
        _probes.AddOrUpdate(jobClass, 1, (_, n) => n + 1);
        return inner.ReadyDepartmentsAsync(jobClass, cancellationToken);
    }

    public Task<IReadOnlySet<Guid>> EnqueueAsync(IReadOnlyList<JobRequest> jobs, DbTransaction? transaction, CancellationToken cancellationToken) =>
        inner.EnqueueAsync(jobs, transaction, cancellationToken);

    public Task<LeasedJob?> DequeueAsync(DequeueRequest request, CancellationToken cancellationToken) => inner.DequeueAsync(request, cancellationToken);

    public Task<IReadOnlySet<Guid>> HeartbeatAsync(Guid owner, IReadOnlyList<JobLease> running, CancellationToken cancellationToken) =>
        inner.HeartbeatAsync(owner, running, cancellationToken);

    public Task<bool> CompleteAsync(Guid jobId, Guid owner, string? resultJson, DbTransaction? transaction, CancellationToken cancellationToken) =>
        inner.CompleteAsync(jobId, owner, resultJson, transaction, cancellationToken);

    public Task<string?> FailAsync(Guid jobId, Guid owner, string error, bool fatal, TimeSpan? retryAfter, CancellationToken cancellationToken) =>
        inner.FailAsync(jobId, owner, error, fatal, retryAfter, cancellationToken);

    public Task<bool> ReleaseAsync(Guid jobId, Guid owner, string reason, TimeSpan delay, CancellationToken cancellationToken) =>
        inner.ReleaseAsync(jobId, owner, reason, delay, cancellationToken);

    public Task<int> CancelRunAsync(Guid runId, string reason, Guid? exceptJobId, CancellationToken cancellationToken) =>
        inner.CancelRunAsync(runId, reason, exceptJobId, cancellationToken);
}

/// <summary>A session that listens on <c>rag_jobs</c>, as another worker's config monitor does.</summary>
internal sealed class RagJobsListener : IAsyncDisposable
{
    private readonly NpgsqlConnection _connection;
    private readonly ConcurrentQueue<string> _payloads = new();

    private RagJobsListener(NpgsqlConnection connection)
    {
        _connection = connection;
        _connection.Notification += (_, e) => _payloads.Enqueue(e.Payload);
    }

    public static async Task<RagJobsListener> StartAsync(string connectionString)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var listener = new RagJobsListener(connection);
        await using var listen = new NpgsqlCommand($"LISTEN {JobWakeUps.ChannelName}", connection);
        await listen.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return listener;
    }

    /// <summary>The next notification's payload, or null when none arrives within <paramref name="timeout"/>.</summary>
    public async Task<string?> NextAsync(TimeSpan timeout)
    {
        if (_payloads.TryDequeue(out var payload))
        {
            return payload;
        }

        await _connection.WaitAsync(timeout, TestContext.Current.CancellationToken);
        return _payloads.TryDequeue(out payload) ? payload : null;
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
