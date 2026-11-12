extern alias fakegateway;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Infrastructure.Security;
using Ume.Rag.TestKit;
using Ume.Rag.Worker.Jobs;
using Ume.Rag.Worker.Processing.Embedding;
using Ume.Rag.Worker.Tests.Connectors;
using Ume.Rag.Worker.Tests.Processing;

namespace Ume.Rag.Worker.Tests.Engine;

/// <summary>
/// A-34's done-when for ingestion (#580, gateway phase 2; engine design §7.3, §7.7; identity §4.7; gateway-changes §5.2, §5.7)
/// against the FakeGateway with <c>act-as-key</c>, read by the worker from its own context: a sync of a collection with a key
/// sends every embedding call as that key through the worker's credential key, and one without a key sends none; a spent
/// budget or a disabled key waits until the next full hour with an attempt counted and fails the document with
/// <c>gateway.budget_exhausted</c> or <c>gateway.key_unavailable</c> on the last attempt; an answer without the key's echo
/// fails the document with <c>gateway.delegation_not_applied</c>, logs event 5011 and stores no vector.
/// </summary>
[Collection(RagDatabaseGroup2.Name)]
public sealed class ActingAsKeyEngineTests(RagDatabaseFixture database, FakeGatewayFactory gateway) : IClassFixture<FakeGatewayFactory>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly DirectoryInfo _blobs = Directory.CreateTempSubdirectory("ume-rag-acting-blobs-");

    public async ValueTask InitializeAsync() => await database.ResetAsync();

    public ValueTask DisposeAsync()
    {
        _blobs.Delete(recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_sync_of_a_collection_with_a_key_sends_every_embedding_call_as_that_key_and_one_without_a_key_sends_none()
    {
        var marker = Guid.NewGuid().ToString("N");
        await using var site = await EngineKit.SiteAsync((name, _) => Task.FromResult(EngineKit.Html(EngineKit.Page($"bygglov {name} {marker}"))));
        var keyed = await EngineKit.SeedUrlListAsync(database, [site.Url("/p/alfa"), site.Url("/p/beta")]);
        var keyless = await EngineKit.SeedUrlListAsync(database, [site.Url("/p/gamma")]);
        await TestGatewayKeys.AssignAsync(database, keyed.CollectionId, TestGatewayKeys.KnowledgeBase);
        await using var worker = await StartWorkerAsync(site, gateway);
        try
        {
            var run = await EngineKit.SyncNowAsync(database, keyed);
            (await EngineKit.WaitForRunAsync(database, run)).ShouldBe(new RunCounters(RunStates.Succeeded, 2, 0, 0, 0, 0, 0));
            var other = await EngineKit.SyncNowAsync(database, keyless);
            (await EngineKit.WaitForRunAsync(database, other)).ShouldBe(new RunCounters(RunStates.Succeeded, 1, 0, 0, 0, 0, 0));

            var acted = TestGatewayKeys.Served(gateway, run);
            acted.ShouldNotBeEmpty();
            acted.ShouldAllBe(r => r.Endpoint == "/v1/embeddings" && r.Step == EmbeddingClient.EmbedStep);
            acted.Select(r => (r.KeyId, r.ViaKeyId)).Distinct().ShouldBe([(TestGatewayKeys.KnowledgeBase, TestGatewayKeys.WorkerCredentialId)],
                "every call runs as the collection's key, through the worker's credential key");
            var plain = TestGatewayKeys.Served(gateway, other);
            plain.ShouldNotBeEmpty();
            plain.Select(r => (r.KeyId, r.ViaKeyId)).Distinct().ShouldBe([(TestGatewayKeys.WorkerCredentialId, (Guid?)null)],
                "a collection without a key runs as the credential key itself");
        }
        finally
        {
            await EngineKit.PauseAsync(database, keyed);
            await EngineKit.PauseAsync(database, keyless);
        }
    }

    [Theory]
    [InlineData(false, GatewayRefusals.BudgetExhausted)]
    [InlineData(true, GatewayRefusals.KeyUnavailable)]
    public async Task A_refused_key_waits_until_the_next_full_hour_and_fails_the_document_with_the_reason_after_the_last_attempt(bool disabled, string reason)
    {
        await using var site = await EngineKit.SiteAsync((name, _) => Task.FromResult(EngineKit.Html(EngineKit.Page($"snöröjning {name} {Guid.NewGuid():N}"))));
        var seed = await EngineKit.SeedUrlListAsync(database, [site.Url("/p/alfa")]);
        await TestGatewayKeys.AssignAsync(database, seed.CollectionId, disabled ? TestGatewayKeys.Disabled : TestGatewayKeys.BudgetSpent);
        // A short idle wait: the dispatcher sleeps until the waiting job's next hour, and the update below sends no notification.
        await using var worker = await StartWorkerAsync(site, gateway, settings: new Dictionary<string, string?> { ["Worker:IdleWaitSeconds"] = "1" });
        try
        {
            var run = await EngineKit.SyncNowAsync(database, seed);
            Guid job = default;
            await Commands.WaitUntilAsync(async () => (job = await WaitingEmbedJobAsync(run, reason)) != default, TimeSpan.FromSeconds(60));

            var waiting = await Sql.JobAsync(database.Connection(null), job);
            waiting.Attempts.ShouldBe((short)1, "the refusal counts an attempt");
            var availableAt = await Sql.ScalarAsync<DateTime>(database.Connection(null), """SELECT "AvailableAt" FROM content."Jobs" WHERE "Id" = $1""", job);
            var fullHour = new DateTime(availableAt.Ticks - (availableAt.Ticks % TimeSpan.TicksPerHour), DateTimeKind.Utc);
            var offHour = availableAt - fullHour;
            (offHour < TimeSpan.FromSeconds(2) || offHour > TimeSpan.FromHours(1) - TimeSpan.FromSeconds(2)).ShouldBeTrue($"AvailableAt {availableAt:O} is the next full hour");
            waiting.AvailableIn.ShouldBeInRange(TimeSpan.Zero, TimeSpan.FromHours(1) + TimeSpan.FromSeconds(2));

            // The job's last attempt (as if the earlier ones had been refused too): the document fails with the reason.
            await Sql.ExecuteAsync(database.Connection(null), """
                UPDATE content."Jobs" SET "Attempts" = "MaxAttempts" - 1, "AvailableAt" = now() WHERE "Id" = $1 AND "State" = 'queued'
                """, job);
            (await EngineKit.WaitForRunAsync(database, run)).ShouldBe(new RunCounters(RunStates.Failed, 0, 0, 0, 0, 1, 0));
            (await RunItemsAsync(run)).ShouldBe($"failed:{reason}");
            (await Sql.JobAsync(database.Connection(null), job)).State.ShouldBe(JobStates.Succeeded, "the run item ends the job, which does not die");
            (await ChunksAsync(seed)).ShouldBe(0L);
            TestGatewayKeys.Served(gateway, run).ShouldBeEmpty("the gateway served nothing as the refused key");
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task An_answer_without_the_keys_echo_fails_the_document_with_event_5011_and_stores_no_vector()
    {
        await using var dropping = gateway.WithWebHostBuilder(web => web.UseSetting("FakeGateway:DropOnBehalfOfEcho", "true"));
        await using var site = await EngineKit.SiteAsync((name, _) => Task.FromResult(EngineKit.Html(EngineKit.Page($"bibliotek {name} {Guid.NewGuid():N}"))));
        var seed = await EngineKit.SeedUrlListAsync(database, [site.Url("/p/alfa")]);
        await TestGatewayKeys.AssignAsync(database, seed.CollectionId, TestGatewayKeys.KnowledgeBase);
        var logs = new CapturingLoggerProvider();
        var cached = await CacheRowsAsync();
        await using var worker = await StartWorkerAsync(site, dropping, logs);
        try
        {
            var run = await EngineKit.SyncNowAsync(database, seed);

            (await EngineKit.WaitForRunAsync(database, run)).ShouldBe(new RunCounters(RunStates.Failed, 0, 0, 0, 0, 1, 0));
            (await RunItemsAsync(run)).ShouldBe($"failed:{GatewayRefusals.DelegationNotApplied}");
            var events = logs.Entries.Where(e => e.Category == SecurityEvents.Category && e.EventId.Id == 5011).ToList();
            events.ShouldNotBeEmpty();
            events.ShouldAllBe(e => e.Message.Contains(TestGatewayKeys.KnowledgeBase.ToString(), StringComparison.Ordinal) &&
                                    e.Message.Contains(seed.CollectionId.ToString(), StringComparison.Ordinal) &&
                                    e.Message.Contains(EmbeddingClient.EmbedStep, StringComparison.Ordinal));
            logs.Text.ShouldNotContain(FakeGatewayFactory.WorkerCredentialKey);
            (await ChunksAsync(seed)).ShouldBe(0L, "no chunk is written");
            (await CacheRowsAsync()).ShouldBe(cached, "no vector goes to the embedding cache");
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    /// <summary>A worker with the engine on <paramref name="fake"/>, acting as keys from its first job.</summary>
    private async Task<WorkerHost> StartWorkerAsync(TestSite site, WebApplicationFactory<fakegateway::Program> fake, CapturingLoggerProvider? logs = null,
        IReadOnlyDictionary<string, string?>? settings = null)
    {
        var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs, logs: logs, settings: settings, services: s =>
        {
            s.AddHttpClient(EmbeddingClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => fake.Server.CreateHandler());
            TestGatewayKeys.ReadContextFrom(s, fake);
        });
        await TestGatewayKeys.ActAsKeysAsync(worker.Services);
        return worker;
    }

    /// <summary>The run's embed job once it waits, queued with <paramref name="reason"/>; default until then.</summary>
    private async Task<Guid> WaitingEmbedJobAsync(Guid runId, string reason) =>
        await Sql.ScalarAsync<Guid>(database.Connection(null), """
            SELECT "Id" FROM content."Jobs" WHERE "RunId" = $1 AND "Class" = 'embed' AND "State" = 'queued' AND "LastError" = $2
            """, runId, reason);

    private Task<string> RunItemsAsync(Guid runId) =>
        Sql.ScalarAsync<string>(database.Connection(null), """
            SELECT string_agg("Outcome" || ':' || coalesce("Reason", '-'), ',') FROM content."RunItems" WHERE "RunId" = $1
            """, runId);

    private Task<long> ChunksAsync(SourceSeed seed) =>
        Sql.ScalarAsync<long>(database.Connection(null), """
            SELECT count(*) FROM content."Chunks" c JOIN content."Documents" d ON d."Id" = c."DocumentId" WHERE d."SourceId" = $1
            """, seed.SourceId);

    private Task<long> CacheRowsAsync() => Sql.ScalarAsync<long>(database.Connection(null), """SELECT count(*) FROM content."EmbeddingCache" """);
}
