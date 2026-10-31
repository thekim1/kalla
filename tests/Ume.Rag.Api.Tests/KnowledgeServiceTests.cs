extern alias fakegateway;

using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Options;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Infrastructure.Blobs;
using Ume.Rag.Infrastructure.Config;
using Ume.Rag.Domain.Services;
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
    public Guid SectionCollection { get; } = Guid.CreateVersion7();
    public Guid SectionSource { get; } = Guid.CreateVersion7();
    public FixtureDocument Plank { get; private set; } = null!;
    public FixtureDocument Sensitive { get; private set; } = null!;
    public Guid UploadSlot { get; } = Guid.CreateVersion7();
    public static readonly byte[] UploadHash = Convert.FromHexString("ab" + new string('0', 62));
    public static readonly byte[] UploadBytes = "%PDF-1.7 taxa"u8.ToArray();
    public const string KeyPepper = "knowledge-api-test-pepper-0123456789abcdef";
    public const string GrantKey = "knowledge-api-test-grant-key-0123456789abc";
    public string BlobRoot { get; } = Directory.CreateTempSubdirectory("ume-rag-api-blobs-").FullName;

    /// <summary>The plain keys of <c>rag-internal</c> and Open WebUI, stored in <c>config."Keys"</c> (the clients have the snapshot's ids).</summary>
    public string InternalKey { get; private set; } = null!;
    public string OwuiKey { get; private set; } = null!;
    public string IntegrationKey { get; private set; } = null!;
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
        }, [new FixtureChunk("Du behöver bygglov för att bygga en altan som är högre än 1,2 meter.") { HeadingPath = "Bygglov › Altan" }]);
        Upload = await writer.AddDocumentAsync(new FixtureDocumentSpec
        {
            CollectionId = PublicCollection, EmbeddingProfileId = ProfileId, SourceId = PublicSource, IdentityKind = "upload",
            CanonicalIdentity = "upload:" + UploadSlot, Title = "Taxa 2026", DisplayName = "taxa-bygglov-2026.pdf", MediaType = "application/pdf", PageCount = 4,
        }, [new FixtureChunk("Avgiften för bygglov för en altan beräknas enligt taxan för 2026.")]);
        await writer.ExecuteAsync($"""UPDATE content."Documents" SET "ContentHash" = '\x{Convert.ToHexString(UploadHash)}' WHERE "Id" = '{Upload.Id}'""");
        var blobs = new FileSystemBlobStore(Options.Create(new BlobStoreOptions { Root = BlobRoot }), TimeProvider.System);
        await blobs.PutAsync(new BlobKey($"uploads/{PublicSource:D}/{UploadSlot:D}/{Convert.ToHexStringLower(UploadHash)}"), new MemoryStream(UploadBytes), null,
            TestContext.Current.CancellationToken);
        InternalKey = await AddKeyAsync(InternalClient, "rag-internal", ClientKinds.Internal, Classification.Sensitive);
        OwuiKey = await AddKeyAsync(OwuiClient, "Open WebUI", ClientKinds.OpenWebUi, Classification.Internal);
        IntegrationKey = await AddKeyAsync(IntegrationClient, "Integration", ClientKinds.Integration, Classification.Internal);

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

    /// <summary>
    /// rag-api on this database (api role) with the FakeGateway, the blob root, and <paramref name="snapshot"/> in place of
    /// the snapshot store (keys still come from <c>config."Keys"</c>).
    /// </summary>
    public WebApplicationFactory<Program> ApiHost(PolicySnapshot? snapshot = null, bool confirmed = true) => new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
    {
        web.UseEnvironment("Development");
        foreach (var (key, value) in new Dictionary<string, string?>
        {
            ["ConnectionStrings:ragdb"] = Connection(Api), ["Security:KeyPepper"] = KeyPepper, ["Security:CacheKeyPepper"] = new string('c', 32),
            ["Security:DownloadGrantKeys:k1"] = GrantKey, ["Gateway:BaseUrl"] = "http://fake-gateway", ["Gateway:Keys:Eu"] = FakeGatewayFactory.EuKey,
            ["BlobStore:Root"] = BlobRoot, ["Rag:Roles"] = "knowledge",
        })
        {
            web.UseSetting(key, value);
        }

        web.ConfigureTestServices(services =>
        {
            services.AddSingleton<IPolicySnapshotSource>(new FixedSnapshot(snapshot ?? Snapshot(), confirmed));
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
            new ClientEntry(OwuiClient, "Open WebUI", ClientKinds.OpenWebUi, Classification.Internal, true, true),
            new ClientEntry(IntegrationClient, "Integration", ClientKinds.Integration, Classification.Internal, false, true),
        }.ToDictionary(c => c.Id);
        var collections = new[]
        {
            Collection(PublicCollection, Classification.Public, true), Collection(UnpublishedCollection, Classification.Public, false),
            Collection(SensitiveCollection, Classification.Sensitive, true), Collection(SectionCollection, Classification.Public, true),
            Collection(DeptACollection, Classification.Public, true), Collection(DeptBCollection, Classification.Public, true),
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
        IDownloadLinks? links = null)
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
        var service = new KnowledgeService(new KnowledgeFixture.FixedSnapshot(snapshot, confirmed), new AccessPolicy(AccessPolicy.DefaultKnowledgeClientKinds),
            new RetrievalEngine(index, new ScopeSizeCache(index, TimeProvider.System)), new PostgresHydrator(dataSource), embedder, counter, dataSource,
            links ?? NoDownloadLinks.Instance, NullLogger<KnowledgeService>.Instance);
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
            "application/pdf", "taxa-bygglov-2026.pdf", KnowledgeFixture.UploadHash), new ContentComparer());
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
