using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Retrieval;
using static Ume.Rag.Retrieval.Tests.Make;

namespace Ume.Rag.Retrieval.Tests;

/// <summary>
/// <see cref="GatewayReranker"/> (B-16; gateway-changes §7.4): one Cohere-compatible <c>POST /v1/rerank</c> at the request's
/// ceiling; in the engine its scores reorder before the cap, and a gateway without the model (404/400), another refusal or a
/// timeout keep the fused order with <c>rerank_unavailable</c>, logged once per model and reason until a call succeeds.
/// </summary>
public sealed class GatewayRerankerTests : IDisposable
{
    private const string Model = "bge-reranker-v2-m3";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Fused and deduped order 5, 1, 2, 6, 3, 4; chunks 1–4 are document 7 (cap 3).
    private readonly FakeIndex _index = new()
    {
        Vector = [Chunk(1, distance: 0.10, document: 7), Chunk(2, distance: 0.20, document: 7), Chunk(3, distance: 0.30, document: 7),
            Chunk(4, distance: 0.40, document: 7), Chunk(5, distance: 0.60, document: 8)],
        Keyword = [Chunk(5, keyword: 0.9, document: 8), Chunk(6, keyword: 0.4, document: 9)],
    };

    private readonly ScopeSizeCache _sizes;

    public GatewayRerankerTests() => _sizes = new ScopeSizeCache(_index, new FakeTimeProvider());

    public void Dispose() => _sizes.Dispose();

    private static readonly Guid[] FusedOrder = [Id(5), Id(1), Id(2), Id(6), Id(3)];

    private static Task<ReadOnlyMemory<float>> Embed(string query, CancellationToken ct) => Task.FromResult<ReadOnlyMemory<float>>(new float[] { 1, 0, 0 });

    private static SearchRequest Request() =>
        new("altan bygglov", Scope(RetrievalPlatformSettings.Default with { GatewayRerankModel = Model }, (CollectionA, RetrievalSettings.Default)), Embed: Embed,
            Rerank: new RerankCall(Residency.Eu, Guid.Parse("01929b6e-0000-7000-8000-0000000000bb"), RerankerKind.Gateway));

    private RetrievalEngine Engine(Gateway gateway, ListLogger? logger = null, TimeSpan? timeout = null) =>
        new(_index, _sizes, new RetrievalEngineOptions { GatewayRerankTimeout = timeout ?? TimeSpan.FromSeconds(5) },
            new GatewayReranker(gateway, logger: logger), new FakeTexts());

    [Fact]
    public async Task One_rerank_call_at_the_ceiling_scores_the_passages_and_reorders_before_the_cap()
    {
        // The reranker favours chunk 4 (cut by the cap without reranking) and chunk 6; results come sorted, as Cohere's do.
        var gateway = new Gateway(body => Results(body, new Dictionary<Guid, double> { [Id(4)] = 0.98, [Id(6)] = 0.91 }));
        using var engine = Engine(gateway);
        var result = await engine.SearchAsync(Request(), Ct);

        result.Hits.Select(h => h.Candidate.ChunkId).ShouldBe([Id(4), Id(6), Id(5), Id(1), Id(2)]);
        result.Hits[0].Scores.RerankScore.ShouldBe(0.98);
        result.Hits[2].Scores.RerankScore.ShouldBe(0.01);
        result.Degraded.ShouldBeNull();
        result.RerankUsage.ShouldBe(new RerankUsage(Model, 321, 0));

        var call = gateway.Calls.ShouldHaveSingleItem();
        call.Path.ShouldBe("/v1/rerank");
        call.Headers["x-ume-max-residency"].ShouldBe("Eu");
        call.Headers["x-ume-step"].ShouldBe(GatewayReranker.Step);
        call.Headers["x-ume-run-id"].ShouldBe("01929b6e-0000-7000-8000-0000000000bb");
        using var body = JsonDocument.Parse(call.Body);
        body.RootElement.GetProperty("model").GetString().ShouldBe(Model);
        body.RootElement.GetProperty("query").GetString().ShouldBe("altan bygglov");
        body.RootElement.GetProperty("return_documents").GetBoolean().ShouldBeFalse();
        body.RootElement.TryGetProperty("top_n", out _).ShouldBeFalse();
        // Every deduped candidate up to rerankCandidates, in fused order, as plain strings.
        body.RootElement.GetProperty("documents").EnumerateArray().Select(d => d.GetString()).ShouldBe(
            new[] { Id(5), Id(1), Id(2), Id(6), Id(3), Id(4) }.Select(id => "text " + id));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)] // no rerank model on the gateway (the default of most deployments)
    [InlineData(HttpStatusCode.BadRequest)] // the model is not of kind Rerank
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task A_refusal_keeps_the_fused_order_and_is_logged_once_until_a_call_succeeds(HttpStatusCode status)
    {
        var refuse = true;
        var gateway = new Gateway(body => refuse ? Error(status) : Results(body, new Dictionary<Guid, double> { [Id(6)] = 0.9 }));
        var logger = new ListLogger();
        using var engine = Engine(gateway, logger);

        for (var i = 0; i < 3; i++)
        {
            var result = await engine.SearchAsync(Request(), Ct);
            result.Hits.Select(h => h.Candidate.ChunkId).ShouldBe(FusedOrder);
            result.Hits.ShouldAllBe(h => h.Scores.RerankScore == null);
            result.Degraded.ShouldBe(RetrievalDegradation.RerankUnavailable);
        }

        gateway.Calls.Count.ShouldBe(3);
        var line = logger.Messages.ShouldHaveSingleItem();
        line.ShouldContain(Model);
        line.ShouldContain(((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture));
        line.ShouldNotContain("altan"); // never the query or the passages

        // A call that succeeds resets the latch: the next refusal is logged again.
        refuse = false;
        (await engine.SearchAsync(Request(), Ct)).Degraded.ShouldBeNull();
        refuse = true;
        await engine.SearchAsync(Request(), Ct);
        logger.Messages.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_timeout_keeps_the_fused_order_and_is_logged_once()
    {
        var gateway = new Gateway(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Error(HttpStatusCode.OK);
        });
        var logger = new ListLogger();
        using var engine = Engine(gateway, logger, TimeSpan.FromMilliseconds(100));
        var started = System.Diagnostics.Stopwatch.StartNew();

        for (var i = 0; i < 2; i++)
        {
            var result = await engine.SearchAsync(Request(), Ct);
            result.Hits.Select(h => h.Candidate.ChunkId).ShouldBe(FusedOrder);
            result.Degraded.ShouldBe(RetrievalDegradation.RerankUnavailable);
        }

        started.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
        logger.Messages.ShouldHaveSingleItem().ShouldContain("timed out");
    }

    [Fact]
    public void The_answer_is_read_by_index_with_missing_or_invalid_results_unscored()
    {
        static IReadOnlyList<double?> Read(string json, int count)
        {
            using var doc = JsonDocument.Parse(json);
            return GatewayReranker.Read(doc.RootElement, count);
        }

        Read("""{"results":[{"index":2,"relevance_score":0.9},{"index":0,"relevance_score":0.1}]}""", 3).ShouldBe([0.1, null, 0.9]);
        Read("""{"results":[{"index":0,"relevance_score":0.5},{"index":0,"relevance_score":0.7},{"index":5,"relevance_score":1},{"index":1,"relevance_score":"x"}]}""", 2)
            .ShouldBe([0.5, null]);
        Should.Throw<RerankOutputException>(() => Read("""{"data":[]}""", 2));
        Should.Throw<RerankOutputException>(() => Read("""{"results":[]}""", 2));
        Should.Throw<RerankOutputException>(() => Read("""{"results":[{"index":9,"relevance_score":0.5}]}""", 2));
    }

    [Theory]
    [InlineData("""{"usage":{"prompt_tokens":12,"total_tokens":15}}""", 12)]
    [InlineData("""{"usage":{"total_tokens":15}}""", 15)]
    [InlineData("""{"meta":{"billed_units":{"input_tokens":40}}}""", 40)]
    [InlineData("""{}""", 0)]
    public void Input_tokens_are_read_as_the_gateway_bills_them(string json, int tokens)
    {
        using var doc = JsonDocument.Parse(json);
        GatewayReranker.InputTokens(doc.RootElement).ShouldBe(tokens);
    }

    /// <summary>Cohere-style results, sorted by score: the given score per chunk id, 0.01 for the others.</summary>
    private static HttpResponseMessage Results(string body, IReadOnlyDictionary<Guid, double> scores)
    {
        using var json = JsonDocument.Parse(body);
        var documents = json.RootElement.GetProperty("documents").EnumerateArray().Select(d => Guid.Parse(d.GetString()!["text ".Length..])).ToList();
        var results = documents.Select((id, i) => new { index = i, relevance_score = scores.GetValueOrDefault(id, 0.01) }).OrderByDescending(r => r.relevance_score);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { id = "r1", results, usage = new { prompt_tokens = 321, total_tokens = 321 } }),
                Encoding.UTF8, "application/json"),
        };
    }

    private static HttpResponseMessage Error(HttpStatusCode status) => new(status)
    {
        Content = new StringContent("""{"error":{"code":"model_not_found","message":"The model does not exist."}}""", Encoding.UTF8, "application/json"),
    };

    public sealed record Call(string Path, IReadOnlyDictionary<string, string> Headers, string Body);

    /// <summary>Collects the formatted log messages of warning level and above.</summary>
    internal sealed class ListLogger : ILogger<GatewayReranker>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
            {
                Messages.Add(formatter(state, exception));
            }
        }
    }

    /// <summary>A gateway stub behind <see cref="IHttpClientFactory"/>: records each call and answers from the body.</summary>
    private sealed class Gateway : HttpMessageHandler, IHttpClientFactory
    {
        private readonly Func<string, CancellationToken, Task<HttpResponseMessage>> _answer;

        public Gateway(Func<string, HttpResponseMessage> answer) => _answer = (body, _) => Task.FromResult(answer(body));

        public Gateway(Func<string, CancellationToken, Task<HttpResponseMessage>> answer) => _answer = answer;

        public List<Call> Calls { get; } = [];

        public HttpClient CreateClient(string name)
        {
            name.ShouldBe(ChatModelReranker.HttpClientName);
            return new HttpClient(this, disposeHandler: false) { BaseAddress = new Uri("http://gateway.invalid/") };
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            lock (Calls)
            {
                Calls.Add(new Call(request.RequestUri!.AbsolutePath, request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value)), body));
            }

            return await _answer(body, cancellationToken);
        }
    }
}
