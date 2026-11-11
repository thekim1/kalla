using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ume.Rag.Domain;
using Ume.Rag.Infrastructure.Gateway;

namespace Ume.Rag.Answering.Chat;

/// <summary>What the gateway's stream carries: text, the usage, the finish reason.</summary>
public abstract record GatewayStreamEvent
{
    public sealed record Delta(string Text) : GatewayStreamEvent;

    public sealed record Usage(int PromptTokens, int CompletionTokens) : GatewayStreamEvent;

    public sealed record Finish(string Reason) : GatewayStreamEvent;
}

/// <summary>The gateway's answer: the model text, token usage and finish reason.</summary>
public sealed record GatewayAnswer(string Text, int PromptTokens, int CompletionTokens, string FinishReason);

/// <summary>
/// Chat calls to the gateway (chat design §8): <c>POST /v1/chat/completions</c> with the assistant's gateway model, an explicit
/// <c>x-ume-max-residency</c> (the <see cref="ResidencyCeilingHandler"/> adds that level's key and checks <c>x-ume-residency</c>
/// before a byte is read), <c>x-ume-step: chat-answer</c> and <c>x-ume-run-id</c> = the message id. Streams are opened with
/// <see cref="HttpCompletionOption.ResponseHeadersRead"/>, so every check runs before our first byte. Errors map to §3.6.
/// </summary>
public sealed partial class GatewayChatClient(IHttpClientFactory clients, IOptions<ChatOptions> options, ILogger<GatewayChatClient> logger)
{
    public const string HttpClientName = "gateway-chat";

    /// <summary>
    /// <c>temperature</c> only when the assistant sets one (#275: the gateway refuses it for models that deprecate it, Claude
    /// among them); a null property is left out of the request, as is <c>stream_options</c> on a non-streamed call and
    /// <c>max_tokens</c> when the limit is unknown (chat design §6.3, §8.1).
    /// </summary>
    private static readonly JsonSerializerOptions RequestJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<GatewayAnswer> CompleteAsync(string model, Residency ceiling, IReadOnlyList<ChatMessageIn> messages, int? maxTokens, double? temperature,
        Guid runId, string step, CancellationToken ct, string? reasoningEffort = null)
    {
        using var response = await SendAsync(model, ceiling, messages, maxTokens, temperature, runId, stream: false, step, reasoningEffort, ct);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var choice = json.RootElement.GetProperty("choices")[0];
        var text = choice.GetProperty("message").TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : "";
        var finish = choice.TryGetProperty("finish_reason", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString()! : "stop";
        var usage = json.RootElement.TryGetProperty("usage", out var u) ? u : default;
        return new GatewayAnswer(text, Int(usage, "prompt_tokens"), Int(usage, "completion_tokens"), finish);
    }

    /// <summary>Opens the stream: status and residency are checked when this returns; the caller then writes its first byte.</summary>
    public Task<HttpResponseMessage> OpenStreamAsync(string model, Residency ceiling, IReadOnlyList<ChatMessageIn> messages, int? maxTokens, double? temperature,
        Guid runId, CancellationToken ct) =>
        SendAsync(model, ceiling, messages, maxTokens, temperature, runId, stream: true, "chat-answer", null, ct);

    /// <summary>The stream's events; an idle gap over <c>Chat:StreamIdleTimeoutSeconds</c> or the total limit ends it with <c>stream_interrupted</c>.</summary>
    public async IAsyncEnumerable<GatewayStreamEvent> ReadAsync(HttpResponseMessage response, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(response);
        using var total = CancellationTokenSource.CreateLinkedTokenSource(ct);
        total.CancelAfter(TimeSpan.FromSeconds(options.Value.MaxAnswerDurationSeconds));
        await using var body = await response.Content.ReadAsStreamAsync(total.Token);
        using var reader = new StreamReader(body);
        // One idle timer for the stream, re-armed before each line (#333: a linked source and timer per line cost ≈ 1 KB each).
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(total.Token);
        var idleTimeout = TimeSpan.FromSeconds(options.Value.StreamIdleTimeoutSeconds);
        var events = new List<GatewayStreamEvent>(3);
        while (true)
        {
            string? line;
            idle.CancelAfter(idleTimeout);
            try
            {
                line = await reader.ReadLineAsync(idle.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new ChatException(502, "stream_interrupted", "The model stopped answering.");
            }

            if (line is null || !GatewayStreamLine.Read(line, events))
            {
                yield break;
            }

            foreach (var item in events)
            {
                yield return item;
            }

            events.Clear();
        }
    }

    private async Task<HttpResponseMessage> SendAsync(string model, Residency ceiling, IReadOnlyList<ChatMessageIn> messages, int? maxTokens, double? temperature,
        Guid runId, bool stream, string step, string? reasoningEffort, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(messages);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("v1/chat/completions", UriKind.Relative))
        {
            Content = JsonContent.Create(new
            {
                model,
                messages = messages.Select(m => new { role = m.Role, content = m.Text }),
                max_tokens = maxTokens,
                temperature,
                reasoning_effort = reasoningEffort,
                stream,
                stream_options = stream ? new { include_usage = true } : null,
            }, options: RequestJson),
        }.WithMaxResidency(ceiling);
        request.Headers.Add("x-ume-step", step);
        request.Headers.Add("x-ume-run-id", runId.ToString());
        using var headers = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headers.CancelAfter(TimeSpan.FromSeconds(options.Value.GatewayHeadersTimeoutSeconds));
        HttpResponseMessage response;
        try
        {
            response = await clients.CreateClient(HttpClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headers.Token);
        }
        catch (GatewayResidencyException)
        {
            throw new ChatException(502, "upstream_error", "The model service answered in a way that could not be accepted.") { ResidencyRefused = true };
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            throw new ChatException(503, "upstream_unavailable", "The model service is unavailable. Try again shortly.");
        }

        if (!response.IsSuccessStatusCode)
        {
            using (response)
            {
                throw await MapAsync(response, ct);
            }
        }

        return response;
    }

    private static int Int(JsonElement usage, string name) =>
        usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : 0;

    /// <summary>
    /// §3.6: the gateway's status and code to ours; its <c>Retry-After</c> passed on for 429 and 503. One Warning names the
    /// refusal (status, the gateway's <c>error.code</c>, its <c>x-request-id</c>; no body text, no address, #275).
    /// </summary>
    private async Task<ChatException> MapAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string? code = null;
        try
        {
            code = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))?["error"]?["code"]?.GetValue<string>();
        }
        catch (JsonException)
        {
        }

        LogRefused(logger, (int)response.StatusCode, Printable(code), Printable(response.Headers.TryGetValues("x-request-id", out var ids) ? ids.FirstOrDefault() : null));
        var retryAfter = response.Headers.RetryAfter?.Delta is { } delta ? (int?)Math.Ceiling(delta.TotalSeconds) : null;
        return (int)response.StatusCode switch
        {
            400 when code == "context_length_exceeded" => new ChatException(400, "context_length_exceeded", "The conversation is too long for the assistant's model.", "messages"),
            402 => new ChatException(402, "budget_exceeded", "The team's budget for the model service is used up."),
            429 => new ChatException(429, "rate_limited", "Too many requests to the model service. Wait and try again.") { RetryAfterSeconds = retryAfter },
            503 => new ChatException(503, "upstream_unavailable", "The model service is unavailable. Try again shortly.") { RetryAfterSeconds = retryAfter },
            _ => new ChatException(502, "upstream_error", "The model service could not answer."),
        };
    }

    /// <summary>A short printable token for the log (codes and request ids are ASCII identifiers; anything else is cut).</summary>
    private static string Printable(string? value) =>
        value is null ? "-" : new string([.. value.Take(80).Where(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' or ':')]);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The gateway refused a chat call: HTTP {Status}, code {Code}, request {RequestId}")]
    private static partial void LogRefused(ILogger logger, int status, string code, string requestId);
}
