using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ume.Rag.Domain;
using Ume.Rag.Infrastructure;
using Ume.Rag.Infrastructure.Gateway;
using Ume.Rag.Infrastructure.Security;
using Ume.Rag.TestKit;

namespace Ume.Rag.Api.Tests;

/// <summary>
/// The <see cref="ResidencyCeilingHandler"/> on a client registered with <c>AddGatewayClient</c> (P0 #8; chat design §8.1,
/// identity design §4.7): no call leaves without an explicit ceiling and that level's key, and an answer from above the
/// ceiling is aborted before the caller reads it. The <see cref="GatewayRetryHandler"/> inside it sends a 502/504 once
/// more with the same ceiling, never a wider one (#390 Feature B point 1).
/// </summary>
public sealed class GatewayClientTests(FakeGatewayFactory fake) : IClassFixture<FakeGatewayFactory>
{
    private const string Client = "gateway-test";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_call_without_a_ceiling_is_refused_before_sending()
    {
        var stub = new CountingHandler();
        using var host = Build(stub);

        await Should.ThrowAsync<GatewayCallRefusedException>(() => SendAsync(host, Post("v1/embeddings")));
        stub.Calls.ShouldBe(0);
    }

    [Theory]
    [InlineData("Any")]
    [InlineData("1")]
    public async Task A_malformed_ceiling_is_refused_before_sending(string ceiling)
    {
        var stub = new CountingHandler();
        using var host = Build(stub);
        var request = Post("v1/embeddings");
        request.Headers.TryAddWithoutValidation(GatewayHeaders.MaxResidency, ceiling);

        await Should.ThrowAsync<GatewayCallRefusedException>(() => SendAsync(host, request));
        stub.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_call_with_another_levels_key_is_refused_before_sending()
    {
        var stub = new CountingHandler();
        using var host = Build(stub);
        var request = Post("v1/chat/completions").WithMaxResidency(Residency.OnPrem);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", FakeGatewayFactory.ExternalKey);

        await Should.ThrowAsync<GatewayCallRefusedException>(() => SendAsync(host, request));
        stub.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_process_without_the_key_of_a_level_cannot_call_at_it()
    {
        // The worker holds OnPrem and Eu only (simplification pass §6).
        var stub = new CountingHandler();
        using var host = Build(stub, levels: ["OnPrem", "Eu"]);

        (await Should.ThrowAsync<GatewayKeyMissingException>(() => SendAsync(host, Post("v1/chat/completions").WithMaxResidency(Residency.External)))).Ceiling.ShouldBe(Residency.External);
        stub.Calls.ShouldBe(0);
    }

    [Theory]
    [InlineData(Residency.OnPrem, "bge-m3", "v1/embeddings")]
    [InlineData(Residency.Eu, "ume/chat-standard", "v1/chat/completions")]
    [InlineData(Residency.External, "ume/chat-advanced", "v1/chat/completions")]
    public async Task The_level_key_is_added_and_an_answer_within_the_ceiling_passes(Residency ceiling, string model, string path)
    {
        using var host = Build(fake.Server.CreateHandler());
        var body = path.EndsWith("embeddings", StringComparison.Ordinal)
            ? $$"""{"model":"{{model}}","input":"Bygglov"}"""
            : $$"""{"model":"{{model}}","messages":[{"role":"user","content":"Hej"}]}""";

        using var response = await SendAsync(host, Post(path, body).WithMaxResidency(ceiling));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        ResidencyCeiling.TryParse(response.Headers.GetValues(GatewayHeaders.Residency).Single(), out var served).ShouldBeTrue();
        ((int)served).ShouldBeLessThanOrEqualTo((int)ceiling);
    }

    [Theory]
    [InlineData("v1/chat/completions", """{"model":"fake-wrong-residency","messages":[{"role":"user","content":"Hej"}]}""")]
    [InlineData("v1/chat/completions", """{"model":"fake-wrong-residency","stream":true,"messages":[{"role":"user","content":"Hej"}]}""")]
    [InlineData("v1/embeddings", """{"model":"fake-wrong-residency-embed","input":"Hej"}""")]
    public async Task An_answer_from_above_the_ceiling_is_aborted_with_event_5003(string path, string body)
    {
        var logs = new CapturingLoggerProvider();
        using var host = Build(fake.Server.CreateHandler(), logs: logs);

        var error = await Should.ThrowAsync<GatewayResidencyException>(() => SendAsync(host, Post(path, body).WithMaxResidency(Residency.OnPrem)));

        error.Ceiling.ShouldBe(Residency.OnPrem);
        error.Observed.ShouldBe("External");
        logs.Entries.ShouldContain(e => e.Category == SecurityEvents.Category && e.EventId.Id == 5003);
    }

    [Fact]
    public async Task An_answer_without_a_residency_header_is_aborted()
    {
        using var host = Build(new CountingHandler { Residency = null });
        await Should.ThrowAsync<GatewayResidencyException>(() => SendAsync(host, Post("v1/embeddings").WithMaxResidency(Residency.OnPrem)));
    }

    [Fact]
    public async Task The_model_list_needs_no_residency_header_but_still_its_ceiling_and_key()
    {
        // No deployment answers GET /v1/models, so the gateway sends no x-ume-residency (V8's check, item 13).
        var counting = new CountingHandler { Residency = null };
        using var host = Build(counting);
        using var list = await SendAsync(host, new HttpRequestMessage(HttpMethod.Get, "v1/models").WithMaxResidency(Residency.Eu));
        list.StatusCode.ShouldBe(HttpStatusCode.OK);
        await Should.ThrowAsync<GatewayCallRefusedException>(() => SendAsync(host, new HttpRequestMessage(HttpMethod.Get, "v1/models")));
        await Should.ThrowAsync<GatewayResidencyException>(() => SendAsync(host, Post("v1/models").WithMaxResidency(Residency.Eu)));
        counting.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task Refusals_from_the_gateway_reach_the_caller_unchanged()
    {
        using var host = Build(fake.Server.CreateHandler());

        using var notMet = await SendAsync(host, Post("v1/chat/completions", """{"model":"ume/chat-eu","messages":[]}""").WithMaxResidency(Residency.OnPrem));
        notMet.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        using var drained = await SendAsync(host, Post("v1/chat/completions", """{"model":"fake-drained","messages":[]}""").WithMaxResidency(Residency.OnPrem));
        drained.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        drained.Headers.RetryAfter.ShouldNotBeNull();
    }

    [Theory]
    [InlineData("fake-fail-500", Residency.OnPrem, HttpStatusCode.BadGateway)]
    [InlineData("fake-fail-timeout", Residency.OnPrem, HttpStatusCode.GatewayTimeout)]
    [InlineData("fake-fail-fallback", Residency.OnPrem, HttpStatusCode.BadGateway)] // an Eu target exists, but above the ceiling
    public async Task A_502_or_504_is_retried_once_with_the_same_ceiling_and_key(string model, Residency ceiling, HttpStatusCode status)
    {
        var recording = new RecordingHandler(fake.Server.CreateHandler());
        using var host = Build(recording);

        using var response = await SendAsync(host, Post("v1/chat/completions", Chat(model)).WithMaxResidency(ceiling));

        response.StatusCode.ShouldBe(status);
        recording.Calls.Count.ShouldBe(2);
        recording.Calls.ShouldAllBe(c => c.Ceiling == ResidencyCeiling.ToHeader(ceiling)); // never wider
        recording.Calls.Select(c => c.Key).Distinct().ShouldBe([FakeGatewayFactory.Keys[ResidencyCeiling.ToHeader(ceiling)]]);
        recording.Calls.Select(c => c.Body).Distinct().Count().ShouldBe(1);
    }

    [Fact]
    public async Task A_retry_that_succeeds_is_the_answer_and_its_residency_is_checked()
    {
        // The first attempt answers 502, the second goes through to the fake; the ceiling check sees the final answer.
        var recording = new RecordingHandler(fake.Server.CreateHandler()) { FailFirst = HttpStatusCode.BadGateway };
        using var host = Build(recording);

        using var response = await SendAsync(host, Post("v1/chat/completions", Chat("ume/chat-standard")).WithMaxResidency(Residency.Eu));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.GetValues(GatewayHeaders.Residency).ShouldBe(["Eu"]);
        recording.Calls.Select(c => c.Ceiling).ShouldBe(["Eu", "Eu"]);

        var wrong = new RecordingHandler(fake.Server.CreateHandler()) { FailFirst = HttpStatusCode.GatewayTimeout };
        using var wrongHost = Build(wrong);
        await Should.ThrowAsync<GatewayResidencyException>(() =>
            SendAsync(wrongHost, Post("v1/chat/completions", Chat("fake-wrong-residency")).WithMaxResidency(Residency.OnPrem)));
        wrong.Calls.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData("ume/chat-eu", HttpStatusCode.Forbidden)] // 403 max_residency_not_met: can never work
    [InlineData("fake-drained", HttpStatusCode.ServiceUnavailable)] // 503 + Retry-After: the caller waits
    [InlineData("no-such-model", HttpStatusCode.NotFound)]
    public async Task Other_refusals_are_not_retried(string model, HttpStatusCode status)
    {
        var recording = new RecordingHandler(fake.Server.CreateHandler());
        using var host = Build(recording);

        using var response = await SendAsync(host, Post("v1/chat/completions", Chat(model)).WithMaxResidency(Residency.OnPrem));

        response.StatusCode.ShouldBe(status);
        recording.Calls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_client_with_its_own_retry_can_leave_the_handler_out()
    {
        var recording = new RecordingHandler(fake.Server.CreateHandler());
        using var host = Build(recording, retryFailed: false);

        using var response = await SendAsync(host, Post("v1/chat/completions", Chat("fake-fail-500")).WithMaxResidency(Residency.OnPrem));

        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        recording.Calls.Count.ShouldBe(1);
    }

    private static string Chat(string model) => $$"""{"model":"{{model}}","messages":[{"role":"user","content":"Hej"}]}""";

    private static IHost Build(HttpMessageHandler primary, string[]? levels = null, CapturingLoggerProvider? logs = null, bool retryFailed = true)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Configuration.AddInMemoryCollection([
            new("Gateway:BaseUrl", "http://fake-gateway"),
            new("Gateway:RetryDelay", "00:00:00"),
            .. FakeGatewayFactory.Keys.Where(k => levels is null || levels.Contains(k.Key))
                .Select(k => new KeyValuePair<string, string?>($"Gateway:Keys:{k.Key}", k.Value)),
        ]);
        builder.Logging.ClearProviders().AddProvider(logs ?? new CapturingLoggerProvider());
        builder.AddGatewayClient(Client, retryFailed).ConfigurePrimaryHttpMessageHandler(() => primary);
        return builder.Build();
    }

    private static Task<HttpResponseMessage> SendAsync(IHost host, HttpRequestMessage request) =>
        host.Services.GetRequiredService<IHttpClientFactory>().CreateClient(Client).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);

    private static HttpRequestMessage Post(string path, string body = """{"model":"bge-m3","input":"x"}""") =>
        new(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>
    /// Records each request that reaches the gateway (ceiling, key, body) and passes it on; <see cref="FailFirst"/> answers
    /// the first one itself with that status.
    /// </summary>
    private sealed class RecordingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public List<(string? Ceiling, string? Key, string Body)> Calls { get; } = [];
        public HttpStatusCode? FailFirst { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var ceiling = request.Headers.TryGetValues(GatewayHeaders.MaxResidency, out var values) ? string.Join(",", values) : null;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add((ceiling, request.Headers.Authorization?.Parameter, body));
            return Calls.Count == 1 && FailFirst is { } status
                ? new HttpResponseMessage(status) { Content = new StringContent("""{"error":{"code":"all_providers_failed"}}""") }
                : await base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>Counts what would reach the gateway; answers 200 with the given residency header.</summary>
    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Residency { get; init; } = "OnPrem";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            if (Residency is not null)
            {
                response.Headers.Add(GatewayHeaders.Residency, Residency);
            }

            return Task.FromResult(response);
        }
    }
}
