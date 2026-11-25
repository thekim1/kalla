extern alias fakegateway;

using Kalla.TestKit;

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Kalla.Domain;
using Kalla.Infrastructure.Gateway;
using Kalla.Pipelines.Jobs;
using Kalla.Worker.Processing;
using Kalla.Worker.Processing.Chunking;
using Kalla.Worker.Processing.Embedding;
using Kalla.Worker.Processing.Persistence;

namespace Kalla.Worker.Tests.Processing;

/// <summary>
/// <c>embed</c> against the FakeGateway in process (processing design §11; P1 README §1): the profile's model, dimensions
/// (1024 until U-b) and ceiling (<c>Eu</c>, the worker's <c>Eu</c> key), the cache, the batch limits, the fixed
/// concurrency, the stored canary result; and the negative proofs: a failed canary check refuses embeddings, a call without a ceiling is
/// refused before sending, an answer above the ceiling or with the wrong dimensions is fatal.
/// </summary>
public sealed class EmbedStageTests(FakeGatewayFactory gateway) : IClassFixture<FakeGatewayFactory>
{
    private const string Level = Classification.Public;

    private static readonly EmbeddingProfileRecord Pilot = new(Guid.Parse("01929b6e-0000-7000-8000-00000000b6e3"), "dev-embedding", "bge-m3", 1024, 8192, Residency.Eu)
    {
        CanaryText = "Umeå kommun ansvarar för förskola, grundskola och snöröjning.",
        CanaryEmbedding = fakegateway::Kalla.FakeGateway.FakeContent.Vector("Umeå kommun ansvarar för förskola, grundskola och snöröjning.", 1024),
        IsDefault = true,
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A call of its own run, counted in an attempt of its own.</summary>
    private static EmbeddingCall Call() => new(Guid.CreateVersion7(), TestUsage.Sink());

    private static ChunkSetDraft Chunks(int count, int tokens = 50) => new()
    {
        Chunks = [.. Enumerable.Range(0, count).Select(i => new ChunkDraft
        {
            Ordinal = i, Prefix = "Bygglov", HeadingPath = "Bygglov", Text = $"Text nummer {i} om bygglov.", SearchText = "x", OverlapChars = 0,
            TextHash = ProcessingJson.Sha256Hex(ChunkText.Embedded("Bygglov", $"Text nummer {i} om bygglov.")), TokenCount = tokens, DocumentType = "page",
        })],
    };

    private sealed record Rig(IHost Host, EmbedStage Stage, InMemoryEmbeddingCache Cache, Recording Recorder, InMemoryProfiles Profiles) : IDisposable
    {
        public void Dispose() => Host.Dispose();
    }

    private Rig Build(int concurrency = 4, HttpMessageHandler? primary = null, params EmbeddingProfileRecord[] profiles)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Configuration.AddInMemoryCollection([
            new("Gateway:BaseUrl", "http://fake-gateway"),
            new("Gateway:Key", FakeGatewayFactory.WorkerCredentialKey),
            new("Worker:Concurrency:embed", concurrency.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        ]);
        builder.Logging.ClearProviders();
        var recorder = new Recording();
        var store = new InMemoryProfiles(profiles.Length == 0 ? [Pilot] : profiles);
        var cache = new InMemoryEmbeddingCache();
        builder.Services.AddSingleton<IEmbeddingProfileStore>(store);
        builder.Services.AddSingleton<IEmbeddingCache>(cache);
        builder.Services.AddSingleton<IChunkSetStore, InMemoryChunkSetStore>();
        builder.Services.AddSingleton<TimeProvider>(new FakeTimeProvider(ProcessingHarness.Now));
        builder.Services.AddOptions<Jobs.WorkerOptions>().BindConfiguration(Jobs.WorkerOptions.SectionName);
        builder.AddRagProcessing();
        builder.Services.UseGatewayCredentialCeiling();
        builder.Services.AddHttpClient(EmbeddingClient.HttpClientName)
            // Recorded as it leaves the client, after the ceiling handler (TestKit's RecordingHandler, #974).
            .ConfigurePrimaryHttpMessageHandler(() => new RecordingHandler(primary ?? gateway.Server.CreateHandler(), recorder.Queue));
        var host = builder.Build();
        return new Rig(host, host.Services.GetRequiredService<EmbedStage>(), cache, recorder, store);
    }

    [Fact]
    public async Task Chunks_are_embedded_with_the_profiles_model_dimensions_and_ceiling_and_cached()
    {
        using var rig = Build();

        var result = await rig.Stage.RunAsync(Chunks(3), Level, Pilot.Id, Call(), Ct);

        result.Vectors.Count.ShouldBe(3);
        result.Vectors.ShouldAllBe(v => v.Length == 1024);
        result.CacheHits.ShouldBe(0);
        var call = rig.Recorder.Requests[^1];
        call.Header(GatewayHeaders.MaxResidency).ShouldBe("Eu");
        call.Header("Authorization").ShouldBe($"Bearer {FakeGatewayFactory.WorkerCredentialKey}");
        call.Header(GatewayHeaders.Step).ShouldBe("embed");
        call.Header(GatewayHeaders.RunId).ShouldNotBeNull();
        call.Body.ShouldContain("\"model\":\"bge-m3\"");
        call.Body.ShouldContain("\"encoding_format\":\"float\"");

        var requests = rig.Recorder.Requests.Count;
        var again = await rig.Stage.RunAsync(Chunks(3), Level, Pilot.Id, Call(), Ct);
        again.CacheHits.ShouldBe(3);
        rig.Recorder.Requests.Count.ShouldBe(requests);
        again.Vectors.ShouldBe(result.Vectors);
    }

    [Fact]
    public void The_processing_services_compose_from_AddRagProcessing()
    {
        using var rig = Build();
        rig.Host.Services.GetRequiredService<DocumentProcessor>().ShouldNotBeNull();
        rig.Host.Services.GetRequiredService<Worker.Processing.Pii.PiiLifecycle>().ShouldNotBeNull();
    }

    [Fact]
    public async Task The_cache_is_per_level_and_profile()
    {
        using var rig = Build();
        await rig.Stage.RunAsync(Chunks(2), Classification.Public, Pilot.Id, Call(), Ct);

        (await rig.Stage.RunAsync(Chunks(2), Classification.Internal, Pilot.Id, Call(), Ct)).CacheHits.ShouldBe(0);
    }

    /// <summary>
    /// Gateway v0.9.0 checks each embedding input against the model's context window, not the whole request, so a batch has
    /// no token cap: 64 real Swedish inputs, each just below the profile's <c>MaxInputTokens</c> (counted with the BGE-M3
    /// counter), go in one request.
    /// </summary>
    [Fact]
    public async Task A_full_batch_of_inputs_near_the_context_window_is_one_request()
    {
        using var rig = Build();
        var marker = Guid.NewGuid().ToString("N");
        var passages = string.Join(' ', File.ReadLines(Path.Combine(Fixtures.Processing, "tokenizer", "passages.jsonl"))
            .Select(line => JsonDocument.Parse(line).RootElement.GetProperty("text").GetString()!));
        var body = passages[..TokenCuts.PrefixLength(Bgem3TokenCounter.Shared, passages, Pilot.MaxInputTokens - 100)];
        var draft = new ChunkSetDraft { Chunks = [.. Enumerable.Range(0, EmbedLimits.MaxInputs).Select(i => Swedish(i, $"{i} {marker} {body}"))] };
        draft.Chunks.ShouldAllBe(c => c.TokenCount > Pilot.MaxInputTokens - 200 && c.TokenCount <= Pilot.MaxInputTokens);
        draft.Chunks.Sum(c => c.TokenCount).ShouldBeGreaterThan(50 * 6_000);
        var before = rig.Recorder.Requests.Count;

        (await rig.Stage.RunAsync(draft, Level, Pilot.Id, Call(), Ct)).Vectors.Count.ShouldBe(EmbedLimits.MaxInputs);

        rig.Recorder.Requests.Count.ShouldBe(before + 1);
        Inputs(rig.Recorder.Requests[^1].Body).Count.ShouldBe(EmbedLimits.MaxInputs);
    }

    [Fact]
    public async Task More_than_64_inputs_are_split_by_count_only()
    {
        using var rig = Build();
        var before = rig.Recorder.Requests.Count;

        await rig.Stage.RunAsync(Chunks(130, tokens: Pilot.MaxInputTokens), Level, Pilot.Id, Call(), Ct);

        rig.Recorder.Requests.Skip(before).Select(r => Inputs(r.Body).Count).ShouldBe([64, 64, 2]);
    }

    [Fact]
    public async Task Sixty_four_short_chunks_are_one_request()
    {
        using var rig = Build();
        var marker = Guid.NewGuid().ToString("N");
        var draft = new ChunkSetDraft { Chunks = [.. Enumerable.Range(0, 64).Select(i => Swedish(i, $"Sophämtning vecka {i} {marker}."))] };
        var before = rig.Recorder.Requests.Count;

        await rig.Stage.RunAsync(draft, Level, Pilot.Id, Call(), Ct);

        rig.Recorder.Requests.Count.ShouldBe(before + 1);
        Inputs(rig.Recorder.Requests[^1].Body).Count.ShouldBe(64);
    }

    [Fact]
    public async Task Duplicate_texts_are_embedded_once()
    {
        using var rig = Build();
        var one = Chunks(1).Chunks[0];
        var draft = new ChunkSetDraft { Chunks = [one, one with { Ordinal = 1 }, one with { Ordinal = 2 }] };
        var before = rig.Recorder.Requests.Count;

        var result = await rig.Stage.RunAsync(draft, Level, Pilot.Id, Call(), Ct);

        result.Vectors.Count.ShouldBe(3);
        rig.Recorder.Requests.Count.ShouldBe(before + 1);
        rig.Recorder.Requests[^1].Body.Split("Text nummer").Length.ShouldBe(2);
    }

    [Fact]
    public async Task Concurrent_embedding_requests_are_bounded_by_the_fixed_class_concurrency()
    {
        var slow = new SlowHandler(gateway.Server.CreateHandler());
        using var rig = Build(concurrency: 2, primary: slow);

        await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
            rig.Stage.RunAsync(new ChunkSetDraft { Chunks = [Chunks(1).Chunks[0] with { TextHash = $"h{i}", Text = $"dokument {i}" }] },
                Level, Pilot.Id, Call(), Ct)));

        slow.MaxInFlight.ShouldBeLessThanOrEqualTo(2);
        slow.MaxInFlight.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Negative_proof_a_failed_canary_check_refuses_embeddings_and_makes_health_unhealthy()
    {
        using var rig = Build(profiles: Pilot with { CanaryCheckedAt = ProcessingHarness.Now, CanarySimilarity = 0.42 });

        var error = await Should.ThrowAsync<StageTransientException>(() => rig.Stage.RunAsync(Chunks(2), Level, Pilot.Id, Call(), Ct));
        error.Code.ShouldBe("embed.canary_mismatch");
        error.CountsAttempt.ShouldBeFalse();
        rig.Recorder.Requests.ShouldBeEmpty();
        (await CanaryHealthAsync(rig)).ShouldBe(HealthStatus.Unhealthy);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.999)]
    [InlineData(1.0)]
    public async Task A_profile_never_checked_or_whose_last_check_passed_is_allowed_and_healthy(double? similarity)
    {
        using var rig = Build(profiles: Pilot with { CanaryCheckedAt = similarity is null ? null : ProcessingHarness.Now, CanarySimilarity = similarity });

        (await rig.Stage.RunAsync(Chunks(1), Level, Pilot.Id, Call(), Ct)).Vectors.Count.ShouldBe(1);
        (await CanaryHealthAsync(rig)).ShouldBe(HealthStatus.Healthy);
    }

    [Fact]
    public async Task A_profile_without_a_recorded_canary_is_allowed()
    {
        using var rig = Build(profiles: Pilot with { CanaryText = null, CanaryEmbedding = null });

        (await rig.Stage.RunAsync(Chunks(1), Level, Pilot.Id, Call(), Ct)).Vectors.Count.ShouldBe(1);
        rig.Recorder.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_new_check_result_on_the_profile_row_applies_at_the_next_embed_without_a_restart()
    {
        using var rig = Build();
        await rig.Stage.RunAsync(Chunks(1), Level, Pilot.Id, Call(), Ct);

        rig.Profiles.Profiles[0] = Pilot with { CanaryCheckedAt = ProcessingHarness.Now, CanarySimilarity = 0.9 };
        (await Should.ThrowAsync<StageTransientException>(() => rig.Stage.RunAsync(Chunks(2), Level, Pilot.Id, Call(), Ct)))
            .Code.ShouldBe("embed.canary_mismatch");
        (await CanaryHealthAsync(rig)).ShouldBe(HealthStatus.Unhealthy);

        rig.Profiles.Profiles[0] = Pilot with { CanaryCheckedAt = ProcessingHarness.Now.AddMinutes(5), CanarySimilarity = 0.99995 };
        (await rig.Stage.RunAsync(Chunks(2), Level, Pilot.Id, Call(), Ct)).Vectors.Count.ShouldBe(2);
        (await CanaryHealthAsync(rig)).ShouldBe(HealthStatus.Healthy);
    }

    [Fact]
    public async Task The_worker_makes_no_canary_calls_at_startup_or_over_time()
    {
        using var rig = Build();
        var clock = (FakeTimeProvider)rig.Host.Services.GetRequiredService<TimeProvider>();
        await rig.Host.StartAsync(Ct);
        for (var i = 0; i < 5; i++)
        {
            clock.Advance(TimeSpan.FromHours(1));
            await Task.Delay(20, Ct);
        }

        (await CanaryHealthAsync(rig)).ShouldBe(HealthStatus.Healthy);
        await rig.Host.StopAsync(Ct);
        rig.Recorder.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Negative_proof_a_call_without_a_ceiling_is_refused_before_sending()
    {
        using var rig = Build();
        var client = rig.Host.Services.GetRequiredService<IHttpClientFactory>().CreateClient(EmbeddingClient.HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/embeddings") { Content = new StringContent("""{"model":"bge-m3","input":"x"}""") };

        await Should.ThrowAsync<GatewayCallRefusedException>(() => client.SendAsync(request, Ct));
        rig.Recorder.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Negative_proof_an_answer_above_the_ceiling_is_fatal()
    {
        var wrong = Pilot with { GatewayModel = "fake-wrong-residency-embed", CanaryEmbedding = null };
        using var rig = Build(profiles: wrong);

        var error = await Should.ThrowAsync<StageFatalException>(() => rig.Stage.RunAsync(Chunks(1), Level, Pilot.Id, Call(), Ct));
        error.Reason.ShouldBe("gateway.residency_above_ceiling");
        rig.Cache.Rows.ShouldBeEmpty();
    }

    [Fact]
    public async Task Vectors_of_the_wrong_dimensions_are_fatal_and_not_cached()
    {
        var other = Pilot with { Dimensions = 768, CanaryEmbedding = null };
        using var rig = Build(profiles: other);

        (await Should.ThrowAsync<StageFatalException>(() => rig.Stage.RunAsync(Chunks(1), Level, Pilot.Id, Call(), Ct))).Reason.ShouldBe("embed.bad_vector");
        rig.Cache.Rows.ShouldBeEmpty();
    }

    /// <summary>
    /// U-b (#142): a profile with <c>RequestDimensions</c> sends <c>"dimensions"</c> from the embed stage and the
    /// evaluator's query embedder, all through <see cref="EmbeddingClient"/>; the FakeGateway honours it (else 1024).
    /// </summary>
    [Fact]
    public async Task A_profile_that_requests_dimensions_sends_them_on_every_call_and_gets_vectors_of_that_length()
    {
        var marker = Guid.NewGuid().ToString("N");
        var canaryText = $"Umeå kommun ansvarar för förskola och snöröjning {marker}.";
        var shortened = Pilot with
        {
            Id = Guid.Parse("01929b6e-0000-7000-8000-00000000b6e5"), Name = "shortened", Dimensions = 256, RequestDimensions = true,
            CanaryText = canaryText, CanaryEmbedding = fakegateway::Kalla.FakeGateway.FakeContent.Vector(canaryText, 256),
        };
        using var rig = Build(profiles: shortened);

        var draft = new ChunkSetDraft { Chunks = [Swedish(0, $"Bygglov för altan {marker}."), Swedish(1, $"Bygglov för garage {marker}.")] };
        var result = await rig.Stage.RunAsync(draft, Level, shortened.Id, Call(), Ct);
        result.Vectors.Count.ShouldBe(2);
        result.Vectors.ShouldAllBe(v => v.Length == 256);
        rig.Recorder.Requests[^1].Body.ShouldContain("\"dimensions\":256");
        var query = new Worker.Evaluation.EvaluationQueryEmbedder(rig.Host.Services.GetRequiredService<EmbeddingClient>(), rig.Cache, TimeProvider.System)
            .For(shortened, Level);
        (await query($"när töms soptunnan {marker}", Guid.CreateVersion7(), TestUsage.Sink(), Ct)).Length.ShouldBe(256);
        Dimensions(rig.Recorder.Requests[^1].Body).ShouldBe(256);
        rig.Recorder.Requests.Count.ShouldBe(2);
    }

    /// <summary>U-b (#142), the other side: without <c>RequestDimensions</c> no call carries a <c>dimensions</c> member (BGE-M3, gateway-changes §6).</summary>
    [Fact]
    public async Task A_profile_that_does_not_request_dimensions_never_sends_them()
    {
        var marker = Guid.NewGuid().ToString("N");
        using var rig = Build();

        await rig.Stage.RunAsync(new ChunkSetDraft { Chunks = [Swedish(0, $"Bygglov för altan {marker}.")] }, Level, Pilot.Id, Call(), Ct);
        var query = new Worker.Evaluation.EvaluationQueryEmbedder(rig.Host.Services.GetRequiredService<EmbeddingClient>(), rig.Cache, TimeProvider.System)
            .For(Pilot, Level);
        (await query($"när töms soptunnan {marker}", Guid.CreateVersion7(), TestUsage.Sink(), Ct)).Length.ShouldBe(1024);

        rig.Recorder.Requests.Count.ShouldBe(2);
        rig.Recorder.Requests.ShouldAllBe(r => Dimensions(r.Body) == null);
        rig.Recorder.Requests.ShouldAllBe(r => !r.Body.Contains("dimensions", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, false, "gateway.rate_limited")]
    [InlineData(HttpStatusCode.InternalServerError, true, "gateway.http_500")]
    [InlineData(HttpStatusCode.PaymentRequired, true, "gateway.budget_exhausted")]
    public async Task Gateway_failures_are_transient_as_engine_design_7_3(HttpStatusCode status, bool countsAttempt, string code)
    {
        using var rig = Build(primary: new StatusHandler(status), profiles: Pilot with { CanaryEmbedding = null });
        var sink = TestUsage.Sink();

        var error = await Should.ThrowAsync<StageTransientException>(() => rig.Stage.RunAsync(Chunks(1), Level, Pilot.Id, new EmbeddingCall(Guid.CreateVersion7(), sink), Ct));
        error.Code.ShouldBe(code);
        error.CountsAttempt.ShouldBe(countsAttempt);
        sink.Usage.HasPending.ShouldBeFalse("a call answered with an error status records nothing");
    }

    /// <summary>
    /// Engine design §10.6: an answered call's tokens and <c>x-fors-cost-usd</c> go to the attempt's usage; without the header
    /// the cost is the estimate from the mirror's prices (here 0.02 USD per million input tokens).
    /// </summary>
    [Fact]
    public async Task An_answered_call_adds_its_gateway_cost_to_the_attempt_and_without_one_its_estimate()
    {
        var priced = new GatewayCallRecorder();
        var pricedHandler = priced.Handler();
        pricedHandler.InnerHandler = gateway.Server.CreateHandler();
        using (var rig = Build(primary: pricedHandler))
        {
            var sink = TestUsage.Sink();
            await rig.Stage.RunAsync(Chunks(3), Level, Pilot.Id, new EmbeddingCall(Guid.CreateVersion7(), sink), Ct);

            var call = priced.Answered.ShouldHaveSingleItem();
            call.CostUsd.ShouldNotBeNull().ShouldBeGreaterThan(0m);
            sink.Usage.PendingCostUsd.ShouldBe(call.CostUsd);
        }

        var unpriced = new GatewayCallRecorder();
        var unpricedHandler = unpriced.Handler();
        unpricedHandler.InnerHandler = new WithoutCostHandler(gateway.Server.CreateHandler());
        using (var rig = Build(primary: unpricedHandler))
        {
            var sink = TestUsage.Sink(new FixedModelPrices(new Dictionary<string, ModelPrices> { [Pilot.GatewayModel] = new(0.02m, null, 0m) }));
            await rig.Stage.RunAsync(Chunks(3), Level, Pilot.Id, new EmbeddingCall(Guid.CreateVersion7(), sink), Ct);

            var call = unpriced.Answered.ShouldHaveSingleItem();
            call.CostUsd.ShouldBeNull();
            call.InputTokens.ShouldBeGreaterThan(0);
            sink.Usage.PendingCostUsd.ShouldBe(Math.Round(call.InputTokens * 0.02m / 1_000_000m, 8, MidpointRounding.AwayFromZero));
        }
    }

    /// <summary>
    /// #703: an answer whose body is not JSON is fatal <c>embed.bad_vector</c>, and the call still counts with the gateway's
    /// <c>x-fors-cost-usd</c> and no tokens, since the gateway charged it.
    /// </summary>
    [Fact]
    public async Task An_answer_whose_body_cannot_be_read_is_fatal_and_counts_its_gateway_cost_alone()
    {
        using var rig = Build(primary: new MalformedBodyHandler(gateway.Server.CreateHandler()), profiles: Pilot with { CanaryEmbedding = null });
        var sink = TestUsage.Sink();

        var error = await Should.ThrowAsync<StageFatalException>(() => rig.Stage.RunAsync(Chunks(1), Level, Pilot.Id, new EmbeddingCall(Guid.CreateVersion7(), sink), Ct));
        error.Reason.ShouldBe("embed.bad_vector");
        sink.Usage.PendingCostUsd.ShouldBe(MalformedBodyHandler.CostUsd);
        rig.Cache.Rows.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "gateway.max_residency_not_met")]
    [InlineData(HttpStatusCode.BadRequest, "embed.input_rejected")]
    public async Task Gateway_refusals_are_fatal(HttpStatusCode status, string reason)
    {
        using var rig = Build(primary: new StatusHandler(status), profiles: Pilot with { CanaryEmbedding = null });

        var error = await Should.ThrowAsync<StageFatalException>(() => rig.Stage.RunAsync(Chunks(1), Level, Pilot.Id, Call(), Ct));
        error.Reason.ShouldBe(reason);
        error.Outcome.ShouldBe(RunItemOutcome.Failed);
    }

    /// <summary>A chunk of real text with its BGE-M3 token count, as the chunker writes it.</summary>
    private static ChunkDraft Swedish(int ordinal, string text)
    {
        var embedded = ChunkText.Embedded("Avfall", text);
        return new ChunkDraft
        {
            Ordinal = ordinal, Prefix = "Avfall", HeadingPath = "Avfall", Text = text, SearchText = "x", OverlapChars = 0,
            TextHash = ProcessingJson.Sha256Hex(embedded), TokenCount = Bgem3TokenCounter.Shared.Count(embedded), DocumentType = "page",
        };
    }

    /// <summary>The <c>input</c> array of an embedding request body.</summary>
    private static List<string> Inputs(string body)
    {
        using var json = JsonDocument.Parse(body);
        return [.. json.RootElement.GetProperty("input").EnumerateArray().Select(e => e.GetString()!)];
    }

    /// <summary>The <c>dimensions</c> member of an embedding request body, or null when it has none.</summary>
    private static int? Dimensions(string body)
    {
        using var json = JsonDocument.Parse(body);
        return json.RootElement.TryGetProperty("dimensions", out var dimensions) ? dimensions.GetInt32() : null;
    }

    private static async Task<HealthStatus> CanaryHealthAsync(Rig rig) =>
        (await rig.Host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync(c => c.Name == "embedding-canary", Ct)).Status;

    /// <summary>What left the worker's client, after the ceiling handler: the queue every RecordingHandler the factory builds shares.</summary>
    private sealed class Recording
    {
        public System.Collections.Concurrent.ConcurrentQueue<RecordedRequest> Queue { get; } = new();

        public IReadOnlyList<RecordedRequest> Requests => [.. Queue];
    }

    private sealed class SlowHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private int _inFlight;

        public int MaxInFlight { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var now = Interlocked.Increment(ref _inFlight);
            lock (this)
            {
                MaxInFlight = Math.Max(MaxInFlight, now);
            }

            try
            {
                await Task.Delay(50, cancellationToken);
                return await base.SendAsync(request, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }

    /// <summary>The gateway's answer without <c>x-fors-cost-usd</c>, as a gateway without the feature sends it.</summary>
    private sealed class WithoutCostHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            response.Headers.Remove(GatewayHeaders.CostUsd);
            return response;
        }
    }

    /// <summary>The gateway's answer, headers kept, with a body cut off mid-JSON and <c>x-fors-cost-usd</c> set to <see cref="CostUsd"/>.</summary>
    private sealed class MalformedBodyHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public const decimal CostUsd = 0.00042m;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            response.Content.Dispose();
            response.Content = new StringContent("""{"data":[{"index":0,"embedding":[0.1,""", System.Text.Encoding.UTF8, "application/json");
            response.Headers.Remove(GatewayHeaders.CostUsd);
            response.Headers.Add(GatewayHeaders.CostUsd, CostUsd.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return response;
        }
    }

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent("""{"error":{"code":"max_residency_not_met","message":"x"}}"""),
            };
            if (status == HttpStatusCode.TooManyRequests)
            {
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(3));
            }

            return Task.FromResult(response);
        }
    }
}
