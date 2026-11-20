using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Kalla.Domain;
using Kalla.Domain.Entities;
using Kalla.Domain.Ports;
using Kalla.Pipelines.Json;
using Kalla.Pipelines.Processing;
using Kalla.TestKit;
using Kalla.Worker.Commands;
using Kalla.Worker.Connectors.Malware;
using Kalla.Worker.Engine;
using Kalla.Worker.Jobs;
using Kalla.Worker.Processing;
using Kalla.Worker.Processing.Persistence;
using Kalla.Worker.Tests.Engine;

namespace Kalla.Worker.Tests.CommandJobs;

/// <summary>A leased command job and what running it returned.</summary>
internal sealed record CommandRun(LeasedJob Job, JobResult Result);

/// <summary>
/// Runs <c>command</c> jobs the way the dispatcher does, without its loop: the admin enqueues the job row, this instance
/// leases it as <c>ume_rag_worker</c> and <see cref="CommandJobHandler"/> runs it with the handlers <c>AddRagCommands</c>
/// registers. <see cref="StealLeaseAsync"/> makes a leased job stale (as a reclaim does), so the fence can be tested.
/// </summary>
internal sealed class CommandRunner(RagDatabaseFixture database, Action<IServiceCollection>? services = null) : IAsyncDisposable
{
    private readonly ServiceProvider _services = Build(database, services);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public WorkerIdentity Identity => _services.GetRequiredService<WorkerIdentity>();

    /// <summary>Enqueues the payload as the admin does and leases it.</summary>
    public async Task<LeasedJob> LeaseAsync<T>(T payload, Guid departmentId, string classification, string? dedupKey = null)
    {
        var request = new JobRequest(JobClasses.Command, departmentId, classification, ContractJson.Serialize(payload)) { DedupKey = dedupKey };
        (await database.Queue(RagDatabase.Admin).EnqueueAsync([request], null, Ct)).ShouldContain(request.Id);
        var leased = await database.Queue(RagDatabase.Worker).DequeueAsync(
            new DequeueRequest(JobClasses.Command, departmentId, Identity.InstanceId, TimeSpan.FromMinutes(5)), Ct);
        leased.ShouldNotBeNull();
        leased.Id.ShouldBe(request.Id);
        return leased;
    }

    public async Task<JobResult> RunAsync(LeasedJob job)
    {
        await using var scope = _services.CreateAsyncScope();
        var usage = new JobUsage(new Processing.FixedModelPrices(), TimeProvider.System);
        return await new CommandJobHandler().ExecuteAsync(new JobContext(job, scope.ServiceProvider, usage), Ct);
    }

    /// <summary>Enqueues, leases and runs.</summary>
    public async Task<CommandRun> RunAsync<T>(T payload, Guid departmentId, string classification = Classification.Internal)
    {
        var job = await LeaseAsync(payload, departmentId, classification);
        return new CommandRun(job, await RunAsync(job));
    }

    /// <summary>Another instance holds the job now: this attempt's completion matches no row.</summary>
    public Task StealLeaseAsync(Guid jobId) =>
        Sql.ExecuteAsync(database.Connection(null), """UPDATE content."Jobs" SET "LeaseOwner" = $2 WHERE "Id" = $1""", jobId, Guid.NewGuid());

    public ValueTask DisposeAsync() => _services.DisposeAsync();

    private static ServiceProvider Build(RagDatabaseFixture database, Action<IServiceCollection>? configure)
    {
        var services = new ServiceCollection();
        services.AddSingleton(database.Database(RagDatabase.Worker));
        services.AddSingleton<IJobQueue>(database.Queue(RagDatabase.Worker));
        services.AddSingleton<WorkerIdentity>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new RunStore(database.Database(RagDatabase.Worker)));
        services.AddRagCommands();
        configure?.Invoke(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}

/// <summary>Content rows for the command tests, written as the superuser.</summary>
internal static class CommandSeeds
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A live document of the source; <paramref name="url"/> is its identity.</summary>
    public static async Task<Guid> DocumentAsync(RagDatabaseFixture database, SourceSeed seed, string url, string mediaType = "text/html",
        DateTimeOffset? lastSeenAt = null)
    {
        var id = Guid.CreateVersion7();
        await Sql.ExecuteAsync(database.Connection(null), """
            INSERT INTO content."Documents" ("Id", "SourceId", "Classification", "IdentityKind", "CanonicalIdentity", "IdentityHash", "ContentHash",
                                            "ContentHashVersion", "Url", "MediaType", "CurrentExecutionId", "FirstSeenAt", "LastSeenAt")
            VALUES ($1, $2, $3, 'url', $4, $5, $6, 1, $4, $7, $8, $9, $9)
            """, id, seed.SourceId, seed.Classification, url, SHA256.HashData(Encoding.UTF8.GetBytes(url)), RandomNumberGenerator.GetBytes(32), mediaType,
            Guid.CreateVersion7(), lastSeenAt ?? DateTimeOffset.UtcNow);
        return id;
    }

    /// <summary>
    /// A chunk set of the document with <paramref name="chunks"/> chunks, swapped in as <paramref name="state"/>; of the
    /// document's content, or of an earlier content when <paramref name="earlier"/>.
    /// </summary>
    public static async Task<Guid> SetAsync(RagDatabaseFixture database, SourceSeed seed, Guid document, string state, PiiCounts? counts = null, int chunks = 2,
        bool earlier = false)
    {
        var writer = new IndexWriter(database.Database(RagDatabase.Worker));
        var hash = earlier
            ? RandomNumberGenerator.GetBytes(32)
            : await Sql.ScalarAsync<byte[]>(database.Connection(null), """SELECT "ContentHash" FROM content."Documents" WHERE "Id" = $1""", document);
        var set = new ChunkSetRecord(Guid.CreateVersion7(), new ChunkSetKey(document, seed.VersionId, null, EngineKit.ProfileId, hash), seed.Classification,
            seed.CollectionId, RandomNumberGenerator.GetBytes(32))
        {
            ChunkCount = chunks,
            PiiCounts = counts ?? new PiiCounts(),
            PiiFlagged = counts is { Personnummer: > 0 },
        };
        var rows = Enumerable.Range(0, chunks).Select(i => new ChunkRecord(Guid.CreateVersion7(), set.Id, document, seed.CollectionId, seed.Classification, i,
            new ChunkDraft
            {
                Ordinal = i, Prefix = "x", HeadingPath = "x", Text = "Text " + i, SearchText = "Text " + i, OverlapChars = 0,
                TextHash = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)), TokenCount = 2, DocumentType = "page",
            }, EngineKit.ProfileId, new float[EngineKit.Dimensions]) { DeletedAt = DateTimeOffset.UtcNow }).ToList();
        await writer.WriteInvisibleAsync(set, rows, Ct);
        await using var connection = new NpgsqlConnection(database.Connection(null));
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await IndexWriter.SwapInAsync(connection, transaction, document, set.Id, state, seed.VersionId, DateTimeOffset.UtcNow, Ct);
        await transaction.CommitAsync(Ct);
        return set.Id;
    }

    /// <summary>A run of the source in <paramref name="state"/>, finished <paramref name="finishedAt"/>.</summary>
    public static async Task<Guid> RunAsync(RagDatabaseFixture database, SourceSeed seed, string state, DateTimeOffset finishedAt)
    {
        var id = Guid.CreateVersion7();
        await Sql.ExecuteAsync(database.Connection(null), """
            INSERT INTO content."Runs" ("Id", "SourceId", "Classification", "Kind", "Trigger", "PipelineVersionId", "Phase", "State", "Added", "Updated",
                                       "Unchanged", "Deleted", "Failed", "ProposedDeletions", "CostUsd", "StartedAt", "FinishedAt")
            VALUES ($1, $2, $3, 'sync', 'manual', $4, 'done', $5, 0, 0, 0, 0, 0, 0, 0, $6 - interval '1 hour', $6)
            """, id, seed.SourceId, seed.Classification, seed.VersionId, state, finishedAt);
        return id;
    }

    public static Task ProposeAsync(RagDatabaseFixture database, SourceSeed seed, Guid runId, Guid document, string decision = "pending") =>
        Sql.ExecuteAsync(database.Connection(null), """
            INSERT INTO content."DeletionProposals" ("RunId", "DocumentId", "SourceId", "Classification", "Reason", "Exempt", "Decision")
            VALUES ($1, $2, $3, $4, 'not-seen', false, $5)
            """, runId, document, seed.SourceId, seed.Classification, decision);

    /// <summary>Searchable chunks of the document's set (not deleted).</summary>
    public static Task<long> VisibleChunksAsync(RagDatabaseFixture database, Guid chunkSetId) =>
        Sql.ScalarAsync<long>(database.Connection(null), """SELECT count(*) FROM content."Chunks" WHERE "ChunkSetId" = $1 AND "DeletedAt" IS NULL""", chunkSetId);

    public static Task<string> SetStateAsync(RagDatabaseFixture database, Guid chunkSetId) =>
        Sql.ScalarAsync<string>(database.Connection(null), """SELECT "State" FROM content."ChunkSets" WHERE "Id" = $1""", chunkSetId);

    /// <summary>The job's stored result.</summary>
    public static async Task<JsonElement> ResultAsync(RagDatabaseFixture database, Guid jobId)
    {
        var row = await Sql.JobAsync(database.Connection(null), jobId);
        row.State.ShouldBe(JobStates.Succeeded);
        using var document = JsonDocument.Parse(row.Result!);
        return document.RootElement.Clone();
    }

    /// <summary>The <c>sync</c> jobs queued for the source.</summary>
    public static Task<long> SyncJobsAsync(RagDatabaseFixture database, Guid sourceId) =>
        Sql.ScalarAsync<long>(database.Connection(null), """
            SELECT count(*) FROM content."Jobs" WHERE "Class" = 'sync' AND "State" = 'queued' AND "Payload" ->> 'sourceId' = $1
            """, sourceId.ToString());

    public static RunFinalizer Finalizer(RagDatabaseFixture database)
    {
        var db = database.Database(RagDatabase.Worker);
        return new RunFinalizer(db, database.Queue(RagDatabase.Worker), new EngineSettings(db, TimeProvider.System, NullLogger<EngineSettings>.Instance),
            Options.Create(new WorkerOptions()), Options.Create(new ClamAvOptions()), TimeProvider.System, NullLogger<RunFinalizer>.Instance);
    }
}
