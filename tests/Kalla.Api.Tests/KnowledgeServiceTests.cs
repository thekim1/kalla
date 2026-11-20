extern alias fakegateway;

using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Options;
using Kalla.Domain.Ports;
using Kalla.Infrastructure.Blobs;
using Kalla.Infrastructure.Config;
using Kalla.Domain.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Kalla.Answering;
using Kalla.Answering.Chat;
using Kalla.Domain;
using Kalla.Domain.Access;
using Kalla.Domain.Entities;
using Kalla.Domain.Retrieval;
using Kalla.Infrastructure;
using Kalla.Infrastructure.Access;
using Kalla.Infrastructure.Gateway;
using Kalla.Retrieval;
using Kalla.Retrieval.Postgres;
using Kalla.TestKit;
using FakeContent = fakegateway::Kalla.FakeGateway.FakeContent;

namespace Kalla.Api.Tests;

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
    public Guid SectionCollection { get; } = Guid.CreateVersion7();
    public Guid SectionSource { get; } = Guid.CreateVersion7();
    public FixtureDocument Plank { get; private set; } = null!;
    public FixtureDocument Sensitive { get; private set; } = null!;
    public Guid UploadSlot { get; } = Guid.CreateVersion7();
    public static readonly byte[] UploadHash = Convert.FromHexString("ab" + new string('0', 62));
    public static readonly byte[] UploadBytes = "%PDF-1.7 taxa"u8.ToArray();
    /// <summary>The crawl that last confirmed Altan, and the upload time of Taxa (knowledge §10.1).</summary>
    public static readonly DateTimeOffset AltanChecked = new(2026, 11, 18, 4, 30, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset UploadedAt = new(2026, 2, 3, 9, 0, 0, TimeSpan.Zero);
    public const string KeyPepper = "knowledge-api-test-pepper-0123456789abcdef";
    public const string GrantKey = "knowledge-api-test-grant-key-0123456789abc";
    public string BlobRoot { get; } = Directory.CreateTempSubdirectory("kalla-api-blobs-").FullName;

    /// <summary>The plain keys of <c>rag-internal</c> and Open WebUI, stored in <c>config."Keys"</c> (the clients have the snapshot's ids).</summary>
    public string InternalKey { get; private set; } = null!;
    public string OwuiKey { get; private set; } = null!;
    public string IntegrationKey { get; private set; } = null!;

    /// <summary>rag-internal keys allowed only from <see cref="AllowedRanges"/> (identity §6.3 step 4); the second is revoked.</summary>
    public string RestrictedKey { get; private set; } = null!;
    public string RevokedRestrictedKey { get; private set; } = null!;
    public static readonly string[] AllowedRanges = ["203.0.113.0/24", "2001:db8::/48"];
    public Guid AssistantId { get; } = Guid.CreateVersion7();
    public Guid InternalClient { get; } = Guid.CreateVersion7();
    public Guid OwuiClient { get; } = Guid.CreateVersion7();
    public Guid IntegrationClient { get; } = Guid.CreateVersion7();
    public Guid DeptACollection { get; } = Guid.CreateVersion7();
    public Guid DeptBCollection { get; } = Guid.CreateVersion7();
    public Guid DeptASource { get; } = Guid.CreateVersion7();
    public Guid DeptBSource { get; } = Guid.CreateVersion7();
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
            FetchedAt = AltanChecked,
        }, [new FixtureChunk("Du behöver bygglov för att bygga en altan som är högre än 1,2 meter.") { HeadingPath = "Bygglov › Altan", Tags = ["altan"] }]);
        Upload = await writer.AddDocumentAsync(new FixtureDocumentSpec
        {
            CollectionId = PublicCollection, EmbeddingProfileId = ProfileId, SourceId = PublicSource, IdentityKind = "upload",
            CanonicalIdentity = "upload:" + UploadSlot, Title = "Taxa 2026", DisplayName = "Bygglov/Taxor/taxa-bygglov-2026.pdf", MediaType = "application/pdf", PageCount = 4,
            FetchedAt = UploadedAt,
        }, [new FixtureChunk("Avgiften för bygglov för en altan beräknas enligt taxan för 2026.")]);
        await writer.ExecuteAsync($"""UPDATE content."Documents" SET "ContentHash" = '\x{Convert.ToHexString(UploadHash)}' WHERE "Id" = '{Upload.Id}'""");
        var blobs = new FileSystemBlobStore(Options.Create(new BlobStoreOptions { Root = BlobRoot }), TimeProvider.System);
        await blobs.PutAsync(new BlobKey($"uploads/{PublicSource:D}/{UploadSlot:D}/{Convert.ToHexStringLower(UploadHash)}"), new MemoryStream(UploadBytes), null,
            TestContext.Current.CancellationToken);
        InternalKey = await AddKeyAsync(InternalClient, "rag-internal", ClientKinds.Internal, Classification.Sensitive);
        OwuiKey = await AddKeyAsync(OwuiClient, "Open WebUI", ClientKinds.OpenWebUi, Classification.Internal);
        IntegrationKey = await AddKeyAsync(IntegrationClient, "Integration", ClientKinds.Integration, Classification.Internal);
        RestrictedKey = await AddKeyToAsync(InternalClient, "restricted", key => key.AllowedIpRanges = [.. AllowedRanges]);
        RevokedRestrictedKey = await AddKeyToAsync(InternalClient, "restricted, revoked", key =>
        {
            key.AllowedIpRanges = [.. AllowedRanges];
            key.RevokedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        });

        // §16.6: the same file uploaded by two departments under different names is two documents, each cited by its own name.
        foreach (var (collection, source, name) in new[] { (DeptACollection, DeptASource, "tomtkarta-bygg.pdf"), (DeptBCollection, DeptBSource, "karta-miljo.pdf") })
        {
            await writer.AddDocumentAsync(new FixtureDocumentSpec
            {
                CollectionId = collection, EmbeddingProfileId = ProfileId, SourceId = source, IdentityKind = "upload", DisplayName = name,
                Title = "Tomtkarta", MediaType = "application/pdf",
            }, [new FixtureChunk("Tomtkartan visar fastighetsgränser och servitut.")]);
        }
        Hidden = await writer.AddDocumentAsync(new FixtureDocumentSpec
        {
            CollectionId = UnpublishedCollection, EmbeddingProfileId = ProfileId, SourceId = UnpublishedSource, Title = "Utkast",
        }, [new FixtureChunk("Bygglov för altan: utkast som inte är publicerat.")]);
        Sensitive = await writer.AddDocumentAsync(new FixtureDocumentSpec
        {
            CollectionId = SensitiveCollection, EmbeddingProfileId = ProfileId, SourceId = SensitiveSource, Classification = "sensitive", Title = "Känsligt",
        }, [new FixtureChunk("Bygglov för altan hos en enskild person.")]);

        // One section of four chunks (the third a subsection, the fourth repeating 16 characters of the third), then another section.
        const string PlankPath = "Plank och mur";
        Plank = await writer.AddDocumentAsync(new FixtureDocumentSpec
        {
            CollectionId = SectionCollection, EmbeddingProfileId = ProfileId, SourceId = SectionSource, Title = "Plank",
        },
        [
            new FixtureChunk("Ett plank är en tät skärm.") { HeadingPath = PlankPath, Ordinal = 0, TokenCount = 100 },
            new FixtureChunk("Plank högre än 110 centimeter kräver bygglov.") { HeadingPath = PlankPath, Ordinal = 1, TokenCount = 100 },
            new FixtureChunk("Mät från marken. Höjden räknas från den lägsta sidan.") { HeadingPath = PlankPath + " › Höjd", Ordinal = 2, TokenCount = 100 },
            new FixtureChunk("Höjden räknas från den lägsta sidan. Fråga oss vid tvekan.") { HeadingPath = PlankPath + " › Höjd", Ordinal = 3, TokenCount = 100, OverlapChars = 37 },
            new FixtureChunk("Murar och stödmurar beskrivs på en egen sida.") { HeadingPath = "Mur", Ordinal = 4, TokenCount = 100 },
        ]);
    }

    private async Task<string> AddKeyAsync(Guid clientId, string name, string kind, string ceiling)
    {
        var generated = new KeyHasher(System.Text.Encoding.UTF8.GetBytes(KeyPepper)).Generate();
        await using var context = TestPostgres.Context(Connection(Admin));
        await context.ExecuteConfigWriteAsync(async (_, ct) =>
        {
            var client = new Client { Id = clientId, Name = name, Kind = kind, ClassificationCeiling = ceiling, UpdatedAt = DateTimeOffset.UtcNow };
            context.AddRange(client, new ApiKey
            {
                Client = client, Name = name, Prefix = generated.Prefix, KeyHash = generated.Hash, CreatedAt = DateTimeOffset.UtcNow, CreatedBy = "test",
            });
            await context.SaveChangesAsync(ct);
            return 0;
        }, TestContext.Current.CancellationToken);
        return generated.PlainText;
    }

    /// <summary>Another key of the existing client <paramref name="clientId"/>, shaped by <paramref name="configure"/>.</summary>
    private async Task<string> AddKeyToAsync(Guid clientId, string name, Action<ApiKey> configure)
    {
        var generated = new KeyHasher(System.Text.Encoding.UTF8.GetBytes(KeyPepper)).Generate();
        await using var context = TestPostgres.Context(Connection(Admin));
        await context.ExecuteConfigWriteAsync(async (_, ct) =>
        {
            var key = new ApiKey
            {
                ClientId = clientId, Name = name, Prefix = generated.Prefix, KeyHash = generated.Hash, CreatedAt = DateTimeOffset.UtcNow, CreatedBy = "test",
            };
            configure(key);
            context.Add(key);
            await context.SaveChangesAsync(ct);
            return 0;
        }, TestContext.Current.CancellationToken);
        return generated.PlainText;
    }

    /// <summary>
    /// kalla-api on this database (api role) with the FakeGateway, the blob root, and <paramref name="snapshot"/> in place of
    /// the snapshot store (keys still come from <c>config."Keys"</c>).
    /// </summary>
    public WebApplicationFactory<Program> ApiHost(PolicySnapshot? snapshot = null, bool confirmed = true) => new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
    {
        web.UseEnvironment("Development");
        foreach (var (key, value) in new Dictionary<string, string?>
        {
            ["ConnectionStrings:ragdb"] = Connection(Api), ["Security:KeyPepper"] = KeyPepper, ["Security:CacheKeyPepper"] = new string('c', 32),
            ["Security:DownloadGrantKeys:k1"] = GrantKey, ["Gateway:BaseUrl"] = "http://fake-gateway", ["Gateway:Key"] = FakeGatewayFactory.ApiCredentialKey,
            ["BlobStore:Root"] = BlobRoot, ["Rag:Roles"] = "knowledge",
            // Generous limits (#781, as #584 for the service hosts): the first call through a fresh host on a busy runner can pass
            // the 2 s default, and the answer is then "search unavailable" instead of the one a test expects. The time-out
            // itself is tested on the embedder alone.
            ["Retrieval:AttemptTimeout"] = "00:00:30", ["Retrieval:TotalTimeout"] = "00:00:30",
        })
        {
            web.UseSetting(key, value);
        }

        web.ConfigureTestServices(services =>
        {
            services.AddSingleton<IPolicySnapshotSource>(new FixedSnapshot(snapshot ?? Snapshot(), confirmed));
            services.UseGatewayCredentialCeiling();
            services.AddHttpClient(GatewayQueryEmbedder.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Gateway.Server.CreateHandler());
        });
    });

    public sealed class FixedSnapshot(PolicySnapshot snapshot, bool confirmed = true) : IPolicySnapshotSource
    {
        public PolicySnapshot Current => snapshot;
        public bool IsConfirmed => confirmed;
    }

    /// <summary>The snapshot, with the profile's model <paramref name="model"/> (FakeGateway behaviour by name).</summary>
    public PolicySnapshot Snapshot(string model = "bge-m3")
    {
        CollectionEntry Collection(Guid id, string level, bool published) =>
            new(id, "c-" + id.ToString("N"), "Samling", Guid.Empty, level, ProfileId, "{}", published ? DateTimeOffset.UnixEpoch : null, false);
        var clients = new[]
        {
            new ClientEntry(InternalClient, "rag-internal", ClientKinds.Internal, Classification.Sensitive, false, true),
            // The deployment's own Open WebUI client (identity §5.2): the one whose users kalla-api trusts.
            new ClientEntry(OwuiClient, "Open WebUI", ClientKinds.OpenWebUi, Classification.Internal, true, true, IsManagedByDeployment: true),
            new ClientEntry(IntegrationClient, "Integration", ClientKinds.Integration, Classification.Internal, false, true),
        }.ToDictionary(c => c.Id);
        var collections = new[]
        {
            Collection(PublicCollection, Classification.Public, true), Collection(UnpublishedCollection, Classification.Public, false),
            Collection(SensitiveCollection, Classification.Sensitive, true), Collection(SectionCollection, Classification.Public, true),
            Collection(DeptACollection, Classification.Public, true), Collection(DeptBCollection, Classification.Public, true),
        }.ToDictionary(c => c.Id);
        var assistant = new AssistantEntry(AssistantId, "bygg", "Bygg", Classification.Internal, HistoryModes.None, AssistantStates.Active, Guid.Empty, null,
            new PublishedVersionEntry(Guid.NewGuid(), 1, "fors/chat-eu", [CollectionLink.Whole(PublicCollection), CollectionLink.Whole(UnpublishedCollection)]),
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
                [SectionCollection] = [SectionSource], [DeptACollection] = [DeptASource], [DeptBCollection] = [DeptBSource],
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
        Directory.Delete(BlobRoot, recursive: true);
    }
}

/// <summary>The knowledge role's core (knowledge design §4, §5.5, §6.3, §10; item 12 part 2) on Postgres and the FakeGateway.</summary>
public sealed class KnowledgeServiceTests(KnowledgeFixture db) : IClassFixture<KnowledgeFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private (KnowledgeService Service, RetrievalCounter Counter, IHost Host) Build(PolicySnapshot snapshot, bool confirmed = true, HttpMessageHandler? gateway = null,
        IDownloadLinks? links = null, IReranker? reranker = null, RetrievalEngineOptions? options = null)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Configuration.AddInMemoryCollection([
            new("Gateway:BaseUrl", "http://fake-gateway"), new("Gateway:Key", FakeGatewayFactory.ApiCredentialKey),
        ]);
        builder.AddGatewayClient(GatewayQueryEmbedder.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => gateway ?? db.Gateway.Server.CreateHandler());
        builder.Services.UseGatewayCredentialCeiling();
        // Made by the container, so disposing the host closes its connections (the api role has a connection limit).
        builder.Services.AddSingleton(_ => RetrievalDataSource.Create(db.Connection(RagDatabase.Api)));
        var host = builder.Build();
        var dataSource = host.Services.GetRequiredService<NpgsqlDataSource>();
        var index = new PostgresSearchIndex(dataSource);
        var counter = new RetrievalCounter(dataSource, TimeProvider.System, NullLogger<RetrievalCounter>.Instance);
        // Generous limits (#584): the first call through a fresh host on a busy runner can pass the 2 s default and turn an
        // expected gateway answer into embedding_unavailable. The timeout tests use the embedder-only Build below.
        var embedding = new QueryEmbeddingOptions { AttemptTimeout = TimeSpan.FromSeconds(30), TotalTimeout = TimeSpan.FromSeconds(30) };
        var embedder = new GatewayQueryEmbedder(host.Services.GetRequiredService<IHttpClientFactory>(), embedding, new byte[32],
            TimeProvider.System, NullLogger<GatewayQueryEmbedder>.Instance);
        var service = new KnowledgeService(new KnowledgeFixture.FixedSnapshot(snapshot, confirmed), new AccessPolicy(AccessPolicy.DefaultKnowledgeClientKinds),
            new RetrievalEngine(index, new ScopeSizeCache(index, TimeProvider.System), options, reranker, new PostgresHydrator(dataSource)), new PostgresHydrator(dataSource),
            embedder, counter, dataSource,
            links ?? NoDownloadLinks.Instance, NullLogger<KnowledgeService>.Instance);
        return (service, counter, host);
    }

    /// <summary>The chat role over <paramref name="knowledge"/>, for the turn plan (no gateway call without history or a fixed answer).</summary>
    private static ChatService Chat(KnowledgeService knowledge, PolicySnapshot snapshot, IHost host)
    {
        var options = Options.Create(new ChatOptions());
        var clients = host.Services.GetRequiredService<IHttpClientFactory>();
        return new ChatService(new KnowledgeFixture.FixedSnapshot(snapshot, true), new AccessPolicy(AccessPolicy.DefaultKnowledgeClientKinds),
            knowledge, new GatewayChatClient(clients, options, NullLogger<GatewayChatClient>.Instance), options,
            host.Services.GetRequiredService<GatewayCeiling>(), TimeProvider.System, NullLogger<ChatService>.Instance);
    }

    private static RetrievalRequest Search(params Guid[] collections) =>
        new("Behöver jag bygglov för altan?", null, collections, Purpose: RetrievalPurpose.Http);

    /// <summary>Records each rerank request and scores the passages in reverse fused order (the last one best).</summary>
    private sealed class RecordingReranker : IReranker
    {
        public List<RerankRequest> Requests { get; } = [];

        public Task<RerankScores?> ScoreAsync(RerankRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult<RerankScores?>(new RerankScores([.. request.Passages.Select((_, i) => (double)i)]));
        }
    }

    [Fact]
    public async Task An_assistant_that_reranks_reranks_at_the_callers_model_ceiling_and_no_other_does()
    {
        // B-12 (Decisions "Rerank pulled forward"): QuerySteps.retrieval.rerank turns it on, off by default; chat then passes its
        // turn's ceiling as RerankCeiling, and the model is Settings.Retrieval.rerankModel.
        var plain = db.Snapshot() with { RetrievalSettings = """{"rerankModel": "fors/small-eu"}""" };
        var assistant = plain.Assistants[db.AssistantId];
        var reranking = plain with
        {
            Assistants = new Dictionary<Guid, AssistantEntry>
            {
                [db.AssistantId] = assistant with { Published = assistant.Published! with { QuerySteps = """{"retrieval": {"rerank": "chat"}}""" } },
            },
        };
        var reranker = new RecordingReranker();
        var (service, _, host) = Build(reranking, reranker: reranker);
        using var _ = host;
        var who = db.Who(db.OwuiClient, reranking);
        var runId = Guid.NewGuid();

        var fused = await service.RetrieveAsync(who, new RetrievalRequest("bygglov altan", db.AssistantId, [db.PublicCollection], RunId: runId), Ct);
        reranker.Requests.ShouldBeEmpty(); // no rerank ceiling (not chat): the fused order
        fused.Results.ShouldAllBe(r => r.Scores.RerankScore == null);

        var reranked = await service.RetrieveAsync(who,
            new RetrievalRequest("bygglov altan", db.AssistantId, [db.PublicCollection], RunId: runId, RerankCeiling: Residency.Eu), Ct);
        var call = reranker.Requests.ShouldHaveSingleItem();
        (call.Model, call.Ceiling, call.RunId, call.Query).ShouldBe(("fors/small-eu", Residency.Eu, (Guid?)runId, "bygglov altan"));
        call.Passages.Count.ShouldBeGreaterThan(1);
        call.Passages.ShouldAllBe(p => p.Text.Length > 0); // the texts come from the hydrator's extra statement
        reranked.Degraded.ShouldBeNull();
        reranked.Results[0].ChunkId.ShouldBe(call.Passages[^1].ChunkId);
        reranked.Results[0].Scores.RerankScore.ShouldBe(call.Passages.Count - 1);

        // Chat asks for the rerank at the turn's ceiling for the assistant with the setting, and never for one without it.
        var question = new ParsedChat(assistant.Slug, "bygglov altan", [], false, false, null);
        await Chat(service, reranking, host).PlanAsync(who, question, Ct);
        reranker.Requests.Count.ShouldBe(2);
        reranker.Requests[1].Ceiling.ShouldBe(ChatService.Ceiling(who, reranking.Assistants[db.AssistantId], reranking));
        reranker.Requests[1].Reranker.ShouldBe(RerankerKind.ChatModel);
        var (plainService, _, plainHost) = Build(plain, reranker: reranker);
        using var __ = plainHost;
        await Chat(plainService, plain, plainHost).PlanAsync(db.Who(db.OwuiClient, plain), question, Ct);
        reranker.Requests.Count.ShouldBe(2);
    }

    /// <summary>The snapshot with the assistant's <c>retrieval.rerank</c> set to <paramref name="rerank"/> and <c>Settings.Retrieval</c> to <paramref name="retrieval"/>.</summary>
    private PolicySnapshot Reranking(string rerank, string retrieval)
    {
        var plain = db.Snapshot() with { RetrievalSettings = retrieval };
        var assistant = plain.Assistants[db.AssistantId];
        return plain with
        {
            Assistants = new Dictionary<Guid, AssistantEntry>
            {
                [db.AssistantId] = assistant with { Published = assistant.Published! with { QuerySteps = $$$"""{"retrieval": {"rerank": "{{{rerank}}}"}}""" } },
            },
        };
    }

    [Theory]
    [InlineData("chat", RerankerKind.ChatModel)]
    [InlineData("gateway", RerankerKind.Gateway)]
    public async Task With_rerank_fully_unconfigured_retrieval_and_chat_make_no_rerank_call_and_degrade_nothing(string rerank, RerankerKind kind)
    {
        // B-16, rerank optional everywhere (user, 2026-11-06): an assistant set to rerank on a platform with no rerank model
        // (the usual setup) gets the plain fused order: no call, no rerank_unavailable, no caveat.
        var snapshot = Reranking(rerank, "{}");
        var reranker = new RecordingReranker();
        var (service, _, host) = Build(snapshot, reranker: reranker);
        using var _ = host;
        var who = db.Who(db.OwuiClient, snapshot);

        var fused = await service.RetrieveAsync(who, new RetrievalRequest("bygglov altan", db.AssistantId, [db.PublicCollection]), Ct);
        var asked = await service.RetrieveAsync(who,
            new RetrievalRequest("bygglov altan", db.AssistantId, [db.PublicCollection], RerankCeiling: Residency.Eu, Reranker: kind), Ct);
        asked.Results.Select(r => r.ChunkId).ShouldBe(fused.Results.Select(r => r.ChunkId));
        asked.Results.ShouldAllBe(r => r.Scores.RerankScore == null);
        asked.Degraded.ShouldBeNull();

        var plan = await Chat(service, snapshot, host).PlanAsync(who, new ParsedChat("bygg", "bygglov altan", [], false, false, null), Ct);
        plan.Settings.Rerank.ShouldBe(kind);
        plan.Retrieval.ShouldNotBeNull().Degraded.ShouldBeNull();
        plan.Degraded.ShouldBeFalse();
        plan.Caveat.ShouldBeNull();
        reranker.Requests.ShouldBeEmpty();
    }

    /// <summary>The engine's reranker as the knowledge role builds it, on the services' gateway client to <paramref name="gateway"/>.</summary>
    private static (RerankRouter Reranker, IHost Host, ListLogger Log) GatewayRerankers(WebApplicationFactory<fakegateway::Program> gateway)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Configuration.AddInMemoryCollection([
            new("Gateway:BaseUrl", "http://fake-gateway"), new("Gateway:Key", FakeGatewayFactory.ApiCredentialKey),
        ]);
        builder.AddGatewayClient(ChatModelReranker.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => gateway.Server.CreateHandler());
        builder.Services.UseGatewayCredentialCeiling();
        var host = builder.Build();
        var clients = host.Services.GetRequiredService<IHttpClientFactory>();
        var log = new ListLogger();
        return (new RerankRouter(new ChatModelReranker(clients), new GatewayReranker(clients, logger: log)), host, log);
    }

    [Fact]
    public async Task GatewayReranker_against_the_FakeGateway_rerank_reorders_and_a_gateway_without_the_model_keeps_the_fused_order()
    {
        // B-16 against the FakeGateway's /v1/rerank (B-14): word-overlap scores with FakeGateway:RerankEnabled; 404 without it.
        var snapshot = Reranking("gateway", """{"gatewayRerankModel": "bge-reranker-v2-m3"}""");
        var options = new RetrievalEngineOptions { GatewayRerankTimeout = TimeSpan.FromSeconds(10) };
        await using var enabled = db.Gateway.WithWebHostBuilder(web => web.UseSetting("FakeGateway:RerankEnabled", "true"));
        var (reranker, rerankHost, _) = GatewayRerankers(enabled);
        using var __ = rerankHost;
        var (service, _, host) = Build(snapshot, reranker: reranker, options: options);
        using var _ = host;
        var who = db.Who(db.OwuiClient, snapshot);
        var request = new RetrievalRequest("bygglov altan", db.AssistantId, [db.PublicCollection], RunId: Guid.NewGuid(), RerankCeiling: Residency.Eu,
            Reranker: RerankerKind.Gateway);

        var reranked = await service.RetrieveAsync(who, request, Ct);
        reranked.Degraded.ShouldBeNull();
        reranked.Results.ShouldAllBe(r => r.Scores.RerankScore != null);
        var scores = reranked.Results.Select(r => r.Scores.RerankScore!.Value).ToList();
        scores.ShouldBe(scores.OrderByDescending(x => x).ToList()); // the rerank order (one document each, no cap at play)
        reranked.Results[0].Scores.RerankScore!.Value.ShouldBe(FakeContent.Overlap("bygglov altan", reranked.Results[0].Text), 1e-9);

        // The same platform and assistant on a gateway with no rerank model (the default): 404, the fused order, logged once.
        var (missing, missingHost, log) = GatewayRerankers(db.Gateway);
        using var ___ = missingHost;
        var (plainService, _, plainHost) = Build(snapshot, reranker: missing, options: options);
        using var ____ = plainHost;
        var fused = await plainService.RetrieveAsync(who, request with { RerankCeiling = null }, Ct);
        for (var i = 0; i < 2; i++)
        {
            var kept = await plainService.RetrieveAsync(who, request, Ct);
            kept.Degraded.ShouldBe(RetrievalDegradation.RerankUnavailable);
            kept.Results.Select(r => r.ChunkId).ShouldBe(fused.Results.Select(r => r.ChunkId));
            kept.Results.ShouldAllBe(r => r.Scores.RerankScore == null);
        }

        log.Messages.ShouldHaveSingleItem().ShouldContain("HTTP 404");
    }

    /// <summary>Collects the formatted log messages of warning level and above.</summary>
    private sealed class ListLogger : ILogger<GatewayReranker>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
            {
                lock (Messages)
                {
                    Messages.Add(formatter(state, exception));
                }
            }
        }
    }

    [Fact]
    public async Task A_source_demote_pattern_ranks_its_pages_lower_and_never_an_upload()
    {
        // P1b item 3 part b (knowledge design §6.5): Sources.DemoteRules from the snapshot, matched on Documents.Url.
        var plain = db.Snapshot();
        var demoted = plain with
        {
            DemoteRules = new Dictionary<Guid, string> { [db.PublicSource] = """[{"pattern": "www.umea.se/bygga/**", "factor": 0.1}]""" },
        };
        var (service, _, host) = Build(demoted);
        using var _ = host;
        var (plainService, _, plainHost) = Build(plain);
        using var __ = plainHost;

        var before = await plainService.RetrieveAsync(db.Who(db.InternalClient, plain), Search(db.PublicCollection), Ct);
        var after = await service.RetrieveAsync(db.Who(db.InternalClient, demoted), Search(db.PublicCollection), Ct);

        before.Results[0].DocumentId.ShouldBe(db.Altan.Id);
        after.Results.Select(r => r.DocumentId).ShouldBe([db.Upload.Id, db.Altan.Id]); // the upload has no URL: never demoted
        after.Coverage.Status.ShouldBe(before.Coverage.Status);
    }

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
            "text/html", "sv", new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero), KnowledgeFixture.AltanChecked));
        var upload = result.Results.Single(r => r.DocumentId == db.Upload.Id).Citation;
        // A folder upload's display name is its relative path (admin design §4.5); the title is the file name.
        (upload.Title, upload.DisplayName, upload.Url, upload.SourceType).ShouldBe(("taxa-bygglov-2026.pdf", "Bygglov/Taxor/taxa-bygglov-2026.pdf", null, "file"));
        // §10.1 (Decisions "Source dates on citations"): a file that states no date is modified at its upload time and never checked.
        (upload.Modified, upload.Checked).ShouldBe((KnowledgeFixture.UploadedAt, (DateTimeOffset?)null));

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
    public async Task An_assistants_published_link_topics_limit_its_collection_and_a_direct_caller_searches_it_whole()
    {
        // Knowledge design §4.2, §6.2 (P1b item 34): only the chunks carrying one of the link's topics; the upload carries none.
        var plain = db.Snapshot();
        var assistant = plain.Assistants[db.AssistantId];
        var topical = plain with
        {
            Assistants = new Dictionary<Guid, AssistantEntry>
            {
                [db.AssistantId] = assistant with
                {
                    Published = assistant.Published! with { CollectionLinks = [new CollectionLink(db.PublicCollection, ["altan"]), CollectionLink.Whole(db.SectionCollection)] },
                },
            },
        };
        var (service, _, host) = Build(topical);
        using var _ = host;
        var who = db.Who(db.OwuiClient, topical);

        var linked = await service.RetrieveAsync(who, new RetrievalRequest("bygglov altan", db.AssistantId, null), Ct);
        linked.Results.Where(r => r.CollectionId == db.PublicCollection).Select(r => r.DocumentId).ShouldBe([db.Altan.Id]);
        linked.Results.ShouldContain(r => r.CollectionId == db.SectionCollection); // linked without topics: the whole collection

        var narrowed = await service.RetrieveAsync(who, new RetrievalRequest("bygglov altan", db.AssistantId, [db.PublicCollection]), Ct);
        narrowed.Results.Select(r => r.DocumentId).ShouldBe([db.Altan.Id]);

        var direct = await service.RetrieveAsync(db.Who(db.InternalClient, topical), Search(db.PublicCollection), Ct);
        direct.Results.Select(r => r.DocumentId).ShouldBe([db.Altan.Id, db.Upload.Id], ignoreOrder: true);
    }

    [Fact]
    public async Task A_chat_requests_topics_narrow_the_published_links_and_never_widen_them()
    {
        // Chat §3.8, knowledge §4.2 (Decisions "Topics per request"): the altan page carries "altan"; the upload and the section
        // collection's pages carry no tag.
        PolicySnapshot Linked(params CollectionLink[] links)
        {
            var plain = db.Snapshot();
            var assistant = plain.Assistants[db.AssistantId];
            return plain with
            {
                Assistants = new Dictionary<Guid, AssistantEntry>
                {
                    [db.AssistantId] = assistant with { Published = assistant.Published! with { CollectionLinks = links } },
                },
            };
        }

        async Task<RetrievalResult> Ask(PolicySnapshot snapshot, params string[]? topics)
        {
            var (service, _, host) = Build(snapshot);
            using var _ = host;
            return await service.RetrieveAsync(db.Who(db.OwuiClient, snapshot), new RetrievalRequest("bygglov altan", db.AssistantId, null, Topics: topics), Ct);
        }

        var mixed = Linked(new CollectionLink(db.PublicCollection, ["altan", "skrivare"]), CollectionLink.Whole(db.SectionCollection));
        var all = await Ask(mixed, null);
        all.SearchedTopics.ShouldBeNull();
        all.Results.ShouldContain(r => r.DocumentId == db.Altan.Id);
        all.Results.ShouldNotContain(r => r.DocumentId == db.Upload.Id); // nothing asked: the links as they are (the upload has no topic)

        var altan = await Ask(mixed, "altan");
        altan.Results.Select(r => r.DocumentId).Distinct().ShouldBe([db.Altan.Id]); // the whole link is searched for "altan" too
        altan.SearchedTopics.ShouldBe(["altan"]);

        var konton = await Ask(mixed, "konton", "skrivare");
        konton.Results.ShouldBeEmpty();
        konton.SearchedTopics.ShouldBe(["konton", "skrivare"]);

        // Never widens: the link's "skrivare" with "altan" asked leaves the public collection out, though its page carries "altan".
        var narrow = Linked(new CollectionLink(db.PublicCollection, ["skrivare"]), CollectionLink.Whole(db.SectionCollection));
        var outside = await Ask(narrow, "altan");
        outside.Results.ShouldNotContain(r => r.CollectionId == db.PublicCollection);
        outside.SearchedTopics.ShouldBe(["altan"]);

        // No link left: nothing is searched and nothing is listed.
        var none = await Ask(Linked(new CollectionLink(db.PublicCollection, ["altan"])), "okänt");
        (none.Results.Count, none.Coverage.Status).ShouldBe((0, CoverageStatus.Low));
        none.SearchedTopics.ShouldBeEmpty();
        none.Timings.KeywordMs.ShouldBe(0);

        // A direct caller (no assistant) is not narrowed.
        var (service, _, host) = Build(mixed);
        using var __ = host;
        var direct = await service.RetrieveAsync(db.Who(db.InternalClient, mixed), Search(db.PublicCollection) with { Topics = ["skrivare"] }, Ct);
        direct.Results.Select(r => r.DocumentId).ShouldBe([db.Altan.Id, db.Upload.Id], ignoreOrder: true);
        direct.SearchedTopics.ShouldBeNull();
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

        (await service.ListCollectionsAsync(who, Ct)).Select(c => c.Id).ShouldBe([db.PublicCollection, db.SensitiveCollection, db.SectionCollection,
            db.DeptACollection, db.DeptBCollection], ignoreOrder: true);
    }

    private static readonly DownloadGrants Grants = new(new DownloadGrantOptions
    {
        Keys = new Dictionary<string, byte[]> { ["k1"] = System.Text.Encoding.UTF8.GetBytes(new string('k', 32)) },
    }, TimeProvider.System);

    private static DownloadGrant GrantOf(string link)
    {
        Grants.Read(link[(link.IndexOf("grant=", StringComparison.Ordinal) + 6)..], out var grant).ShouldBe(GrantStatus.Valid);
        return grant!;
    }

    [Fact]
    public async Task An_uploads_citation_carries_a_grant_that_opens_its_content()
    {
        var snapshot = db.Snapshot();
        var (service, _, host) = Build(snapshot, links: new GrantDownloadLinks(Grants));
        using var _ = host;
        var who = db.Who(db.InternalClient, snapshot);

        var result = await service.RetrieveAsync(who, Search(db.PublicCollection), Ct);

        result.Results.Single(r => r.DocumentId == db.Altan.Id).Citation.DownloadUrl.ShouldBeNull();
        var link = result.Results.Single(r => r.DocumentId == db.Upload.Id).Citation.DownloadUrl.ShouldNotBeNull();
        link.ShouldStartWith($"/v1/documents/{db.Upload.Id}/content?grant=g1.k1.");
        var grant = GrantOf(link);
        (grant.DocumentId, grant.CollectionId, grant.ClientId, grant.Level).ShouldBe((db.Upload.Id, db.PublicCollection, db.InternalClient, Classification.Public));

        var content = await service.FindContentAsync(grant, db.Upload.Id, Ct);
        content.ShouldBe(new DocumentContent(db.Upload.Id, new($"uploads/{db.PublicSource:D}/{db.UploadSlot:D}/ab{new string('0', 62)}"), Classification.Public,
            "application/pdf", "Bygglov/Taxor/taxa-bygglov-2026.pdf", KnowledgeFixture.UploadHash), new ContentComparer());
        (await service.GetDocumentAsync(grant, db.Upload.Id, Ct)).Citation.Title.ShouldBe("taxa-bygglov-2026.pdf");
        (await service.FindContentAsync(who, db.Upload.Id, Ct)).Key.ShouldBe(content.Key);
    }

    [Fact]
    public async Task A_grant_is_rechecked_at_the_current_version_and_every_failure_is_not_found()
    {
        var snapshot = db.Snapshot();
        var grant = new DownloadGrant(db.Upload.Id, db.PublicCollection, null, db.InternalClient, null, Classification.Public, DateTimeOffset.UtcNow.AddDays(1));

        async Task<string?> Refusal(PolicySnapshot at, DownloadGrant g, Guid? document = null)
        {
            var (service, _, host) = Build(at);
            using var _ = host;
            var denied = await Should.ThrowAsync<RetrievalDeniedException>(() => service.FindContentAsync(g, document ?? g.DocumentId, Ct));
            (denied.Status, denied.Code).ShouldBe((404, KnowledgeCodes.NotFound));
            return denied.Reason;
        }

        (await Refusal(snapshot, grant, db.Altan.Id)).ShouldBe("grant_scope");
        (await Refusal(snapshot, grant with { DocumentId = db.Hidden.Id })).ShouldBe(DenyReasons.NotFound); // not in the grant's collection
        (await Refusal(snapshot with { Clients = snapshot.Clients.ToDictionary(e => e.Key, e => e.Value with { IsEnabled = false }) }, grant))
            .ShouldBe(DenyReasons.ClientDisabled);
        (await Refusal(snapshot with { Collections = snapshot.Collections.ToDictionary(e => e.Key, e => e.Value with { PublishedAt = null }) }, grant))
            .ShouldBe(DenyReasons.NotFound);
        (await Refusal(snapshot, grant with { ClientId = db.OwuiClient, CollectionId = db.SensitiveCollection })).ShouldBe(DenyReasons.CeilingExceeded);
        (await Refusal(snapshot, grant with { AssistantId = db.AssistantId, CollectionId = db.SensitiveCollection, ClientId = db.InternalClient }))
            .ShouldBe(DenyReasons.NotFound); // the assistant is not linked to it
        (await Refusal(snapshot, grant with { KeyId = Guid.NewGuid() })).ShouldBe("key_unusable");

        var (unconfirmed, _, host) = Build(snapshot, confirmed: false);
        using (host)
        {
            (await Should.ThrowAsync<RetrievalDeniedException>(() => unconfirmed.FindContentAsync(grant, db.Upload.Id, Ct))).Status.ShouldBe(503);
        }
    }

    [Fact]
    public async Task A_web_page_is_not_served_as_content_after_the_access_check()
    {
        var snapshot = db.Snapshot();
        var (service, _, host) = Build(snapshot);
        using var _ = host;
        var who = db.Who(db.InternalClient, snapshot);

        (await Should.ThrowAsync<RetrievalDeniedException>(() => service.FindContentAsync(who, db.Altan.Id, Ct))).Code.ShouldBe(KnowledgeCodes.ContentNotServed);
        (await Should.ThrowAsync<RetrievalDeniedException>(() => service.FindContentAsync(who, db.Hidden.Id, Ct))).Code.ShouldBe(KnowledgeCodes.NotFound);
    }

    private sealed class ContentComparer : IEqualityComparer<DocumentContent>
    {
        public bool Equals(DocumentContent? x, DocumentContent? y) =>
            x is not null && y is not null && x with { ContentHash = [] } == y with { ContentHash = [] } && x.ContentHash.SequenceEqual(y.ContentHash);

        public int GetHashCode(DocumentContent obj) => obj.DocumentId.GetHashCode();
    }

    [Fact]
    public async Task Section_expansion_rebuilds_the_hits_section_from_its_neighbours()
    {
        var snapshot = db.Snapshot();
        var (service, _, host) = Build(snapshot);
        using var _ = host;

        var result = await service.RetrieveAsync(db.Who(db.InternalClient, snapshot),
            new RetrievalRequest("plank bygglov 110 centimeter", null, [db.SectionCollection], TopK: 1, Mode: RetrievalMode.Keyword,
                Expand: ExpandMode.Section, Purpose: RetrievalPurpose.Http), Ct);

        var hit = result.Results.ShouldHaveSingleItem();
        hit.Text.ShouldBe("Plank högre än 110 centimeter kräver bygglov.");
        hit.SectionRef.ShouldBe(0);
        var section = result.Sections.ShouldHaveSingleItem();
        (section.Id, section.DocumentId, section.HeadingPath, section.TokenEstimate, section.Truncated).ShouldBe((0, db.Plank.Id, "Plank och mur", 400, false));
        section.Text.ShouldBe("""
            Plank och mur
            Ett plank är en tät skärm.
            Plank högre än 110 centimeter kräver bygglov.
            ### Höjd
            Mät från marken. Höjden räknas från den lägsta sidan.
            Fråga oss vid tvekan.
            """.ReplaceLineEndings("\n"));

        var flat = await service.RetrieveAsync(db.Who(db.InternalClient, snapshot),
            new RetrievalRequest("plank bygglov 110 centimeter", null, [db.SectionCollection], TopK: 1, Mode: RetrievalMode.Keyword, Purpose: RetrievalPurpose.Http), Ct);
        flat.Sections.ShouldBeEmpty();
        flat.Results.ShouldHaveSingleItem().SectionRef.ShouldBeNull();
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

    /// <summary>
    /// The production limits (1.5 s an attempt, 2 s in all) are too tight for a busy CI runner: a first call through the
    /// FakeGateway's in-process server timed out and failed these tests (#826, #837, #838 on 2026-11-17), as the service
    /// hosts' did before #584 and #781. No test here measures the limits: the time-out test throws at once.
    /// </summary>
    private static QueryEmbeddingOptions Generous(int? cacheEntries = null)
    {
        var options = new QueryEmbeddingOptions { AttemptTimeout = TimeSpan.FromSeconds(30), TotalTimeout = TimeSpan.FromSeconds(30) };
        if (cacheEntries is { } entries)
        {
            options.CacheEntries = entries;
        }

        return options;
    }

    private static (GatewayQueryEmbedder, IHost) Build(HttpMessageHandler handler, QueryEmbeddingOptions? options = null, ILogger<GatewayQueryEmbedder>? logger = null)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Configuration.AddInMemoryCollection([new("Gateway:BaseUrl", "http://fake-gateway"), new("Gateway:Key", FakeGatewayFactory.ApiCredentialKey)]);
        builder.AddGatewayClient(GatewayQueryEmbedder.HttpClientName, retryFailed: false).ConfigurePrimaryHttpMessageHandler(() => handler);
        builder.Services.UseGatewayCredentialCeiling();
        var host = builder.Build();
        return (new GatewayQueryEmbedder(host.Services.GetRequiredService<IHttpClientFactory>(), options ?? Generous(), new byte[32], TimeProvider.System,
            logger ?? NullLogger<GatewayQueryEmbedder>.Instance), host);
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
        vectors.ShouldAllBe(v => v.Vector.Length == 1024);
        again.Vector.ToArray().ShouldBe(vectors[0].Vector.ToArray());
        embedder.QueryMac("bygglov altan").ShouldNotContain("bygglov");
        // Chat design §8.3: the one call's usage goes to one request only; the joined and cached ones record nothing.
        vectors.Count(v => v.Usage is not null).ShouldBe(1);
        again.Usage.ShouldBeNull();
    }

    [Fact]
    public async Task The_call_reports_the_fake_gateways_tokens_and_cost()
    {
        // The FakeGateway bills an embedding's input at its catalogue price, 0.02 USD per million tokens (x-fors-cost-usd).
        var costs = new List<string>();
        var (embedder, host) = Build(new CostRecording(fake.Server.CreateHandler(), costs));
        using var _ = host;
        using var __ = embedder;

        var embedding = await embedder.EmbedAsync(Profile, "hur ansöker jag om bygglov för en altan", null, Ct);

        var tokens = FakeContent.Tokens("hur ansöker jag om bygglov för en altan");
        embedding.Usage.ShouldBe(new GatewayCallUsage(GatewayQueryEmbedder.Step, "bge-m3", tokens, 0, 0, Math.Round(tokens * 0.02m / 1_000_000m, 8)));
        costs.ShouldHaveSingleItem().ShouldBe(embedding.Usage!.CostUsd!.Value.ToString("0.00000000", System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Keeps the <c>x-fors-cost-usd</c> of every answer, as the FakeGateway sent it.</summary>
    private sealed class CostRecording(HttpMessageHandler inner, List<string> costs) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            costs.AddRange(response.Headers.TryGetValues("x-fors-cost-usd", out var values) ? values : []);
            return response;
        }
    }

    [Fact]
    public async Task The_in_process_cache_stays_within_its_cap_and_a_restart_starts_empty()
    {
        // To-verify #162 (test part): the one-slot pilot keeps the cache in process; a restart empties it without errors.
        var counting = new Counting(fake.Server.CreateHandler());
        var (embedder, host) = Build(counting, Generous(cacheEntries: 3));
        using (host)
        using (embedder)
        {
            foreach (var query in new[] { "bygglov altan", "bygglov plank", "förskoleplats", "simhall", "parkering" })
            {
                await embedder.EmbedAsync(Profile, query, null, Ct);
            }

            embedder.CachedCount.ShouldBeLessThanOrEqualTo(3);
        }

        var callsBefore = counting.Calls;
        var (restarted, host2) = Build(counting, Generous(cacheEntries: 3));
        using var _ = host2;
        using var __ = restarted;
        restarted.CachedCount.ShouldBe(0);
        (await restarted.EmbedAsync(Profile, "bygglov altan", null, Ct)).Vector.Length.ShouldBe(1024);
        counting.Calls.ShouldBe(callsBefore + 1); // a gateway call after the restart, then cached
        await restarted.EmbedAsync(Profile, "bygglov altan", null, Ct);
        counting.Calls.ShouldBe(callsBefore + 1);
    }

    [Fact]
    public async Task A_profile_that_requests_dimensions_sends_them_and_others_do_not()
    {
        // #142: text-embedding-3-large at 1024 needs "dimensions"; BGE-M3 must never get it. The FakeGateway answers
        // with the parameter's length, else 1024, and the embedder refuses a vector of another length than the profile's.
        var (embedder, host) = Build(fake.Server.CreateHandler());
        using var _ = host;
        using var __ = embedder;
        var requesting = Profile with { Id = Guid.NewGuid(), Dimensions = 256, RequestDimensions = true };

        (await embedder.EmbedAsync(requesting, "bygglov altan", null, Ct)).Vector.Length.ShouldBe(256);
        await Should.ThrowAsync<QueryEmbeddingUnavailableException>(() =>
            embedder.EmbedAsync(requesting with { Id = Guid.NewGuid(), RequestDimensions = false }, "bygglov plank", null, Ct));
        (await embedder.EmbedAsync(Profile, "bygglov plank", null, Ct)).Vector.Length.ShouldBe(1024);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task A_502_or_504_is_retried_once_with_the_profiles_ceiling(HttpStatusCode status)
    {
        // #390: all_providers_failed is retried with the same ceiling, never a wider one; the client's own retry handler
        // is left out (production registers gateway-query with retryFailed: false), so exactly two calls.
        var ceilings = new List<string>();
        var failing = new Counting(fake.Server.CreateHandler(), status);
        var (embedder, host) = Build(new Recording(failing, ceilings));
        using var _ = host;
        using var __ = embedder;

        await Should.ThrowAsync<QueryEmbeddingUnavailableException>(() => embedder.EmbedAsync(Profile, "bygglov " + status, null, Ct));

        failing.Calls.ShouldBe(2);
        ceilings.ShouldBe(["Eu", "Eu"]);
    }

    [Theory]
    [InlineData(false, 2)] // 503 without Retry-After: one retry (knowledge §5.3, the query embedding only)
    [InlineData(true, 1)] // 503 with Retry-After: degrade at once
    public async Task A_503_is_retried_once_only_without_retry_after(bool retryAfter, int calls)
    {
        var scripted = new Scripted(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            if (retryAfter)
            {
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(5));
            }

            return response;
        });
        var (embedder, host) = Build(scripted);
        using var _ = host;
        using var __ = embedder;

        await Should.ThrowAsync<QueryEmbeddingUnavailableException>(() => embedder.EmbedAsync(Profile, "bygglov 503 " + retryAfter, null, Ct));

        scripted.Calls.ShouldBe(calls);
    }

    [Fact]
    public async Task A_connection_failure_then_a_502_is_one_retry_in_all()
    {
        // #410 item 4: the embedder's one retry covers every reason together, and gateway-query has no GatewayRetryHandler
        // of its own, so no 502 is sent a third time.
        var scripted = new Scripted(call => call == 1 ? throw new HttpRequestException("refused") : new HttpResponseMessage(HttpStatusCode.BadGateway));
        var (embedder, host) = Build(scripted);
        using var _ = host;
        using var __ = embedder;

        await Should.ThrowAsync<QueryEmbeddingUnavailableException>(() => embedder.EmbedAsync(Profile, "bygglov anslutning", null, Ct));

        scripted.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task A_failed_call_says_why_in_the_log_without_the_query()
    {
        // #781: a time-out, like every other failure of the call, used to leave nothing in the log.
        using var logs = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(l => l.AddProvider(logs));
        var hanging = new Scripted(_ => throw new TaskCanceledException("the attempt's limit"));
        var (embedder, host) = Build(hanging, logger: factory.CreateLogger<GatewayQueryEmbedder>());
        using var _ = host;
        using var __ = embedder;

        var failure = await Should.ThrowAsync<QueryEmbeddingUnavailableException>(() => embedder.EmbedAsync(Profile, "bygglov anslutning", null, Ct));

        failure.Reason.ShouldBe(RetrievalDegradation.EmbeddingUnavailable);
        var entry = logs.Entries.Single(e => e.Level == LogLevel.Warning);
        entry.Message.ShouldContain("embedding_unavailable");
        entry.Message.ShouldContain("timed out");
        entry.Message.ShouldContain(nameof(TaskCanceledException));
        logs.Text.ShouldNotContain("bygglov");
    }

    private sealed class Scripted(Func<int, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(answer(Interlocked.Increment(ref Calls)));
    }

    private sealed class Recording(HttpMessageHandler inner, List<string> ceilings) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ceilings.Add(string.Join(",", request.Headers.GetValues("x-fors-max-residency")));
            return base.SendAsync(request, cancellationToken);
        }
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

    /// <summary>
    /// The gateway as seen by a call made as a key (knowledge-api §5.3, §5.4; #580 review): <paramref name="answer"/> per
    /// call and acted key, a usable answer a 1024-dimension embedding with the key echoed unless <paramref name="echo"/> is false.
    /// </summary>
    private sealed class Acting(Func<int, Guid?, HttpResponseMessage?> answer, bool echo = true) : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref Calls);
            var key = request.Headers.TryGetValues(GatewayHeaders.OnBehalfOf, out var values) ? Guid.Parse(values.Single()) : (Guid?)null;
            var response = answer(call, key) ?? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$$"""{"data":[{"embedding":[{{{string.Join(",", Enumerable.Repeat("0.1", 1024))}}}]}],"usage":{"prompt_tokens":3,"total_tokens":3}}""",
                    System.Text.Encoding.UTF8, "application/json"),
            };
            if (response.IsSuccessStatusCode)
            {
                response.Headers.Add(GatewayHeaders.Residency, "Eu");
                if (echo && key is { } id)
                {
                    response.Headers.Add(GatewayHeaders.OnBehalfOf, id.ToString("D"));
                }
            }

            return Task.FromResult(response);
        }
    }

    private static HttpResponseMessage Refusal(int status, string code) => new((HttpStatusCode)status)
    {
        Content = new StringContent($$$"""{"error":{"code":"{{{code}}}","message":"Refused."}}""", System.Text.Encoding.UTF8, "application/json"),
    };

    private static (GatewayQueryEmbedder, IHost) BuildActing(HttpMessageHandler handler)
    {
        var (embedder, host) = Build(handler);
        host.Services.GetRequiredService<GatewayFeatures>().Update([GatewayFeatures.ActAsKeyFeature]);
        return (embedder, host);
    }

    [Theory]
    [InlineData(403, "key_revoked")]
    [InlineData(403, "key_disabled")]
    [InlineData(402, "budget_exceeded")]
    public async Task One_keys_refusals_degrade_its_requests_only_and_never_open_the_circuit(int status, string code)
    {
        // #580 review, point 1: five questions to an assistant whose key is refused must not switch the embedding off for others.
        var refused = Guid.NewGuid();
        var acting = new Acting((_, key) => key == refused ? Refusal(status, code) : null);
        var (embedder, host) = BuildActing(acting);
        using var _ = host;
        using var __ = embedder;

        using (GatewayCallScope.Begin(new GatewayCall(refused)))
        {
            for (var i = 0; i < 6; i++)
            {
                var failed = await Should.ThrowAsync<QueryEmbeddingUnavailableException>(() => embedder.EmbedAsync(Profile, "spärrad " + i, null, Ct));
                failed.InnerException.ShouldBeOfType<GatewayKeyRefusedException>();
            }
        }

        embedder.CircuitOpen.ShouldBeFalse();
        acting.Calls.ShouldBe(6); // each refused once, never retried
        using (GatewayCallScope.Begin(new GatewayCall(Guid.NewGuid())))
        {
            (await embedder.EmbedAsync(Profile, "en annan assistent", null, Ct)).Vector.Length.ShouldBe(1024);
        }
    }

    [Fact]
    public async Task A_missing_echo_is_not_retried_and_does_not_count_toward_the_circuit()
    {
        // #580 review, point 2: the echo failure was retried once (a second 5011) and counted toward the circuit.
        var acting = new Acting((_, _) => null, echo: false);
        var (embedder, host) = BuildActing(acting);
        using var _ = host;
        using var __ = embedder;

        using (GatewayCallScope.Begin(new GatewayCall(Guid.NewGuid())))
        {
            for (var i = 0; i < 5; i++)
            {
                var failed = await Should.ThrowAsync<QueryEmbeddingUnavailableException>(() => embedder.EmbedAsync(Profile, "eko " + i, null, Ct));
                failed.InnerException.ShouldBeOfType<GatewayDelegationEchoException>();
            }
        }

        acting.Calls.ShouldBe(5);
        embedder.CircuitOpen.ShouldBeFalse();
    }

    [Fact]
    public async Task The_cache_and_the_single_flight_are_per_acting_key()
    {
        // #580 review, point 3 (knowledge-api §5.4): (profileId, gatewayKeyId, queryMac), so each assistant's key pays for its own.
        var acting = new Acting((_, _) => null);
        var (embedder, host) = BuildActing(acting);
        using var _ = host;
        using var __ = embedder;
        Guid x = Guid.NewGuid(), y = Guid.NewGuid();

        async Task<QueryEmbedding> As(Guid key)
        {
            using var scope = GatewayCallScope.Begin(new GatewayCall(key));
            return await embedder.EmbedAsync(Profile, "bygglov altan", null, Ct);
        }

        var first = await As(x);
        var cached = await As(x);
        var other = await As(y);

        acting.Calls.ShouldBe(2); // x once (then cached), y its own call
        (first.Usage is not null, cached.Usage is null, other.Usage is not null).ShouldBe((true, true, true));
        GatewayQueryEmbedder.CacheKey(Profile.Id, x, "m").ShouldNotBe(GatewayQueryEmbedder.CacheKey(Profile.Id, y, "m"));
        GatewayQueryEmbedder.CacheKey(Profile.Id, null, "m").ShouldBe($"{Profile.Id:N}:-:m");
    }
}
