extern alias fakegateway;

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Ume.Rag.Domain;
using Ume.Rag.Infrastructure.Gateway;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Worker.Processing;
using Ume.Rag.Worker.Processing.Chunking;
using Ume.Rag.Worker.Processing.Embedding;
using Ume.Rag.Worker.Processing.Persistence;

namespace Ume.Rag.Worker.Tests.Processing;

/// <summary>
/// <c>embed</c> against the FakeGateway in process (processing design §11; P1 README §1): the profile's model, dimensions
/// (1024 until U-b) and ceiling (<c>Eu</c>, the worker's <c>Eu</c> key), the cache, the batch limits, the fixed
/// concurrency, the canary; and the negative proofs: a canary mismatch refuses embeddings, a call without a ceiling is
/// refused before sending, an answer above the ceiling or with the wrong dimensions is fatal.
/// </summary>
public sealed class EmbedStageTests(FakeGatewayFactory gateway) : IClassFixture<FakeGatewayFactory>
{
    private const string Level = Classification.Public;

    private static readonly EmbeddingProfileRecord Pilot = new(Guid.Parse("01929b6e-0000-7000-8000-00000000b6e3"), "dev-embedding", "bge-m3", 1024, 8192, Residency.Eu)
    {
        CanaryText = "Umeå kommun ansvarar för förskola, grundskola och snöröjning.",
        CanaryEmbedding = fakegateway::Ume.Rag.FakeGateway.FakeContent.Vector("Umeå kommun ansvarar för förskola, grundskola och snöröjning.", 1024),
        IsDefault = true,
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ChunkSetDraft Chunks(int count, int tokens = 50) => new()
    {
        Chunks = [.. Enumerable.Range(0, count).Select(i => new ChunkDraft
        {
            Ordinal = i, Prefix = "Bygglov", HeadingPath = "Bygglov", Text = $"Text nummer {i} om bygglov.", SearchText = "x", OverlapChars = 0,
            TextHash = ProcessingJson.Sha256Hex(ChunkText.Embedded("Bygglov", $"Text nummer {i} om bygglov.")), TokenCount = tokens, DocumentType = "page",
        })],
    };

    private sealed record Rig(IHost Host, EmbedStage Stage, EmbeddingCanary Canary, InMemoryEmbeddingCache Cache, Recording Recorder, InMemoryProfiles Profiles) : IDisposable
    {
        public void Dispose() => Host.Dispose();
    }

    private Rig Build(int concurrency = 4, HttpMessageHandler? primary = null, string? maxTokensPerRequest = null, params EmbeddingProfileRecord[] profiles)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Configuration.AddInMemoryCollection([
            new("Gateway:BaseUrl", "http://fake-gateway"),
            new("Gateway:Keys:OnPrem", FakeGatewayFactory.OnPremKey),
            new("Gateway:Keys:Eu", FakeGatewayFactory.EuKey),
            new("Worker:Concurrency:embed", concurrency.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        ]);
        if (maxTokensPerRequest is not null)
        {
            builder.Configuration.AddInMemoryCollection([new("Worker:Embed:MaxTokensPerRequest", maxTokensPerRequest)]);
        }

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
        builder.Services.AddHttpClient(EmbeddingClient.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => primary ?? gateway.Server.CreateHandler())
            .AddHttpMessageHandler(() => new RecordingHandler(recorder));
        var host = builder.Build();
        return new Rig(host, host.Services.GetRequiredService<EmbedStage>(), host.Services.GetRequiredService<EmbeddingCanary>(), cache, recorder, store);
    }

    [Fact]
    public async Task Chunks_are_embedded_with_the_profiles_model_dimensions_and_ceiling_and_cached()
    {
        using var rig = Build();
        await rig.Canary.CheckAllAsync(Ct);

        var result = await rig.Stage.RunAsync(Chunks(3), Level, Pilot.Id, new EmbeddingCall(Guid.CreateVersion7()), Ct);

        result.Vectors.Count.ShouldBe(3);
        result.Vectors.ShouldAllBe(v => v.Length == 1024);
        result.CacheHits.ShouldBe(0);
        var call = rig.Recorder.Requests[^1];
        call.Headers.GetValues(GatewayHeaders.MaxResidency).Single().ShouldBe("Eu");
        call.Headers.Authorization!.Parameter.ShouldBe(FakeGatewayFactory.EuKey);
        call.Headers.GetValues(EmbeddingClient.StepHeader).Single().ShouldBe("embed");
        call.Headers.Contains(EmbeddingClient.RunIdHeader).ShouldBeTrue();
        call.Body.ShouldContain("\"model\":\"bge-m3\"");
        call.Body.ShouldContain("\"encoding_format\":\"float\"");

        var requests = rig.Recorder.Requests.Count;
        var again = await rig.Stage.RunAsync(Chunks(3), Level, Pilot.Id, new EmbeddingCall(null), Ct);
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
        rig.Host.Services.GetServices<IHostedService>().ShouldContain(s => s is EmbeddingCanaryService);
    }

    [Fact]
    public async Task The_cache_is_per_level_and_profile()
    {
        using var rig = Build();
        await rig.Canary.CheckAllAsync(Ct);
        await rig.Stage.RunAsync(Chunks(2), Classification.Public, Pilot.Id, new EmbeddingCall(null), Ct);

        (await rig.Stage.RunAsync(Chunks(2), Classification.Internal, Pilot.Id, new EmbeddingCall(null), Ct)).CacheHits.ShouldBe(0);
    }

    [Fact]
    public void Batches_hold_at_most_64_inputs_and_the_token_cap()
    {
        var cap = new EmbedOptions().MaxTokensPerRequest;
        cap.ShouldBe(6_000);
        EmbedStage.Batches(Chunks(130).Chunks, cap).Select(b => b.Count).ShouldBe([64, 64, 2]);
        var batches = EmbedStage.Batches(Chunks(10, tokens: 2_500).Chunks, cap).ToList();
        batches.Select(b => b.Count).ShouldBe([2, 2, 2, 2, 2]);
        batches.ShouldAllBe(b => b.Sum(c => c.TokenCount) <= cap);
        EmbedStage.Batches(Chunks(2, tokens: 7_000).Chunks, cap).Select(b => b.Count).ShouldBe([1, 1], "a chunk above the cap goes alone");
    }

    /// <summary>
    /// To-verify #43: the gateway estimates a request's tokens as body bytes / 4 against BGE-M3's context window (8,192).
    /// Real Swedish chunks (four parity-corpus passages each, counted with the BGE-M3 counter) under the default cap.
    /// </summary>
    [Fact]
    public async Task With_the_default_cap_no_request_carries_more_than_6000_tokens_or_a_body_estimate_above_the_context_window()
    {
        using var rig = Build();
        await rig.Canary.CheckAllAsync(Ct);
        var marker = Guid.NewGuid().ToString("N");
        var passages = File.ReadLines(Path.Combine(Fixtures.Processing, "tokenizer", "passages.jsonl"))
            .Select(line => JsonDocument.Parse(line).RootElement.GetProperty("text").GetString()!)
            .Take(240)
            .ToList();
        var draft = new ChunkSetDraft { Chunks = [.. passages.Chunk(4).Select((group, i) => Swedish(i, $"{string.Join(' ', group)} {marker}"))] };
        draft.Chunks.Sum(c => c.TokenCount).ShouldBeGreaterThan(2 * 6_000);
        var before = rig.Recorder.Requests.Count;

        (await rig.Stage.RunAsync(draft, Level, Pilot.Id, new EmbeddingCall(null), Ct)).Vectors.Count.ShouldBe(draft.Chunks.Count);

        var requests = rig.Recorder.Requests.Skip(before).Select(r => r.Body).ToList();
        requests.Count.ShouldBeGreaterThan(2);
        requests.Sum(body => Inputs(body).Count).ShouldBe(draft.Chunks.Count);
        foreach (var body in requests)
        {
            Inputs(body).Sum(input => Bgem3TokenCounter.Shared.Count(input)).ShouldBeLessThanOrEqualTo(6_000);
            (Encoding.UTF8.GetByteCount(body) / 4).ShouldBeLessThan(8_192);
        }
    }

    [Fact]
    public async Task Sixty_four_short_chunks_are_one_request()
    {
        using var rig = Build();
        await rig.Canary.CheckAllAsync(Ct);
        var marker = Guid.NewGuid().ToString("N");
        var draft = new ChunkSetDraft { Chunks = [.. Enumerable.Range(0, 64).Select(i => Swedish(i, $"Sophämtning vecka {i} {marker}."))] };
        var before = rig.Recorder.Requests.Count;

        await rig.Stage.RunAsync(draft, Level, Pilot.Id, new EmbeddingCall(null), Ct);

        rig.Recorder.Requests.Count.ShouldBe(before + 1);
        Inputs(rig.Recorder.Requests[^1].Body).Count.ShouldBe(64);
    }

    [Fact]
    public async Task The_cap_is_a_deployment_knob_and_the_profiles_input_limit_caps_it_further()
    {
        var small = Pilot with { Id = Guid.Parse("01929b6e-0000-7000-8000-00000000b6e4"), Name = "small", MaxInputTokens = 600, IsDefault = false };
        using var rig = Build(maxTokensPerRequest: "1000", profiles: [Pilot, small]);
        await rig.Canary.CheckAllAsync(Ct);
        var draft = Chunks(12, tokens: 300);
        var before = rig.Recorder.Requests.Count;

        await rig.Stage.RunAsync(draft, Level, Pilot.Id, new EmbeddingCall(null), Ct);

        rig.Recorder.Requests.Skip(before).Select(r => Inputs(r.Body).Count).ShouldBe([3, 3, 3, 3]);
        before = rig.Recorder.Requests.Count;
        await rig.Stage.RunAsync(draft, Level, small.Id, new EmbeddingCall(null), Ct);

        rig.Recorder.Requests.Skip(before).Select(r => Inputs(r.Body).Count).ShouldBe([2, 2, 2, 2, 2, 2]);
    }

    [Theory]
    [InlineData("511")]
    [InlineData("8193")]
    public void An_invalid_cap_fails_options_validation(string value)
    {
        using var rig = Build(maxTokensPerRequest: value);

        Should.Throw<OptionsValidationException>(() => rig.Host.Services.GetRequiredService<IOptions<EmbedOptions>>().Value)
            .Message.ShouldContain(nameof(EmbedOptions.MaxTokensPerRequest));
    }

    [Fact]
    public async Task Duplicate_texts_are_embedded_once()
    {
        using var rig = Build();
        await rig.Canary.CheckAllAsync(Ct);
        var one = Chunks(1).Chunks[0];
        var draft = new ChunkSetDraft { Chunks = [one, one with { Ordinal = 1 }, one with { Ordinal = 2 }] };
        var before = rig.Recorder.Requests.Count;

        var result = await rig.Stage.RunAsync(draft, Level, Pilot.Id, new EmbeddingCall(null), Ct);

        result.Vectors.Count.ShouldBe(3);
        rig.Recorder.Requests.Count.ShouldBe(before + 1);
        rig.Recorder.Requests[^1].Body.Split("Text nummer").Length.ShouldBe(2);
    }

    [Fact]
    public async Task Concurrent_embedding_requests_are_bounded_by_the_fixed_class_concurrency()
    {
        var slow = new SlowHandler(gateway.Server.CreateHandler());
        using var rig = Build(concurrency: 2, primary: slow);
        await rig.Canary.CheckAllAsync(Ct);

        await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
            rig.Stage.RunAsync(new ChunkSetDraft { Chunks = [Chunks(1).Chunks[0] with { TextHash = $"h{i}", Text = $"dokument {i}" }] },
                Level, Pilot.Id, new EmbeddingCall(null), Ct)));

        slow.MaxInFlight.ShouldBeLessThanOrEqualTo(2);
        slow.MaxInFlight.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Negative_proof_a_canary_mismatch_refuses_embeddings_and_makes_health_unhealthy()
    {
        var changed = Pilot with { CanaryEmbedding = fakegateway::Ume.Rag.FakeGateway.FakeContent.Vector("En helt annan text om något annat.", 1024) };
        using var rig = Build(profiles: changed);
        await rig.Canary.CheckAllAsync(Ct);

        rig.Canary.For(Pilot.Id)!.State.ShouldBe(EmbeddingCanary.CanaryState.Failed);
        rig.Canary.For(Pilot.Id)!.Similarity!.Value.ShouldBeLessThan(EmbedLimits.CanaryMinCosine);
        var requests = rig.Recorder.Requests.Count;
        var error = await Should.ThrowAsync<StageTransientException>(() => rig.Stage.RunAsync(Chunks(2), Level, Pilot.Id, new EmbeddingCall(null), Ct));
        error.Code.ShouldBe("embed.canary_mismatch");
        error.CountsAttempt.ShouldBeFalse();
        rig.Recorder.Requests.Count.ShouldBe(requests);
        (await rig.Canary.CheckHealthAsync(new HealthCheckContext(), Ct)).Status.ShouldBe(HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task A_profile_whose_canary_has_not_been_checked_is_refused()
    {
        using var rig = Build();
        (await Should.ThrowAsync<StageTransientException>(() => rig.Stage.RunAsync(Chunks(1), Level, Pilot.Id, new EmbeddingCall(null), Ct))).Code.ShouldBe("embed.canary_unchecked");
    }

    [Fact]
    public async Task A_matching_canary_passes_and_a_profile_without_one_is_degraded()
    {
        using var rig = Build(profiles: [Pilot, Pilot with { Id = Guid.CreateVersion7(), Name = "no-canary", CanaryEmbedding = null, IsDefault = false }]);
        await rig.Canary.CheckAllAsync(Ct);

        rig.Canary.For(Pilot.Id)!.State.ShouldBe(EmbeddingCanary.CanaryState.Passed);
        rig.Canary.For(Pilot.Id)!.Similarity!.Value.ShouldBeGreaterThan(0.9999);
        (await rig.Canary.CheckHealthAsync(new HealthCheckContext(), Ct)).Status.ShouldBe(HealthStatus.Degraded);
    }

    [Fact]
    public async Task The_canary_service_checks_at_start_and_every_hour()
    {
        using var rig = Build();
        var clock = (FakeTimeProvider)rig.Host.Services.GetRequiredService<TimeProvider>();
        await rig.Host.StartAsync(Ct);
        await WaitAsync(() => rig.Recorder.Requests.Count >= 1);
        rig.Recorder.Requests.Count.ShouldBe(1); // at start, then nothing until the hour has passed
        for (var i = 0; i < 100 && rig.Recorder.Requests.Count < 2; i++)
        {
            // The service may not have reached its delay yet when the clock first moves: advance until it fires.
            clock.Advance(EmbeddingCanaryService.Interval);
            await Task.Delay(20, Ct);
        }

        await rig.Host.StopAsync(Ct);
        rig.Recorder.Requests.Count.ShouldBeGreaterThanOrEqualTo(2);
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
        await rig.Canary.CheckAllAsync(Ct);

        var error = await Should.ThrowAsync<StageFatalException>(() => rig.Stage.RunAsync(Chunks(1), Level, Pilot.Id, new EmbeddingCall(null), Ct));
        error.Reason.ShouldBe("gateway.residency_above_ceiling");
        rig.Cache.Rows.ShouldBeEmpty();
    }

    [Fact]
    public async Task Vectors_of_the_wrong_dimensions_are_fatal_and_not_cached()
    {
        var other = Pilot with { Dimensions = 768, CanaryEmbedding = null };
        using var rig = Build(profiles: other);
        await rig.Canary.CheckAllAsync(Ct);

        (await Should.ThrowAsync<StageFatalException>(() => rig.Stage.RunAsync(Chunks(1), Level, Pilot.Id, new EmbeddingCall(null), Ct))).Reason.ShouldBe("embed.bad_vector");
        rig.Cache.Rows.ShouldBeEmpty();
    }

    /// <summary>
    /// U-b (#142): a profile with <c>RequestDimensions</c> sends <c>"dimensions"</c> from the embed stage, the canary and the
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
            CanaryText = canaryText, CanaryEmbedding = fakegateway::Ume.Rag.FakeGateway.FakeContent.Vector(canaryText, 256),
        };
        using var rig = Build(profiles: shortened);

        (await rig.Canary.CheckAsync(shortened, Ct)).State.ShouldBe(EmbeddingCanary.CanaryState.Passed);
        Dimensions(rig.Recorder.Requests[^1].Body).ShouldBe(256);
        var draft = new ChunkSetDraft { Chunks = [Swedish(0, $"Bygglov för altan {marker}."), Swedish(1, $"Bygglov för garage {marker}.")] };
        var result = await rig.Stage.RunAsync(draft, Level, shortened.Id, new EmbeddingCall(null), Ct);
        result.Vectors.Count.ShouldBe(2);
        result.Vectors.ShouldAllBe(v => v.Length == 256);
        rig.Recorder.Requests[^1].Body.ShouldContain("\"dimensions\":256");
        var query = new Worker.Evaluation.EvaluationQueryEmbedder(rig.Host.Services.GetRequiredService<EmbeddingClient>(), rig.Cache, TimeProvider.System)
            .For(shortened, Level);
        (await query($"när töms soptunnan {marker}", Ct)).Length.ShouldBe(256);
        Dimensions(rig.Recorder.Requests[^1].Body).ShouldBe(256);
        rig.Recorder.Requests.Count.ShouldBe(3);
    }

    /// <summary>U-b (#142), the other side: without <c>RequestDimensions</c> no call carries a <c>dimensions</c> member (BGE-M3, gateway-changes §6).</summary>
    [Fact]
    public async Task A_profile_that_does_not_request_dimensions_never_sends_them()
    {
        var marker = Guid.NewGuid().ToString("N");
        using var rig = Build();

        (await rig.Canary.CheckAsync(Pilot, Ct)).State.ShouldBe(EmbeddingCanary.CanaryState.Passed);
        await rig.Stage.RunAsync(new ChunkSetDraft { Chunks = [Swedish(0, $"Bygglov för altan {marker}.")] }, Level, Pilot.Id, new EmbeddingCall(null), Ct);
        var query = new Worker.Evaluation.EvaluationQueryEmbedder(rig.Host.Services.GetRequiredService<EmbeddingClient>(), rig.Cache, TimeProvider.System)
            .For(Pilot, Level);
        (await query($"när töms soptunnan {marker}", Ct)).Length.ShouldBe(1024);

        rig.Recorder.Requests.Count.ShouldBe(3);
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
        await rig.Canary.CheckAllAsync(Ct);

        var error = await Should.ThrowAsync<StageTransientException>(() => rig.Stage.RunAsync(Chunks(1), Level, Pilot.Id, new EmbeddingCall(null), Ct));
        error.Code.ShouldBe(code);
        error.CountsAttempt.ShouldBe(countsAttempt);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "gateway.max_residency_not_met")]
    [InlineData(HttpStatusCode.BadRequest, "embed.input_rejected")]
    public async Task Gateway_refusals_are_fatal(HttpStatusCode status, string reason)
    {
        using var rig = Build(primary: new StatusHandler(status), profiles: Pilot with { CanaryEmbedding = null });
        await rig.Canary.CheckAllAsync(Ct);

        var error = await Should.ThrowAsync<StageFatalException>(() => rig.Stage.RunAsync(Chunks(1), Level, Pilot.Id, new EmbeddingCall(null), Ct));
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

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10, Ct);
        }

        condition().ShouldBeTrue();
    }

    /// <summary>What left the worker's client, after the ceiling handler.</summary>
    private sealed class Recording
    {
        public System.Collections.Concurrent.ConcurrentQueue<(System.Net.Http.Headers.HttpRequestHeaders Headers, string Body)> Queue { get; } = new();

        public IReadOnlyList<(System.Net.Http.Headers.HttpRequestHeaders Headers, string Body)> Requests => [.. Queue];
    }

    /// <summary>Records each request into a shared <see cref="Recording"/> without changing it.</summary>
    private sealed class RecordingHandler(Recording recording) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            recording.Queue.Enqueue((request.Headers, request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            return await base.SendAsync(request, cancellationToken);
        }
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
