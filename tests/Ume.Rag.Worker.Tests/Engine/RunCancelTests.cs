using Ume.Rag.Domain;
using Ume.Rag.TestKit;
using Ume.Rag.Worker.Engine;

namespace Ume.Rag.Worker.Tests.Engine;

/// <summary>A cancelled run keeps the counters of what it did before the cancel (engine design §10.1, §10.4).</summary>
[Collection(RagDatabaseGroup3.Name)]
public sealed class RunCancelTests(RagDatabaseFixture database) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await database.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_cancelled_run_keeps_its_counters()
    {
        var seed = await EngineKit.SeedUploadAsync(database, Classification.Public);
        var runId = Guid.CreateVersion7();
        await Sql.ExecuteAsync(database.Connection(RagDatabase.Worker), """
            INSERT INTO content."Runs" ("Id", "SourceId", "Classification", "Kind", "Trigger", "Phase", "State",
                                       "Added", "Updated", "Unchanged", "Deleted", "Failed", "ProposedDeletions", "CostUsd")
            VALUES ($1, $2, $3, 'sync', 'manual', 'fetch', 'running', 0, 0, 0, 0, 0, 0, 0)
            """, runId, seed.SourceId, seed.Classification);
        // Two pages added, one of them first failed and then retried, one unchanged, one failed.
        foreach (var (identity, outcome) in new[]
                 {
                     ("https://example.se/a", "added"), ("https://example.se/b", "failed"), ("https://example.se/b", "added"),
                     ("https://example.se/c", "unchanged"), ("https://example.se/d", "failed"),
                 })
        {
            await Sql.ExecuteAsync(database.Connection(RagDatabase.Worker), """
                INSERT INTO content."RunItems" ("RunId", "Classification", "Identity", "Outcome", "CostUsd") VALUES ($1, $2, $3, $4, 0.01)
                """, runId, seed.Classification, identity, outcome);
        }

        (await new RunStore(database.Database(RagDatabase.Worker)).CancelAsync(runId, Ct)).ShouldBeTrue();

        var row = await Sql.ScalarAsync<string>(database.Connection(null), """
            SELECT concat_ws(' ', "State", "Added", "Updated", "Unchanged", "Failed", "CostUsd"::numeric(6,2), "FinishedAt" IS NOT NULL)
            FROM content."Runs" WHERE "Id" = $1
            """, runId);
        row.ShouldBe("cancelled 2 0 1 1 0.05 t");
    }
}
