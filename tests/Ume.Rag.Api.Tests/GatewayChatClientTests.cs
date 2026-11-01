using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ume.Rag.Answering.Chat;
using Ume.Rag.Domain;
using Ume.Rag.TestKit;

namespace Ume.Rag.Api.Tests;

/// <summary>
/// #275: <c>temperature</c> is sent only when the assistant sets one (the gateway refuses it for models that deprecate it),
/// and a refusal the chat maps to 502 leaves one Warning with the gateway's status, code and request id, without body text
/// or address.
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
}
