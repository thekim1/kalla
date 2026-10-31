extern alias fakegateway;

using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Ume.Rag.Answering;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Access;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Domain.Retrieval;
using Ume.Rag.Infrastructure;
using Ume.Rag.Infrastructure.Access;
using Ume.Rag.Retrieval;
using Ume.Rag.Retrieval.Postgres;
using Ume.Rag.TestKit;
using FakeContent = fakegateway::Ume.Rag.FakeGateway.FakeContent;

namespace Ume.Rag.Api.Tests;

/// <summary>
/// Its own ragdb with fixture chunks embedded as the FakeGateway embeds (1024-dimension feature hashing), and a snapshot
/// built by hand: a public collection with a web page and an upload, an unpublished collection, a sensitive one, an
/// assistant over the public and the unpublished collection, and the clients <c>rag-internal</c> and Open WebUI.
/// </summary>
public sealed class KnowledgeFixture : RagDatabase, IAsyncLifetime
{
    public const int Dimensions = 1024;

    public FakeGatewayFactory Gateway { get; } = new();

    public Guid ProfileId { get; private set; }
    public Guid PublicCollection { get; } = Guid.CreateVersion7();
    public Guid UnpublishedCollection { get; } = Guid.CreateVersion7();
    public Guid SensitiveCollection { get; } = Guid.CreateVersion7();
    public Guid PublicSource { get; } = Guid.CreateVersion7();
    public Guid UnpublishedSource { get; } = Guid.CreateVersion7();
    public Guid SensitiveSource { get; } = Guid.CreateVersion7();
    public Guid AssistantId { get; } = Guid.CreateVersion7();
    public Guid InternalClient { get; } = Guid.CreateVersion7();
    public Guid OwuiClient { get; } = Guid.CreateVersion7();
    public FixtureDocument Altan { get; private set; } = null!;
    public FixtureDocument Upload { get; private set; } = null!;
    public FixtureDocument Hidden { get; private set; } = null!;

    async ValueTask IAsyncLifetime.InitializeAsync()
    {
        await InitializeAsync();
        await using var writer = new ContentFixtureWriter(Connection(Worker), Connection(Admin), (text, dims) => FakeContent.Vector(text, dims));
        ProfileId = await writer.AddEmbeddingProfileAsync(Dimensions);
        Altan = await writer.AddDocumentAsync(new FixtureDocumentSpec
        {
            CollectionId = PublicCollection, EmbeddingProfileId = ProfileId, SourceId = PublicSource, Title = "Altan och uteplats",
            Url = "https://www.umea.se/bygga/altan", MediaType = "text/html", Language = "sv", SourceModifiedAt = new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero),
        }, [new FixtureChunk("Du behöver bygglov för att bygga en altan som är högre än 1,2 meter.") { HeadingPath = "Bygglov › Altan" }]);
        Upload = await writer.AddDocumentAsync(new FixtureDocumentSpec
        {
            CollectionId = PublicCollection, EmbeddingProfileId = ProfileId, SourceId = PublicSource, IdentityKind = "upload",
            Title = "Taxa 2026", DisplayName = "taxa-bygglov-2026.pdf", MediaType = "application/pdf", PageCount = 4,
        }, [new FixtureChunk("Avgiften för bygglov för en altan beräknas enligt taxan för 2026.")]);
        Hidden = await writer.AddDocumentAsync(new FixtureDocumentSpec
        {
            CollectionId = UnpublishedCollection, EmbeddingProfileId = ProfileId, SourceId = UnpublishedSource, Title = "Utkast",
        }, [new FixtureChunk("Bygglov för altan: utkast som inte är publicerat.")]);
        await writer.AddDocumentAsync(new FixtureDocumentSpec
        {
            CollectionId = SensitiveCollection, EmbeddingProfileId = ProfileId, SourceId = SensitiveSource, Classification = "sensitive", Title = "Känsligt",
        }, [new FixtureChunk("Bygglov för altan hos en enskild person.")]);
    }

    /// <summary>The snapshot, with the profile's model <paramref name="model"/> (FakeGateway behaviour by name).</summary>
    public PolicySnapshot Snapshot(string model = "bge-m3")
    {
        CollectionEntry Collection(Guid id, string level, bool published) =>
            new(id, "c-" + id.ToString("N")[..8], "Samling", Guid.Empty, level, ProfileId, "{}", published ? DateTimeOffset.UnixEpoch : null, false);
        var clients = new[]
        {
            new ClientEntry(InternalClient, "rag-internal", ClientKinds.Internal, Classification.Sensitive, false, true),
            new ClientEntry(OwuiClient, "Open WebUI", ClientKinds.OpenWebUi, Classification.Internal, true, true),
        }.ToDictionary(c => c.Id);
        var collections = new[]
        {
            Collection(PublicCollection, Classification.Public, true), Collection(UnpublishedCollection, Classification.Public, false),
            Collection(SensitiveCollection, Classification.Sensitive, true),
        }.ToDictionary(c => c.Id);
        var assistant = new AssistantEntry(AssistantId, "bygg", "Bygg", Classification.Internal, HistoryModes.None, AssistantStates.Active, Guid.Empty, null,
            new PublishedVersionEntry(Guid.NewGuid(), 1, "ume/chat-eu", [PublicCollection, UnpublishedCollection]),
            [new AudienceEntry(AssistantAudienceKinds.Client, OwuiClient, null)]);
        return PolicySnapshot.Empty with
        {
            Version = 3,
            Clients = clients,
            Assistants = new Dictionary<Guid, AssistantEntry> { [AssistantId] = assistant },
            Collections = collections,
            SourcesByCollection = new Dictionary<Guid, IReadOnlyList<Guid>>
            {
                [PublicCollection] = [PublicSource], [UnpublishedCollection] = [UnpublishedSource], [SensitiveCollection] = [SensitiveSource],
            },
            EmbeddingProfiles = new Dictionary<Guid, EmbeddingProfileEntry>
            {
                [ProfileId] = new(ProfileId, "pilot", model, Dimensions, Residency.Eu, EmbeddingProfileStates.Active, null),
            },
            LevelPolicies = Classification.All.ToDictionary(l => l, l => new LevelPolicyEntry(l, [HistoryModes.None], l == Classification.Sensitive),
                StringComparer.Ordinal),
        };
    }

    public RagPrincipal Who(Guid client, PolicySnapshot snapshot) => new(
        client == InternalClient ? SubjectKind.Application : SubjectKind.OwuiUser, "s", null, null, snapshot.Clients[client],
        client == InternalClient ? CredentialKind.ServiceKey : CredentialKind.SecretKey, null, new HashSet<string>(), MembershipFacts.None, snapshot.Version);

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        await Gateway.DisposeAsync();
        await DisposeAsync();
    }
}

/// <summary>The knowledge role's core (knowledge design §4, §5.5, §6.3, §10; item 12 part 2) on Postgres and the FakeGateway.</summary>
public sealed class KnowledgeServiceTests(KnowledgeFixture db) : IClassFixture<KnowledgeFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FixedSnapshot(PolicySnapshot snapshot, bool confirmed = true) : IPolicySnapshotSource
    {
        public PolicySnapshot Current => snapshot;
        public bool IsConfirmed => confirmed;
    }

    private (KnowledgeService Service, RetrievalCounter Counter, IHost Host) Build(PolicySnapshot snapshot, bool confirmed = true, HttpMessageHandler? gateway = null)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Configuration.AddInMemoryCollection([
            new("Gateway:BaseUrl", "http://fake-gateway"), new("Gateway:Keys:Eu", FakeGatewayFactory.EuKey),
        ]);
        builder.AddGatewayClient(GatewayQueryEmbedder.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => gateway ?? db.Gateway.Server.CreateHandler());
        var host = builder.Build();
        var dataSource = RetrievalDataSource.Create(db.Connection(RagDatabase.Api));
        var index = new PostgresSearchIndex(dataSource);
        var counter = new RetrievalCounter(dataSource, TimeProvider.System, NullLogger<RetrievalCounter>.Instance);
        var embedder = new GatewayQueryEmbedder(host.Services.GetRequiredService<IHttpClientFactory>(), new QueryEmbeddingOptions(), new byte[32],
            TimeProvider.System, NullLogger<GatewayQueryEmbedder>.Instance);
        var service = new KnowledgeService(new FixedSnapshot(snapshot, confirmed), new AccessPolicy(AccessPolicy.DefaultKnowledgeClientKinds),
            new RetrievalEngine(index, new ScopeSizeCache(index, TimeProvider.System)), new PostgresHydrator(dataSource), embedder, counter, dataSource,
            NoDownloadLinks.Instance, NullLogger<KnowledgeService>.Instance);
        return (service, counter, host);
    }

    private static RetrievalRequest Search(params Guid[] collections) =>
        new("Behöver jag bygglov för altan?", null, collections, Purpose: RetrievalPurpose.Http);

    [Fact]
    public async Task An_internal_caller_gets_hybrid_results_with_citations_and_the_counters_are_written()
    {
        var snapshot = db.Snapshot();
        var (service, counter, host) = Build(snapshot);
        using var _ = host;

        var result = await service.RetrieveAsync(db.Who(db.InternalClient, snapshot), Search(db.PublicCollection), Ct);

        result.Degraded.ShouldBeNull();
        result.ConfigVersion.ShouldBe(3);
        result.Results.Select(r => r.Rank).ShouldBe(Enumerable.Range(1, result.Results.Count));
        result.Results.Select(r => r.DocumentId).ShouldBe([db.Altan.Id, db.Upload.Id], ignoreOrder: true);
        var altan = result.Results.Single(r => r.DocumentId == db.Altan.Id);
        altan.HeadingPath.ShouldBe("Bygglov › Altan");
        altan.Scores.VectorRank.ShouldNotBeNull();
        altan.Scores.KeywordRank.ShouldNotBeNull();
        altan.Citation.ShouldBe(new Citation(db.Altan.Id, "Altan och uteplats", null, "https://www.umea.se/bygga/altan", null, "web", altan.Citation.DocumentType,
            "text/html", "sv", new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero)));
        var upload = result.Results.Single(r => r.DocumentId == db.Upload.Id).Citation;
        (upload.Title, upload.DisplayName, upload.Url, upload.SourceType).ShouldBe(("taxa-bygglov-2026.pdf", "taxa-bygglov-2026.pdf", null, "file"));

        service.MarkCited(result, [altan.Rank]);
        (await counter.FlushAsync(Ct)).ShouldBeGreaterThan(0);
        await using var connection = new NpgsqlConnection(db.Connection(null));
        await connection.OpenAsync(Ct);
        await using var stats = new NpgsqlCommand("""SELECT "Retrieved" || ':' || "Cited" FROM content."RetrievalStats" WHERE "DocumentId" = $1""", connection)
        {
            Parameters = { new() { Value = db.Altan.Id } },
        };
        ((string?)await stats.ExecuteScalarAsync(Ct)).ShouldBe("1:1");
    }

    [Fact]
    public async Task An_assistant_searches_only_its_live_collections_and_narrowing_outside_them_is_404()
    {
        var snapshot = db.Snapshot();
        var (service, _, host) = Build(snapshot);
        using var __ = host;
        var who = db.Who(db.OwuiClient, snapshot);

        var result = await service.RetrieveAsync(who, new RetrievalRequest("bygglov altan", db.AssistantId, null), Ct);
        result.Results.ShouldNotBeEmpty();
        result.Results.ShouldAllBe(r => r.CollectionId == db.PublicCollection); // the unpublished collection is skipped silently

        var outside = await Should.ThrowAsync<RetrievalDeniedException>(() =>
            service.RetrieveAsync(who, new RetrievalRequest("bygglov altan", db.AssistantId, [db.SensitiveCollection]), Ct));
        (outside.Status, outside.Code).ShouldBe((404, KnowledgeCodes.CollectionNotFound));
    }

    [Fact]
    public async Task Refusals_follow_the_policy_and_happen_before_any_search()
    {
        var snapshot = db.Snapshot();
        var (service, _, host) = Build(snapshot);
        using var _ = host;

        var unpublished = await Should.ThrowAsync<RetrievalDeniedException>(() =>
            service.RetrieveAsync(db.Who(db.InternalClient, snapshot), Search(db.UnpublishedCollection), Ct));
        (unpublished.Status, unpublished.Code, unpublished.Subject).ShouldBe((404, KnowledgeCodes.CollectionNotFound, db.UnpublishedCollection.ToString()));

        var owui = await Should.ThrowAsync<RetrievalDeniedException>(() => service.RetrieveAsync(db.Who(db.OwuiClient, snapshot), Search(db.PublicCollection), Ct));
        (owui.Status, owui.Code).ShouldBe((403, DenyReasons.ClientNotAllowed));

        var (stale, _, staleHost) = Build(snapshot, confirmed: false);
        using var ___ = staleHost;
        (await Should.ThrowAsync<RetrievalDeniedException>(() => stale.RetrieveAsync(db.Who(db.InternalClient, snapshot), Search(db.PublicCollection), Ct)))
            .Status.ShouldBe(503);

        (await Should.ThrowAsync<RetrievalDeniedException>(() => service.RetrieveAsync(db.Who(db.InternalClient, snapshot),
            Search(db.PublicCollection) with { TopK = 51 }, Ct))).Code.ShouldBe(KnowledgeCodes.InvalidRequest);
    }

    [Fact]
    public async Task A_failed_embedding_degrades_hybrid_to_keyword_and_refuses_vector()
    {
        // The fake answers this model from External: above the profile's Eu ceiling, so the answer is discarded (§5.5).
        var snapshot = db.Snapshot("fake-wrong-residency-embed");
        var (service, _, host) = Build(snapshot);
        using var _ = host;
        var who = db.Who(db.InternalClient, snapshot);

        var degraded = await service.RetrieveAsync(who, Search(db.PublicCollection), Ct);
        degraded.Degraded.ShouldBe(RetrievalDegradation.EmbeddingResidency);
        degraded.Coverage.Status.ShouldBe(CoverageStatus.Unknown);
        degraded.Results.ShouldNotBeEmpty();
        degraded.Results.ShouldAllBe(r => r.Scores.VectorRank == null);

        var vector = await Should.ThrowAsync<RetrievalDeniedException>(() => service.RetrieveAsync(who, Search(db.PublicCollection) with { Mode = RetrievalMode.Vector }, Ct));
        (vector.Status, vector.Code).ShouldBe((503, KnowledgeCodes.EmbeddingUnavailable));
    }

    [Fact]
    public async Task Documents_and_collections_are_visible_only_within_the_callers_collections()
    {
        var snapshot = db.Snapshot();
        var (service, _, host) = Build(snapshot);
        using var _ = host;
        var who = db.Who(db.InternalClient, snapshot);

        var document = await service.GetDocumentAsync(who, db.Upload.Id, Ct);
        (document.Citation.Title, document.PageCount, document.CollectionId).ShouldBe(("taxa-bygglov-2026.pdf", 4, db.PublicCollection));
        (await Should.ThrowAsync<RetrievalDeniedException>(() => service.GetDocumentAsync(who, db.Hidden.Id, Ct))).Status.ShouldBe(404);
        (await Should.ThrowAsync<RetrievalDeniedException>(() => service.GetDocumentAsync(who, Guid.NewGuid(), Ct))).Status.ShouldBe(404);

        (await service.ListCollectionsAsync(who, Ct)).Select(c => c.Id).ShouldBe([db.PublicCollection, db.SensitiveCollection], ignoreOrder: true);
    }
}

/// <summary>The query embedder's cache, single flight and circuit (knowledge design §5.3, §5.4).</summary>
public sealed class QueryEmbedderTests(FakeGatewayFactory fake) : IClassFixture<FakeGatewayFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly EmbeddingProfileEntry Profile = new(Guid.NewGuid(), "pilot", "bge-m3", 1024, Residency.Eu, EmbeddingProfileStates.Active, null);

    private sealed class Counting(HttpMessageHandler inner, HttpStatusCode? fail = null) : DelegatingHandler(inner)
    {
        public int Calls;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            if (fail is { } status)
            {
                return new HttpResponseMessage(status);
            }

            await Task.Delay(50, cancellationToken); // long enough for concurrent misses to meet
            return await base.SendAsync(request, cancellationToken);
        }
    }

    private static (GatewayQueryEmbedder, IHost) Build(HttpMessageHandler handler)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Configuration.AddInMemoryCollection([new("Gateway:BaseUrl", "http://fake-gateway"), new("Gateway:Keys:Eu", FakeGatewayFactory.EuKey)]);
        builder.AddGatewayClient(GatewayQueryEmbedder.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
        var host = builder.Build();
        return (new GatewayQueryEmbedder(host.Services.GetRequiredService<IHttpClientFactory>(), new QueryEmbeddingOptions(), new byte[32], TimeProvider.System,
            NullLogger<GatewayQueryEmbedder>.Instance), host);
    }

    [Fact]
    public async Task Concurrent_and_repeated_queries_cost_one_gateway_call()
    {
        var counting = new Counting(fake.Server.CreateHandler());
        var (embedder, host) = Build(counting);
        using var _ = host;
        using var __ = embedder;

        var vectors = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => embedder.EmbedAsync(Profile, "bygglov altan", null, Ct)));
        var again = await embedder.EmbedAsync(Profile, "bygglov altan", null, Ct);

        counting.Calls.ShouldBe(1);
        vectors.ShouldAllBe(v => v.Length == 1024);
        again.ToArray().ShouldBe(vectors[0].ToArray());
        embedder.QueryMac("bygglov altan").ShouldNotContain("bygglov");
    }

    [Fact]
    public async Task Five_failures_open_the_circuit_and_skip_the_gateway()
    {
        var counting = new Counting(fake.Server.CreateHandler(), HttpStatusCode.InternalServerError);
        var (embedder, host) = Build(counting);
        using var _ = host;
        using var __ = embedder;

        for (var i = 0; i < 5; i++)
        {
            await Should.ThrowAsync<QueryEmbeddingUnavailableException>(() => embedder.EmbedAsync(Profile, "fråga " + i, null, Ct));
        }

        embedder.CircuitOpen.ShouldBeTrue();
        await Should.ThrowAsync<QueryEmbeddingUnavailableException>(() => embedder.EmbedAsync(Profile, "en till", null, Ct));
        counting.Calls.ShouldBe(5); // 500 is not retried, and the open circuit makes no call
    }
}
