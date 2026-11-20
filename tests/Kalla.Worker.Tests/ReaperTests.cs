using Npgsql;
using Kalla.Domain;
using Kalla.Domain.Entities;
using Kalla.Domain.Ports;
using Kalla.Infrastructure.Jobs;
using Kalla.TestKit;

namespace Kalla.Worker.Tests;

/// <summary>
/// The reaper with the production timings (engine design §9.4, data-model §4.2): an instance silent for 30 s loses its
/// jobs without using an attempt; the third reclaim dead-letters a poison suspect.
/// </summary>
[Collection(RagDatabaseGroup4.Name)]
public sealed class ReaperTests(RagDatabaseFixture database) : IAsyncLifetime
{
    private static readonly TimeSpan InstanceTimeout = TimeSpan.FromSeconds(30);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await database.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_job_of_an_instance_silent_for_30_seconds_is_reclaimed_without_using_an_attempt()
    {
        var instance = await RegisterAsync(silentFor: TimeSpan.FromSeconds(29));
        var job = await LeaseAsync(instance);
        var reaper = new JobReaper(database.Database(RagDatabase.Worker));

        (await reaper.ReapAsync(InstanceTimeout, Ct)).Reclaimed.ShouldBeEmpty();
        (await Sql.JobAsync(database.Connection(null), job.Id)).State.ShouldBe(JobStates.Running);

        await SilenceAsync(instance, TimeSpan.FromSeconds(31));
        await using var listener = await RagJobsListener.StartAsync(database.Connection(null));
        var result = await reaper.ReapAsync(InstanceTimeout, Ct);

        result.Ran.ShouldBeTrue();
        result.Reclaimed.ShouldBe([new ReclaimedJob(job.Id, JobClasses.Command, JobStates.Queued, 1)]);
        (await listener.NextAsync(TimeSpan.FromSeconds(5))).ShouldBe(JobClasses.Command, "idle workers elsewhere take the reclaimed job at once");
        var row = await Sql.JobAsync(database.Connection(null), job.Id);
        row.State.ShouldBe(JobStates.Queued);
        row.Attempts.ShouldBe((short)0);
        row.Reclaims.ShouldBe((short)1);
        row.LeaseOwner.ShouldBeNull();
        row.AvailableIn.ShouldBeLessThanOrEqualTo(TimeSpan.Zero);
    }

    [Fact]
    public async Task An_expired_lease_is_reclaimed_even_when_its_instance_is_alive()
    {
        var instance = await RegisterAsync(silentFor: TimeSpan.Zero);
        var job = await LeaseAsync(instance);
        await Sql.ExecuteAsync(database.Connection(null), """UPDATE content."Jobs" SET "LeaseUntil" = now() - interval '1 second' WHERE "Id" = $1""", job.Id);

        var result = await new JobReaper(database.Database(RagDatabase.Worker)).ReapAsync(InstanceTimeout, Ct);

        result.Reclaimed.Select(r => r.Id).ShouldBe([job.Id]);
        (await Sql.JobAsync(database.Connection(null), job.Id)).Attempts.ShouldBe((short)0);
    }

    [Fact]
    public async Task The_third_reclaim_dead_letters_the_job_as_a_poison_suspect()
    {
        var reaper = new JobReaper(database.Database(RagDatabase.Worker));
        var queue = database.Queue(RagDatabase.Worker);
        var job = Commands.Command("reprocess-document");
        await queue.EnqueueAsync([job], null, Ct);

        for (short reclaim = 1; reclaim <= JobReaper.MaxReclaims; reclaim++)
        {
            // A worker that dies while running it (the document kills its process), never registered as alive again.
            var leased = await queue.DequeueAsync(new DequeueRequest(JobClasses.Command, job.DepartmentId, Guid.NewGuid(), TimeSpan.FromMinutes(1)), Ct);
            leased.ShouldNotBeNull().Attempts.ShouldBe((short)1);

            var reclaimed = (await reaper.ReapAsync(InstanceTimeout, Ct)).Reclaimed.ShouldHaveSingleItem();
            reclaimed.Reclaims.ShouldBe(reclaim);
            var row = await Sql.JobAsync(database.Connection(null), job.Id);
            row.Attempts.ShouldBe((short)0);
            row.State.ShouldBe(reclaim < JobReaper.MaxReclaims ? JobStates.Queued : JobStates.Dead);
        }

        var dead = await Sql.JobAsync(database.Connection(null), job.Id);
        dead.LastError.ShouldBe(JobReaper.PoisonSuspect);
        dead.Completed.ShouldBeTrue();
        (await queue.DequeueAsync(new DequeueRequest(JobClasses.Command, job.DepartmentId, Guid.NewGuid(), TimeSpan.FromMinutes(1)), Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Instances_silent_for_five_minutes_are_removed()
    {
        var stale = await RegisterAsync(silentFor: TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        var live = await RegisterAsync(silentFor: TimeSpan.FromMinutes(4));

        (await new JobReaper(database.Database(RagDatabase.Worker)).ReapAsync(InstanceTimeout, Ct)).InstancesRemoved.ShouldBe(1);

        (await Sql.ScalarAsync<long>(database.Connection(null), """SELECT count(*) FROM content."WorkerInstances" WHERE "InstanceId" = $1""", stale)).ShouldBe(0);
        (await Sql.ScalarAsync<long>(database.Connection(null), """SELECT count(*) FROM content."WorkerInstances" WHERE "InstanceId" = $1""", live)).ShouldBe(1);
    }

    [Fact]
    public async Task One_pass_at_a_time_across_instances()
    {
        var instance = await RegisterAsync(silentFor: TimeSpan.FromMinutes(1));
        var job = await LeaseAsync(instance);
        await using var other = new NpgsqlConnection(database.Connection(RagDatabase.Worker));
        await other.OpenAsync(Ct);
        await using var transaction = await other.BeginTransactionAsync(Ct);
        await using (var hold = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock($1)", other, transaction)
        {
            Parameters = { new NpgsqlParameter<long> { TypedValue = JobReaper.LockKey } },
        })
        {
            (await hold.ExecuteScalarAsync(Ct)).ShouldBe(true);
        }

        var skipped = await new JobReaper(database.Database(RagDatabase.Worker)).ReapAsync(InstanceTimeout, Ct);

        skipped.Ran.ShouldBeFalse();
        (await Sql.JobAsync(database.Connection(null), job.Id)).State.ShouldBe(JobStates.Running);
    }

    private async Task<Guid> RegisterAsync(TimeSpan silentFor)
    {
        var instance = Guid.CreateVersion7();
        await new WorkerInstanceStore(database.Database(RagDatabase.Worker)).HeartbeatAsync(instance, "test", [JobClasses.Command], Ct);
        await SilenceAsync(instance, silentFor);
        return instance;
    }

    private Task SilenceAsync(Guid instance, TimeSpan silentFor) =>
        Sql.ExecuteAsync(database.Connection(null),
            """UPDATE content."WorkerInstances" SET "LastHeartbeatAt" = now() - $2 WHERE "InstanceId" = $1""", instance, silentFor);

    private async Task<LeasedJob> LeaseAsync(Guid owner)
    {
        var queue = database.Queue(RagDatabase.Worker);
        var job = new JobRequest(JobClasses.Command, Guid.NewGuid(), Classification.Internal, """{"kind":"reprocess-document"}""");
        await queue.EnqueueAsync([job], null, Ct);
        return (await queue.DequeueAsync(new DequeueRequest(JobClasses.Command, job.DepartmentId, owner, TimeSpan.FromMinutes(1)), Ct)).ShouldNotBeNull();
    }
}
