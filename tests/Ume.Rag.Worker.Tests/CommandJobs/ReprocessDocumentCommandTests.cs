using Ume.Rag.Domain.Entities;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Pipelines.Json;
using Ume.Rag.TestKit;
using Ume.Rag.Worker.Commands;
using Ume.Rag.Worker.Tests.Engine;
using Ume.Rag.Worker.Tests.Processing;

namespace Ume.Rag.Worker.Tests.CommandJobs;

/// <summary>
/// <c>reprocess-document</c> (P1b item 8; admin design §4.7) through the dispatcher of a worker with the engine: one document
/// runs again from its stored content in a <c>reprocess</c> run of its source under a new execution id, without a fetch,
/// into its own chunk set (salted with the execution, #134) that is swapped in; the run finishes from the run items; a retried job keeps its run and execution; <c>fetch</c> and unknown documents are
/// refused.
/// </summary>
[Collection(RagDatabaseGroup1.Name)]
public sealed class ReprocessDocumentCommandTests(RagDatabaseFixture database, FakeGatewayFactory gateway) : IClassFixture<FakeGatewayFactory>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly DirectoryInfo _blobs = Directory.CreateTempSubdirectory("ume-rag-reprocess-blobs-");

    public async ValueTask InitializeAsync() => await database.ResetAsync();

    public ValueTask DisposeAsync()
    {
        _blobs.Delete(recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_document_is_reprocessed_from_stored_content_in_a_reprocess_run_under_a_new_execution()
    {
        var fetches = 0;
        await using var site = await EngineKit.SiteAsync((name, _) =>
        {
            Interlocked.Increment(ref fetches);
            return Task.FromResult(EngineKit.Html(EngineKit.Page("bygglov " + name)));
        });
        var seed = await EngineKit.SeedUrlListAsync(database, [site.Url("/p/a"), site.Url("/p/b")]);
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs);
        try
        {
            (await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, seed))).Added.ShouldBe(2);
            var document = await Sql.ScalarAsync<Guid>(database.Connection(null), """
                SELECT "Id" FROM content."Documents" WHERE "SourceId" = $1 AND "CanonicalIdentity" LIKE '%/p/a'
                """, seed.SourceId);
            var before = await ExecutionAsync(document);
            var fetched = fetches;

            var job = await EnqueueAsync(seed, new ReprocessDocumentCommand { DocumentId = document });
            await Commands.WaitForStateAsync(database, job, JobStates.Succeeded);

            var result = await CommandSeeds.ResultAsync(database, job);
            result.GetProperty("runId").GetGuid().ShouldBe(job);
            var execution = result.GetProperty("executionId").GetGuid();
            execution.ShouldNotBe(before);
            (await ExecutionAsync(document)).ShouldBe(execution);
            var run = await EngineKit.WaitForRunAsync(database, job);
            run.ShouldBe(new RunCounters(RunStates.Succeeded, 0, 1, 0, 0, 0, 0));
            (await Sql.ScalarAsync<string>(database.Connection(null), """SELECT "Kind" || ':' || "Trigger" FROM content."Runs" WHERE "Id" = $1""", job))
                .ShouldBe("reprocess:manual");
            (await Sql.ScalarAsync<string>(database.Connection(null), """
                SELECT string_agg("Outcome" || ':' || "ExecutionId"::text, ',') FROM content."RunItems" WHERE "RunId" = $1
                """, job)).ShouldBe($"updated:{execution}");
            fetches.ShouldBe(fetched, "a reprocess never fetches");
            (await EngineKit.LiveChunksAsync(database, seed)).ShouldBeGreaterThanOrEqualTo(2);

            var fetch = await EnqueueAsync(seed, new ReprocessDocumentCommand { DocumentId = document, FromStage = "fetch" });
            var unknown = await EnqueueAsync(seed, new ReprocessDocumentCommand { DocumentId = Guid.NewGuid() });
            await Commands.WaitForStateAsync(database, fetch, JobStates.Failed);
            await Commands.WaitForStateAsync(database, unknown, JobStates.Failed);
            (await Sql.JobAsync(database.Connection(null), fetch)).LastError.ShouldBe(ReprocessDocumentHandler.FetchUnsupported);
            (await Sql.JobAsync(database.Connection(null), unknown)).LastError.ShouldBe(ReprocessDocumentHandler.DocumentUnknown);
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    private async Task<Guid> EnqueueAsync(SourceSeed seed, ReprocessDocumentCommand command)
    {
        var request = new JobRequest(JobClasses.Command, seed.DepartmentId, seed.Classification, ContractJson.Serialize(command))
        {
            DedupKey = JobDedupKeys.Reprocess(command.DocumentId),
        };
        (await database.Queue(RagDatabase.Admin).EnqueueAsync([request], null, Ct)).ShouldContain(request.Id);
        return request.Id;
    }

    private Task<Guid> ExecutionAsync(Guid document) =>
        Sql.ScalarAsync<Guid>(database.Connection(null), """SELECT "CurrentExecutionId" FROM content."Documents" WHERE "Id" = $1""", document);
}
