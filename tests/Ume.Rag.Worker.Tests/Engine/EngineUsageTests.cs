extern alias fakegateway;

using Ume.Rag.TestKit;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Worker.Processing.Embedding;
using Ume.Rag.Worker.Tests.Processing;
using FakeGatewayOptions = fakegateway::Ume.Rag.FakeGateway.FakeGatewayOptions;
using FakeModel = fakegateway::Ume.Rag.FakeGateway.FakeModel;

namespace Ume.Rag.Worker.Tests.Engine;

/// <summary>
/// A-31's done-when for ingestion (engine design §7.7, §10.2, §10.4, §10.6; processing design §11) against the FakeGateway,
/// whose every embedding answer carries <c>x-ume-cost-usd</c>: a sync of two documents gives each run item the sum of its
/// embedding calls' gateway cost and the run that sum, <c>CollectionDailyUsage</c> holds the day's <c>embed</c> row with the
/// same calls, tokens and cost, every call carried the run's id, and a second run, all cache hits, adds nothing. A-40's
/// done-when (engine design §17): every answered call counts in <c>rag.embed.tokens</c> and <c>rag.ingestion.cost</c>, the
/// cost split by <c>source</c>.
/// </summary>
[Collection(RagDatabaseGroup3.Name)]
public sealed class EngineUsageTests(RagDatabaseFixture database, FakeGatewayFactory gateway) : IClassFixture<FakeGatewayFactory>, IAsyncLifetime
{
    private const string NoCostModel = fakegateway::Ume.Rag.FakeGateway.FakeCatalog.NoCostModel;

    /// <summary>An embedding profile on <see cref="NoCostModel"/>, for the collection whose calls are estimated.</summary>
    private static readonly Guid NoCostProfileId = Guid.Parse("01929b6e-0000-7000-8000-0000000e1e40");

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

    [Fact]
    public async Task Every_answered_embedding_call_counts_its_input_tokens_and_its_cost_split_by_source_with_the_job_class_as_the_only_other_tag()
    {
        // fake-no-cost, served as an embedding model by this test's fake, answers without x-ume-cost-usd; the mirror prices it
        // (per million: 0.15 in), so its calls are estimated. bge-m3's answers carry the gateway's cost.
        await Sql.ExecuteAsync(database.Connection(null), """
            INSERT INTO config."GatewayModels" ("Name", "Kind", "Residencies", "Source", "SupportsTools", "SupportsToolsSource", "InputPerMillionUsd",
                                                "CachedInputPerMillionUsd", "OutputPerMillionUsd")
            VALUES ($1, 'model', ARRAY['OnPrem'], 'manual', false, 'manual', 0.15, 0.075, 0.60)
            ON CONFLICT ("Name") DO UPDATE SET "InputPerMillionUsd" = 0.15, "CachedInputPerMillionUsd" = 0.075, "OutputPerMillionUsd" = 0.60
            """, NoCostModel);
        var marker = Guid.NewGuid().ToString("N");
        string Title(string name) => $"bygglov {name} {marker}";
        await using var site = await EngineKit.SiteAsync((name, _) => Task.FromResult(EngineKit.Html(EngineKit.Page(Title(name)))));
        var charged = await EngineKit.SeedUrlListAsync(database, [site.Url("/p/alfa")]);
        var estimated = await EngineKit.SeedUrlListAsync(database, [site.Url("/p/beta")]);
        await Sql.ExecuteAsync(database.Connection(null), """
            INSERT INTO config."EmbeddingProfiles" ("Id", "Name", "GatewayModel", "Dimensions", "Distance", "MaxInputTokens", "IsDefault", "State", "MaxResidency", "UpdatedAt")
            VALUES ($1, 'engine-test-no-cost', $2, $3, 'cosine', 8192, false, 'active', 'Eu', now())
            ON CONFLICT ("Id") DO NOTHING
            """, NoCostProfileId, NoCostModel, EngineKit.Dimensions);
        await Sql.ExecuteAsync(database.Connection(null), """UPDATE config."Collections" SET "EmbeddingProfileId" = $2 WHERE "Id" = $1""",
            estimated.CollectionId, NoCostProfileId);
        await using var fake = gateway.WithWebHostBuilder(b => b.ConfigureServices(s => s.PostConfigure<FakeGatewayOptions>(o =>
            o.Models.Single(m => m.Name == NoCostModel).Kind = FakeModel.Embedding)));
        var recorder = new GatewayCallRecorder();
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs,
            services: s => s.AddHttpClient(EmbeddingClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => fake.Server.CreateHandler())
                .AddHttpMessageHandler(recorder.Handler).Services.UseGatewayCredentialCeiling());
        using var metrics = new MeasurementRecorder(worker.Services.GetRequiredService<IMeterFactory>(), "rag.embed.tokens", "rag.ingestion.cost");
        try
        {
            foreach (var seed in new[] { charged, estimated })
            {
                (await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, seed))).Added.ShouldBe(1);
            }

            var calls = recorder.Answered;
            calls.Count.ShouldBe(recorder.Calls.Count, "every call was answered");
            var gatewayCalls = calls.Where(c => c.Body.Contains(Title("alfa"), StringComparison.Ordinal)).ToList();
            var estimatedCalls = calls.Where(c => c.Body.Contains(Title("beta"), StringComparison.Ordinal)).ToList();
            (gatewayCalls.Count + estimatedCalls.Count).ShouldBe(calls.Count, "every call embeds one of the two documents");
            gatewayCalls.ShouldNotBeEmpty();
            gatewayCalls.ShouldAllBe(c => c.CostUsd > 0);
            estimatedCalls.ShouldNotBeEmpty();
            estimatedCalls.ShouldAllBe(c => c.CostUsd == null, "fake-no-cost sends no cost");

            var tokens = metrics.Of("rag.embed.tokens");
            tokens.Count.ShouldBe(calls.Count, "once per answered call");
            tokens.Sum(m => m.Value).ShouldBe((double)calls.Sum(c => c.InputTokens), "the input tokens of every embedding call");
            tokens.ShouldAllBe(m => m.Tags.Count == 0, "rag.embed.tokens has no tags");

            var cost = metrics.Of("rag.ingestion.cost");
            cost.Count.ShouldBe(calls.Count, "once per answered call");
            cost.ShouldAllBe(m => m.Tags.Keys.Order().SequenceEqual(new[] { "class", "source" }), "the job class and the source, no id or text");
            cost.ShouldAllBe(m => m.Tags["class"] == JobClasses.Embed, "the job class the embedding calls ran in");
            var bySource = cost.GroupBy(m => m.Tags["source"]).ToDictionary(g => g.Key!, g => g.ToList());
            bySource.Keys.Order().ShouldBe(["estimate", "gateway"]);
            bySource["gateway"].Count.ShouldBe(gatewayCalls.Count);
            bySource["gateway"].Sum(m => m.Value).ShouldBe((double)gatewayCalls.Sum(c => c.CostUsd!.Value), 1e-9, "the gateway's x-ume-cost-usd");
            var estimate = estimatedCalls.Sum(c => Math.Round(c.InputTokens * 0.15m / 1_000_000m, 8, MidpointRounding.AwayFromZero));
            estimate.ShouldBeGreaterThan(0m);
            bySource["estimate"].Count.ShouldBe(estimatedCalls.Count);
            bySource["estimate"].Sum(m => m.Value).ShouldBe((double)estimate, 1e-9, "the estimate from the mirror's prices");
        }
        finally
        {
            await EngineKit.PauseAsync(database, charged);
            await EngineKit.PauseAsync(database, estimated);
            await Sql.ExecuteAsync(database.Connection(null), """DELETE FROM config."GatewayModels" WHERE "Name" = $1""", NoCostModel);
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

/// <summary>The measurements a worker's meter records on the named counters, with their tags (other hosts' meters are ignored).</summary>
internal sealed class MeasurementRecorder : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentQueue<RecordedMeasurement> _measurements = new();

    public MeasurementRecorder(IMeterFactory factory, params string[] names)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (names.Contains(instrument.Name) && ReferenceEquals(instrument.Meter.Scope, factory))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Add(instrument, value, tags));
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Add(instrument, value, tags));
        _listener.Start();
    }

    /// <summary>What <paramref name="name"/> recorded so far.</summary>
    public IReadOnlyList<RecordedMeasurement> Of(string name) => [.. _measurements.Where(m => m.Name == name)];

    public void Dispose() => _listener.Dispose();

    private void Add(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags) =>
        _measurements.Enqueue(new RecordedMeasurement(instrument.Name, value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value?.ToString())));
}

internal sealed record RecordedMeasurement(string Name, double Value, IReadOnlyDictionary<string, string?> Tags);
