extern alias fakegateway;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Kalla.Answering.Chat;
using Kalla.Domain;
using Kalla.Domain.Access;
using Kalla.Domain.Entities;
using Kalla.TestKit;
using FakeContent = fakegateway::Kalla.FakeGateway.FakeContent;

namespace Kalla.Api.Tests;

/// <summary>A knowledge database of its own with the smoke collection seeded once (<see cref="SmokeSet"/>).</summary>
public sealed class SmokeFixture : IAsyncLifetime
{
    public KnowledgeFixture Db { get; } = new();

    public SmokeSeed Seed { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await ((IAsyncLifetime)Db).InitializeAsync();
        await using var writer = new ContentFixtureWriter(Db.Connection(RagDatabase.Worker), Db.Connection(RagDatabase.Admin),
            (text, dims) => FakeContent.Vector(text, dims));
        Seed = await SmokeSet.SeedAsync(writer, Db.ProfileId, cancellationToken: TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => ((IAsyncDisposable)Db).DisposeAsync();
}

/// <summary>
/// The evaluation smoke set (P1 item 17; knowledge design §16.4): the 30 questions of <c>tests/evaluation/smoke</c> over
/// its collection, seeded by <see cref="SmokeSet"/> into this class's own knowledge database with FakeGateway
/// embeddings. Retrieval: recall@10 ≥ 0.8 (and recall@3 ≥ 0.8) through <c>POST /v1/retrieve</c>. Chat: every answer cites a source.
/// </summary>
public sealed class SmokeEvaluationTests(SmokeFixture smoke) : IClassFixture<SmokeFixture>
{
    private const string Model = "smoke";
    private static readonly Guid SmokeAssistant = Guid.Parse("01929b6e-0000-7000-8000-00000005e0e2");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly KnowledgeFixture db = smoke.Db;
    private SmokeSeed Seed => smoke.Seed;

    /// <summary>The fixture's snapshot plus the smoke collection and an assistant over it for the integration client.</summary>
    private PolicySnapshot Snapshot()
    {
        var snapshot = db.Snapshot();
        var collection = new CollectionEntry(SmokeSet.CollectionId, "smoke", "Smoke", Guid.Empty, Classification.Public, db.ProfileId, SmokeSet.RetrievalSettings,
            DateTimeOffset.UnixEpoch, false);
        var assistant = new AssistantEntry(SmokeAssistant, Model, "Smoke", Classification.Internal, HistoryModes.None, AssistantStates.Active, Guid.Empty, null,
            new PublishedVersionEntry(Guid.NewGuid(), 1, "fors/chat-eu", [CollectionLink.Whole(SmokeSet.CollectionId)]),
            [new AudienceEntry(AssistantAudienceKinds.Client, db.IntegrationClient, null)]);
        return snapshot with
        {
            Collections = new Dictionary<Guid, CollectionEntry>(snapshot.Collections) { [collection.Id] = collection },
            SourcesByCollection = new Dictionary<Guid, IReadOnlyList<Guid>>(snapshot.SourcesByCollection) { [collection.Id] = [SmokeSet.SourceId] },
            Assistants = new Dictionary<Guid, AssistantEntry>(snapshot.Assistants) { [assistant.Id] = assistant },
        };
    }

    private WebApplicationFactory<Program> Host() => db.ApiHost(Snapshot()).WithWebHostBuilder(web =>
    {
        web.UseSetting("Rag:Roles", "knowledge,chat");
        web.UseSetting("Chat:MessagesPerMinutePerUser", "0"); // 30 questions from one caller in a few seconds
        // 60 query embeddings in a row on a loaded CI runner: the 1.5 s attempt limit degraded one to keyword-only (#214's run).
        web.UseSetting("Retrieval:AttemptTimeout", "00:00:10");
        web.UseSetting("Retrieval:TotalTimeout", "00:00:20");
        web.ConfigureTestServices(services =>
            services.UseGatewayCredentialCeiling().AddHttpClient(GatewayChatClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => db.Gateway.Server.CreateHandler()));
    });

    private static HttpClient Client(WebApplicationFactory<Program> api, string key)
    {
        var client = api.CreateHttpsClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }

    [Fact]
    public void The_smoke_set_has_30_questions_over_its_documents()
    {
        var questions = SmokeSet.Questions();
        questions.Count.ShouldBe(30);
        questions.Select(q => q.Id).ShouldBeUnique();
        questions.SelectMany(q => q.Expected).ShouldAllBe(file => Seed.Documents.ContainsKey(file));
        Seed.Documents.Keys.ShouldAllBe(file => questions.Any(q => q.Expected.Contains(file))); // every page is asked about
    }

    [Fact]
    public async Task Recall_at_10_through_the_retrieve_endpoint_is_at_least_0_8()
    {
        await using var api = Host();
        using var client = Client(api, db.InternalKey);
        var misses = new List<string>();
        var recall = 0.0;
        var atK = new Dictionary<int, double> { [1] = 0, [3] = 0, [5] = 0 };
        var worst = 0.0;
        var questions = SmokeSet.Questions();
        foreach (var question in questions)
        {
            using var response = await client.PostAsJsonAsync("/v1/retrieve", new { query = question.Question, collections = new[] { "smoke" }, top_k = 10, expand = "none" }, Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            json.RootElement.GetProperty("degraded").ValueKind.ShouldBe(JsonValueKind.Null); // hybrid, not a keyword-only fallback
            worst = Math.Max(worst, json.RootElement.GetProperty("coverage").GetProperty("best_distance").GetDouble());
            var ranked = json.RootElement.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("document").GetProperty("id").GetGuid()).ToList();
            var found = ranked.Take(10).ToHashSet();
            var hit = question.Expected.Count(file => found.Contains(Seed.Documents[file]));
            foreach (var k in atK.Keys)
            {
                atK[k] += (double)question.Expected.Count(file => ranked.Take(k).Contains(Seed.Documents[file])) / question.Expected.Count / questions.Count;
            }

            recall += (double)hit / question.Expected.Count;
            if (hit < question.Expected.Count)
            {
                misses.Add($"{question.Id} {question.Question}");
            }
        }

        recall /= questions.Count;
        TestContext.Current.TestOutputHelper?.WriteLine($"recall@10 = {recall:F3} (@1 {atK[1]:F3}, @3 {atK[3]:F3}, @5 {atK[5]:F3}); largest best distance {worst:F3}; misses: {(misses.Count == 0 ? "none" : string.Join("; ", misses))}");
        recall.ShouldBeGreaterThanOrEqualTo(0.8, string.Join("\n", misses));
        atK[3].ShouldBeGreaterThanOrEqualTo(0.8); // 13 pages: at 10 most of the collection comes back, so also check the top 3
    }

    [Fact]
    public async Task Every_answer_cites_a_source()
    {
        await using var api = Host();
        using var client = Client(api, db.IntegrationKey);
        var uncited = new List<string>();
        foreach (var question in SmokeSet.Questions())
        {
            using var response = await client.PostAsJsonAsync("/v1/chat/completions",
                new { model = Model, messages = new[] { new { role = "user", content = question.Question } } }, Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            var message = json.RootElement.GetProperty("choices")[0].GetProperty("message");
            var annotations = message.TryGetProperty("annotations", out var a) ? a.EnumerateArray().ToList() : [];
            if (annotations.Count == 0 || !annotations.All(x => x.GetProperty("type").GetString() == "url_citation"))
            {
                uncited.Add($"{question.Id} {question.Question}: {message.GetProperty("content").GetString()}");
            }
        }

        uncited.ShouldBeEmpty();
    }
}
