extern alias fakegateway;

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Pipelines.Json;
using Ume.Rag.TestKit;
using Ume.Rag.Worker.Processing.Embedding;
using Ume.Rag.Worker.Tests.Engine;
using Ume.Rag.Worker.Tests.Processing;

namespace Ume.Rag.Worker.Tests.Evaluation;

/// <summary>
/// A fixture collection for the evaluation tests: three live documents (one chunk each, texts unique to the test), a set
/// whose <c>human</c> layer has a compound-only question, a plain keyword question, an answerable question whose
/// document is not in the collection and two unanswerable questions, and one <c>generated</c> question.
/// </summary>
internal sealed record EvalFixture(SourceSeed Seed, Guid SetId, Guid Permit, Guid Waste, Guid Library, EvalQuestions Questions)
{
    public Guid CollectionId => Seed.CollectionId;
}

/// <param name="CompoundOnly">Matches its document's keyword side only through the decompounder ("tillstånd" in "parkeringstillstånd").</param>
/// <param name="Keyword">Matches its document's keyword side without the decompounder ("avfallstaxan").</param>
/// <param name="Unreachable">Answerable, but its expected document is not in the collection: never found.</param>
/// <param name="Unanswerable">No expected documents: the expected outcome is "not found".</param>
/// <param name="Generated">An answerable question of the generated layer.</param>
internal sealed record EvalQuestions(Guid CompoundOnly, Guid Keyword, Guid Unreachable, IReadOnlyList<Guid> Unanswerable, Guid Generated);

internal static class EvalKit
{
    public const string CompoundOnlyQuestion = "Hur får man tillstånd?";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static float[] Embed(string text, int dimensions) => fakegateway::Ume.Rag.FakeGateway.FakeContent.Vector(text, dimensions);

    public static async Task<EvalFixture> SeedAsync(RagDatabaseFixture database)
    {
        var seed = await EngineKit.SeedUrlListAsync(database, [new Uri("https://site.test/p/eval")]);
        await EngineKit.PauseAsync(database, seed);
        await Sql.ExecuteAsync(database.Connection(RagDatabase.Worker), "SELECT content.rag_ensure_profile_index($1, $2)", EngineKit.ProfileId, EngineKit.Dimensions);

        // Texts of their own: the embedding cache and the database are shared by every test.
        var tag = Guid.NewGuid().ToString("N")[..12];
        await using var writer = new ContentFixtureWriter(database.Connection(RagDatabase.Worker), database.Connection(RagDatabase.Admin), Embed);
        async Task<Guid> DocumentAsync(string name, string text) => (await writer.AddDocumentAsync(new FixtureDocumentSpec
        {
            CollectionId = seed.CollectionId,
            EmbeddingProfileId = EngineKit.ProfileId,
            SourceId = seed.SourceId,
            Classification = seed.Classification,
            Url = $"https://site.test/{name}/{tag}",
            Title = name,
        }, [new FixtureChunk(text)], Ct)).Id;

        var permit = await DocumentAsync("parkering", $"Parkeringstillstånd för rörelsehindrade beviljas av kommunen. Ärende {tag}.");
        var waste = await DocumentAsync("avfall", $"Avfallstaxan för 2027 är beslutad av fullmäktige. Ärende {tag}.");
        var library = await DocumentAsync("bibliotek", $"Stadsbiblioteket lånar ut böcker och tidskrifter. Ärende {tag}.");

        var setId = Guid.CreateVersion7();
        await Sql.ExecuteAsync(database.Connection(null),
            """INSERT INTO content."EvaluationSets" ("Id", "CollectionId", "Classification") VALUES ($1, $2, $3)""", setId, seed.CollectionId, seed.Classification);
        async Task<Guid> QuestionAsync(string layer, string question, params Guid[] expected)
        {
            var id = Guid.CreateVersion7();
            await Sql.ExecuteAsync(database.Connection(null), """
                INSERT INTO content."EvaluationQuestions" ("Id", "SetId", "Classification", "Layer", "Question", "ExpectedDocumentIds", "State")
                VALUES ($1, $2, $3, $4, $5, $6, 'active')
                """, id, setId, seed.Classification, layer, question, expected);
            return id;
        }

        // The generated question first: the run still puts the human layer first.
        var generated = await QuestionAsync(EvaluationLayers.Generated, "Beviljas parkeringstillstånd av kommunen?", permit);
        var compound = await QuestionAsync(EvaluationLayers.Human, CompoundOnlyQuestion, permit);
        var keyword = await QuestionAsync(EvaluationLayers.Human, "Vad säger avfallstaxan?", waste);
        var unreachable = await QuestionAsync(EvaluationLayers.Human, "Var finns stadsbiblioteket?", Guid.NewGuid());
        var off1 = await QuestionAsync(EvaluationLayers.Human, "Vilken färg har stadshusets tak?");
        var off2 = await QuestionAsync(EvaluationLayers.Human, "Vem vann melodifestivalen i fjol?");
        return new EvalFixture(seed, setId, permit, waste, library, new EvalQuestions(compound, keyword, unreachable, [off1, off2], generated));
    }

    /// <summary>A worker running only the <c>eval</c> class, embedding and reranking through the FakeGateway in process.</summary>
    public static Task<WorkerHost> StartWorkerAsync(RagDatabaseFixture database, FakeGatewayFactory gateway, DirectoryInfo blobs,
        Action<IServiceCollection>? services = null) =>
        WorkerHost.StartAsync(database, s =>
        {
            s.AddHttpClient(EmbeddingClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => gateway.Server.CreateHandler());
            s.AddHttpClient(Ume.Rag.Retrieval.ChatModelReranker.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => gateway.Server.CreateHandler());
            services?.Invoke(s);
        }, new Dictionary<string, string?>
        {
            ["Gateway:BaseUrl"] = "http://fake-gateway",
            ["Gateway:Keys:OnPrem"] = FakeGatewayFactory.OnPremKey,
            ["Gateway:Keys:Eu"] = FakeGatewayFactory.EuKey,
            ["BlobStore:Root"] = blobs.FullName,
            ["Worker:Classes"] = JobClasses.Eval,
        }, engine: true);

    /// <summary>Enqueues an <c>eval</c> job as the admin does and waits until it ends; returns its row.</summary>
    public static Task<JobRow> RunJobAsync<T>(RagDatabaseFixture database, EvalFixture fixture, T payload, string dedupKey, string state = JobStates.Succeeded) =>
        RunJobAsync(database, fixture.Seed, payload, dedupKey, state);

    /// <summary>Enqueues an <c>eval</c> job for the seed's collection as the admin does and waits until it ends; returns its row.</summary>
    public static async Task<JobRow> RunJobAsync<T>(RagDatabaseFixture database, SourceSeed seed, T payload, string dedupKey, string state = JobStates.Succeeded)
    {
        var job = new JobRequest(JobClasses.Eval, seed.DepartmentId, seed.Classification, ContractJson.Serialize(payload)) { DedupKey = dedupKey };
        (await database.Queue(RagDatabase.Admin).EnqueueAsync([job], null, Ct)).ShouldContain(job.Id);
        await Commands.WaitUntilAsync(async () => (await Sql.JobAsync(database.Connection(null), job.Id)).State is JobStates.Succeeded or JobStates.Failed or JobStates.Dead,
            TimeSpan.FromSeconds(60));
        var row = await Sql.JobAsync(database.Connection(null), job.Id);
        row.State.ShouldBe(state, $"job ended {row.State} with {row.LastError}");
        return row;
    }

    public static T Result<T>(JobRow row) => ContractJson.Deserialize<T>(JsonDocument.Parse(row.Result!).RootElement);

    /// <summary>The run's results by question.</summary>
    public static async Task<IReadOnlyDictionary<Guid, ResultRow>> ResultsAsync(RagDatabaseFixture database, Guid runId)
    {
        await using var connection = new NpgsqlConnection(database.Connection(null));
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            SELECT "QuestionId", "ExpectedRank", "Hit", "Coverage", "BestDistance", "BestKeywordScore" FROM content."EvaluationResults" WHERE "RunId" = $1
            """, connection) { Parameters = { new NpgsqlParameter { Value = runId } } };
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new Dictionary<Guid, ResultRow>();
        while (await reader.ReadAsync(Ct))
        {
            rows[reader.GetGuid(0)] = new ResultRow(
                reader.IsDBNull(1) ? null : reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetBoolean(2), reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetFloat(4), reader.IsDBNull(5) ? null : reader.GetFloat(5));
        }

        return rows;
    }

    public static async Task<RunRow> RunAsync(RagDatabaseFixture database, Guid runId)
    {
        await using var connection = new NpgsqlConnection(database.Connection(null));
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            SELECT "Kind", "Variant", "QuestionCount", "Found", "RecallAtK", "Mrr", "ComparedRunId", "Wins", "Losses", "SubjectId", "SubjectVersion"
            FROM content."EvaluationRuns" WHERE "Id" = $1
            """, connection) { Parameters = { new NpgsqlParameter { Value = runId } } };
        await using var reader = await command.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).ShouldBeTrue($"run {runId} was not written");
        return new RunRow(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3), reader.IsDBNull(4) ? null : reader.GetDecimal(4),
            reader.IsDBNull(5) ? null : reader.GetDecimal(5), reader.IsDBNull(6) ? null : reader.GetGuid(6), reader.IsDBNull(7) ? null : reader.GetInt32(7),
            reader.IsDBNull(8) ? null : reader.GetInt32(8), reader.IsDBNull(9) ? null : reader.GetGuid(9), reader.IsDBNull(10) ? null : reader.GetInt32(10));
    }

    /// <summary>An earlier finished run of the set (fake history), with optional results by question (rank; null = not found).</summary>
    public static async Task<Guid> PastRunAsync(RagDatabaseFixture database, EvalFixture fixture, string kind, DateTimeOffset startedAt, decimal recall,
        IReadOnlyDictionary<Guid, int?>? ranks = null)
    {
        var id = Guid.CreateVersion7();
        await Sql.ExecuteAsync(database.Connection(null), """
            INSERT INTO content."EvaluationRuns" ("Id", "SetId", "Classification", "Kind", "QuestionCount", "K", "Found", "RecallAtK", "Mrr", "Reranked", "Variant",
                                                 "StartedAt", "FinishedAt")
            VALUES ($1, $2, $3, $4, 100, 10, $5, $6, $6, false, 'hybrid', $7, $7 + interval '1 minute')
            """, id, fixture.SetId, fixture.Seed.Classification, kind, (int)(recall * 100), recall, startedAt);
        foreach (var (question, rank) in ranks ?? new Dictionary<Guid, int?>())
        {
            await Sql.ExecuteAsync(database.Connection(null), """
                INSERT INTO content."EvaluationResults" ("RunId", "QuestionId", "Classification", "ExpectedRank", "Hit") VALUES ($1, $2, $3, $4, $5)
                """, id, question, fixture.Seed.Classification, rank is { } r ? r : (object)DBNull.Value, rank is not null);
        }

        return id;
    }

    public static Task PublishAsync(RagDatabaseFixture database, EvalFixture fixture, bool published = true) =>
        PublishAsync(database, fixture.CollectionId, published);

    public static Task PublishAsync(RagDatabaseFixture database, Guid collectionId, bool published = true) =>
        Sql.ExecuteAsync(database.Connection(null), """UPDATE config."Collections" SET "PublishedAt" = CASE WHEN $2 THEN now() END WHERE "Id" = $1""",
            collectionId, published);
}

internal sealed record ResultRow(int? ExpectedRank, bool? Hit, string? Coverage, float? BestDistance, float? BestKeywordScore);

internal sealed record RunRow(string Kind, string Variant, int QuestionCount, int Found, decimal? RecallAtK, decimal? Mrr, Guid? ComparedRunId, int? Wins,
    int? Losses, Guid? SubjectId, int? SubjectVersion);
