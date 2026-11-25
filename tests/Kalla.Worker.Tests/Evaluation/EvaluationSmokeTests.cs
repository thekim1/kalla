using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Kalla.Domain;
using Kalla.Domain.Entities;
using Kalla.Pipelines.Jobs;
using Kalla.TestKit;
using Kalla.Worker.Connectors.Uploads;
using Kalla.Worker.Tests.Connectors;
using Kalla.Worker.Tests.Engine;
using Kalla.Worker.Tests.Processing;

namespace Kalla.Worker.Tests.Evaluation;

/// <summary>
/// The evaluation smoke of <c>verify.yml</c>'s backend job (knowledge design §16.4, repo-and-deployment §8.2; P1 item 17):
/// the 13 pages of <c>tests/evaluation/smoke</c> ingested through the worker as one committed upload batch of a public
/// upload source (detect, the worker's Markdown parser, chunk, embed on the FakeGateway), then the 30 questions through
/// kalla-api in process reading its snapshot from the database: <c>POST /v1/retrieve</c> recall@10 ≥ 0.8 (recall@1, @3 and
/// @5 reported) and every <c>/v1/chat/completions</c> answer of an assistant over the collection cites a source. The class
/// carries <see cref="Category"/>, so the backend job's test script leaves it out and the job runs it as a process of its own.
/// </summary>
[Collection(RagDatabaseGroup1.Name)]
[Trait(TestCategory.Name, Category)]
public sealed class EvaluationSmokeTests(RagDatabaseFixture database, FakeGatewayFactory gateway) : IClassFixture<FakeGatewayFactory>, IAsyncLifetime
{
    /// <summary>The trait value <c>verify.yml</c> filters on.</summary>
    public const string Category = "EvaluationSmoke";

    private readonly DirectoryInfo _blobs = Directory.CreateTempSubdirectory("kalla-eval-smoke-");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await database.ResetAsync();

    public ValueTask DisposeAsync()
    {
        _blobs.Delete(recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task The_smoke_pages_ingested_by_the_worker_reach_recall_at_10_of_0_8_and_every_answer_is_cited()
    {
        var seed = await EngineKit.SeedUploadAsync(database, Classification.Public);
        await EvaluationSmokeKit.PublishAsync(database, seed.CollectionId);
        var pages = SmokeSet.Documents();
        var slots = pages.ToDictionary(p => p.FileName, _ => Guid.CreateVersion7());
        try
        {
            await using var site = await EngineKit.SiteAsync((_, _) => Task.FromResult(EngineKit.Html(EngineKit.Page("unused"))));
            await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs);
            var run = await UploadKit.CommitAsync(database, _blobs, seed,
                [.. pages.Select(p => (UploadOperation.Add, slots[p.FileName], (string?)p.FileName, (byte[]?)Encoding.UTF8.GetBytes(p.Markdown)))]);

            (await EngineKit.WaitForRunAsync(database, run)).ShouldBe(new RunCounters(RunStates.Succeeded, 13, 0, 0, 0, 0, 0));
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }

        var documents = new Dictionary<Guid, string>();
        foreach (var (file, slot) in slots)
        {
            documents[await Sql.ScalarAsync<Guid>(database.Connection(null), """
                SELECT "Id" FROM content."Documents" WHERE "SourceId" = $1 AND "CanonicalIdentity" = $2 AND "LiveChunkSetId" IS NOT NULL
                """, seed.SourceId, UploadConnector.IdentityOf(slot))] = file;
        }

        documents.Count.ShouldBe(13);
        var slug = await Sql.ScalarAsync<string>(database.Connection(null), """SELECT "Slug" FROM config."Collections" WHERE "Id" = $1""", seed.CollectionId);
        var (_, internalKey) = await EvaluationSmokeKit.AddClientAsync(database, ClientKinds.Internal, Classification.Sensitive);
        var (integration, integrationKey) = await EvaluationSmokeKit.AddClientAsync(database, ClientKinds.Integration, Classification.Internal);
        var assistant = await EvaluationSmokeKit.AddAssistantAsync(database, seed, integration);
        await using var api = EvaluationSmokeKit.ApiHost(database, gateway, _blobs, "knowledge,chat");

        using (var retrieval = await EvaluationSmokeKit.ClientAsync(api, internalKey))
        {
            await RecallAsync(retrieval, slug, documents);
        }

        using var chat = await EvaluationSmokeKit.ClientAsync(api, integrationKey);
        await EveryAnswerCitedAsync(chat, assistant);
    }

    /// <summary>Recall@k over the 30 questions: the share of each question's expected pages among the first k results' documents.</summary>
    private static async Task RecallAsync(HttpClient client, string slug, IReadOnlyDictionary<Guid, string> documents)
    {
        var questions = SmokeSet.Questions();
        var recall = new Dictionary<int, double> { [1] = 0, [3] = 0, [5] = 0, [10] = 0 };
        var misses = new List<string>();
        foreach (var question in questions)
        {
            var ranked = (await EvaluationSmokeKit.RetrieveAsync(client, slug, question.Question)).Select(r => documents.GetValueOrDefault(r.DocumentId)).ToList();
            foreach (var k in recall.Keys)
            {
                recall[k] += (double)question.Expected.Count(ranked.Take(k).Contains) / question.Expected.Count / questions.Count;
            }

            if (!question.Expected.All(ranked.Take(10).Contains))
            {
                misses.Add($"{question.Id} {question.Question}");
            }
        }

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"recall@10 = {recall[10]:F3} (@1 {recall[1]:F3}, @3 {recall[3]:F3}, @5 {recall[5]:F3}); misses at 10: {(misses.Count == 0 ? "none" : string.Join("; ", misses))}");
        recall[10].ShouldBeGreaterThanOrEqualTo(0.8, string.Join("\n", misses));
    }

    /// <summary>Every question asked once of the assistant: each answer carries at least one annotation, and all are <c>url_citation</c>.</summary>
    private static async Task EveryAnswerCitedAsync(HttpClient client, string assistant)
    {
        var uncited = new List<string>();
        foreach (var question in SmokeSet.Questions())
        {
            using var response = await client.PostAsJsonAsync("/v1/chat/completions",
                new { model = assistant, messages = new[] { new { role = "user", content = question.Question } } }, Ct);
            var body = await response.Content.ReadAsStringAsync(Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
            using var json = JsonDocument.Parse(body);
            var message = json.RootElement.GetProperty("choices")[0].GetProperty("message");
            var annotations = message.TryGetProperty("annotations", out var a) ? a.EnumerateArray().ToList() : [];
            if (annotations.Count == 0 || !annotations.All(x => x.GetProperty("type").GetString() == "url_citation"))
            {
                uncited.Add($"{question.Id} {question.Question}: {message.GetProperty("content").GetString()}");
            }
        }

        TestContext.Current.TestOutputHelper?.WriteLine($"cited answers: {SmokeSet.Questions().Count - uncited.Count} of {SmokeSet.Questions().Count}");
        uncited.ShouldBeEmpty();
    }
}
