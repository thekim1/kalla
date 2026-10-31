using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Pipelines.Json;
using Ume.Rag.TestKit;
using Ume.Rag.Worker.Connectors.Egress;
using Ume.Rag.Worker.Connectors.Hosts;
using Ume.Rag.Worker.Processing.Embedding;
using Ume.Rag.Worker.Tests.Connectors;
using Ume.Rag.Worker.Tests.Processing;

namespace Ume.Rag.Worker.Tests.Engine;

/// <summary>A seeded source and the config rows around it.</summary>
internal sealed record SourceSeed(Guid SourceId, Guid CollectionId, Guid DepartmentId, Guid PipelineId, Guid VersionId, string Classification);

/// <summary><c>site.test</c> as a municipal host with a generous budget, so the tests do not wait for politeness.</summary>
internal sealed class FastCrawlPolicy : ICrawlPolicySource
{
    public CrawlPolicy Current { get; } = new(["site.test"],
        new Dictionary<string, HostOverride>(StringComparer.OrdinalIgnoreCase));
}

/// <summary>Blocks embedding requests while armed; counts the requests that reached it.</summary>
internal sealed class EmbeddingGate : DelegatingHandler
{
    private volatile TaskCompletionSource _open = Opened();

    public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Arm() => _open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Open() => _open.TrySetResult();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var gate = _open;
        if (!gate.Task.IsCompleted)
        {
            Blocked.TrySetResult();
        }

        await gate.Task.WaitAsync(cancellationToken);
        return await base.SendAsync(request, cancellationToken);
    }

    private static TaskCompletionSource Opened()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }
}

/// <summary>
/// The engine end to end on the test database: config rows for a URL-list source, the in-process Kestrel test site
/// (connectors design §18), the FakeGateway in process for embeddings, and a worker with the engine (Program.cs's wiring)
/// whose egress, DNS and host budget point at the site.
/// </summary>
internal static class EngineKit
{
    public static readonly Guid ProfileId = Guid.Parse("01929b6e-0000-7000-8000-0000000e1e01");
    public const int Dimensions = 1024;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A web page with enough text to chunk; <paramref name="extra"/> is added to the body.</summary>
    public static string Page(string title, string extra = "") => $$"""
        <!doctype html><html lang="sv"><head><title>{{title}}</title></head><body><main>
        <h1>{{title}}</h1>
        <p>Umeå kommun ansvarar för {{title}}. Här finns information om hur du ansöker, vad det kostar och vem du kontaktar.</p>
        <h2>Så ansöker du</h2>
        <p>Du ansöker via e-tjänsten. Handläggningstiden är normalt fyra veckor. {{extra}}</p>
        </main></body></html>
        """;

    public static async Task<TestSite> SiteAsync(Func<string, HttpContext, Task<IResult>> page) =>
        await TestSite.StartAsync(app => app.MapGet("/p/{name}", (string name, HttpContext context) => page(name, context)));

    public static IResult Html(string body) => Results.Content(body, "text/html; charset=utf-8");

    public static async Task<SourceSeed> SeedUrlListAsync(RagDatabaseFixture database, IEnumerable<Uri> urls, string collectionClassification = Classification.Internal)
    {
        var suffix = Guid.CreateVersion7().ToString("N")[..12];
        await using var db = TestPostgres.Context(database.Connection(null));
        if (!db.EmbeddingProfiles.Any(p => p.Id == ProfileId))
        {
            db.EmbeddingProfiles.Add(new EmbeddingProfile
            {
                Id = ProfileId,
                Name = "engine-test-embedding",
                GatewayModel = "bge-m3",
                Dimensions = Dimensions,
                MaxInputTokens = 8192,
                MaxResidency = Residency.Eu,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        var department = new Department { Id = Guid.CreateVersion7(), Name = "Engine " + suffix, CostCenterCode = suffix, SyncedAt = DateTimeOffset.UtcNow };
        var collection = new Collection
        {
            Slug = "engine-" + suffix,
            Name = "Engine " + suffix,
            DepartmentId = department.Id,
            Classification = collectionClassification,
            EmbeddingProfileId = ProfileId,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var pipeline = new Pipeline { DepartmentId = department.Id, Name = "url-list " + suffix, TemplateId = "url-list", UpdatedAt = DateTimeOffset.UtcNow };
        db.Add(department);
        db.Add(collection);
        db.Add(pipeline);
        await db.SaveChangesAsync(Ct);
        var version = new PipelineVersion
        {
            PipelineId = pipeline.Id,
            Number = 1,
            State = PipelineVersionStates.Published,
            Definition = """{"schema":"pipeline/v2","template":"url-list","templateVersion":1,"params":{}}""",
            PublishedAt = DateTimeOffset.UtcNow,
        };
        db.Add(version);
        await db.SaveChangesAsync(Ct);
        pipeline.PublishedVersionId = version.Id;
        var source = new Source
        {
            CollectionId = collection.Id,
            Type = SourceTypes.UrlList,
            Name = "Lista " + suffix,
            Classification = collectionClassification,
            Settings = JsonSerializer.Serialize(new { urls = urls.Select(u => u.AbsoluteUri).ToArray() }),
            PipelineId = pipeline.Id,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Add(source);
        await db.SaveChangesAsync(Ct);
        return new SourceSeed(source.Id, collection.Id, department.Id, pipeline.Id, version.Id, collectionClassification);
    }

    /// <summary>Replaces the source's URL list (an admin edit).</summary>
    public static Task SetUrlsAsync(RagDatabaseFixture database, SourceSeed seed, IEnumerable<Uri> urls) =>
        Sql.ExecuteAsync(database.Connection(null), """UPDATE config."Sources" SET "Settings" = $2::jsonb WHERE "Id" = $1""", seed.SourceId,
            JsonSerializer.Serialize(new { urls = urls.Select(u => u.AbsoluteUri).ToArray() }));

    /// <summary>Pauses the source, so no later test's scheduler or finalizer picks it up.</summary>
    public static Task PauseAsync(RagDatabaseFixture database, SourceSeed seed) =>
        Sql.ExecuteAsync(database.Connection(null), """UPDATE config."Sources" SET "State" = 'paused' WHERE "Id" = $1""", seed.SourceId);

    /// <summary>
    /// A worker with the engine, wired to the site, the FakeGateway and a blob root of its own; <paramref name="settings"/> and
    /// <paramref name="services"/> add to (and override) the defaults.
    /// </summary>
    public static Task<WorkerHost> StartWorkerAsync(RagDatabaseFixture database, TestSite site, FakeGatewayFactory gateway, DirectoryInfo blobs,
        EmbeddingGate? gate = null, CapturingLoggerProvider? logs = null, IReadOnlyDictionary<string, string?>? settings = null,
        Action<IServiceCollection>? services = null) =>
        StartCoreAsync(database, site, gateway, blobs, gate, logs ?? Logs, settings, services);

    public static CapturingLoggerProvider Logs { get; } = new();

    private static Task<WorkerHost> StartCoreAsync(RagDatabaseFixture database, TestSite site, FakeGatewayFactory gateway, DirectoryInfo blobs,
        EmbeddingGate? gate, CapturingLoggerProvider? logs, IReadOnlyDictionary<string, string?>? settings, Action<IServiceCollection>? extra) =>
        WorkerHost.StartAsync(database, services =>
        {
            services.AddSingleton<IDnsResolver>(site.Resolver());
            services.AddSingleton<IEgressPolicySource>(new FixedEgressPolicy(site.Port, site.SecurePort));
            services.AddSingleton<ICrawlPolicySource>(new FastCrawlPolicy());
            var client = services.AddHttpClient(EmbeddingClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => gateway.Server.CreateHandler());
            if (gate is not null)
            {
                client.AddHttpMessageHandler(() => gate);
            }

            extra?.Invoke(services);
        }, Merge(new Dictionary<string, string?>
        {
            ["Gateway:BaseUrl"] = "http://fake-gateway",
            ["Gateway:Keys:OnPrem"] = FakeGatewayFactory.OnPremKey,
            ["Gateway:Keys:Eu"] = FakeGatewayFactory.EuKey,
            ["BlobStore:Root"] = blobs.FullName,
            ["Crawl:AllowedPrivateRanges"] = "127.0.0.1/32",
            ["Worker:FinalizerSeconds"] = "1",
        }, settings), logs, engine: true);

    private static Dictionary<string, string?> Merge(Dictionary<string, string?> defaults, IReadOnlyDictionary<string, string?>? settings)
    {
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            defaults[key] = value;
        }

        return defaults;
    }

    /// <summary>Pauses every source but <paramref name="seeds"/>, so a sweep over all documents sees only this test's.</summary>
    public static Task PauseOtherSourcesAsync(RagDatabaseFixture database, params SourceSeed[] seeds) =>
        Sql.ExecuteAsync(database.Connection(null), """UPDATE config."Sources" SET "State" = 'paused' WHERE NOT ("Id" = ANY($1))""",
            (object)seeds.Select(s => s.SourceId).ToArray());

    /// <summary>A "sync now" as the admin enqueues it (dedup <c>sync:{sourceId}</c>); the run's id is the job's.</summary>
    public static async Task<Guid> SyncNowAsync(RagDatabaseFixture database, SourceSeed seed)
    {
        var payload = new SyncPayload { SourceId = seed.SourceId, Kind = SyncKind.Sync, Trigger = RunTrigger.Manual };
        var job = new Domain.Ports.JobRequest(JobClasses.Sync, seed.DepartmentId, seed.Classification, ContractJson.Serialize(payload))
        {
            DedupKey = JobDedupKeys.Sync(seed.SourceId),
            Priority = 10,
        };
        (await database.Queue(RagDatabase.Admin).EnqueueAsync([job], null, Ct)).ShouldContain(job.Id);
        return job.Id;
    }

    public static async Task<RunCounters> WaitForRunAsync(RagDatabaseFixture database, Guid runId, TimeSpan? timeout = null)
    {
        var waited = Stopwatch.StartNew();
        while (true)
        {
            if (await RunAsync(database, runId) is { State: not RunStates.Running } run)
            {
                return run;
            }

            if (waited.Elapsed > (timeout ?? TimeSpan.FromSeconds(90)))
            {
                throw new TimeoutException($"Run {runId} did not finish; jobs: {await JobsTextAsync(database, runId)}\n" +
                    string.Join("\n", Logs.Entries.Where(e => e.Level >= Microsoft.Extensions.Logging.LogLevel.Warning).Take(5)));
            }

            await Task.Delay(100, Ct);
        }
    }

    public static async Task<RunCounters?> RunAsync(RagDatabaseFixture database, Guid runId)
    {
        await using var connection = new NpgsqlConnection(database.Connection(null));
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            SELECT "State", "Added", "Updated", "Unchanged", "Deleted", "Failed", "ProposedDeletions" FROM content."Runs" WHERE "Id" = $1
            """, connection) { Parameters = { new NpgsqlParameter { Value = runId } } };
        await using var reader = await command.ExecuteReaderAsync(Ct);
        return await reader.ReadAsync(Ct)
            ? new RunCounters(reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6))
            : null;
    }

    /// <summary>The source's searchable chunks: not deleted, in the live set of a live document, with a vector and a keyword vector.</summary>
    public static Task<long> LiveChunksAsync(RagDatabaseFixture database, SourceSeed seed) =>
        Sql.ScalarAsync<long>(database.Connection(null), """
            SELECT count(*) FROM content."Chunks" c
            JOIN content."Documents" d ON d."LiveChunkSetId" = c."ChunkSetId" AND d."DeletedAt" IS NULL
            JOIN content."ChunkSets" s ON s."Id" = c."ChunkSetId" AND s."State" = 'live'
            WHERE d."SourceId" = $1 AND c."DeletedAt" IS NULL AND c."Embedding" IS NOT NULL AND c."SearchVector" IS NOT NULL
            """, seed.SourceId);

    /// <summary>Searchable chunks of the source that are not in a live set: must always be 0.</summary>
    public static Task<long> StrayChunksAsync(RagDatabaseFixture database, SourceSeed seed) =>
        Sql.ScalarAsync<long>(database.Connection(null), """
            SELECT count(*) FROM content."Chunks" c JOIN content."Documents" d ON d."Id" = c."DocumentId"
            WHERE d."SourceId" = $1 AND c."DeletedAt" IS NULL AND c."ChunkSetId" IS DISTINCT FROM d."LiveChunkSetId"
            """, seed.SourceId);

    /// <summary>Documents of the source with more than one live or held set (a duplicate).</summary>
    public static Task<long> DuplicateSetsAsync(RagDatabaseFixture database, SourceSeed seed) =>
        Sql.ScalarAsync<long>(database.Connection(null), """
            SELECT count(*) FROM (
                SELECT s."DocumentId" FROM content."ChunkSets" s JOIN content."Documents" d ON d."Id" = s."DocumentId"
                WHERE d."SourceId" = $1 AND s."State" IN ('live', 'held') GROUP BY s."DocumentId" HAVING count(*) > 1) x
            """, seed.SourceId);

    public static Task<long> DocumentsAsync(RagDatabaseFixture database, SourceSeed seed, bool live = true) =>
        Sql.ScalarAsync<long>(database.Connection(null),
            live ? """SELECT count(*) FROM content."Documents" WHERE "SourceId" = $1 AND "DeletedAt" IS NULL AND "LiveChunkSetId" IS NOT NULL"""
                 : """SELECT count(*) FROM content."Documents" WHERE "SourceId" = $1""", seed.SourceId);

    private static async Task<string> JobsTextAsync(RagDatabaseFixture database, Guid runId)
    {
        await using var connection = new NpgsqlConnection(database.Connection(null));
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            SELECT string_agg("Class" || ':' || "State" || ':' || coalesce("LastError", '-'), ', ') FROM content."Jobs" WHERE "RunId" = $1 OR "Id" = $1
            """, connection) { Parameters = { new NpgsqlParameter { Value = runId } } };
        return (await command.ExecuteScalarAsync(Ct))?.ToString() ?? "none";
    }
}

internal sealed record RunCounters(string State, int Added, int Updated, int Unchanged, int Deleted, int Failed, int ProposedDeletions);
