extern alias fakegateway;

using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Kalla.Domain;
using Kalla.Infrastructure;
using Kalla.Infrastructure.Gateway;
using Kalla.TestKit;
using FakeUsage = fakegateway::Kalla.FakeGateway.FakeUsage;

namespace Kalla.Api.Tests;

/// <summary>
/// B-42 (#580, gateway phase 2; identity §4.7, gateway-changes §5.2, §5.7): the shared gateway client acting as an object's
/// assigned key, the echo check (event 5011) and the typed refusal, against the FakeGateway's dev assigned keys; and the
/// FakeGateway resolving <c>x-ume-on-behalf-of</c> as the gateway does and recording each request under the key it ran as.
/// </summary>
public sealed class ActingAsKeyTests(FakeGatewayFactory fake) : IClassFixture<FakeGatewayFactory>
{
    private const string Client = "acting-test";

    // The FakeGateway's dev assigned keys (its appsettings.json).
    private static readonly Guid Chatbot = new("019a0000-0000-7000-8000-00000000b001"); // Eu only; ume/chat-standard, ume/chat-eu, bge-m3
    private static readonly Guid OnPromOnly = new("019a0000-0000-7000-8000-00000000b002");
    private static readonly Guid Paused = new("019a0000-0000-7000-8000-00000000b003"); // Disabled
    private static readonly Guid ExternalOnly = new("019a0000-0000-7000-8000-00000000b004");
    private static readonly Guid Revoked = new("019a0000-0000-7000-8000-00000000b005");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Chat(string model) => $$"""{"model":"{{model}}","messages":[{"role":"user","content":"Hej"}]}""";

    private static HttpRequestMessage Post(string body, GatewayCall? call = null, Residency ceiling = Residency.Eu)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.WithMaxResidency(ceiling);
        return call is null ? request : request.For(call);
    }

    /// <summary>A host with the shared gateway client over <paramref name="primary"/>; <paramref name="features"/> as if read from the context.</summary>
    private static IHost Build(HttpMessageHandler primary, string[]? features = null, bool requireEcho = true, CapturingLoggerProvider? logs = null,
        string key = FakeGatewayFactory.ApiCredentialKey)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Configuration.AddInMemoryCollection([
            new("Gateway:BaseUrl", "http://fake-gateway"),
            new("Gateway:RetryDelay", "00:00:00"),
            new("Gateway:Key", key),
            new("Gateway:RequireDelegationEcho", requireEcho ? "true" : "false"),
        ]);
        builder.Logging.ClearProviders().AddProvider(logs ?? new CapturingLoggerProvider());
        builder.AddGatewayClient(Client).ConfigurePrimaryHttpMessageHandler(() => primary);
        builder.Services.UseGatewayCredentialCeiling();
        builder.Services.AddHttpClient(GatewayContextReader.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => primary);
        var host = builder.Build();
        if (features is not null)
        {
            host.Services.GetRequiredService<GatewayFeatures>().Update(features);
        }

        return host;
    }

    private static Task<HttpResponseMessage> SendAsync(IHost host, HttpRequestMessage request) =>
        host.Services.GetRequiredService<IHttpClientFactory>().CreateClient(Client).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);

    private static string[] Acting => [GatewayFeatures.ActAsKeyFeature];

    [Fact]
    public async Task Each_service_reads_the_features_from_its_own_context_and_an_unchanged_one_is_a_304()
    {
        var recording = new Recording(fake.Server.CreateHandler());
        using var host = Build(recording);
        var reader = host.Services.GetRequiredService<GatewayContextReader>();

        (await reader.ReadAsync(Ct)).ShouldBeTrue();
        host.Services.GetRequiredService<GatewayFeatures>().ActAsKey.ShouldBeTrue();
        (await reader.ReadAsync(Ct)).ShouldBeTrue();
        recording.Requests.Select(r => r.Status).ShouldBe([HttpStatusCode.OK, HttpStatusCode.NotModified]);
        recording.Requests.ShouldAllBe(r => r.OnBehalfOf == null && r.Path == "/v1/ume/context");
    }

    [Fact]
    public async Task A_call_for_an_objects_key_runs_as_it_and_the_fake_records_it_under_that_key()
    {
        var recording = new Recording(fake.Server.CreateHandler());
        using var host = Build(recording, Acting);
        var runId = Guid.NewGuid().ToString();
        var request = Post(Chat("ume/chat-eu"), new GatewayCall(Chatbot, "chat-answer", AssistantId: Guid.NewGuid()));
        request.Headers.Add(FakeUsage.RunIdHeader, runId);
        request.Headers.Add(FakeUsage.StepHeader, "chat-answer");

        using var response = await SendAsync(host, request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        recording.Requests.Single().OnBehalfOf.ShouldBe(Chatbot.ToString("D"));
        var record = fake.Services.GetRequiredService<FakeUsage>().Records.Single(r => r.RunId == runId);
        (record.KeyId, record.Step, record.Model).ShouldBe((Chatbot, "chat-answer", "ume/chat-eu"));
        record.ViaKeyId.ShouldNotBeNull().ShouldNotBe(Chatbot);
    }

    [Fact]
    public async Task No_header_without_act_as_key_or_without_a_key_and_never_on_the_context()
    {
        var recording = new Recording(fake.Server.CreateHandler());
        using (var host = Build(recording, features: []))
        using (var response = await SendAsync(host, Post(Chat("ume/chat-eu"), new GatewayCall(Chatbot))))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using (var host = Build(recording, Acting))
        {
            using var keyless = await SendAsync(host, Post(Chat("ume/chat-eu"), new GatewayCall(null)));
            using var plain = await SendAsync(host, Post(Chat("ume/chat-eu")));
            using var context = await SendAsync(host, new HttpRequestMessage(HttpMethod.Get, "v1/ume/context").For(new GatewayCall(Chatbot)));
            context.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        recording.Requests.ShouldAllBe(r => r.OnBehalfOf == null);
        recording.Requests.Count.ShouldBe(4);
    }

    [Fact]
    public async Task A_missing_echo_aborts_the_call_with_event_5011_unless_the_check_is_off()
    {
        await using var dropping = fake.WithWebHostBuilder(web => web.UseSetting("FakeGateway:DropOnBehalfOfEcho", "true"));
        var logs = new CapturingLoggerProvider();
        var assistant = Guid.NewGuid();
        using (var host = Build(dropping.Server.CreateHandler(), Acting, logs: logs))
        {
            var refused = await Should.ThrowAsync<GatewayDelegationEchoException>(() =>
                SendAsync(host, Post(Chat("ume/chat-eu"), new GatewayCall(Chatbot, "chat-answer", AssistantId: assistant))));
            refused.GatewayKeyId.ShouldBe(Chatbot);
        }

        var entry = logs.Entries.Single(e => e.EventId.Id == 5011);
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Message.ShouldContain(Chatbot.ToString());
        entry.Message.ShouldContain(assistant.ToString());

        using var lenient = Build(dropping.Server.CreateHandler(), Acting, requireEcho: false);
        using var passed = await SendAsync(lenient, Post(Chat("ume/chat-eu"), new GatewayCall(Chatbot)));
        passed.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_echo_is_compared_as_a_guid(bool upperCase)
    {
        var echo = upperCase ? Chatbot.ToString("D").ToUpperInvariant() : Guid.NewGuid().ToString("D");
        using var host = Build(new Answering(HttpStatusCode.OK, echo: echo), Acting);

        if (upperCase)
        {
            using var response = await SendAsync(host, Post(Chat("ume/chat-eu"), new GatewayCall(Chatbot)));
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        else
        {
            (await Should.ThrowAsync<GatewayDelegationEchoException>(() => SendAsync(host, Post(Chat("ume/chat-eu"), new GatewayCall(Chatbot))))).Echo.ShouldBe(echo);
        }
    }

    [Fact]
    public async Task A_refused_key_is_one_typed_refusal_and_never_retried()
    {
        await using var gateway = fake.WithWebHostBuilder(web =>
        {
            web.UseSetting("FakeGateway:AssignedKeys:0:BudgetExceeded", "Team");
            web.UseSetting("FakeGateway:AssignedKeys:1:Status", "Expired");
        });
        var recording = new Recording(gateway.Server.CreateHandler());
        using var host = Build(recording, Acting);

        async Task<GatewayKeyRefusedException> RefusedAsync(Guid key, string model = "ume/chat-eu", Residency ceiling = Residency.Eu) =>
            await Should.ThrowAsync<GatewayKeyRefusedException>(() => SendAsync(host, Post(Chat(model), new GatewayCall(key), ceiling)));

        (await RefusedAsync(Guid.NewGuid())).ShouldSatisfyAllConditions(e => e.Status.ShouldBe(403), e => e.Code.ShouldBe("delegation_not_allowed"));
        (await RefusedAsync(Paused)).ShouldSatisfyAllConditions(e => e.Status.ShouldBe(403), e => e.Code.ShouldBe("key_disabled"));
        (await RefusedAsync(OnPromOnly, "ume/chat-onprem", Residency.OnPrem)).ShouldSatisfyAllConditions(e => e.Status.ShouldBe(403), e => e.Code.ShouldBe("key_expired"));
        (await RefusedAsync(Revoked)).ShouldSatisfyAllConditions(e => e.Status.ShouldBe(403), e => e.Code.ShouldBe("key_revoked"));
        var budget = await RefusedAsync(Chatbot);
        (budget.Status, budget.Code, budget.GatewayKeyId).ShouldBe((402, "budget_exceeded", Chatbot));
        budget.GatewayMessage.ShouldNotBeNull().ShouldStartWith("The team's budget");
        recording.Requests.Count.ShouldBe(5); // one request each: never retried
    }

    [Fact]
    public async Task The_worker_key_and_an_external_only_key_share_no_residency_so_acting_is_not_allowed()
    {
        using var host = Build(fake.Server.CreateHandler(), Acting, key: FakeGatewayFactory.WorkerCredentialKey);
        var refused = await Should.ThrowAsync<GatewayKeyRefusedException>(() => SendAsync(host, Post(Chat("ume/chat-eu"), new GatewayCall(ExternalOnly))));
        (refused.Status, refused.Code).ShouldBe((403, "delegation_not_allowed"));
    }

    [Fact]
    public async Task A_bad_request_on_an_acted_call_is_logged_at_error_and_returned()
    {
        var logs = new CapturingLoggerProvider();
        var stub = new Answering(HttpStatusCode.BadRequest, """{"error":{"code":"invalid_request","message":"x-ume-on-behalf-of must be one key id"}}""");
        using var host = Build(stub, Acting, logs: logs);

        using var response = await SendAsync(host, Post(Chat("ume/chat-eu"), new GatewayCall(Chatbot)));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        stub.Calls.ShouldBe(1);
        logs.Entries.ShouldContain(e => e.Level == LogLevel.Error && e.Message.Contains("invalid_request"));
    }

    [Fact]
    public async Task The_model_list_as_a_key_lists_its_models_within_both_keys_residencies()
    {
        using var host = Build(fake.Server.CreateHandler(), Acting);
        using var request = new HttpRequestMessage(HttpMethod.Get, "v1/models").WithMaxResidency(Residency.External).For(new GatewayCall(Chatbot));

        using var response = await SendAsync(host, request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var ids = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("data").EnumerateArray()
            .Select(m => m.GetProperty("id").GetString()).ToList();
        ids.ShouldBe(["ume/chat-standard", "ume/chat-eu"], ignoreOrder: true); // bge-m3 runs OnPrem only, outside the key's Eu
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("019a0000-0000-7000-8000-00000000b001, 019a0000-0000-7000-8000-00000000b001")]
    public async Task The_fake_refuses_a_malformed_or_repeated_header_and_a_key_that_is_no_credential_key(string header)
    {
        using var client = fake.CreateClient();
        using var malformed = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = new StringContent(Chat("ume/chat-eu"), Encoding.UTF8, "application/json") };
        malformed.Headers.Authorization = new("Bearer", FakeGatewayFactory.ApiCredentialKey);
        malformed.Headers.Add("x-ume-max-residency", "Eu");
        foreach (var value in header.Split(", "))
        {
            malformed.Headers.TryAddWithoutValidation(GatewayHeaders.OnBehalfOf, value);
        }

        (await client.SendAsync(malformed, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var ordinary = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = new StringContent(Chat("ume/chat-eu"), Encoding.UTF8, "application/json") };
        ordinary.Headers.Authorization = new("Bearer", FakeGatewayFactory.EuKey);
        ordinary.Headers.Add("x-ume-max-residency", "Eu");
        ordinary.Headers.Add(GatewayHeaders.OnBehalfOf, Chatbot.ToString());
        (await client.SendAsync(ordinary, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    /// <summary>Passes each request on and records its path, <c>x-ume-on-behalf-of</c> and the answer's status.</summary>
    private sealed class Recording(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public List<(string Path, string? OnBehalfOf, HttpStatusCode Status)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            Requests.Add((request.RequestUri!.AbsolutePath, request.Headers.TryGetValues(GatewayHeaders.OnBehalfOf, out var v) ? string.Join(",", v) : null, response.StatusCode));
            return response;
        }
    }

    /// <summary>Answers every request itself with <paramref name="status"/>, the body, an <c>x-ume-residency</c> of Eu and the echo given.</summary>
    private sealed class Answering(HttpStatusCode status, string body = "{}", string? echo = null) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            response.Headers.Add(GatewayHeaders.Residency, "Eu");
            if (echo is not null)
            {
                response.Headers.Add(GatewayHeaders.OnBehalfOf, echo);
            }

            return Task.FromResult(response);
        }
    }
}
