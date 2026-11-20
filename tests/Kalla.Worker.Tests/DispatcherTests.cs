using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Kalla.Domain;
using Kalla.Domain.Entities;
using Kalla.Domain.Ports;
using Kalla.Infrastructure.Config;
using Kalla.TestKit;
using Kalla.Worker.Commands;
using Kalla.Worker.Jobs;
using static Kalla.Worker.Tests.Commands;

namespace Kalla.Worker.Tests;

/// <summary>
/// The worker end to end against the real schema and roles (P0 checklist #20): the admin enqueues, the dispatcher leases
/// and applies, the reaper recovers from a killed worker.
/// </summary>
[Collection(RagDatabaseGroup4.Name)]
public sealed class DispatcherTests(RagDatabaseFixture database) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await database.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task The_cancel_run_command_is_applied()
    {
        var run = Guid.NewGuid();
        var department = Guid.NewGuid();
        var queue = database.Queue(RagDatabase.Worker);
        JobRequest[] runJobs = [.. Enumerable.Range(0, 3).Select(_ => new JobRequest(JobClasses.Fetch, department, Classification.Internal, "{}") { RunId = run })];
        await queue.EnqueueAsync(runJobs, null, Ct);
        await using var worker = await WorkerHost.StartAsync(database);

        var command = await EnqueueAsync(database, Command(CancelRunCommand.KindName, department, new { runId = run }, dedupKey: $"cancel:{run}"));
        await WaitForStateAsync(database, command, JobStates.Succeeded);

        var row = await Sql.JobAsync(database.Connection(null), command);
        row.Result.ShouldBe("""{"cancelledJobs": 3}""");
        row.Attempts.ShouldBe((short)1);
        foreach (var job in runJobs)
        {
            var cancelled = await Sql.JobAsync(database.Connection(null), job.Id);
            cancelled.State.ShouldBe(JobStates.Cancelled);
            cancelled.LastError.ShouldBe(CancelRunCommand.CancelledReason);
        }
    }

    [Fact]
    public async Task A_job_enqueued_by_another_process_wakes_the_worker_through_rag_jobs()
    {
        // The idle wait is 60 s and the admin's queue has its own data source and JobWakeUps, as in another process: a start
        // well inside it can only come from the rag_jobs notification on the config monitor's connection.
        var handler = new BlockingCommand(released: true);
        await using var worker = await WorkerHost.StartAsync(database, s => s.AddSingleton<ICommandHandler>(handler),
            new Dictionary<string, string?> { ["Worker:IdleWaitSeconds"] = "60", ["Worker:Classes"] = JobClasses.Command });
        // The monitor confirms its first version after its LISTEN statements; then let the lane make its first, empty probe.
        var config = worker.Services.GetRequiredService<ConfigVersionState>();
        await WaitUntilAsync(() => Task.FromResult(config.LastConfirmedAt is not null));
        await Task.Delay(500, Ct);

        var enqueuedAt = handler.Now;
        var job = await EnqueueAsync(database, Command(BlockingCommand.KindName));
        var startedAt = await handler.StartedAsync(job, TimeSpan.FromSeconds(10));

        var latency = startedAt - enqueuedAt;
        TestContext.Current.TestOutputHelper?.WriteLine(string.Create(CultureInfo.InvariantCulture, $"started {latency.TotalMilliseconds:F0} ms after the enqueue"));
        latency.ShouldBeLessThan(TimeSpan.FromSeconds(30), "woken by the notification, not the 60 s idle wait");
        await WaitForStateAsync(database, job, JobStates.Succeeded);
    }

    [Fact]
    public async Task A_malformed_or_unknown_command_fails_at_once()
    {
        await using var worker = await WorkerHost.StartAsync(database);

        var unknown = await EnqueueAsync(database, Command("no-such-kind"));
        var malformed = await EnqueueAsync(database, Command(CancelRunCommand.KindName, parameters: new { runId = "not-a-guid" }));

        await WaitForStateAsync(database, unknown, JobStates.Failed);
        await WaitForStateAsync(database, malformed, JobStates.Failed);
        (await Sql.JobAsync(database.Connection(null), unknown)).LastError.ShouldBe(CommandJobHandler.UnknownKind);
        (await Sql.JobAsync(database.Connection(null), malformed)).LastError.ShouldBe(CommandJobHandler.InvalidPayload);
    }

    [Fact]
    public async Task A_killed_workers_job_is_reclaimed_by_another_instance_without_using_an_attempt()
    {
        // Done-when of P0 #20. Scaled timings (heartbeat 1 s, instance timeout 3 s, reaper 1 s); the production rule
        // (30 s of silence) is ReaperTests.A_job_of_an_instance_silent_for_30_seconds_is_reclaimed_without_using_an_attempt.
        var stuck = new BlockingCommand();
        await using var a = await WorkerHost.StartAsync(database, s => s.AddSingleton<ICommandHandler>(stuck));
        var job = await EnqueueAsync(database, Command(BlockingCommand.KindName));
        await stuck.StartedAsync(job, TimeSpan.FromSeconds(10));
        var leased = await Sql.JobAsync(database.Connection(null), job);
        leased.LeaseOwner.ShouldBe(a.InstanceId);
        leased.Attempts.ShouldBe((short)1);

        var resumed = new BlockingCommand(released: true);
        await using var b = await WorkerHost.StartAsync(database, s => s.AddSingleton<ICommandHandler>(resumed));
        var killedAt = resumed.Now;
        await a.KillAsync();

        var restartedAt = await resumed.StartedAsync(job, TimeSpan.FromSeconds(30));
        await WaitForStateAsync(database, job, JobStates.Succeeded);

        var done = await Sql.JobAsync(database.Connection(null), job);
        done.Attempts.ShouldBe((short)1);
        done.Reclaims.ShouldBe((short)1);
        done.LeaseOwner.ShouldBe(b.InstanceId);
        // Reclaimed only once the instance had been silent for its timeout, measured on the database clock from its last
        // heartbeat (frozen at the kill) to the reclaim (the reaper sets AvailableAt). Not from the kill: under CPU load
        // (2-vCPU runners, B starting up) A's last heartbeat can lag its 1 s interval, and the reclaim then comes sooner
        // after the kill while the rule still holds.
        var silence = await Sql.ScalarAsync<double>(database.Connection(null), """
            SELECT extract(epoch FROM j."AvailableAt" - w."LastHeartbeatAt")::float8
            FROM content."Jobs" j, content."WorkerInstances" w WHERE j."Id" = $1 AND w."InstanceId" = $2
            """, job, a.InstanceId);
        // Then at the next reaper pass, and dequeued at once: the reaper wakes the dispatcher.
        var downtime = restartedAt - killedAt;
        TestContext.Current.TestOutputHelper?.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"killed worker's job restarted after {downtime.TotalSeconds:F2} s; silent {silence:F2} s at the reclaim"));
        silence.ShouldBeGreaterThanOrEqualTo(WorkerHost.InstanceTimeoutSeconds);
        (await Sql.ScalarAsync<long>(database.Connection(null), """SELECT count(*) FROM content."WorkerInstances" WHERE "InstanceId" = $1""", a.InstanceId))
            .ShouldBe(1, "a killed instance leaves its row for the 5-minute sweep");
    }

    [Fact]
    public async Task A_clean_stop_releases_running_jobs_without_an_attempt_and_unregisters()
    {
        var stuck = new BlockingCommand();
        var worker = await WorkerHost.StartAsync(database, s => s.AddSingleton<ICommandHandler>(stuck),
            new Dictionary<string, string?> { ["Worker:StopGraceSeconds"] = "1" });
        var job = await EnqueueAsync(database, Command(BlockingCommand.KindName));
        await stuck.StartedAsync(job, TimeSpan.FromSeconds(10));

        await worker.StopAsync();

        var row = await Sql.JobAsync(database.Connection(null), job);
        row.State.ShouldBe(JobStates.Queued);
        row.Attempts.ShouldBe((short)0);
        row.Reclaims.ShouldBe((short)0);
        row.LastError.ShouldBe(JobDispatcher.ShutdownReason);
        (await Sql.ScalarAsync<long>(database.Connection(null), """SELECT count(*) FROM content."WorkerInstances" WHERE "InstanceId" = $1""", worker.InstanceId))
            .ShouldBe(0);
    }

    [Fact]
    public async Task A_stop_after_the_host_was_disposed_does_nothing()
    {
        // A WebApplicationFactory stops the host while Program's RunAsync stops and disposes it: on a busy machine the
        // second stop reaches a disposed dispatcher (#805).
        var worker = await WorkerHost.StartAsync(database);
        var dispatcher = worker.Services.GetServices<IHostedService>().OfType<JobDispatcher>().Single();

        await worker.StopAsync();

        await Should.NotThrowAsync(() => dispatcher.StopAsync(Ct));
    }

    [Fact]
    public async Task A_job_refused_for_an_unconfirmed_config_is_requeued_without_using_an_attempt()
    {
        var refuseOnce = new RefuseOnceCommand();
        await using var worker = await WorkerHost.StartAsync(database, s => s.AddSingleton<ICommandHandler>(refuseOnce));

        var job = await EnqueueAsync(database, Command(RefuseOnceCommand.KindName));
        await WaitForStateAsync(database, job, JobStates.Succeeded);

        refuseOnce.Runs(job).ShouldBe(2);
        var row = await Sql.JobAsync(database.Connection(null), job);
        row.Attempts.ShouldBe((short)1);
        row.LastError.ShouldBe(JobDispatcher.ConfigUnconfirmedReason);
    }

    [Fact]
    public async Task Free_slots_go_to_the_department_with_the_fewest_running_jobs()
    {
        var gate = new BlockingCommand();
        var busy = Guid.NewGuid();
        var quiet = Guid.NewGuid();
        // The busy department's six jobs are older, so age alone would give it both slots.
        Guid[] busyJobs = [.. await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => EnqueueAsync(database, Command(BlockingCommand.KindName, busy))))];
        await Task.Delay(50, Ct);
        Guid[] quietJobs = [await EnqueueAsync(database, Command(BlockingCommand.KindName, quiet)), await EnqueueAsync(database, Command(BlockingCommand.KindName, quiet))];
        await using var worker = await WorkerHost.StartAsync(database, s => s.AddSingleton<ICommandHandler>(gate),
            new Dictionary<string, string?> { ["Worker:Concurrency:command"] = "2" });

        await WaitUntilAsync(() => Task.FromResult(gate.StartedCount == 2));
        await Task.Delay(300, Ct);

        gate.StartedCount.ShouldBe(2);
        var running = new List<Guid>();
        foreach (var id in busyJobs.Concat(quietJobs))
        {
            var row = await Sql.JobAsync(database.Connection(null), id);
            if (row.State == JobStates.Running)
            {
                running.Add(row.DepartmentId);
            }
        }

        running.ShouldBe([busy, quiet], ignoreOrder: true);
        gate.Release();
        foreach (var id in busyJobs.Concat(quietJobs))
        {
            await WaitForStateAsync(database, id, JobStates.Succeeded);
        }
    }

    [Fact]
    public async Task Under_load_the_worker_stays_within_its_pool_and_the_role_cap()
    {
        // 20 command slots on a pool of 10: jobs wait for connections instead of opening more. Each job holds a RagDbContext
        // connection while the dispatcher's queue statements run beside it, both from the one ragdb data source (issue #22).
        // The worker role's cap is 12 (data-model §1.1): pool 10 + the config monitor's listening connection + 1 for a
        // stop-then-start.
        var hold = new HoldConnectionCommand();
        await using var worker = await WorkerHost.StartAsync(database, s => s.AddSingleton<ICommandHandler>(hold),
            new Dictionary<string, string?> { ["Worker:Concurrency:command"] = "20" });
        var jobs = Enumerable.Range(0, 200).Select(_ => Command(HoldConnectionCommand.KindName)).ToList();
        await database.Queue(RagDatabase.Admin).EnqueueAsync(jobs, null, Ct);

        long peak = 0;
        var watch = Stopwatch.StartNew();
        while (await Sql.ScalarAsync<long>(database.Connection(null),
                   """SELECT count(*) FROM content."Jobs" WHERE "State" IN ('queued','running')""") > 0)
        {
            peak = Math.Max(peak, await Sql.ScalarAsync<long>(database.Connection(null),
                "SELECT count(*) FROM pg_stat_activity WHERE usename = 'ume_rag_worker'"));
            watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(60));
            await Task.Delay(20, Ct);
        }

        (await Sql.ScalarAsync<long>(database.Connection(null), """SELECT count(*) FROM content."Jobs" WHERE "State" = 'succeeded'""")).ShouldBe(200);
        var cap = await Sql.ScalarAsync<int>(database.Connection(null), "SELECT rolconnlimit FROM pg_roles WHERE rolname = 'ume_rag_worker'");
        cap.ShouldBe(12);
        peak.ShouldBeLessThanOrEqualTo(WorkerExtensions.DefaultMaxPoolSize + 1);
        peak.ShouldBeGreaterThanOrEqualTo(WorkerExtensions.DefaultMaxPoolSize / 2, "the load should have used the pool");
        TestContext.Current.TestOutputHelper?.WriteLine(string.Create(CultureInfo.InvariantCulture, $"peak worker connections {peak} of cap {cap}"));
    }
}
