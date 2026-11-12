extern alias fakegateway;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Infrastructure;
using Ume.Rag.Pipelines.Json;
using Ume.Rag.Pipelines.Processing;
using Ume.Rag.TestKit;
using Ume.Rag.Worker.Engine;
using Ume.Rag.Worker.Evaluation;
using Ume.Rag.Worker.Evaluation.Generation;
using Ume.Rag.Worker.Jobs;
using Ume.Rag.Worker.Processing;
using Ume.Rag.Worker.Processing.Chunking;
using Ume.Rag.Worker.Processing.Embedding;
using Ume.Rag.Worker.Processing.Persistence;
using Ume.Rag.Worker.Tests.CommandJobs;
using Ume.Rag.Worker.Tests.Engine;
using Ume.Rag.Worker.Tests.Processing;
using FakeAccess = fakegateway::Ume.Rag.FakeGateway.FakeAccess;
using FakeChatScripts = fakegateway::Ume.Rag.FakeGateway.FakeChatScripts;
using FakeContent = fakegateway::Ume.Rag.FakeGateway.FakeContent;

namespace Ume.Rag.Worker.Tests.Evaluation;

/// <summary>One chat call the worker sent to the eval models: its step, model, whether it set a temperature, output format, ceiling and key.</summary>
internal sealed record EvalCall(string Step, string Model, bool HasTemperature, string? ResponseFormat, string Ceiling, string Key, string User);

/// <summary>
/// The FakeGateway in process, one per test (its chat scripts cycle per step and process, so every test starts at the
/// first answer). <see cref="Script"/> sets the fake's own <c>FakeGateway:ChatScripts:&lt;step&gt;</c> (#221): the fake
/// answers <c>eval-generate</c> and <c>eval-judge</c> from it, or from its built-in defaults without a script.
/// <see cref="RecordingHandler"/> records the chat calls the worker sends (<see cref="Calls"/>) and never answers them.
/// </summary>
public sealed class EvalGatewayFactory : WebApplicationFactory<fakegateway::Program>
{
    private readonly ScriptSource _scripts = new();

    internal ConcurrentQueue<EvalCall> Calls { get; } = new();

    /// <summary>
    /// Replaces the step's script: one answer is the answer to every call, several are played in call order and cycle (the
    /// cycle goes on across scripts of one test); none removes the script.
    /// </summary>
    public void Script(string step, params string[] answers) => _scripts.Set(step, answers);

    /// <summary>A handler into the fake that records each chat completion call on its way.</summary>
    public HttpMessageHandler RecordingHandler() => new CallRecorder(Calls) { InnerHandler = Server.CreateHandler() };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration(config =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FakeGateway:Keys:OnPrem"] = FakeGatewayFactory.OnPremKey,
                ["FakeGateway:Keys:Eu"] = FakeGatewayFactory.EuKey,
                ["FakeGateway:Keys:External"] = "fake-external-key-0123456789",
                ["FakeGateway:CredentialKeys:worker:Key"] = FakeGatewayFactory.WorkerCredentialKey,
                ["FakeGateway:CredentialKeys:worker:MaxResidency"] = "Eu",
            });
            config.AddInMemoryCollection(TestGatewayKeys.Settings);
            config.Add(_scripts);
        });
    }

    /// <summary>The scripts as a configuration source of the fake, changeable while it runs.</summary>
    private sealed class ScriptSource : ConfigurationProvider, IConfigurationSource
    {
        public IConfigurationProvider Build(IConfigurationBuilder builder) => this;

        public void Set(string step, string[] answers)
        {
            var key = $"{FakeChatScripts.SectionName}:{step}";
            foreach (var old in Data.Keys.Where(k => k.Equals(key, StringComparison.OrdinalIgnoreCase) || k.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                Data.Remove(old);
            }

            if (answers.Length == 1)
            {
                Data[key] = answers[0];
            }
            else
            {
                for (var i = 0; i < answers.Length; i++)
                {
                    Data[$"{key}:{i}"] = answers[i];
                }
            }

            OnReload();
        }
    }

    private sealed class CallRecorder(ConcurrentQueue<EvalCall> calls) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/chat/completions" && request.Content is not null)
            {
                var body = JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken)) as JsonObject ?? [];
                calls.Enqueue(new EvalCall(Header(request, EmbeddingClient.StepHeader), body["model"]?.GetValue<string>() ?? string.Empty,
                    body.ContainsKey("temperature"), body["response_format"]?["type"]?.GetValue<string>(), Header(request, FakeAccess.MaxResidencyHeader),
                    request.Headers.Authorization?.Parameter ?? string.Empty, FakeContent.LastUserText(body["messages"])));
            }

            return await base.SendAsync(request, cancellationToken);
        }

        private static string Header(HttpRequestMessage request, string name) =>
            request.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : string.Empty;
    }
}

/// <summary>
/// The set generator as <c>AddRagEvalSets</c> registers it, as <c>ume_rag_worker</c>, with the embedding client and both
/// gateway clients on <see cref="EvalGatewayFactory"/>. Jobs are enqueued as the admin and leased as this instance, as the
/// dispatcher would, and run directly.
/// </summary>
internal sealed class EvalRig : IAsyncDisposable
{
    private readonly IHost _host;

    private EvalRig(IHost host, CapturingLoggerProvider logs)
    {
        _host = host;
        Logs = logs;
    }

    public CapturingLoggerProvider Logs { get; }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static EvalRig Start(RagDatabaseFixture database, EvalGatewayFactory gateway, bool gatewayKey = true, string? maxResidency = null)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        var settings = new Dictionary<string, string?>
        {
            ["Gateway:BaseUrl"] = "http://fake-gateway",
            ["Logging:LogLevel:Default"] = "Debug",
        };
        if (gatewayKey)
        {
            settings["Gateway:Key"] = FakeGatewayFactory.WorkerCredentialKey;
        }

        if (maxResidency is not null)
        {
            settings["Gateway:MaxResidency"] = maxResidency;
        }

        builder.Configuration.AddInMemoryCollection(settings);
        var logs = new CapturingLoggerProvider();
        builder.Logging.ClearProviders().AddProvider(logs);
        builder.Services.AddSingleton(database.Database(RagDatabase.Worker));
        builder.Services.AddSingleton<IJobQueue>(database.Queue(RagDatabase.Worker));
        builder.Services.AddSingleton<WorkerIdentity>();
        builder.Services.AddSingleton<IEmbeddingProfileStore, PostgresEmbeddingProfileStore>();
        builder.Services.AddSingleton<IGatewayModelPrices, PostgresGatewayModelPrices>();
        builder.CapGatewayCalls(Residency.Eu); // as AddRagWorker does: the worker's maximum (identity design §4.7)
        builder.AddGatewayClient(EmbeddingClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => gateway.RecordingHandler());
        builder.Services.AddSingleton<EmbeddingClient>();
        builder.AddRagEvalSets();
        builder.Services.AddHttpClient(EvalModelClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => gateway.RecordingHandler());
        builder.Services.AddHttpClient(EvalModelClient.CatalogueClientName).ConfigurePrimaryHttpMessageHandler(() => gateway.RecordingHandler());
        return new EvalRig(builder.Build(), logs);
    }

    public WorkerIdentity Identity => _host.Services.GetRequiredService<WorkerIdentity>();

    /// <summary>Enqueues the payload as the admin does (class <c>eval</c>, the collection's level) and leases it.</summary>
    public async Task<LeasedJob> LeaseAsync<T>(RagDatabaseFixture database, T payload, SourceSeed seed)
    {
        var request = new JobRequest(JobClasses.Eval, seed.DepartmentId, seed.Classification, ContractJson.Serialize(payload));
        (await database.Queue(RagDatabase.Admin).EnqueueAsync([request], null, Ct)).ShouldContain(request.Id);
        var leased = await database.Queue(RagDatabase.Worker).DequeueAsync(
            new DequeueRequest(JobClasses.Eval, seed.DepartmentId, Identity.InstanceId, TimeSpan.FromMinutes(5)), Ct);
        leased.ShouldNotBeNull();
        leased.Id.ShouldBe(request.Id);
        return leased;
    }

    public async Task<JobResult> RunAsync(LeasedJob job)
    {
        // As the eval class handler does: the payload's kind, and the handler from the job's scope.
        var kind = JsonNode.Parse(job.Payload)!["kind"]!.GetValue<string>();
        await using var scope = _host.Services.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetService<IEvalSetHandler>();
        handler.ShouldNotBeNull();
        var usage = new JobUsage(scope.ServiceProvider.GetRequiredService<IGatewayModelPrices>(), TimeProvider.System);
        return await handler.ExecuteAsync(new JobContext(job, scope.ServiceProvider, usage), kind, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync();
        }
        else
        {
            _host.Dispose();
        }
    }
}

/// <summary>A chunk to seed: its body, document type, content kind and large-table flag (#129).</summary>
internal sealed record SeedChunk(string Text, string DocumentType = "page", string ContentKind = ChunkContentKinds.Text, bool LargeTable = false);

/// <summary>A seeded document: its live set and chunk ids in order.</summary>
internal sealed record SeededDocument(Guid DocumentId, Guid SetId, IReadOnlyList<Guid> ChunkIds, byte[] ContentHash);

/// <summary>Content and settings rows for the set tests, written as the superuser; every text carries the test's tag.</summary>
internal static class EvalSeeds
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A word of letters only, unique per test, so questions stay Swedish to the detector and differ between tests.</summary>
    public static string Tag() => "x" + new string([.. Guid.NewGuid().ToString("N")[..10].Select(c => (char)('a' + Convert.ToInt32(c.ToString(), 16)))]);

    /// <summary>A Swedish body of about 90 tokens about <paramref name="topic"/>, unique by <paramref name="unique"/>.</summary>
    public static string Body(string topic, string unique) =>
        $"Umeå kommun ansvarar för {topic} i hela kommunen. Verksamheten följer de regler som fullmäktige har beslutat och informationen " +
        "uppdateras när reglerna ändras. Den som vill veta mer kan läsa vidare på webbplatsen eller kontakta kundtjänst under kontorstid. " +
        $"Handläggningstiden är normalt fyra veckor från att ärendet är komplett. Avgiften bestäms av taxan som gäller för året. Referens {unique}.";

    /// <summary>Sets the evaluation settings; returns what to restore (the row's previous values, or null when there was no row).</summary>
    public static async Task<PlatformSettings?> SetModelsAsync(RagDatabaseFixture database, string? model, string? judgeModel = null, int maxQuestions = 100)
    {
        await using var db = TestPostgres.Context(database.Connection(null));
        var row = await db.Settings.FirstOrDefaultAsync(Ct);
        PlatformSettings? previous = row is null
            ? null
            : new PlatformSettings { EvaluationModel = row.EvaluationModel, EvaluationJudgeModel = row.EvaluationJudgeModel, EvaluationMaxQuestions = row.EvaluationMaxQuestions };
        if (row is null)
        {
            row = new PlatformSettings { UpdatedAt = DateTimeOffset.UtcNow };
            db.Settings.Add(row);
        }

        row.EvaluationModel = model;
        row.EvaluationJudgeModel = judgeModel;
        row.EvaluationMaxQuestions = maxQuestions;
        await db.SaveChangesAsync(Ct);
        return previous;
    }

    /// <summary>Puts back what <see cref="SetModelsAsync"/> returned the first time.</summary>
    public static async Task RestoreModelsAsync(RagDatabaseFixture database, bool hadRow, PlatformSettings? previous)
    {
        if (!hadRow)
        {
            await Sql.ExecuteAsync(database.Connection(null), """DELETE FROM config."Settings" """);
            return;
        }

        await Sql.ExecuteAsync(database.Connection(null), """
            UPDATE config."Settings" SET "EvaluationModel" = $1, "EvaluationJudgeModel" = $2, "EvaluationMaxQuestions" = $3
            """, (object?)previous!.EvaluationModel ?? DBNull.Value, (object?)previous.EvaluationJudgeModel ?? DBNull.Value, previous.EvaluationMaxQuestions);
    }

    /// <summary>A live document of the source with one live set of <paramref name="chunks"/>.</summary>
    public static async Task<SeededDocument> DocumentAsync(RagDatabaseFixture database, SourceSeed seed, string title, IReadOnlyList<SeedChunk> chunks,
        bool piiFlagged = false)
    {
        var document = await CommandSeeds.DocumentAsync(database, seed, $"https://site.test/{Guid.NewGuid():N}");
        await Sql.ExecuteAsync(database.Connection(null), """UPDATE content."Documents" SET "Title" = $2 WHERE "Id" = $1""", document, title);
        return await SetAsync(database, seed, document, chunks, piiFlagged);
    }

    /// <summary>The document's content changes: a new content hash and a new live set of <paramref name="chunks"/>.</summary>
    public static async Task<SeededDocument> ChangeAsync(RagDatabaseFixture database, SourceSeed seed, Guid document, IReadOnlyList<SeedChunk> chunks)
    {
        await Sql.ExecuteAsync(database.Connection(null), """UPDATE content."Documents" SET "ContentHash" = $2 WHERE "Id" = $1""", document,
            RandomNumberGenerator.GetBytes(32));
        return await SetAsync(database, seed, document, chunks, piiFlagged: false);
    }

    /// <summary>Soft-deletes the document as a confirmed deletion does.</summary>
    public static Task DeleteAsync(RagDatabaseFixture database, Guid document) =>
        new IndexWriter(database.Database(RagDatabase.Worker)).SoftDeleteDocumentsAsync([document], DateTimeOffset.UtcNow, Ct);

    /// <summary>The set's generated questions (all states), oldest first.</summary>
    public static async Task<IReadOnlyList<StoredQuestion>> QuestionsAsync(RagDatabaseFixture database, Guid setId)
    {
        await using var connection = new NpgsqlConnection(database.Connection(null));
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            SELECT "Id", "Question", coalesce("ExpectedAnswer", ''), "ExpectedDocumentIds", "SourceContentHash", "SourceChunkId", "Origin"::text, "State",
                   "RetiredAt" IS NOT NULL, "Classification", "CreatedBy"
            FROM content."EvaluationQuestions" WHERE "SetId" = $1 AND "Layer" = 'generated' ORDER BY "Id"
            """, connection) { Parameters = { new NpgsqlParameter { Value = setId } } };
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<StoredQuestion>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(new StoredQuestion(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetFieldValue<Guid[]>(3), reader.GetFieldValue<byte[]>(4),
                reader.GetGuid(5), JsonNode.Parse(reader.GetString(6))!.AsObject(), reader.GetString(7), reader.GetBoolean(8), reader.GetString(9),
                reader.GetString(10)));
        }

        return rows;
    }

    private static async Task<SeededDocument> SetAsync(RagDatabaseFixture database, SourceSeed seed, Guid document, IReadOnlyList<SeedChunk> chunks, bool piiFlagged)
    {
        var writer = new IndexWriter(database.Database(RagDatabase.Worker));
        var hash = await Sql.ScalarAsync<byte[]>(database.Connection(null), """SELECT "ContentHash" FROM content."Documents" WHERE "Id" = $1""", document);
        var set = new ChunkSetRecord(Guid.CreateVersion7(), new ChunkSetKey(document, seed.VersionId, null, EngineKit.ProfileId, hash), seed.Classification,
            seed.CollectionId, RandomNumberGenerator.GetBytes(32))
        {
            ChunkCount = chunks.Count,
            PiiCounts = piiFlagged ? new PiiCounts { Personnummer = 1 } : new PiiCounts(),
            PiiFlagged = piiFlagged,
        };
        var rows = chunks.Select((c, i) => new ChunkRecord(Guid.CreateVersion7(), set.Id, document, seed.CollectionId, seed.Classification, i,
            new ChunkDraft
            {
                Ordinal = i, Prefix = "Avsnitt", HeadingPath = "Avsnitt", Text = c.Text, SearchText = c.Text, OverlapChars = 0,
                TextHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(c.Text))), TokenCount = Bgem3TokenCounter.Shared.Count(c.Text),
                DocumentType = c.DocumentType, ContentKind = c.ContentKind, LargeTable = c.LargeTable,
            }, EngineKit.ProfileId, new float[EngineKit.Dimensions]) { DeletedAt = DateTimeOffset.UtcNow }).ToList();
        await writer.WriteInvisibleAsync(set, rows, Ct);
        await using var connection = new NpgsqlConnection(database.Connection(null));
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await IndexWriter.SwapInAsync(connection, transaction, document, set.Id, ChunkSetState.Live, seed.VersionId, DateTimeOffset.UtcNow, Ct);
        await transaction.CommitAsync(Ct);
        return new SeededDocument(document, set.Id, [.. rows.Select(r => r.Id)], hash);
    }
}

/// <summary>A generated question as stored.</summary>
internal sealed record StoredQuestion(Guid Id, string Question, string ExpectedAnswer, Guid[] ExpectedDocumentIds, byte[] SourceContentHash, Guid SourceChunkId,
    JsonObject Origin, string State, bool Retired, string Classification, string CreatedBy);
