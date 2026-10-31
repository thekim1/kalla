using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Pipelines.Json;
using Ume.Rag.TestKit;
using Ume.Rag.Worker.Evaluation;
using Ume.Rag.Worker.Jobs;
using Ume.Rag.Worker.Tests.CommandJobs;
using Ume.Rag.Worker.Tests.Engine;

namespace Ume.Rag.Worker.Tests.Evaluation;

/// <summary>
/// The set triggers of the evaluation scheduler on the test database (processing design §13.1, §13.3; engine design
/// §9.6): <c>generate-set</c> once after a collection's first syncs finish, the weekly <c>top-up-set</c> once per week,
/// and a <c>top-up-set</c> when <c>D</c> grew by 25 % since the last round (not by 10 %). The scheduler scans every
/// collection, so each test looks only at the set jobs of its own.
/// </summary>
[Collection(RagDatabaseGroup.Name)]
public sealed class EvalSetTriggerTests(RagDatabaseFixture database) : IAsyncLifetime
{
    private const string Model = "ume/chat-onprem";

    private PlatformSettings? _previous;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await database.ResetAsync();
        _previous = await EvalSeeds.SetModelsAsync(database, Model);
    }

    public async ValueTask DisposeAsync() => await EvalSeeds.RestoreModelsAsync(database, _previous is not null, _previous);

    [Fact]
    public async Task The_first_finished_syncs_enqueue_exactly_one_generate_set()
    {
        var seed = await EngineKit.SeedUrlListAsync(database, []);
        try
        {
            await EvalSeeds.DocumentAsync(database, seed, "Ett " + EvalSeeds.Tag(), [new($"Text {Guid.NewGuid()}")]);
            var run = await CommandSeeds.RunAsync(database, seed, RunStates.Running, DateTimeOffset.UtcNow);
            var scheduler = Scheduler(TimeProvider.System);

            await scheduler.TickAsync(Ct);
            (await SetJobsAsync(seed)).ShouldBeEmpty("the first sync is still running");

            await Sql.ExecuteAsync(database.Connection(null), """UPDATE content."Runs" SET "State" = 'succeeded' WHERE "Id" = $1""", run);
            await EvalSeeds.SetModelsAsync(database, null);
            await scheduler.TickAsync(Ct);
            (await SetJobsAsync(seed)).ShouldBeEmpty("without Settings.EvaluationModel the job would refuse");

            await EvalSeeds.SetModelsAsync(database, Model);
            await scheduler.TickAsync(Ct);
            await scheduler.TickAsync(Ct);
            var jobs = await SetJobsAsync(seed);
            jobs.Select(j => (j.Kind, j.State)).ShouldBe([(EvalKinds.GenerateSet, JobStates.Queued)], "one job, the second tick enqueues nothing");
            ContractJson.Deserialize<EvalGenerateSetPayload>(System.Text.Json.JsonDocument.Parse(jobs[0].Payload).RootElement).CollectionId.ShouldBe(seed.CollectionId);
            (jobs[0].DepartmentId, jobs[0].Classification).ShouldBe((seed.DepartmentId, seed.Classification));

            await MarkAsync(seed, JobStates.Failed);
            await scheduler.TickAsync(Ct);
            (await SetJobsAsync(seed)).Count.ShouldBe(1, "a generate-set that failed is not repeated while its job row is kept");
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task The_weekly_top_up_is_enqueued_once_per_week()
    {
        var seed = await EngineKit.SeedUrlListAsync(database, []);
        await EngineKit.PauseAsync(database, seed);
        await EvalSeeds.DocumentAsync(database, seed, "Ett " + EvalSeeds.Tag(), [new($"Text {Guid.NewGuid()}")]);
        await GeneratedLayerAsync(seed, questions: 2, roundDocuments: 1);
        var weekly = EvalScheduler.LatestWeeklyOccurrence(DateTimeOffset.UtcNow).ShouldNotBeNull();
        var clock = new FakeTimeProvider(weekly.AddDays(2));
        var scheduler = Scheduler(clock);

        await scheduler.TickAsync(Ct);
        await scheduler.TickAsync(Ct);
        (await SetJobsAsync(seed)).Select(j => (j.Kind, j.State)).ShouldBe([(EvalKinds.TopUpSet, JobStates.Queued)]);

        await MarkAsync(seed, JobStates.Succeeded);
        await scheduler.TickAsync(Ct);
        (await SetJobsAsync(seed)).Count.ShouldBe(1, "this week's top-up ran");

        clock.Advance(TimeSpan.FromDays(7));
        EvalScheduler.LatestWeeklyOccurrence(clock.GetUtcNow()).ShouldNotBeNull().ShouldBeGreaterThan(DateTimeOffset.UtcNow, "next week's check");
        await scheduler.TickAsync(Ct);
        await scheduler.TickAsync(Ct);
        (await SetJobsAsync(seed)).Select(j => (j.Kind, j.State)).ShouldBe([(EvalKinds.TopUpSet, JobStates.Succeeded), (EvalKinds.TopUpSet, JobStates.Queued)]);
    }

    [Theory]
    [InlineData(4, 5, true)]
    [InlineData(10, 11, false)]
    public async Task A_top_up_follows_when_the_document_count_grew_by_25_percent_since_the_last_round(int before, int now, bool topUp)
    {
        var seed = await EngineKit.SeedUrlListAsync(database, []);
        await EngineKit.PauseAsync(database, seed);
        var tag = EvalSeeds.Tag();
        for (var i = 0; i < now; i++)
        {
            await EvalSeeds.DocumentAsync(database, seed, $"Dokument {i} {tag}", [new($"Text {Guid.NewGuid()}")]);
        }

        await GeneratedLayerAsync(seed, questions: 3, roundDocuments: before);
        // This week's check ran an hour after the occurrence; the scheduler looks three days later.
        var weekly = EvalScheduler.LatestWeeklyOccurrence(DateTimeOffset.UtcNow).ShouldNotBeNull();
        await PastSetJobAsync(seed, weekly.AddHours(1));
        var scheduler = Scheduler(new FakeTimeProvider(weekly.AddDays(3)));

        await scheduler.TickAsync(Ct);

        var jobs = (await SetJobsAsync(seed)).Select(j => (j.Kind, j.State)).ToList();
        jobs.ShouldBe(topUp
            ? [(EvalKinds.TopUpSet, JobStates.Succeeded), (EvalKinds.TopUpSet, JobStates.Queued)]
            : [(EvalKinds.TopUpSet, JobStates.Succeeded)], $"D {before} → {now}");
    }

    private EvalScheduler Scheduler(TimeProvider clock) =>
        new(database.Database(RagDatabase.Worker), database.Queue(RagDatabase.Worker), Options.Create(new WorkerOptions()), clock, NullLogger<EvalScheduler>.Instance);

    /// <summary>A set with <paramref name="questions"/> active generated questions of round 1, written when <c>D</c> was <paramref name="roundDocuments"/>.</summary>
    private async Task GeneratedLayerAsync(SourceSeed seed, int questions, int roundDocuments)
    {
        var setId = Guid.CreateVersion7();
        await Sql.ExecuteAsync(database.Connection(null),
            """INSERT INTO content."EvaluationSets" ("Id", "CollectionId", "Classification") VALUES ($1, $2, $3)""", setId, seed.CollectionId, seed.Classification);
        for (var i = 0; i < questions; i++)
        {
            await Sql.ExecuteAsync(database.Connection(null), """
                INSERT INTO content."EvaluationQuestions" ("Id", "SetId", "Classification", "Layer", "Question", "ExpectedDocumentIds", "Origin", "State")
                VALUES ($1, $2, $3, 'generated', $4, $5, $6::jsonb, 'active')
                """, Guid.CreateVersion7(), setId, seed.Classification, $"Fråga {i} {Guid.NewGuid()}?", new[] { Guid.NewGuid() },
                $$"""{"round": 1, "documents": {{roundDocuments}}}""");
        }
    }

    /// <summary>A finished <c>top-up-set</c> of the collection enqueued at <paramref name="enqueuedAt"/>.</summary>
    private async Task PastSetJobAsync(SourceSeed seed, DateTimeOffset enqueuedAt)
    {
        var job = new JobRequest(JobClasses.Eval, seed.DepartmentId, seed.Classification, ContractJson.Serialize(new EvalTopUpSetPayload { CollectionId = seed.CollectionId }))
        {
            DedupKey = JobDedupKeys.EvalGenerate(seed.CollectionId),
        };
        (await database.Queue(RagDatabase.Admin).EnqueueAsync([job], null, Ct)).ShouldContain(job.Id);
        await Sql.ExecuteAsync(database.Connection(null), """UPDATE content."Jobs" SET "State" = 'succeeded', "EnqueuedAt" = $2 WHERE "Id" = $1""", job.Id,
            enqueuedAt.ToUniversalTime());
    }

    /// <summary>Ends the collection's open set job in <paramref name="state"/>.</summary>
    private Task MarkAsync(SourceSeed seed, string state) =>
        Sql.ExecuteAsync(database.Connection(null), """UPDATE content."Jobs" SET "State" = $2 WHERE "DedupKey" = $1 AND "State" IN ('queued', 'running')""",
            JobDedupKeys.EvalGenerate(seed.CollectionId), state);

    /// <summary>The collection's set jobs (dedup <c>eval-generate:{collectionId}</c>), oldest first.</summary>
    private async Task<List<SetJob>> SetJobsAsync(SourceSeed seed)
    {
        await using var connection = new NpgsqlConnection(database.Connection(null));
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            SELECT "Payload" ->> 'kind', "State", "Payload"::text, "DepartmentId", "Classification"
            FROM content."Jobs" WHERE "Class" = 'eval' AND "DedupKey" = $1 ORDER BY "EnqueuedAt", "Id"
            """, connection) { Parameters = { new NpgsqlParameter { Value = JobDedupKeys.EvalGenerate(seed.CollectionId) } } };
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var jobs = new List<SetJob>();
        while (await reader.ReadAsync(Ct))
        {
            jobs.Add(new SetJob(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetGuid(3), reader.GetString(4)));
        }

        return jobs;
    }

    private sealed record SetJob(string Kind, string State, string Payload, Guid DepartmentId, string Classification);
}

/// <summary>
/// The trigger rule of <see cref="EvalSetTriggers.Due"/> at its edges: first syncs, the weekly occurrence, the 25 % growth
/// of <c>D</c> since the last round, the layer's target and the growth cooldown.
/// </summary>
public sealed class EvalSetTriggerRuleTests
{
    private static readonly DateTimeOffset Weekly = new(2026, 11, 1, 3, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = Weekly.AddDays(3);

    private static EvalSetCandidate Collection(bool layer = true, int? roundDocuments = 100, int active = 10, int documents = 100, bool synced = true,
        DateTimeOffset? lastEnqueued = null) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), Classification.Internal, layer, roundDocuments, active, documents, synced, lastEnqueued ?? Weekly.AddHours(1));

    [Fact]
    public void Generate_set_follows_the_first_finished_syncs_once()
    {
        EvalSetTriggers.Due(Collection(layer: false, synced: false) with { LastEnqueuedAt = null }, Now, Weekly, 100).ShouldBe(EvalSetTrigger.None);
        EvalSetTriggers.Due(Collection(layer: false, documents: 0) with { LastEnqueuedAt = null }, Now, Weekly, 100).ShouldBe(EvalSetTrigger.None);
        EvalSetTriggers.Due(Collection(layer: false) with { LastEnqueuedAt = null }, Now, Weekly, 100).ShouldBe(EvalSetTrigger.FirstSyncs);
        EvalSetTriggers.Due(Collection(layer: false), Now, Weekly, 100).ShouldBe(EvalSetTrigger.None, "a set job of the collection exists");
    }

    [Fact]
    public void The_weekly_top_up_is_due_when_no_set_job_was_enqueued_since_the_occurrence()
    {
        EvalSetTriggers.Due(Collection() with { LastEnqueuedAt = null }, Now, Weekly, 100).ShouldBe(EvalSetTrigger.Weekly);
        EvalSetTriggers.Due(Collection(lastEnqueued: Weekly.AddMinutes(-1)), Now, Weekly, 100).ShouldBe(EvalSetTrigger.Weekly);
        EvalSetTriggers.Due(Collection(lastEnqueued: Weekly), Now, Weekly, 100).ShouldBe(EvalSetTrigger.None);
    }

    [Theory]
    [InlineData(100, 125, EvalSetTrigger.Growth)]
    [InlineData(100, 124, EvalSetTrigger.None)]
    [InlineData(4, 5, EvalSetTrigger.Growth)]
    [InlineData(10, 11, EvalSetTrigger.None)]
    [InlineData(0, 50, EvalSetTrigger.None)]
    public void A_growth_top_up_needs_25_percent_more_documents_than_the_last_round(int before, int now, EvalSetTrigger expected)
    {
        EvalSetTriggers.Due(Collection(roundDocuments: before, active: 3, documents: now), Now, Weekly, 100).ShouldBe(expected);
        EvalSetTriggers.Due(Collection(roundDocuments: null, active: 3, documents: now), Now, Weekly, 100).ShouldBe(EvalSetTrigger.None, "no D recorded");
    }

    [Fact]
    public void A_growth_top_up_waits_for_a_layer_below_target_and_the_cooldown()
    {
        EvalSetTriggers.Due(Collection(roundDocuments: 100, active: 100, documents: 200), Now, Weekly, 100).ShouldBe(EvalSetTrigger.None, "the layer is at 100");
        EvalSetTriggers.Due(Collection(roundDocuments: 100, active: 99, documents: 200), Now, Weekly, 100).ShouldBe(EvalSetTrigger.Growth);
        EvalSetTriggers.Due(Collection(roundDocuments: 100, active: 10, documents: 200, lastEnqueued: Now.AddHours(-12)), Now, Weekly, 100)
            .ShouldBe(EvalSetTrigger.None, "a set job less than a day ago");
    }
}
