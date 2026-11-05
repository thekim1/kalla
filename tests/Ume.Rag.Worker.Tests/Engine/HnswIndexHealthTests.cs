using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

        Numbers(recall).ShouldBe((1, 20, 1.0));
    }

    [Fact]
    public async Task Collections_searched_exactly_are_not_measured()
    {
        var seed = await EngineKit.SeedUrlListAsync(database, []);
        var profile = await _writer.AddEmbeddingProfileAsync(Dimensions, Ct);
        await DocumentsAsync(seed, profile, 12);

        var recall = await Health().MeasureAsync(profile, Dimensions, EveryCollection with { ExactScanMaxChunks = 12 }, Ct);

        Numbers(recall).ShouldBe((0, 0, null), "a collection of at most exactScanMaxChunks chunks is searched exactly");
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
        Numbers(outcome.After!).ShouldBe((1, 20, 1.0));
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

    /// <summary>
    /// B-9 (Decisions "M-2 open design calls"): three collections of 6 chunks are each searched exactly (at most
    /// exactScanMaxChunks 10), but an assistant linking all three searches 18 chunks with HNSW, so that scope is sampled:
    /// 18 queries, drawn from all three collections.
    /// </summary>
    [Fact]
    public async Task Small_collections_an_assistant_links_together_are_sampled_as_one_hnsw_scope()
    {
        var profile = await _writer.AddEmbeddingProfileAsync(Dimensions, Ct);
        var seeds = new List<SourceSeed>();
        for (var i = 0; i < 3; i++)
        {
            var seed = await EngineKit.SeedUrlListAsync(database, []);
            await DocumentsAsync(seed, profile, 6);
            seeds.Add(seed);
        }

        var settings = EveryCollection with { ExactScanMaxChunks = 10 };
        Numbers(await Health().MeasureAsync(profile, Dimensions, settings, Ct)).ShouldBe((0, 0, null), "no collection alone is above the limit, no assistant links them");

        var assistant = await AssistantAsync([.. seeds.Select(s => (s.CollectionId, Array.Empty<string>()))]);
        var recall = await Health().MeasureAsync(profile, Dimensions, settings, Ct);

        Numbers(recall).ShouldBe((1, 18, 1.0), "the linked scope has 18 chunks, every one a query (sample 20)");
        var worst = recall.Worst.ShouldNotBeNull();
        worst.Scope.CollectionIds.ShouldBe([.. seeds.Select(s => s.CollectionId)], ignoreOrder: true);
        worst.Scope.TopicLinks.ShouldBeEmpty();
        worst.Scope.Assistants.ShouldBe([assistant]);
        worst.Queries.ShouldBe(18);
    }

    [Fact]
    public async Task An_assistant_scope_has_only_live_collections_of_its_profile_and_an_archived_assistant_none()
    {
        var profile = await _writer.AddEmbeddingProfileAsync(Dimensions, Ct);
        var first = await EngineKit.SeedUrlListAsync(database, []);
        var second = await EngineKit.SeedUrlListAsync(database, []);
        var unpublished = await EngineKit.SeedUrlListAsync(database, []);
        await DocumentsAsync(first, profile, 6);
        await DocumentsAsync(second, profile, 6);
        await DocumentsAsync(unpublished, profile, 6);
        await Sql.ExecuteAsync(database.Connection(null), """UPDATE config."Collections" SET "PublishedAt" = NULL WHERE "Id" = $1""", unpublished.CollectionId);
        var settings = EveryCollection with { ExactScanMaxChunks = 11 };

        var assistant = await AssistantAsync([(first.CollectionId, []), (unpublished.CollectionId, [])]);
        (await Health().ScopesAsync(profile, settings, Ct)).ShouldBeEmpty("the unpublished collection is not searched, so the scope is 6 chunks");

        await Sql.ExecuteAsync(database.Connection(null), """UPDATE config."Assistants" SET "State" = 'archived' WHERE "Id" = $1""", assistant);
        var archived = await AssistantAsync([(first.CollectionId, []), (second.CollectionId, [])]);
        await Sql.ExecuteAsync(database.Connection(null), """UPDATE config."Assistants" SET "State" = 'archived' WHERE "Id" = $1""", archived);
        (await Health().ScopesAsync(profile, settings, Ct)).ShouldBeEmpty("an archived assistant is not searched");

        var active = await AssistantAsync([(second.CollectionId, []), (first.CollectionId, [])]);
        var twin = await AssistantAsync([(first.CollectionId, []), (second.CollectionId, [])]);
        var scope = (await Health().ScopesAsync(profile, settings, Ct)).ShouldHaveSingleItem("the same collections from two assistants are one scope");
        scope.CollectionIds.ShouldBe([first.CollectionId, second.CollectionId], ignoreOrder: true);
        scope.Assistants.ShouldBe([active, twin], ignoreOrder: true);

        await Sql.ExecuteAsync(database.Connection(null), """UPDATE config."Collections" SET "EmbeddingProfileId" = $2 WHERE "Id" = $1""",
            second.CollectionId, await _writer.AddEmbeddingProfileAsync(Dimensions, Ct));
        (await Health().ScopesAsync(profile, settings, Ct)).ShouldBeEmpty("the scope is the first live link's profile only (KnowledgeService §4.3)");
    }

    /// <summary>
    /// A link with topics: only the collection's chunks with the tag are in the scope, and the sample and both top-10s are
    /// filtered as statement V is. 6 of 10 chunks of the topic collection carry it, plus 6 of a whole collection: 12 queries.
    /// </summary>
    [Fact]
    public async Task A_topic_link_limits_the_sample_and_the_search_to_its_tagged_chunks()
    {
        var profile = await _writer.AddEmbeddingProfileAsync(Dimensions, Ct);
        var whole = await EngineKit.SeedUrlListAsync(database, []);
        var topical = await EngineKit.SeedUrlListAsync(database, []);
        await DocumentsAsync(whole, profile, 6);
        await DocumentsAsync(topical, profile, 6, tags: ["bygglov"]);
        await DocumentsAsync(topical, profile, 4, tags: ["skola"]);
        var assistant = await AssistantAsync([(whole.CollectionId, []), (topical.CollectionId, ["bygglov"])]);

        // Each collection alone (6, 10) is searched exactly; the linked scope is above 11 whether sized by whole
        // collections (16) or by the topic's chunks (12, B-10).
        var recall = await Health().MeasureAsync(profile, Dimensions, EveryCollection with { ExactScanMaxChunks = 11 }, Ct);

        Numbers(recall).ShouldBe((1, 12, 1.0));
        var scope = recall.Worst.ShouldNotBeNull().Scope;
        scope.TopicLinks.ShouldBe([(topical.CollectionId, "bygglov")]);
        scope.Assistants.ShouldBe([assistant]);
    }

    /// <summary>
    /// B-10: a narrow topic in a collection over <c>exactScanMaxChunks</c> is searched exactly by retrieval (the scope is
    /// sized by its tagged chunks, <see cref="Ume.Rag.Retrieval.ScopeSizeCache.SizeAsync"/>), so it is not sampled; the
    /// collection searched whole still is.
    /// </summary>
    [Fact]
    public async Task A_narrow_topic_link_in_a_collection_over_the_limit_is_searched_exactly_and_not_sampled()
    {
        var profile = await _writer.AddEmbeddingProfileAsync(Dimensions, Ct);
        var large = await EngineKit.SeedUrlListAsync(database, []);
        await DocumentsAsync(large, profile, 3, tags: ["avfall"]);
        await DocumentsAsync(large, profile, 7);
        await AssistantAsync([(large.CollectionId, ["avfall"])]);
        var settings = EveryCollection with { ExactScanMaxChunks = 5 };

        // The collection alone: 10 chunks, over 5, HNSW. The link's topic: 3 chunks, exact (whole-collection sizing gave 10).
        var scopes = await Health().ScopesAsync(profile, settings, Ct);

        scopes.Select(s => (s.CollectionIds.Single(), s.TopicLinks.Count)).ShouldBe([(large.CollectionId, 0)]);
        var recall = await Health().MeasureAsync(profile, Dimensions, settings, Ct);
        Numbers(recall).ShouldBe((1, 10, 1.0));
    }

    [Fact]
    public async Task The_worst_scope_is_the_profiles_recall_and_each_scope_below_the_floor_is_logged_by_name()
    {
        var profile = await _writer.AddEmbeddingProfileAsync(Dimensions, Ct);
        var first = await EngineKit.SeedUrlListAsync(database, []);
        var second = await EngineKit.SeedUrlListAsync(database, []);
        await DocumentsAsync(first, profile, 8);
        await DocumentsAsync(second, profile, 8);
        var assistant = await AssistantAsync([(first.CollectionId, []), (second.CollectionId, [])]);
        var logs = new CapturingLoggerProvider();

        var outcome = await Health(logs).CheckAsync(profile, Dimensions, EveryCollection with { RecallFloor = 1.01 }, Ct);

        outcome.ShouldNotBeNull();
        outcome.Before.Scopes.ShouldBe(3, "each collection alone and the assistant's two together");
        outcome.Before.ByScope.Count.ShouldBe(3);
        outcome.Before.Recall.ShouldBe(outcome.Before.ByScope.Min(s => s.Recall));
        var failing = logs.Entries.Where(e => e.Level == LogLevel.Warning && e.Message.Contains("of the scope", StringComparison.Ordinal)).ToList();
        failing.Count.ShouldBe(6, "three scopes below the floor, before and after the rebuild");
        failing.ShouldContain(e => e.Message.Contains($"assistants {assistant:D}", StringComparison.Ordinal) &&
                                   e.Message.Contains(first.CollectionId.ToString("D"), StringComparison.Ordinal) &&
                                   e.Message.Contains(second.CollectionId.ToString("D"), StringComparison.Ordinal));
    }

    [Fact]
    public void The_rebuild_connection_is_ragdb_maintenance_else_ragdb_and_unpooled()
    {
        const string ragdb = "Host=db;Database=ragdb;Username=ume_rag_worker;Application Name=pooled";
        const string maintenance = "Host=db-direct;Database=ragdb;Username=ume_rag_worker;Application Name=maintenance";

        var both = new NpgsqlConnectionStringBuilder(HnswIndexHealth.ConnectionString(Configuration(ragdb, maintenance)));
        both.Host.ShouldBe("db-direct");
        both.ApplicationName.ShouldBe("maintenance");
        both.Pooling.ShouldBeFalse();
        both.Options.ShouldBeNull("the timeout is a SET on the session, never a startup option (PgBouncer drops options)");

        var fallback = new NpgsqlConnectionStringBuilder(HnswIndexHealth.ConnectionString(Configuration(ragdb, null)));
        fallback.Host.ShouldBe("db");
        fallback.Pooling.ShouldBeFalse();
        new NpgsqlConnectionStringBuilder(HnswIndexHealth.ConnectionString(Configuration(ragdb, ""))).Host.ShouldBe("db", "an empty value falls back too");

        Should.Throw<InvalidOperationException>(() => HnswIndexHealth.ConnectionString(Configuration(null, null)));
    }

    /// <summary>
    /// The rebuild on a real session: the maintenance string's startup option caps statements at 500 ms (standing in for
    /// the role's 60 s), and a transaction holding <c>SHARE UPDATE EXCLUSIVE</c> on <c>content."Chunks"</c> keeps the
    /// <c>REINDEX</c> waiting for 1.5 s. It survives only because the session ran <c>SET statement_timeout = 0</c> first;
    /// the waiting backend carries the maintenance string's application name, not ragdb's.
    /// </summary>
    [Fact]
    public async Task The_rebuild_runs_on_the_maintenance_connection_and_sets_the_timeout_by_SET()
    {
        var seed = await EngineKit.SeedUrlListAsync(database, []);
        var profile = await _writer.AddEmbeddingProfileAsync(Dimensions, Ct);
        await DocumentsAsync(seed, profile, 30);
        var index = HnswIndexHealth.IndexName(profile);
        var tag = profile.ToString("N")[..12];
        var worker = database.Connection(RagDatabase.Worker);
        var configuration = Configuration(
            new NpgsqlConnectionStringBuilder(worker) { ApplicationName = $"pooled-{tag}" }.ConnectionString,
            new NpgsqlConnectionStringBuilder(worker) { ApplicationName = $"maintenance-{tag}", Options = "-c statement_timeout=500" }.ConnectionString);
        var health = new HnswIndexHealth(_dataSource, HnswIndexHealth.ConnectionString(configuration),
            new HnswIndexMetrics(_metrics.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>()), NullLogger<HnswIndexHealth>.Instance);

        using var source = new ActivitySource($"Ume.Rag.Worker.Tests.Rebuild.{tag}");
        var statements = new List<(ActivityTraceId Trace, string Sql)>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Npgsql" || s == source,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a =>
            {
                if (a.Source.Name == "Npgsql" && a.GetTagItem("db.query.text") is string sql)
                {
                    lock (statements)
                    {
                        statements.Add((a.TraceId, sql));
                    }
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        await using var blocker = new NpgsqlConnection(database.Connection(null));
        await blocker.OpenAsync(Ct);
        await using var transaction = await blocker.BeginTransactionAsync(Ct);
        await using (var lockChunks = new NpgsqlCommand("""LOCK TABLE content."Chunks" IN SHARE UPDATE EXCLUSIVE MODE""", blocker, transaction))
        {
            await lockChunks.ExecuteNonQueryAsync(Ct);
        }

        Task rebuild;
        ActivityTraceId trace;
        using (var parent = source.StartActivity("rebuild")!)
        {
            trace = parent.TraceId;
            rebuild = health.RebuildAsync(index, Ct);
        }

        var waiting = Stopwatch.StartNew();
        while (await Sql.ScalarAsync<long>(database.Connection(null), """
                   SELECT count(*) FROM pg_stat_activity WHERE application_name = $1 AND wait_event_type = 'Lock' AND query LIKE 'REINDEX%'
                   """, $"maintenance-{tag}") == 0)
        {
            rebuild.IsCompleted.ShouldBeFalse("the rebuild ended before it waited for the lock");
            waiting.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(20), "no REINDEX waited on the maintenance connection");
            await Task.Delay(50, Ct);
        }

        (await Sql.ScalarAsync<long>(database.Connection(null), "SELECT count(*) FROM pg_stat_activity WHERE application_name = $1", $"pooled-{tag}"))
            .ShouldBe(0, "nothing of the rebuild runs on ragdb");
        await Task.Delay(TimeSpan.FromMilliseconds(1500), Ct);
        rebuild.IsCompleted.ShouldBeFalse("the REINDEX outlived the 500 ms startup option, so the session's SET applied");

        await transaction.CommitAsync(Ct);
        await rebuild.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        string[] sent;
        lock (statements)
        {
            sent = [.. statements.Where(s => s.Trace == trace).Select(s => s.Sql)];
        }

        sent.ShouldBe([HnswIndexHealth.NoStatementTimeoutSql, $"REINDEX INDEX CONCURRENTLY content.\"{index}\""]);
        HnswIndexHealth.NoStatementTimeoutSql.ShouldBe("SET statement_timeout = 0");
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

    private static IConfiguration Configuration(string? ragdb, string? maintenance) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:ragdb"] = ragdb,
            ["ConnectionStrings:ragdb-maintenance"] = maintenance,
        })
        .Build();

    private HnswIndexHealth Health(CapturingLoggerProvider? logs = null) => new(_dataSource, database.Connection(RagDatabase.Worker),
        new HnswIndexMetrics(_metrics.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>()),
        logs is null ? NullLogger<HnswIndexHealth>.Instance : new LoggerFactory([logs]).CreateLogger<HnswIndexHealth>());

    private static (int Scopes, int Queries, double? Recall) Numbers(HnswRecall recall) => (recall.Scopes, recall.Queries, recall.Recall);

    /// <summary>An active assistant whose published version links <paramref name="links"/> (collection, topics); returns its id.</summary>
    private async Task<Guid> AssistantAsync(IReadOnlyList<(Guid CollectionId, string[] Tags)> links)
    {
        var assistantId = Guid.CreateVersion7();
        var versionId = Guid.CreateVersion7();
        var json = JsonSerializer.Serialize(links.Select(l => new { collectionId = l.CollectionId, tags = l.Tags }));
        await Sql.ExecuteAsync(database.Connection(null), $"""
            BEGIN;
            INSERT INTO config."Assistants" ("Id", "Slug", "Name", "DepartmentId", "Classification", "HistoryMode", "RetentionDays", "AllowedOrigins", "State", "UpdatedAt")
            VALUES ('{assistantId}', 'hnsw-{assistantId:N}', 'Index', '{Guid.NewGuid()}', 'internal', 'none', 90, '{"{}"}', 'active', now());
            INSERT INTO config."AssistantVersions" ("Id", "AssistantId", "Number", "State", "GatewayModel", "SystemPrompt", "QuerySteps", "NotFound", "CollectionLinks", "CreatedAt", "UpdatedAt", "PublishedBy", "PublishedAt")
            VALUES ('{versionId}', '{assistantId}', 1, 'published', 'gpt-test', 'x', '{"{}"}', '{"{}"}', '{json}', now(), now(), 'test', now());
            UPDATE config."Assistants" SET "PublishedVersionId" = '{versionId}' WHERE "Id" = '{assistantId}';
            COMMIT;
            """);
        return assistantId;
    }

    /// <summary>
    /// <paramref name="count"/> live one-chunk documents of the seed's collection, with random embeddings of the profile;
    /// the collection is published on the profile (retrieval searches only live collections, of their own profile).
    /// </summary>
    private async Task DocumentsAsync(SourceSeed seed, Guid profile, int count, string[]? tags = null)
    {
        await Sql.ExecuteAsync(database.Connection(null), """
            UPDATE config."Collections" SET "PublishedAt" = coalesce("PublishedAt", now()), "EmbeddingProfileId" = $2 WHERE "Id" = $1
            """, seed.CollectionId, profile);
        var random = new Random(BitConverter.ToInt32(seed.CollectionId.ToByteArray(), 12) ^ count); // distinct embeddings per collection
        for (var i = 0; i < count; i++)
        {
            var embedding = Enumerable.Range(0, Dimensions).Select(_ => (float)(random.NextDouble() * 2 - 1)).ToArray();
            await _writer.AddDocumentAsync(new FixtureDocumentSpec
            {
                CollectionId = seed.CollectionId,
                EmbeddingProfileId = profile,
                SourceId = seed.SourceId,
                Classification = seed.Classification,
            }, [new FixtureChunk($"Stycke {i} {Guid.NewGuid()}") { Embedding = embedding, Tags = tags }], Ct);
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
