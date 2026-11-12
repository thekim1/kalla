using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Worker.Processing.Embedding;
using Ume.Rag.Worker.Tests.Processing;

namespace Ume.Rag.Worker.Tests.Engine;

/// <summary>
/// A-31's done-when for ingestion (engine design §7.7, §10.2, §10.4, §10.6; processing design §11) against the FakeGateway,
/// whose every embedding answer carries <c>x-ume-cost-usd</c>: a sync of two documents gives each run item the sum of its
/// embedding calls' gateway cost and the run that sum, <c>CollectionDailyUsage</c> holds the day's <c>embed</c> row with the
/// same calls, tokens and cost, every call carried the run's id, and a second run, all cache hits, adds nothing.
/// </summary>
[Collection(RagDatabaseGroup3.Name)]
public sealed class EngineUsageTests(RagDatabaseFixture database, FakeGatewayFactory gateway) : IClassFixture<FakeGatewayFactory>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly DirectoryInfo _blobs = Directory.CreateTempSubdirectory("ume-rag-usage-blobs-");

    public async ValueTask InitializeAsync() => await database.ResetAsync();

    public ValueTask DisposeAsync()
    {
        _blobs.Delete(recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_sync_of_two_documents_gives_each_run_item_its_calls_cost_the_run_their_sum_and_the_day_its_embed_row()
    {
        var marker = Guid.NewGuid().ToString("N");
        string Title(string name) => $"bygglov {name} {marker}";
        await using var site = await EngineKit.SiteAsync((name, _) => Task.FromResult(EngineKit.Html(EngineKit.Page(Title(name)))));
        var seed = await EngineKit.SeedUrlListAsync(database, [site.Url("/p/alfa"), site.Url("/p/beta")]);
        var recorder = new GatewayCallRecorder();
        var firstDay = EngineKit.StockholmToday();
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs,
            services: s => s.AddHttpClient(EmbeddingClient.HttpClientName).AddHttpMessageHandler(recorder.Handler));
        try
        {
            var runId = await EngineKit.SyncNowAsync(database, seed);
            (await EngineKit.WaitForRunAsync(database, runId)).ShouldBe(new RunCounters(RunStates.Succeeded, 2, 0, 0, 0, 0, 0));

            var calls = recorder.Answered;
            calls.Count.ShouldBe(recorder.Calls.Count, "every call was answered");
            calls.ShouldAllBe(c => c.Path == "/v1/embeddings" && c.Step == EmbeddingClient.EmbedStep && c.CostUsd > 0 && c.InputTokens > 0);
            calls.ShouldAllBe(c => c.RunId == runId.ToString(), "every call carries the run's id as x-ume-run-id");
            var items = await RunItemCostsAsync(runId);
            foreach (var name in new[] { "alfa", "beta" })
            {
                var own = calls.Where(c => c.Body.Contains(Title(name), StringComparison.Ordinal)).ToList();
                own.ShouldNotBeEmpty(name);
                items.Single(i => i.Identity.EndsWith("/p/" + name, StringComparison.Ordinal)).CostUsd.ShouldBe(own.Sum(c => c.CostUsd!.Value),
                    $"{name}: the sum of its embedding calls' x-ume-cost-usd");
            }

            var total = calls.Sum(c => c.CostUsd!.Value);
            (await Sql.ScalarAsync<decimal>(database.Connection(null), """SELECT "CostUsd" FROM content."Runs" WHERE "Id" = $1""", runId))
                .ShouldBe(total, "the finalizer sums the run items");

            var usage = await EngineKit.DailyUsageAsync(database, seed.CollectionId);
            usage.Keys.ShouldBe([EmbeddingClient.EmbedStep]);
            var embed = usage[EmbeddingClient.EmbedStep];
            (embed.Calls, embed.InputTokens, embed.CachedInputTokens, embed.OutputTokens, embed.CostUsd, embed.EstimatedCalls, embed.EstimatedCostUsd)
                .ShouldBe((calls.Count, calls.Sum(c => c.InputTokens), 0L, 0L, total, 0, 0m));
            embed.Classifications.ShouldBe([seed.Classification]);
            embed.Departments.ShouldBe([seed.DepartmentId], "the collection's department at the time of the calls");
            embed.Days.ShouldAllBe(d => d >= firstDay && d <= EngineKit.StockholmToday(), "the Europe/Stockholm day of the calls");

            // Nothing changed: every document is unchanged, no call is made and the day's row stays as it was.
            var second = await EngineKit.SyncNowAsync(database, seed);
            (await EngineKit.WaitForRunAsync(database, second)).Unchanged.ShouldBe(2);
            recorder.Calls.Count.ShouldBe(calls.Count);
            var after = (await EngineKit.DailyUsageAsync(database, seed.CollectionId))[EmbeddingClient.EmbedStep];
            (after.Calls, after.InputTokens, after.CostUsd).ShouldBe((embed.Calls, embed.InputTokens, embed.CostUsd));
            (await Sql.ScalarAsync<decimal>(database.Connection(null), """SELECT "CostUsd" FROM content."Runs" WHERE "Id" = $1""", second)).ShouldBe(0m);
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    private async Task<IReadOnlyList<(string Identity, decimal? CostUsd)>> RunItemCostsAsync(Guid runId)
    {
        await using var connection = new NpgsqlConnection(database.Connection(null));
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""SELECT "Identity", "CostUsd" FROM content."RunItems" WHERE "RunId" = $1""", connection)
        {
            Parameters = { new NpgsqlParameter { Value = runId } },
        };
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var items = new List<(string, decimal?)>();
        while (await reader.ReadAsync(Ct))
        {
            items.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetDecimal(1)));
        }

        return items;
    }
}
