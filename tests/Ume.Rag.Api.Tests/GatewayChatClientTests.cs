using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ume.Rag.Answering.Chat;
using Ume.Rag.Domain;
using Ume.Rag.Infrastructure.Gateway;
using Ume.Rag.TestKit;

namespace Ume.Rag.Api.Tests;

/// <summary>
/// #275: <c>temperature</c> is sent only when the assistant sets one (the gateway refuses it for models that deprecate it),
/// and a refusal the chat maps to 502 leaves one Warning with the gateway's status, code and request id, without body text
/// or address. B-38 (chat design §8.3): a stream asks for its cost and ends with the comment's, which is never an event; a
/// JSON answer gives its usage as soon as it answers. #695: a JSON answer's headers come only after the whole answer, so the
/// headers timeout bounds a stream only, and a JSON answer gets <c>Chat:MaxAnswerDurationSeconds</c>.
/// </summary>
public sealed class GatewayChatClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Gateway(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<JsonElement> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement.Clone());
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            response.Headers.Add("x-request-id", "req-7f3a");
            return response;
        }
    }

    private sealed class Clients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("http://gateway-secret.invalid/") };
    }

    private const string Answer = """{"choices":[{"message":{"content":"Svar."},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":1}}""";

    [Fact]
    public async Task Temperature_is_left_out_unless_the_assistant_sets_it()
    {
        using var gateway = new Gateway(HttpStatusCode.OK, Answer);
        using var logs = new CapturingLoggerProvider();
        using var loggers = LoggerFactory.Create(b => b.AddProvider(logs));
        var client = new GatewayChatClient(new Clients(gateway), Options.Create(new ChatOptions()), loggers.CreateLogger<GatewayChatClient>());

        await client.CompleteAsync("claude-opus-5-5", Residency.Eu, [new ChatMessageIn("user", "Hej?")], 64, null, Guid.CreateVersion7(), "chat-answer", Ct);
        await client.CompleteAsync("gpt-6.1-sol", Residency.Eu, [new ChatMessageIn("user", "Hej?")], 64, 0.4, Guid.CreateVersion7(), "chat-answer", Ct);

        gateway.Requests[0].TryGetProperty("temperature", out _).ShouldBeFalse();
        gateway.Requests[0].TryGetProperty("stream_options", out _).ShouldBeFalse();
        gateway.Requests[1].GetProperty("temperature").GetDouble().ShouldBe(0.4);
        AssistantChatSettings.Parse("{}").Temperature.ShouldBeNull();
        AssistantChatSettings.Parse("""{"answer":{"temperature":0.7}}""").Temperature.ShouldBe(0.7);
    }

    [Fact]
    public async Task A_refusal_is_one_warning_with_status_code_and_request_id()
    {
        using var gateway = new Gateway(HttpStatusCode.BadRequest,
            """{"error":{"message":"`temperature` is deprecated for this model.","type":"invalid_request_error","code":"provider_rejected"}}""");
        using var logs = new CapturingLoggerProvider();
        using var loggers = LoggerFactory.Create(b => b.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
        var client = new GatewayChatClient(new Clients(gateway), Options.Create(new ChatOptions()), loggers.CreateLogger<GatewayChatClient>());

        var refused = await Should.ThrowAsync<ChatException>(() =>
            client.CompleteAsync("claude-opus-5-5", Residency.Eu, [new ChatMessageIn("user", "Hej?")], 64, 0.2, Guid.CreateVersion7(), "chat-answer", Ct));

        refused.Status.ShouldBe(502);
        var warning = logs.Entries.ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldBe("The gateway refused a chat call: HTTP 400, code provider_rejected, request req-7f3a");
        logs.Text.ShouldNotContain("deprecated");
        logs.Text.ShouldNotContain("gateway-secret");
    }

    [Fact]
    public async Task Condense_sends_the_reasoning_effort_and_no_temperature()
    {
        // #287: the condense call as ChatService makes it (temperature null, reasoning effort from Settings.Chat).
        using var gateway = new Gateway(HttpStatusCode.OK, Answer);
        using var loggers = LoggerFactory.Create(_ => { });
        var client = new GatewayChatClient(new Clients(gateway), Options.Create(new ChatOptions()), loggers.CreateLogger<GatewayChatClient>());

        await client.CompleteAsync("gpt-6-luna", Residency.Eu, [new ChatMessageIn("user", "Och kan man se dem på webben?")], 96, null, Guid.CreateVersion7(), "condense", Ct,
            reasoningEffort: "none");
        await client.CompleteAsync("gpt-6-luna", Residency.Eu, [new ChatMessageIn("user", "Hej?")], 96, null, Guid.CreateVersion7(), "condense", Ct);

        gateway.Requests[0].GetProperty("reasoning_effort").GetString().ShouldBe("none");
        gateway.Requests[0].TryGetProperty("temperature", out _).ShouldBeFalse();
        gateway.Requests[1].TryGetProperty("reasoning_effort", out _).ShouldBeFalse();
        PlatformChatSettings.Parse("""{"condenseModel":"gpt-6-luna","condenseReasoningEffort":"none"}""", new ChatOptions()).CondenseReasoningEffort.ShouldBe("none");
        PlatformChatSettings.Parse("{}", new ChatOptions()).CondenseReasoningEffort.ShouldBeNull();
        new ChatOptions().CondenseTimeout.ShouldBe(TimeSpan.FromMilliseconds(1200));
    }

    /// <summary>A gateway that answers every call with <paramref name="body"/> as <paramref name="mediaType"/>, with <c>x-ume-cost-usd</c> when given.</summary>
    private sealed class CostingGateway(string body, string mediaType, string? cost = null) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, mediaType) };
            if (cost is not null)
            {
                response.Headers.Add(GatewayHeaders.CostUsd, cost);
            }

            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task A_stream_asks_for_its_cost_and_ends_with_the_comments_cost_which_is_never_passed_on()
    {
        // Chat design §8.1, §8.3, §9.1: x-ume-stream-cost: 1 on the stream; the comment just before [DONE] is the call's cost.
        const string Stream = """
            data: {"choices":[{"index":0,"delta":{"content":"Hej"},"finish_reason":null}],"usage":null}

            data: {"choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":null}

            data: {"choices":[],"usage":{"prompt_tokens":900,"completion_tokens":40,"total_tokens":940,"prompt_tokens_details":{"cached_tokens":512}}}

            : ume-cost-usd=0.000123

            data: [DONE]

            """;
        using var gateway = new CostingGateway(Stream, "text/event-stream");
        using var loggers = LoggerFactory.Create(_ => { });
        var client = new GatewayChatClient(new Clients(gateway), Options.Create(new ChatOptions()), loggers.CreateLogger<GatewayChatClient>());

        using var response = await client.OpenStreamAsync("ume/chat-eu", Residency.Eu, [new ChatMessageIn("user", "Hej?")], 64, null, Guid.CreateVersion7(), Ct);
        var events = new List<GatewayStreamEvent>();
        await foreach (var item in client.ReadAsync(response, Ct))
        {
            events.Add(item);
        }

        gateway.Requests.ShouldHaveSingleItem().Headers.GetValues(GatewayHeaders.StreamCost).ShouldBe(["1"]);
        events.ShouldBe([
            new GatewayStreamEvent.Delta("Hej"), new GatewayStreamEvent.Finish("stop"), new GatewayStreamEvent.Usage(new GatewayTokens(900, 512, 40)),
            new GatewayStreamEvent.End(0.000123m)]);
    }

    [Fact]
    public async Task A_json_answer_gives_its_usage_once_it_answers_even_when_its_body_then_fails()
    {
        const string Body = """{"choices":[{"message":{"content":"Svar."},"finish_reason":"stop"}],"usage":{"prompt_tokens":1200,"completion_tokens":80,"prompt_tokens_details":{"cached_tokens":1024}}}""";
        using var gateway = new CostingGateway(Body, "application/json", "0.00012345");
        using var loggers = LoggerFactory.Create(_ => { });
        var client = new GatewayChatClient(new Clients(gateway), Options.Create(new ChatOptions()), loggers.CreateLogger<GatewayChatClient>());
        var usage = new List<GatewayCallUsage>();

        var answer = await client.CompleteAsync("ume/chat-eu", Residency.Eu, [new ChatMessageIn("user", "Hej?")], 96, null, Guid.CreateVersion7(), "condense", Ct,
            usage: usage.Add);

        (answer.Text, answer.PromptTokens, answer.CompletionTokens).ShouldBe(("Svar.", 1200, 80));
        usage.ShouldBe([new GatewayCallUsage("condense", "ume/chat-eu", 1200, 1024, 80, 0.00012345m)]);
        gateway.Requests.ShouldHaveSingleItem().Headers.Contains(GatewayHeaders.StreamCost).ShouldBeFalse();

        // A 2xx answer without choices, or not JSON at all, was still charged: its usage first, then the failure.
        foreach (var broken in new[] { "{}", "not json" })
        {
            using var malformed = new CostingGateway(broken, "application/json", "0.00000100");
            var brokenClient = new GatewayChatClient(new Clients(malformed), Options.Create(new ChatOptions()), loggers.CreateLogger<GatewayChatClient>());
            usage.Clear();
            await Should.ThrowAsync<Exception>(() => brokenClient.CompleteAsync("ume/chat-eu", Residency.Eu, [new ChatMessageIn("user", "Hej?")], 96, null,
                Guid.CreateVersion7(), "follow-ups", Ct, usage: usage.Add));
            usage.ShouldBe([new GatewayCallUsage("follow-ups", "ume/chat-eu", 0, 0, 0, 0.000001m)]);
        }
    }

    /// <summary>A gateway that answers <see cref="Answer"/> as JSON after <paramref name="delay"/>, or never when it is infinite.</summary>
    private sealed class SlowGateway(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Answer, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task A_json_answer_slower_than_the_headers_timeout_is_bounded_by_the_answer_duration_instead()
    {
        // #695: 1.5 s is past the 1 s headers timeout; a JSON answer still arrives, a stream that slow is upstream_unavailable.
        var options = Options.Create(new ChatOptions { GatewayHeadersTimeoutSeconds = 1, MaxAnswerDurationSeconds = 30 });
        using var gateway = new SlowGateway(TimeSpan.FromMilliseconds(1500));
        using var loggers = LoggerFactory.Create(_ => { });
        var client = new GatewayChatClient(new Clients(gateway), options, loggers.CreateLogger<GatewayChatClient>());

        var answer = await client.CompleteAsync("ume/chat-eu", Residency.Eu, [new ChatMessageIn("user", "Hej?")], 2000, null, Guid.CreateVersion7(), "chat-answer", Ct);
        answer.Text.ShouldBe("Svar.");

        var stream = await Should.ThrowAsync<ChatException>(() =>
            client.OpenStreamAsync("ume/chat-eu", Residency.Eu, [new ChatMessageIn("user", "Hej?")], 2000, null, Guid.CreateVersion7(), Ct));
        (stream.Status, stream.Code).ShouldBe((503, "upstream_unavailable"));
    }

    [Fact]
    public async Task A_json_answer_that_never_comes_ends_at_the_answer_duration_as_upstream_unavailable()
    {
        var options = Options.Create(new ChatOptions { GatewayHeadersTimeoutSeconds = 15, MaxAnswerDurationSeconds = 1 });
        using var gateway = new SlowGateway(Timeout.InfiniteTimeSpan);
        using var loggers = LoggerFactory.Create(_ => { });
        var client = new GatewayChatClient(new Clients(gateway), options, loggers.CreateLogger<GatewayChatClient>());

        var refused = await Should.ThrowAsync<ChatException>(() =>
            client.CompleteAsync("ume/chat-eu", Residency.Eu, [new ChatMessageIn("user", "Hej?")], 2000, null, Guid.CreateVersion7(), "chat-answer", Ct)
                .WaitAsync(TimeSpan.FromSeconds(10), Ct));
        (refused.Status, refused.Code).ShouldBe((503, "upstream_unavailable"));
    }
}
