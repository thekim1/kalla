extern alias api;

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Ume.Rag.Answering;
using Ume.Rag.Answering.Chat;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Domain.Services;
using Ume.Rag.Infrastructure.Access;
using Ume.Rag.TestKit;
using Ume.Rag.Worker.Tests.Engine;
using Ume.Rag.Worker.Tests.Processing;

namespace Ume.Rag.Worker.Tests.Evaluation;

/// <summary>One <c>POST /v1/retrieve</c> result: the chunk and its document.</summary>
internal sealed record RetrievedChunk(Guid ChunkId, Guid DocumentId);

/// <summary>
/// The smoke set (<see cref="SmokeSet"/>, knowledge design §16.3–§16.4) against rag-api in process on the test database:
/// the collection published with <see cref="SmokeSet.RetrievalSettings"/>, clients and keys in <c>config</c>, and rag-api
/// reading its policy snapshot from the database as deployed (no fixed snapshot), with the FakeGateway for query
/// embeddings and chat.
/// </summary>
internal static class EvaluationSmokeKit
{
    private const string KeyPepper = "evaluation-smoke-test-pepper-0123456789abcdef";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Publishes the collection with the smoke set's FakeGateway coverage calibration, and gives every level a data
    /// protection policy (history <c>none</c>) when it has none: the migrations seed no rows (data-model §2.1), and access
    /// fails closed on a level without one.
    /// </summary>
    public static async Task PublishAsync(RagDatabaseFixture database, Guid collectionId)
    {
        await Sql.ExecuteAsync(database.Connection(null), """
            INSERT INTO config."DataProtectionPolicies" ("Classification", "AllowedHistoryModes", "DefaultHistoryMode", "RetentionDefaultDays",
                "RetentionMaxDays", "EncryptionRequired", "ReviewSecondApprover", "ClientsWithoutHistoryOnly", "UpdatedAt")
            SELECT l, ARRAY['none']::varchar(20)[], 'none', 30, 90, l <> 'public', false, l = 'sensitive', now()
            FROM unnest(ARRAY['public', 'internal', 'sensitive']) AS l
            ON CONFLICT DO NOTHING
            """);
        await Sql.ExecuteAsync(database.Connection(null), """
            UPDATE config."Collections" SET "RetrievalSettings" = $2::jsonb, "PublishedAt" = now() WHERE "Id" = $1
            """, collectionId, SmokeSet.RetrievalSettings);
    }

    /// <summary>A client of <paramref name="kind"/> with one key; returns its id and the plain key.</summary>
    public static async Task<(Guid ClientId, string Key)> AddClientAsync(RagDatabaseFixture database, string kind, string ceiling)
    {
        var generated = new KeyHasher(Encoding.UTF8.GetBytes(KeyPepper)).Generate();
        var client = new Client { Name = $"{kind} {Guid.NewGuid():N}", Kind = kind, ClassificationCeiling = ceiling, UpdatedAt = DateTimeOffset.UtcNow };
        await using var db = TestPostgres.Context(database.Connection(null));
        db.AddRange(client, new ApiKey
        {
            Client = client, Name = client.Name, Prefix = generated.Prefix, KeyHash = generated.Hash, CreatedAt = DateTimeOffset.UtcNow, CreatedBy = "test",
        });
        await db.SaveChangesAsync(Ct);
        return (client.Id, generated.PlainText);
    }

    /// <summary>An active <c>internal</c> assistant (history <c>none</c>) over the seed's collection, published, for <paramref name="clientId"/>; returns its slug.</summary>
    public static async Task<string> AddAssistantAsync(RagDatabaseFixture database, SourceSeed seed, Guid clientId)
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = TestPostgres.Context(database.Connection(null));
        var assistant = new Assistant
        {
            Slug = "smoke-" + Guid.NewGuid().ToString("N")[..12], Name = "Smoke", DepartmentId = seed.DepartmentId, Classification = Classification.Internal,
            UpdatedAt = now,
        };
        db.Add(assistant);
        await db.SaveChangesAsync(Ct);
        var version = new AssistantVersion
        {
            AssistantId = assistant.Id, Number = 1, GatewayModel = "ume/chat-eu", CollectionLinks = [CollectionLink.Whole(seed.CollectionId)], CreatedAt = now, UpdatedAt = now,
        };
        db.AssistantVersions.Add(version);
        db.AssistantAudiences.Add(new AssistantAudience { AssistantId = assistant.Id, Kind = AssistantAudienceKinds.Client, SubjectId = clientId });
        await db.SaveChangesAsync(Ct);
        version.State = AssistantVersionStates.Published;
        version.PublishedAt = now;
        version.PublishedBy = "test";
        assistant.PublishedVersionId = version.Id;
        await db.SaveChangesAsync(Ct);
        return assistant.Slug;
    }

    /// <summary>
    /// rag-api with <paramref name="roles"/> on the test database as <c>ume_rag_api</c>, with its real key store and policy
    /// snapshot, the FakeGateway for query embeddings and chat answers, and no chat rate limit (30 questions from one caller).
    /// </summary>
    public static WebApplicationFactory<api::Program> ApiHost(RagDatabaseFixture database, FakeGatewayFactory gateway, DirectoryInfo blobs,
        string roles = "knowledge") => new WebApplicationFactory<api::Program>().WithWebHostBuilder(web =>
    {
        web.UseEnvironment("Development");
        foreach (var (key, value) in new Dictionary<string, string?>
        {
            ["ConnectionStrings:ragdb"] = database.Connection(RagDatabase.Api), ["Security:KeyPepper"] = KeyPepper,
            ["Security:CacheKeyPepper"] = new string('c', 32), ["Security:DownloadGrantKeys:k1"] = new string('g', 32),
            ["Gateway:BaseUrl"] = "http://fake-gateway", ["Gateway:Key"] = FakeGatewayFactory.WorkerCredentialKey, ["BlobStore:Root"] = blobs.FullName,
            ["Rag:Roles"] = roles, ["Chat:MessagesPerMinutePerUser"] = "0",
        })
        {
            web.UseSetting(key, value);
        }

        web.ConfigureTestServices(services =>
        {
            services.AddHttpClient(GatewayQueryEmbedder.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => gateway.Server.CreateHandler());
            services.AddHttpClient(GatewayChatClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => gateway.Server.CreateHandler());
        });
    });

    /// <summary>A client of <paramref name="api"/> with <paramref name="key"/>, once the api has confirmed its first snapshot.</summary>
    public static async Task<HttpClient> ClientAsync(WebApplicationFactory<api::Program> api, string key)
    {
        var snapshots = api.Services.GetRequiredService<IPolicySnapshotSource>();
        var waited = Stopwatch.StartNew();
        while (!snapshots.IsConfirmed)
        {
            waited.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(15), "rag-api confirms its first snapshot");
            await Task.Delay(50, Ct);
        }

        var client = api.CreateHttpsClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }

    /// <summary>
    /// <c>POST /v1/retrieve</c> over the collection <paramref name="slug"/>: top 10, <c>expand: none</c>, hybrid; the results in
    /// rank order. The response must not be degraded (a keyword-only fallback is not the evaluator's search).
    /// </summary>
    public static async Task<IReadOnlyList<RetrievedChunk>> RetrieveAsync(HttpClient client, string slug, string query)
    {
        using var response = await client.PostAsJsonAsync("/v1/retrieve",
            new { query, collections = new[] { slug }, top_k = 10, mode = "hybrid", expand = "none" }, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("degraded").ValueKind.ShouldBe(JsonValueKind.Null, query);
        return [.. json.RootElement.GetProperty("results").EnumerateArray()
            .Select(r => new RetrievedChunk(r.GetProperty("chunk_id").GetGuid(), r.GetProperty("document").GetProperty("id").GetGuid()))];
    }
}
