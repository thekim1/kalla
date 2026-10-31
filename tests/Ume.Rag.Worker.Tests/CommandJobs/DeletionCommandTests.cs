using Ume.Rag.Domain.Entities;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Worker.Commands;
using Ume.Rag.Worker.Jobs;
using Ume.Rag.Worker.Tests.Engine;

namespace Ume.Rag.Worker.Tests.CommandJobs;

/// <summary>
/// <c>confirm-deletion</c>, <c>reject-deletion</c> and <c>release-hold</c> (P1b item 8; connectors design §13.3): a confirm
/// re-validates and soft-deletes, and the finalizer recomputes the run; a reject holds the documents and degrades the run;
/// a release clears the source's holds. Unknown ids and runs not waiting are refused, a repeated decision changes nothing,
/// and a job that lost its lease writes nothing.
/// </summary>
[Collection(RagDatabaseGroup.Name)]
public sealed class DeletionCommandTests(RagDatabaseFixture database) : IAsyncLifetime
{
    private const string Admin = "admin-oid";

    private static class ProposalDecisionsText
    {
        public const string Pending = "pending";
        public const string Confirmed = "confirmed:" + Admin;
        public const string Dropped = "dropped:" + Admin;
        public const string Rejected = "rejected:" + Admin;
    }

    private CommandRunner _runner = null!;

    public async ValueTask InitializeAsync()
    {
        // After the reset: it disposes the data sources the runner's queue and database use.
        await database.ResetAsync();
        _runner = new CommandRunner(database);
    }

    public ValueTask DisposeAsync() => _runner.DisposeAsync();

    [Fact]
    public async Task A_confirm_deletes_the_proposed_documents_drops_one_seen_again_and_the_finalizer_recomputes_the_run()
    {
        var seed = await EngineKit.SeedUrlListAsync(database, []);
        try
        {
            var finished = DateTimeOffset.UtcNow.AddHours(-1);
            var run = await CommandSeeds.RunAsync(database, seed, RunStates.AwaitingDeletionConfirmation, finished);
            var gone = await CommandSeeds.DocumentAsync(database, seed, $"https://site.test/{Guid.NewGuid():N}", lastSeenAt: finished.AddDays(-3));
            var goneSet = await CommandSeeds.SetAsync(database, seed, gone, ChunkSetStates.Live);
            var seenAgain = await CommandSeeds.DocumentAsync(database, seed, $"https://site.test/{Guid.NewGuid():N}", lastSeenAt: DateTimeOffset.UtcNow);
            var kept = await CommandSeeds.DocumentAsync(database, seed, $"https://site.test/{Guid.NewGuid():N}", lastSeenAt: finished.AddDays(-3));
            var keptSet = await CommandSeeds.SetAsync(database, seed, kept, ChunkSetStates.Live);
            await CommandSeeds.ProposeAsync(database, seed, run, gone);
            await CommandSeeds.ProposeAsync(database, seed, run, seenAgain);

            var confirm = await _runner.RunAsync(new ConfirmDeletionCommand { RunId = run, DecidedBy = Admin }, seed.DepartmentId);

            confirm.Result.ShouldBeOfType<JobResult.Committed>();
            var result = await CommandSeeds.ResultAsync(database, confirm.Job.Id);
            (result.GetProperty("deleted").GetInt32(), result.GetProperty("dropped").GetInt32()).ShouldBe((1, 1));
            (await DecisionAsync(run, gone)).ShouldBe(ProposalDecisionsText.Confirmed);
            (await DecisionAsync(run, seenAgain)).ShouldBe(ProposalDecisionsText.Dropped);
            (await DeletedAsync(gone)).ShouldBeTrue();
            (await DeletedAsync(seenAgain)).ShouldBeFalse();
            (await DeletedAsync(kept)).ShouldBeFalse();
            (await CommandSeeds.VisibleChunksAsync(database, goneSet)).ShouldBe(0);
            (await CommandSeeds.VisibleChunksAsync(database, keptSet)).ShouldBe(2);
            (await Sql.ScalarAsync<string>(database.Connection(null), """
                SELECT string_agg("Outcome" || ':' || "Reason", ',') FROM content."RunItems" WHERE "RunId" = $1 AND "DocumentId" = $2
                """, run, gone)).ShouldBe("deleted:not-seen");
            (await StateAsync(run)).ShouldBe(RunStates.Running);

            await CommandSeeds.Finalizer(database).FinalizeAsync(TestContext.Current.CancellationToken);
            (await EngineKit.RunAsync(database, run))!.ShouldBe(new RunCounters(RunStates.Succeeded, 0, 0, 0, 1, 0, 0));

            var again = await _runner.RunAsync(new ConfirmDeletionCommand { RunId = run, DecidedBy = Admin }, seed.DepartmentId);
            again.Result.ShouldBe(new JobResult.FatalError(DeletionRows.NotAwaiting));
            (await Sql.ScalarAsync<long>(database.Connection(null), """SELECT count(*) FROM content."RunItems" WHERE "RunId" = $1""", run)).ShouldBe(1);
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task A_reject_holds_the_documents_and_degrades_the_run_and_wrong_runs_are_refused()
    {
        var seed = await EngineKit.SeedUrlListAsync(database, []);
        try
        {
            var finished = DateTimeOffset.UtcNow.AddHours(-1);
            var run = await CommandSeeds.RunAsync(database, seed, RunStates.AwaitingDeletionConfirmation, finished);
            var first = await CommandSeeds.DocumentAsync(database, seed, $"https://site.test/{Guid.NewGuid():N}", lastSeenAt: finished.AddDays(-3));
            var second = await CommandSeeds.DocumentAsync(database, seed, $"https://site.test/{Guid.NewGuid():N}", lastSeenAt: finished.AddDays(-3));
            await CommandSeeds.ProposeAsync(database, seed, run, first);
            await CommandSeeds.ProposeAsync(database, seed, run, second);
            var succeeded = await CommandSeeds.RunAsync(database, seed, RunStates.Succeeded, finished);

            (await _runner.RunAsync(new RejectDeletionCommand { RunId = Guid.NewGuid(), DecidedBy = Admin }, seed.DepartmentId)).Result
                .ShouldBe(new JobResult.FatalError(DeletionRows.RunUnknown));
            (await _runner.RunAsync(new RejectDeletionCommand { RunId = succeeded, DecidedBy = Admin }, seed.DepartmentId)).Result
                .ShouldBe(new JobResult.FatalError(DeletionRows.NotAwaiting));
            (await _runner.RunAsync(new ConfirmDeletionCommand { RunId = succeeded, DecidedBy = Admin }, seed.DepartmentId)).Result
                .ShouldBe(new JobResult.FatalError(DeletionRows.NotAwaiting));

            var reject = await _runner.RunAsync(new RejectDeletionCommand { RunId = run, DecidedBy = Admin }, seed.DepartmentId);

            reject.Result.ShouldBeOfType<JobResult.Committed>();
            (await CommandSeeds.ResultAsync(database, reject.Job.Id)).GetProperty("held").GetInt32().ShouldBe(2);
            (await DecisionAsync(run, first)).ShouldBe(ProposalDecisionsText.Rejected);
            (await DecisionAsync(run, second)).ShouldBe(ProposalDecisionsText.Rejected);
            (await HeldByAsync(seed.SourceId)).ShouldBe($"{Admin},{Admin}");
            (await DeletedAsync(first)).ShouldBeFalse();
            (await StateAsync(run)).ShouldBe(RunStates.Degraded);

            (await _runner.RunAsync(new RejectDeletionCommand { RunId = run, DecidedBy = "someone-else" }, seed.DepartmentId)).Result
                .ShouldBe(new JobResult.FatalError(DeletionRows.NotAwaiting));
            (await HeldByAsync(seed.SourceId)).ShouldBe($"{Admin},{Admin}");
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task A_confirm_whose_lease_was_lost_deletes_nothing()
    {
        var seed = await EngineKit.SeedUrlListAsync(database, []);
        try
        {
            var finished = DateTimeOffset.UtcNow.AddHours(-1);
            var run = await CommandSeeds.RunAsync(database, seed, RunStates.AwaitingDeletionConfirmation, finished);
            var document = await CommandSeeds.DocumentAsync(database, seed, $"https://site.test/{Guid.NewGuid():N}", lastSeenAt: finished.AddDays(-3));
            await CommandSeeds.ProposeAsync(database, seed, run, document);
            var job = await _runner.LeaseAsync(new ConfirmDeletionCommand { RunId = run, DecidedBy = Admin }, seed.DepartmentId, seed.Classification);
            await _runner.StealLeaseAsync(job.Id);

            (await _runner.RunAsync(job)).ShouldBeOfType<JobResult.Success>();

            (await DeletedAsync(document)).ShouldBeFalse();
            (await DecisionAsync(run, document)).ShouldBe(ProposalDecisionsText.Pending);
            (await StateAsync(run)).ShouldBe(RunStates.AwaitingDeletionConfirmation);
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task A_release_clears_only_the_sources_holds_and_a_repeat_releases_none()
    {
        var seed = await EngineKit.SeedUrlListAsync(database, []);
        var other = await EngineKit.SeedUrlListAsync(database, []);
        try
        {
            await HoldAsync(await CommandSeeds.DocumentAsync(database, seed, $"https://site.test/{Guid.NewGuid():N}"));
            await HoldAsync(await CommandSeeds.DocumentAsync(database, seed, $"https://site.test/{Guid.NewGuid():N}"));
            await CommandSeeds.DocumentAsync(database, seed, $"https://site.test/{Guid.NewGuid():N}");
            await HoldAsync(await CommandSeeds.DocumentAsync(database, other, $"https://site.test/{Guid.NewGuid():N}"));

            var release = await _runner.RunAsync(new ReleaseHoldCommand { SourceId = seed.SourceId }, seed.DepartmentId);
            var repeat = await _runner.RunAsync(new ReleaseHoldCommand { SourceId = seed.SourceId }, seed.DepartmentId);

            release.Result.ShouldBeOfType<JobResult.Committed>();
            (await CommandSeeds.ResultAsync(database, release.Job.Id)).GetProperty("released").GetInt32().ShouldBe(2);
            (await CommandSeeds.ResultAsync(database, repeat.Job.Id)).GetProperty("released").GetInt32().ShouldBe(0);
            (await HeldByAsync(seed.SourceId)).ShouldBeNull();
            (await HeldByAsync(other.SourceId)).ShouldBe(Admin);
            (await _runner.RunAsync(new ReleaseHoldCommand { SourceId = Guid.NewGuid() }, seed.DepartmentId)).Result
                .ShouldBe(new JobResult.FatalError(ReleaseHoldHandler.SourceUnknown));
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
            await EngineKit.PauseAsync(database, other);
        }
    }

    [Fact]
    public async Task A_stale_release_hold_job_releases_nothing_and_its_retry_releases_once()
    {
        var seed = await EngineKit.SeedUrlListAsync(database, []);
        try
        {
            await HoldAsync(await CommandSeeds.DocumentAsync(database, seed, $"https://site.test/{Guid.NewGuid():N}"));
            var job = await _runner.LeaseAsync(new ReleaseHoldCommand { SourceId = seed.SourceId }, seed.DepartmentId, seed.Classification);
            await _runner.StealLeaseAsync(job.Id);

            (await _runner.RunAsync(job)).ShouldBeOfType<JobResult.Success>();
            (await HeldByAsync(seed.SourceId)).ShouldBe(Admin, "a job that lost its lease writes nothing");

            var retry = await _runner.RunAsync(new ReleaseHoldCommand { SourceId = seed.SourceId }, seed.DepartmentId);
            (await CommandSeeds.ResultAsync(database, retry.Job.Id)).GetProperty("released").GetInt32().ShouldBe(1);
            (await HeldByAsync(seed.SourceId)).ShouldBeNull();
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    private Task HoldAsync(Guid document) =>
        Sql.ExecuteAsync(database.Connection(null), """UPDATE content."Documents" SET "HeldAt" = now(), "HeldBy" = $2 WHERE "Id" = $1""", document, Admin);

    private Task<string?> HeldByAsync(Guid sourceId) =>
        Sql.ScalarAsync<string?>(database.Connection(null), """
            SELECT string_agg("HeldBy", ',') FROM content."Documents" WHERE "SourceId" = $1 AND "HeldAt" IS NOT NULL
            """, sourceId);

    private Task<bool> DeletedAsync(Guid document) =>
        Sql.ScalarAsync<bool>(database.Connection(null), """
            SELECT "DeletedAt" IS NOT NULL AND "PurgeAfter" > now() + interval '13 days' FROM content."Documents" WHERE "Id" = $1
            """, document);

    private Task<string> DecisionAsync(Guid run, Guid document) =>
        Sql.ScalarAsync<string>(database.Connection(null), """
            SELECT "Decision" || coalesce(':' || "DecidedBy", '') FROM content."DeletionProposals" WHERE "RunId" = $1 AND "DocumentId" = $2
            """, run, document);

    private Task<string> StateAsync(Guid run) =>
        Sql.ScalarAsync<string>(database.Connection(null), """SELECT "State" FROM content."Runs" WHERE "Id" = $1""", run);
}
