using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Kalla.Domain;
using Kalla.Infrastructure;
using Kalla.Infrastructure.Gateway;
using Kalla.Infrastructure.Security;
using Kalla.TestKit;

namespace Kalla.Api.Tests;

/// <summary>
/// The <see cref="ResidencyCeilingHandler"/> on a client registered with <c>AddGatewayClient</c> (P0 #8; chat design §8.1,
/// identity design §4.7): no call leaves without an explicit ceiling and the service's credential key, and an answer from above the
/// ceiling is aborted before the caller reads it. The <see cref="GatewayRetryHandler"/> inside it sends a 502/504 once
/// more with the same ceiling, never a wider one (#390 Feature B point 1), and a connection failure with no response
/// once more within 300 ms, never after the first byte (chat design §8.3, B-17).
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

    [Theory]
    [InlineData(Residency.OnPrem, "bge-m3", "v1/embeddings")]
    [InlineData(Residency.Eu, "fors/chat-standard", "v1/chat/completions")]
    [InlineData(Residency.External, "fors/chat-advanced", "v1/chat/completions")]
    public async Task The_credential_key_is_sent_at_every_ceiling_up_to_the_maximum(Residency ceiling, string model, string path)
    {
        // B-37: one key per service (identity design §4.7); the fake accepts it at any ceiling up to its residencies.
        var recording = new RecordingHandler(fake.Server.CreateHandler());
        using var host = Build(recording, key: FakeGatewayFactory.ApiCredentialKey);
        var body = path.EndsWith("embeddings", StringComparison.Ordinal)
            ? $$"""{"model":"{{model}}","input":"Bygglov"}"""
            : $$"""{"model":"{{model}}","messages":[{"role":"user","content":"Hej"}]}""";

        using var response = await SendAsync(host, Post(path, body).WithMaxResidency(ceiling));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        recording.Calls.ShouldHaveSingleItem().ShouldBe((ResidencyCeiling.ToHeader(ceiling), FakeGatewayFactory.ApiCredentialKey, body));
        ResidencyCeiling.TryParse(response.Headers.GetValues(GatewayHeaders.Residency).Single(), out var served).ShouldBeTrue();
        ((int)served).ShouldBeLessThanOrEqualTo((int)ceiling);
    }

    [Fact]
    public async Task A_call_above_the_services_maximum_is_refused_before_sending_and_logged()
    {
        // A credential key that allows Eu at most (register M2): a public assistant's External call is a configuration gap, not sent.
        var logs = new CapturingLoggerProvider();
        var stub = new CountingHandler();
        using var host = Build(stub, logs: logs, key: FakeGatewayFactory.ApiCredentialKey, maxResidency: Residency.Eu);

        (await Should.ThrowAsync<GatewayKeyMissingException>(() => SendAsync(host, Post("v1/chat/completions").WithMaxResidency(Residency.External))))
            .Ceiling.ShouldBe(Residency.External);
        using var eu = await SendAsync(host, Post("v1/embeddings").WithMaxResidency(Residency.Eu));

        stub.Calls.ShouldBe(1);
        logs.Entries.Where(e => e.Category == typeof(ResidencyCeilingHandler).FullName).ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            e => e.Level.ShouldBe(LogLevel.Warning),
            e => e.Message.ShouldContain("External"),
            e => e.Message.ShouldContain("maximum Eu"));
    }

    [Fact]
    public async Task The_workers_maximum_is_Eu_whatever_the_configuration_says()
    {
        // CapGatewayCalls(Eu) is the worker's registration; Gateway:ServiceMaximum in configuration is not bound, and a
        // credential key that allows every residency does not raise the worker above Eu: an embedding profile's Eu ceiling
        // stays callable (B-37).
        var stub = new CountingHandler();
        using var host = Build(stub, key: FakeGatewayFactory.WorkerCredentialKey, cap: Residency.Eu);

        await Should.ThrowAsync<GatewayKeyMissingException>(() => SendAsync(host, Post("v1/chat/completions").WithMaxResidency(Residency.External)));
        using var eu = await SendAsync(host, Post("v1/embeddings").WithMaxResidency(Residency.Eu));
        using var onPrem = await SendAsync(host, Post("v1/embeddings").WithMaxResidency(Residency.OnPrem));

        stub.Calls.ShouldBe(2);
    }

    /// <summary>
    /// A host whose every gateway client, the context reader's too, goes to the FakeGateway: the ceiling comes from the
    /// credential key's residencies as the fake's context lists them (M-17 C9), not from a test seam.
    /// </summary>
    private (IHost Host, RecordingHandler Recording) BuildFromContext(string credentialMaxResidency)
    {
        // A derived factory is disposed with the class fixture.
        var gateway = fake.WithWebHostBuilder(web => web.UseSetting("FakeGateway:CredentialKeys:api:MaxResidency", credentialMaxResidency));
        var recording = new RecordingHandler(gateway.Server.CreateHandler());
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Configuration.AddInMemoryCollection([
            new("Gateway:BaseUrl", "http://fake-gateway"), new("Gateway:RetryDelay", "00:00:00"), new("Gateway:Key", FakeGatewayFactory.ApiCredentialKey),
        ]);
        builder.AddGatewayClient(Client).ConfigurePrimaryHttpMessageHandler(() => recording);
        builder.Services.AddHttpClient(GatewayContextReader.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => gateway.Server.CreateHandler());
        return (builder.Build(), recording);
    }

    [Fact]
    public async Task The_ceiling_is_the_credential_keys_residencies_from_the_context_and_no_call_sends_External_above_them()
    {
        // Done-when of M-17 C9: a credential key limited to OnPrem and Eu (register M2); no Gateway:MaxResidency exists.
        var (host, recording) = BuildFromContext("Eu");
        using var _ = host;
        var ceiling = host.Services.GetRequiredService<GatewayCeiling>();

        (await ceiling.MaximumAsync(Ct)).ShouldBe(Residency.Eu); // the first use waits for the context read
        (await ceiling.CapAsync(Residency.External, Ct)).ShouldBe(Residency.Eu); // a public assistant's level, lowered
        await Should.ThrowAsync<GatewayKeyMissingException>(() => SendAsync(host, Post("v1/chat/completions").WithMaxResidency(Residency.External)));
        using var eu = await SendAsync(host, Post("v1/embeddings").WithMaxResidency(Residency.Eu));

        recording.Calls.ShouldHaveSingleItem().Ceiling.ShouldBe("Eu"); // the External call never left the process
    }

    [Fact]
    public async Task The_context_read_names_the_organisation_and_the_integration_api()
    {
        // M-17 C11 and C12 (gateway v0.10.2): read with the ceiling, from the same context.
        var (host, _) = BuildFromContext("Eu");
        using var disposeHost = host;
        await host.Services.GetRequiredService<GatewayCeiling>().MaximumAsync(Ct);
        var features = host.Services.GetRequiredService<GatewayFeatures>();

        features.OrganisationName.ShouldBe("Umeå kommun");
        features.IntegrationApiUrl.ShouldBe("http://fake-gateway/integration/v1");
        features.OidcAuthority.ShouldBeNull(); // the fake's default: the gateway's sign-in is not set up yet
    }

    [Fact]
    public void The_organisation_name_the_integration_api_and_the_provider_address_count_only_under_their_features_and_empty_is_none()
    {
        const string authority = "https://idp.example.se/realms/ume";
        var features = new GatewayFeatures();
        features.Update(["organisation"]);
        features.UpdateGateway("Exempelstad", "https://llm-admin.example.se/integration/v1", authority);
        (features.OrganisationName, features.IntegrationApiUrl, features.OidcAuthority).ShouldBe((null, null, null)); // a gateway before v0.10.2

        features.Update([GatewayFeatures.OrganisationNameFeature, GatewayFeatures.IntegrationApiUrlFeature, GatewayFeatures.OidcAuthorityFeature]);
        features.UpdateGateway(" Exempelstad ", "https://llm-admin.example.se/integration/v1", $" {authority} ");
        (features.OrganisationName, features.IntegrationApiUrl, features.OidcAuthority)
            .ShouldBe(("Exempelstad", "https://llm-admin.example.se/integration/v1", authority));

        features.UpdateGateway(" ", null, null);
        (features.OrganisationName, features.IntegrationApiUrl, features.OidcAuthority).ShouldBe((null, null, null));
    }

    [Theory]
    [InlineData("http://idp.example.se/realms/ume")]
    [InlineData("/realms/ume")]
    [InlineData("idp.example.se")]
    [InlineData("https://user:secret@idp.example.se/realms/ume")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://idp.example.se/realms/\u0007ume")]
    [InlineData(" ")]
    public void A_provider_address_that_is_not_an_absolute_https_address_is_none(string value)
    {
        // M-20 S8: the address is offered in the setup wizard's sign-in form, so only what the gateway promises is taken.
        var features = new GatewayFeatures();
        features.Update([GatewayFeatures.OidcAuthorityFeature]);
        features.UpdateGateway(null, null, value);
        features.OidcAuthority.ShouldBeNull();
        features.UpdateGateway(null, null, "https://idp.example.se/" + new string('a', GatewayFeatures.OidcAuthorityMaxLength));
        features.OidcAuthority.ShouldBeNull(); // longer than the wizard takes
    }

    [Fact]
    public async Task A_key_that_allows_every_residency_lets_a_public_call_run_at_External()
    {
        var (host, _) = BuildFromContext("External");
        using var disposeHost = host;
        (await host.Services.GetRequiredService<GatewayCeiling>().CapAsync(Residency.External, Ct)).ShouldBe(Residency.External);
    }

    [Fact]
    public async Task Before_the_first_context_answer_every_ceiling_is_OnPrem()
    {
        // No context to read (the reader's client has no gateway behind it): fail closed.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Configuration.AddInMemoryCollection([new("Gateway:BaseUrl", "http://fake-gateway"), new("Gateway:Key", FakeGatewayFactory.ApiCredentialKey)]);
        var stub = new CountingHandler();
        builder.AddGatewayClient(Client).ConfigurePrimaryHttpMessageHandler(() => stub);
        builder.Services.AddHttpClient(GatewayContextReader.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new UnreachableHandler());
        using var host = builder.Build();
        var ceiling = host.Services.GetRequiredService<GatewayCeiling>();

        ceiling.Maximum.ShouldBe(Residency.OnPrem);
        (await ceiling.CapAsync(Residency.External, Ct)).ShouldBe(Residency.OnPrem);
        await Should.ThrowAsync<GatewayKeyMissingException>(() => SendAsync(host, Post("v1/embeddings").WithMaxResidency(Residency.Eu)));
        using var onPrem = await SendAsync(host, Post("v1/embeddings").WithMaxResidency(Residency.OnPrem));
        stub.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task A_context_read_that_hangs_ends_with_the_wait_and_the_call_goes_on_at_OnPrem_without_a_bare_cancellation()
    {
        // #691 review: a black-holed gateway (the connect hangs) must not throw TaskCanceledException into the caller.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Configuration.AddInMemoryCollection([
            new("Gateway:BaseUrl", "http://fake-gateway"), new("Gateway:Key", FakeGatewayFactory.ApiCredentialKey), new("Gateway:FirstContextWait", "00:00:00.200"),
        ]);
        var stub = new CountingHandler();
        builder.AddGatewayClient(Client).ConfigurePrimaryHttpMessageHandler(() => stub);
        builder.Services.AddHttpClient(GatewayContextReader.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new HangingHandler());
        using var host = builder.Build();
        var ceiling = host.Services.GetRequiredService<GatewayCeiling>();

        var waited = System.Diagnostics.Stopwatch.StartNew();
        (await ceiling.CapAsync(Residency.External, Ct)).ShouldBe(Residency.OnPrem);
        waited.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
        // The clean refusal above the ceiling, not a TaskCanceledException; and an OnPrem call goes.
        await Should.ThrowAsync<GatewayKeyMissingException>(() => SendAsync(host, Post("v1/embeddings").WithMaxResidency(Residency.Eu)));
        using var onPrem = await SendAsync(host, Post("v1/embeddings").WithMaxResidency(Residency.OnPrem));
        stub.Calls.ShouldBe(1);
    }

    /// <summary>Never answers until the request is cancelled: a gateway behind a firewall that drops packets.</summary>
    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    [Theory]
    [InlineData(new string[0], Residency.External)]
    [InlineData(new[] { "OnPrem" }, Residency.OnPrem)]
    [InlineData(new[] { "OnPrem", "Eu" }, Residency.Eu)]
    [InlineData(new[] { "Eu", "Mars" }, Residency.Eu)]
    [InlineData(new[] { "Mars" }, Residency.OnPrem)]
    public void The_credential_ceiling_is_the_highest_known_residency_and_an_empty_list_allows_all(string[] residencies, Residency expected) =>
        GatewayFeatures.HighestOf(residencies).ShouldBe(expected);

    /// <summary>Answers nothing: the gateway is not reachable.</summary>
    private sealed class UnreachableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("unreachable");
    }

    [Fact]
    public async Task A_call_with_a_key_other_than_the_services_is_refused_before_sending()
    {
        var stub = new CountingHandler();
        using var host = Build(stub, key: FakeGatewayFactory.ApiCredentialKey);
        var request = Post("v1/chat/completions").WithMaxResidency(Residency.Eu);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", FakeGatewayFactory.WorkerCredentialKey);

        await Should.ThrowAsync<GatewayCallRefusedException>(() => SendAsync(host, request));
        stub.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task The_fake_narrows_a_ceiling_above_the_credential_keys_residencies()
    {
        // As the gateway: the worker's key allows OnPrem and Eu, so an External ceiling is answered within Eu, and a model
        // served only at External has no target within it (403 max_residency_not_met).
        using var host = Build(fake.Server.CreateHandler(), key: FakeGatewayFactory.WorkerCredentialKey);

        using var advanced = await SendAsync(host, Post("v1/chat/completions", """{"model":"fors/chat-advanced","messages":[{"role":"user","content":"Hej"}]}""")
            .WithMaxResidency(Residency.External));
        using var response = await SendAsync(host, Post("v1/chat/completions", """{"model":"fors/chat-standard","messages":[{"role":"user","content":"Hej"}]}""")
            .WithMaxResidency(Residency.External));

        advanced.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await advanced.Content.ReadAsStringAsync(Ct)).ShouldContain("max_residency_not_met");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        ResidencyCeiling.TryParse(response.Headers.GetValues(GatewayHeaders.Residency).Single(), out var served).ShouldBeTrue();
        ((int)served).ShouldBeLessThanOrEqualTo((int)Residency.Eu);
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
        // No deployment answers GET /v1/models, so the gateway sends no x-fors-residency (V8's check, item 13).
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

        using var notMet = await SendAsync(host, Post("v1/chat/completions", """{"model":"fors/chat-eu","messages":[]}""").WithMaxResidency(Residency.OnPrem));
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
        recording.Calls.Select(c => c.Key).Distinct().ShouldBe([FakeGatewayFactory.ApiCredentialKey]);
        recording.Calls.Select(c => c.Body).Distinct().Count().ShouldBe(1);
    }

    [Fact]
    public async Task A_retry_that_succeeds_is_the_answer_and_its_residency_is_checked()
    {
        // The first attempt answers 502, the second goes through to the fake; the ceiling check sees the final answer.
        var recording = new RecordingHandler(fake.Server.CreateHandler()) { FailFirst = HttpStatusCode.BadGateway };
        using var host = Build(recording);

        using var response = await SendAsync(host, Post("v1/chat/completions", Chat("fors/chat-standard")).WithMaxResidency(Residency.Eu));

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
    [InlineData("fors/chat-eu", HttpStatusCode.Forbidden)] // 403 max_residency_not_met: can never work
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

    [Theory]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.GatewayTimeout, true)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, false)]
    [InlineData(HttpStatusCode.TooManyRequests, false)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    public void The_shared_retry_decision_is_502_and_504_only(HttpStatusCode status, bool retried) =>
        GatewayRetryHandler.Retries(status).ShouldBe(retried);

    [Fact]
    public async Task A_client_with_its_own_retry_can_leave_the_handler_out()
    {
        var recording = new RecordingHandler(fake.Server.CreateHandler());
        using var host = Build(recording, retryFailed: false);

        using var response = await SendAsync(host, Post("v1/chat/completions", Chat("fake-fail-500")).WithMaxResidency(Residency.OnPrem));

        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        recording.Calls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_connection_failure_is_retried_once_with_the_same_request()
    {
        var recording = new RecordingHandler(fake.Server.CreateHandler()) { RefuseFirst = true };
        using var host = Build(recording);

        using var response = await SendAsync(host, Post("v1/chat/completions", Chat("fors/chat-standard")).WithMaxResidency(Residency.Eu));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.GetValues(GatewayHeaders.Residency).ShouldBe(["Eu"]);
        recording.Calls.Count.ShouldBe(2);
        recording.Calls.Select(c => c.Ceiling).ShouldBe(["Eu", "Eu"]); // never wider
        recording.Calls.Select(c => c.Key).Distinct().ShouldBe([FakeGatewayFactory.ApiCredentialKey]);
        recording.Calls.Select(c => c.Body).Distinct().Count().ShouldBe(1);
    }

    [Fact]
    public async Task A_refused_tcp_connection_is_retried_once_then_reaches_the_caller()
    {
        // A real SocketsHttpHandler against a port nobody listens on: the refusal is classified as a connection failure.
        var port = ClosedPort();
        var connects = 0;
        var sockets = new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                Interlocked.Increment(ref connects);
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        using var host = Build(sockets);

        var thrown = await Should.ThrowAsync<HttpRequestException>(() =>
            SendAsync(host, Post("v1/chat/completions", Chat("fors/chat-standard")).WithMaxResidency(Residency.Eu)));

        thrown.HttpRequestError.ShouldBe(HttpRequestError.ConnectionError);
        connects.ShouldBe(2); // one retry in all
    }

    [Fact]
    public async Task One_retry_in_all_a_connection_failure_then_a_502_reaches_the_caller()
    {
        var recording = new RecordingHandler(fake.Server.CreateHandler()) { RefuseFirst = true };
        using var host = Build(recording);

        using var response = await SendAsync(host, Post("v1/chat/completions", Chat("fake-fail-500")).WithMaxResidency(Residency.OnPrem));

        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        recording.Calls.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_stream_that_fails_after_its_first_byte_is_never_retried()
    {
        var breaking = new BreakingStreamHandler();
        using var host = Build(breaking);

        using var response = await SendAsync(host, Post("v1/chat/completions", Chat("fors/chat-standard")).WithMaxResidency(Residency.OnPrem));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var stream = await response.Content.ReadAsStreamAsync(Ct);
        var buffer = new byte[64];
        (await stream.ReadAsync(buffer, Ct)).ShouldBeGreaterThan(0);
        await Should.ThrowAsync<IOException>(async () => (await stream.ReadAsync(buffer, Ct)).ShouldBe(0));

        breaking.Calls.ShouldBe(1);
    }

    [Theory]
    [InlineData(HttpRequestError.ConnectionError, true)]
    [InlineData(HttpRequestError.NameResolutionError, false)]
    [InlineData(HttpRequestError.SecureConnectionError, false)]
    [InlineData(HttpRequestError.ResponseEnded, false)] // the gateway may already have begun answering
    [InlineData(HttpRequestError.InvalidResponse, false)]
    [InlineData(HttpRequestError.HttpProtocolError, false)]
    [InlineData(HttpRequestError.Unknown, false)]
    public void The_shared_retry_decision_on_failures_is_a_connection_error_only(HttpRequestError error, bool retried) =>
        GatewayRetryHandler.Retries(new HttpRequestException(error, "x")).ShouldBe(retried);

    [Fact]
    public async Task A_connection_failure_on_a_client_with_its_own_retry_is_not_retried_here()
    {
        var recording = new RecordingHandler(fake.Server.CreateHandler()) { RefuseFirst = true };
        using var host = Build(recording, retryFailed: false);

        await Should.ThrowAsync<HttpRequestException>(() =>
            SendAsync(host, Post("v1/chat/completions", Chat("fors/chat-standard")).WithMaxResidency(Residency.Eu)));
        recording.Calls.Count.ShouldBe(1);
    }

    private static int ClosedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string Chat(string model) => $$"""{"model":"{{model}}","messages":[{"role":"user","content":"Hej"}]}""";

    private static IHost Build(HttpMessageHandler primary, CapturingLoggerProvider? logs = null, bool retryFailed = true,
        string key = FakeGatewayFactory.ApiCredentialKey, Residency? maxResidency = null, Residency? cap = null)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Configuration.AddInMemoryCollection([
            new("Gateway:BaseUrl", "http://fake-gateway"),
            new("Gateway:RetryDelay", "00:00:00"),
            new("Gateway:Key", key),
            new("Gateway:ServiceMaximum", "External"), // ignored: the maximum is set in code only
        ]);
        builder.Logging.ClearProviders().AddProvider(logs ?? new CapturingLoggerProvider());
        if (cap is { } serviceMaximum)
        {
            builder.CapGatewayCalls(serviceMaximum);
        }

        builder.AddGatewayClient(Client, retryFailed).ConfigurePrimaryHttpMessageHandler(() => primary);
        // The credential key's residencies (M-17 C9): up to maxResidency, all by default.
        builder.Services.UseGatewayCredentialCeiling(maxResidency ?? Residency.External);
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

        /// <summary>The first request fails as a refused connection: no response at all.</summary>
        public bool RefuseFirst { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var ceiling = request.Headers.TryGetValues(GatewayHeaders.MaxResidency, out var values) ? string.Join(",", values) : null;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add((ceiling, request.Headers.Authorization?.Parameter, body));
            if (Calls.Count == 1 && RefuseFirst)
            {
                throw new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused (fake-gateway:80)",
                    new SocketException((int)SocketError.ConnectionRefused));
            }

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

    /// <summary>Answers 200 within the ceiling with a body that breaks after its first bytes.</summary>
    private sealed class BreakingStreamHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BreakingStream()) };
            response.Headers.Add(GatewayHeaders.Residency, "OnPrem");
            return Task.FromResult(response);
        }
    }

    private sealed class BreakingStream : Stream
    {
        private bool _sent;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_sent)
            {
                throw new IOException("The connection was reset after the first event.");
            }

            _sent = true;
            var first = "data: {\"choices\":[]}\n\n"u8;
            first.CopyTo(buffer.AsSpan(offset, count));
            return first.Length;
        }
    }
}
