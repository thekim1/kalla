using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Pipelines.Json;
using Ume.Rag.Retrieval.Postgres;
using Ume.Rag.TestKit;
using Ume.Rag.Worker.Engine;
using Ume.Rag.Worker.Jobs;
using Ume.Rag.Worker.Tests.CommandJobs;

namespace Ume.Rag.Worker.Tests.Engine;

/// <summary>
/// The index-health check of knowledge design §6.4 on the test database: recall@10 of a profile's HNSW index against exact
/// search, the concurrent rebuild below the floor (with the audit entry of the command), the nightly schedule (one
/// <c>check-hnsw-index</c> per profile, dedup <c>hnsw-check:{profile}</c>) and the trigger after a run that made more than
/// 10 % of the profile's chunks live. Each test has an embedding profile of its own, so its index and counts are its own.
/// </summary>
[Collection(RagDatabaseGroup3.Name)]
public sealed class HnswIndexHealthTests(RagDatabaseFixture database) : IAsyncLifetime
{
    private const int Dimensions = 16;

    private static readonly HnswHealthSettings EveryCollection = new(HnswHealthSettings.DefaultRecallFloor, 20, ExactScanMaxChunks: 0, MinHnswSelectivity: 0);

    private readonly ServiceProvider _metrics = new ServiceCollection().AddMetrics().BuildServiceProvider();
    private ContentFixtureWriter _writer = null!;
    private NpgsqlDataSource _dataSource = null!;
    private (bool HadRow, string? Retrieval) _settings;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await database.ResetAsync();
        // The worker role's MAINTAIN on content."Chunks" (data-model §1.2), which the rebuild needs.
        await Sql.ExecuteAsync(database.Connection(null), """GRANT MAINTAIN ON content."Chunks" TO ume_rag_worker""");
        _writer = new ContentFixtureWriter(database.Connection(RagDatabase.Worker), database.Connection(RagDatabase.Admin));
        _dataSource = RetrievalDataSource.Create(database.Connection(RagDatabase.Worker));
        await using var db = TestPostgres.Context(database.Connection(null));
        var row = await db.Settings.FirstOrDefaultAsync(Ct);
        _settings = (row is not null, row?.Retrieval);
    }

    public async ValueTask DisposeAsync()
    {
        await RestoreRetrievalAsync();
        await _writer.DisposeAsync();
        await _dataSource.DisposeAsync();
        await _metrics.DisposeAsync();
    }

    [Fact]
    public async Task A_healthy_index_has_recall_one()
    {
        var seed = await EngineKit.SeedUrlListAsync(database, []);
        var profile = await _writer.AddEmbeddingProfileAsync(Dimensions, Ct);
        await DocumentsAsync(seed, profile, 40);

        var recall = await Health().MeasureAsync(profile, Dimensions, EveryCollection, Ct);

        recall.ShouldBe(new HnswRecall(1, 20, 1.0));
    }

    [Fact]
    public async Task Collections_searched_exactly_are_not_measured()
    {
        var seed = await EngineKit.SeedUrlListAsync(database, []);
        var profile = await _writer.AddEmbeddingProfileAsync(Dimensions, Ct);
        await DocumentsAsync(seed, profile, 12);

        var recall = await Health().MeasureAsync(profile, Dimensions, EveryCollection with { ExactScanMaxChunks = 12 }, Ct);

        recall.ShouldBe(new HnswRecall(0, 0, null), "a collection of at most exactScanMaxChunks chunks is searched exactly");
    }

    [Fact]
    public async Task Below_the_floor_the_index_is_rebuilt_and_measured_again()
    {
        var seed = await EngineKit.SeedUrlListAsync(database, []);
        var profile = await _writer.AddEmbeddingProfileAsync(Dimensions, Ct);
        await DocumentsAsync(seed, profile, 30);
        var before = await FileNodeAsync(profile);

        var outcome = await Health().CheckAsync(profile, Dimensions, EveryCollection with { RecallFloor = 1.01 }, Ct);

        outcome.ShouldNotBeNull();
        outcome.IndexName.ShouldBe(HnswIndexHealth.IndexName(profile));
        outcome.Before.Recall.ShouldBe(1.0);
        outcome.Rebuilt.ShouldBeTrue();
        outcome.After.ShouldBe(new HnswRecall(1, 20, 1.0));
        (await FileNodeAsync(profile)).ShouldNotBe(before, "REINDEX writes the index anew");
        (await Sql.ScalarAsync<bool>(database.Connection(null), """
            SELECT i.indisvalid FROM pg_index i WHERE i.indexrelid = to_regclass($1)
            """, $"content.\"{HnswIndexHealth.IndexName(profile)}\"")).ShouldBeTrue();
    }

    [Fact]
    public async Task At_or_above_the_floor_nothing_is_rebuilt()
    {
        var seed = await EngineKit.SeedUrlListAsync(database, []);
        var profile = await _writer.AddEmbeddingProfileAsync(Dimensions, Ct);
        await DocumentsAsync(seed, profile, 30);
        var before = await FileNodeAsync(profile);

        var outcome = await Health().CheckAsync(profile, Dimensions, EveryCollection, Ct);

        outcome.ShouldNotBeNull();
        outcome.Rebuilt.ShouldBeFalse();
        (await FileNodeAsync(profile)).ShouldBe(before);
    }

    [Fact]
    public void A_tie_with_the_last_exact_distance_counts_as_a_hit()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        HnswIndexHealth.Hits([(a, 0.0), (b, 0.3)], [(a, 0.0), (c, 0.3004)]).ShouldBe(2);
        HnswIndexHealth.Hits([(a, 0.0), (b, 0.3)], [(a, 0.0), (c, 0.4)]).ShouldBe(1);
    }

    [Fact]
    public async Task The_command_rebuilds_below_the_floor_and_audits_it_as_a_system_action()
    {
        var seed = await EngineKit.SeedUrlListAsync(database, []);
        var profile = await _writer.AddEmbeddingProfileAsync(Dimensions, Ct);
        await DocumentsAsync(seed, profile, 30);
        await SetRetrievalAsync("""{"exactScanMaxChunks":0,"minHnswSelectivity":0,"hnswRecallFloor":1.01,"hnswCheckSample":5}""");
        await using var runner = new CommandRunner(database, s => s.AddSingleton(Health()));

        var run = await runner.RunAsync(new CheckHnswIndexCommand { EmbeddingProfileId = profile, Trigger = HnswCheckTrigger.Nightly }, seed.DepartmentId);

        run.Result.ShouldBeOfType<JobResult.Committed>();
        var row = await Sql.JobAsync(database.Connection(null), run.Job.Id);
        var result = ContractJson.Deserialize<CheckHnswIndexResult>(JsonDocument.Parse(row.Result!).RootElement);
        result.ShouldBe(new CheckHnswIndexResult { Scopes = 1, Queries = 5, Recall = 1.0, Rebuilt = true, RecallAfter = 1.0 });
        var audit = await Sql.ScalarAsync<string>(database.Connection(null), """
            SELECT concat_ws('|', "Actor", "Action", "EntityType", "Service", "Details" ->> 'index', "Details" ->> 'trigger', "Details" ->> 'recallAfter')
            FROM audit."AuditLog" WHERE "EntityId" = $1
            """, profile.ToString("D"));
        audit.ShouldBe($"system:worker|hnsw-reindex|embeddingprofile|rag-worker|{HnswIndexHealth.IndexName(profile)}|nightly|1");
    }

    [Fact]
    public async Task The_command_of_a_healthy_index_writes_no_audit_entry()
    {
        var seed = await EngineKit.SeedUrlListAsync(database, []);
        var profile = await _writer.AddEmbeddingProfileAsync(Dimensions, Ct);
        await DocumentsAsync(seed, profile, 30);
        await SetRetrievalAsync("""{"exactScanMaxChunks":0,"minHnswSelectivity":0,"hnswCheckSample":5}""");
        await using var runner = new CommandRunner(database, s => s.AddSingleton(Health()));

        var run = await runner.RunAsync(new CheckHnswIndexCommand { EmbeddingProfileId = profile, Trigger = HnswCheckTrigger.Sync }, seed.DepartmentId);

        run.Result.ShouldBeOfType<JobResult.Committed>();
        var result = ContractJson.Deserialize<CheckHnswIndexResult>(JsonDocument.Parse((await Sql.JobAsync(database.Connection(null), run.Job.Id)).Result!).RootElement);
        result.ShouldBe(new CheckHnswIndexResult { Scopes = 1, Queries = 5, Recall = 1.0 });
        (await Sql.ScalarAsync<long>(database.Connection(null), """SELECT count(*) FROM audit."AuditLog" WHERE "EntityId" = $1""", profile.ToString("D")))
            .ShouldBe(0);
    }

    [Fact]
    public async Task The_nightly_schedule_enqueues_one_check_per_profile()
    {
        var large = await EngineKit.SeedUrlListAsync(database, []);
        var small = await EngineKit.SeedUrlListAsync(database, []);
        var profile = await _writer.AddEmbeddingProfileAsync(Dimensions, Ct);
        var smallProfile = await _writer.AddEmbeddingProfileAsync(Dimensions, Ct);
        await DocumentsAsync(small, profile, 3);
        await DocumentsAsync(large, profile, 8);
        await DocumentsAsync(small, smallProfile, 5);
        await SetRetrievalAsync("""{"exactScanMaxChunks":5}""");
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var scheduler = new HnswCheckScheduler(database.Database(RagDatabase.Worker), database.Queue(RagDatabase.Worker), Options.Create(new WorkerOptions()),
            time, NullLogger<HnswCheckScheduler>.Instance);

        await scheduler.TickAsync(Ct);
        await scheduler.TickAsync(Ct);

        var jobs = await ChecksAsync(profile);
        jobs.Count.ShouldBe(1, "the second tick enqueues nothing");
        jobs[0].DedupKey.ShouldBe(JobDedupKeys.HnswCheck(profile));
        jobs[0].DepartmentId.ShouldBe(large.DepartmentId, "the job is the largest collection's");
        jobs[0].Payload.ShouldBe(new CheckHnswIndexCommand { EmbeddingProfileId = profile, Trigger = HnswCheckTrigger.Nightly });
        (await ChecksAsync(smallProfile)).ShouldBeEmpty("no collection of that profile is above exactScanMaxChunks");

        await Sql.ExecuteAsync(database.Connection(null), """UPDATE content."Jobs" SET "State" = 'succeeded' WHERE "DedupKey" = $1""",
            JobDedupKeys.HnswCheck(profile));
        await scheduler.TickAsync(Ct);
        (await ChecksAsync(profile)).Count.ShouldBe(1, "a finished check is not repeated before the next night");
        time.Advance(TimeSpan.FromDays(1));
        await scheduler.TickAsync(Ct);
        (await ChecksAsync(profile)).Count.ShouldBe(2, "the next night enqueues the next check");
    }

    [Fact]
    public async Task A_finished_run_triggers_a_check_when_it_made_more_than_ten_percent_live()
    {
        var other = await EngineKit.SeedUrlListAsync(database, []);
        var synced = await EngineKit.SeedUrlListAsync(database, []);
        var profile = await _writer.AddEmbeddingProfileAsync(Dimensions, Ct);
        await DocumentsAsync(other, profile, 20);
        await SetRetrievalAsync("""{"exactScanMaxChunks":5}""");
        var finalizer = CommandSeeds.Finalizer(database);

        var below = await RunningRunAsync(synced);
        await DocumentsAsync(synced, profile, 2);
        await finalizer.FinalizeAsync(Ct);
        (await RunStateAsync(below)).ShouldNotBe(RunStates.Running);
        (await ChecksAsync(profile)).ShouldBeEmpty("2 of 22 live chunks is not more than 10 %");

        var above = await RunningRunAsync(synced);
        await DocumentsAsync(synced, profile, 3);
        await finalizer.FinalizeAsync(Ct);
        (await RunStateAsync(above)).ShouldNotBe(RunStates.Running);
        var jobs = await ChecksAsync(profile);
        jobs.Count.ShouldBe(1, "3 of 25 live chunks is more than 10 %");
        jobs[0].DepartmentId.ShouldBe(synced.DepartmentId);
        jobs[0].Payload.ShouldBe(new CheckHnswIndexCommand { EmbeddingProfileId = profile, Trigger = HnswCheckTrigger.Sync });
    }

    private HnswIndexHealth Health() => new(_dataSource, database.Connection(RagDatabase.Worker), new HnswIndexMetrics(_metrics.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>()),
        NullLogger<HnswIndexHealth>.Instance);

    /// <summary><paramref name="count"/> live one-chunk documents of the seed's collection, with random embeddings of the profile.</summary>
    private async Task DocumentsAsync(SourceSeed seed, Guid profile, int count)
    {
        var random = new Random(count);
        for (var i = 0; i < count; i++)
        {
            var embedding = Enumerable.Range(0, Dimensions).Select(_ => (float)(random.NextDouble() * 2 - 1)).ToArray();
            await _writer.AddDocumentAsync(new FixtureDocumentSpec
            {
                CollectionId = seed.CollectionId,
                EmbeddingProfileId = profile,
                SourceId = seed.SourceId,
                Classification = seed.Classification,
            }, [new FixtureChunk($"Stycke {i} {Guid.NewGuid()}") { Embedding = embedding }], Ct);
        }
    }

    private Task<uint> FileNodeAsync(Guid profile) =>
        Sql.ScalarAsync<uint>(database.Connection(null), "SELECT pg_relation_filenode(to_regclass($1))::oid", $"content.\"{HnswIndexHealth.IndexName(profile)}\"");

    private async Task<Guid> RunningRunAsync(SourceSeed seed)
    {
        var id = Guid.CreateVersion7();
        await Sql.ExecuteAsync(database.Connection(null), """
            INSERT INTO content."Runs" ("Id", "SourceId", "Classification", "Kind", "Trigger", "PipelineVersionId", "Phase", "State", "Added", "Updated",
                                       "Unchanged", "Deleted", "Failed", "ProposedDeletions", "CostUsd", "StartedAt")
            VALUES ($1, $2, $3, 'reprocess', 'manual', $4, 'fetch', 'running', 0, 0, 0, 0, 0, 0, 0, now())
            """, id, seed.SourceId, seed.Classification, seed.VersionId);
        return id;
    }

    private Task<string> RunStateAsync(Guid runId) =>
        Sql.ScalarAsync<string>(database.Connection(null), """SELECT "State" FROM content."Runs" WHERE "Id" = $1""", runId);

    private async Task<List<(string DedupKey, Guid DepartmentId, CheckHnswIndexCommand Payload)>> ChecksAsync(Guid profile)
    {
        await using var connection = new NpgsqlConnection(database.Connection(null));
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            SELECT "DedupKey", "DepartmentId", "Payload"::text FROM content."Jobs"
            WHERE "Class" = 'command' AND "Payload" ->> 'kind' = 'check-hnsw-index' AND "Payload" ->> 'embeddingProfileId' = $1
            ORDER BY "EnqueuedAt"
            """, connection) { Parameters = { new NpgsqlParameter { Value = profile.ToString() } } };
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var jobs = new List<(string, Guid, CheckHnswIndexCommand)>();
        while (await reader.ReadAsync(Ct))
        {
            jobs.Add((reader.GetString(0), reader.GetGuid(1), ContractJson.Deserialize<CheckHnswIndexCommand>(JsonDocument.Parse(reader.GetString(2)).RootElement)));
        }

        return jobs;
    }

    private async Task SetRetrievalAsync(string json)
    {
        await using var db = TestPostgres.Context(database.Connection(null));
        var row = await db.Settings.FirstOrDefaultAsync(Ct);
        if (row is null)
        {
            row = new PlatformSettings { UpdatedAt = DateTimeOffset.UtcNow };
            db.Settings.Add(row);
        }

        row.Retrieval = json;
        await db.SaveChangesAsync(Ct);
    }

    private async Task RestoreRetrievalAsync()
    {
        if (!_settings.HadRow)
        {
            await Sql.ExecuteAsync(database.Connection(null), """DELETE FROM config."Settings" """);
            return;
        }

        await Sql.ExecuteAsync(database.Connection(null), """UPDATE config."Settings" SET "Retrieval" = $1::jsonb""", _settings.Retrieval ?? "{}");
    }
}
