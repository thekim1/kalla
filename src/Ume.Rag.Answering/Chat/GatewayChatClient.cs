using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ume.Rag.Domain;
using Ume.Rag.Infrastructure.Gateway;

namespace Ume.Rag.Answering.Chat;

/// <summary>What the gateway's stream carries: text, the usage, the finish reason, and its end with the stream's cost.</summary>
public abstract record GatewayStreamEvent
{
    public sealed record Delta(string Text) : GatewayStreamEvent;

    /// <summary>The usage chunk's tokens (input, cached input, output; chat design §8.3).</summary>
    public sealed record Usage(GatewayTokens Tokens) : GatewayStreamEvent;

    public sealed record Finish(string Reason) : GatewayStreamEvent;

    /// <summary>
    /// The stream ended (<c>data: [DONE]</c> or its last line): the cost from the comment <c>: ume-cost-usd=</c>, null when the
    /// gateway wrote none. A stream that breaks off has no end event.
    /// </summary>
    public sealed record End(decimal? CostUsd) : GatewayStreamEvent;
}

/// <summary>The gateway's answer: the model text, token usage and finish reason.</summary>
public sealed record GatewayAnswer(string Text, int PromptTokens, int CompletionTokens, string FinishReason);

/// <summary>
/// Chat calls to the gateway (chat design §8): <c>POST /v1/chat/completions</c> with the assistant's gateway model, an explicit
/// <c>x-ume-max-residency</c> (the <see cref="ResidencyCeilingHandler"/> adds that level's key and checks <c>x-ume-residency</c>
/// before a byte is read), <c>x-ume-step: chat-answer</c> and <c>x-ume-run-id</c> = the message id. Streams are opened with
/// <see cref="HttpCompletionOption.ResponseHeadersRead"/>, so every check runs before our first byte, and with
/// <c>x-ume-stream-cost: 1</c>; they are read through <see cref="SseReader"/>, so the cost comment never reaches a client of
/// ours. Errors map to §3.6. Each call's usage is read once (<see cref="GatewayUsageReader"/>, §8.3).
/// </summary>
public sealed partial class GatewayChatClient(IHttpClientFactory clients, IOptions<ChatOptions> options, ILogger<GatewayChatClient> logger)
{
    public const string HttpClientName = "gateway-chat";

    /// <summary>The <c>x-ume-step</c> of the answer call.</summary>
    public const string AnswerStep = "chat-answer";

    /// <summary>
    /// <c>temperature</c> only when the assistant sets one (#275: the gateway refuses it for models that deprecate it, Claude
    /// among them); a null property is left out of the request, as is <c>stream_options</c> on a non-streamed call and
    /// <c>max_tokens</c> when the limit is unknown (chat design §6.3, §8.1).
    /// </summary>
    private static readonly JsonSerializerOptions RequestJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// One JSON answer. Once the gateway has answered <c>2xx</c> the call's usage goes to <paramref name="usage"/> (its tokens and
    /// <c>x-ume-cost-usd</c>; a body that is not JSON, or one cut off by the time limit, gives no tokens), before the body is read
    /// for the text: the call was charged even when the body then fails. The headers of a non-streamed answer come only after the
    /// whole answer is generated, so the call is bounded by <c>Chat:MaxAnswerDurationSeconds</c>, not the headers timeout (#695);
    /// a caller with a shorter budget (condense, follow-ups) cancels <paramref name="ct"/> itself.
    /// </summary>
    public async Task<GatewayAnswer> CompleteAsync(string model, Residency ceiling, IReadOnlyList<ChatMessageIn> messages, int? maxTokens, double? temperature,
        Guid runId, string step, CancellationToken ct, string? reasoningEffort = null, Action<GatewayCallUsage>? usage = null)
    {
        var limit = TimeSpan.FromSeconds(options.Value.MaxAnswerDurationSeconds);
        var started = TimeProvider.System.GetTimestamp();
        using var response = await SendAsync(model, ceiling, messages, maxTokens, temperature, runId, stream: false, step, reasoningEffort, limit, ct);
        using var total = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var left = limit - TimeProvider.System.GetElapsedTime(started);
        total.CancelAfter(left > TimeSpan.Zero ? left : TimeSpan.Zero);
        JsonDocument json;
        try
        {
            json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(total.Token), cancellationToken: total.Token);
        }
        catch (JsonException)
        {
            usage?.Invoke(GatewayUsageReader.Read(response, default, step, model));
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            usage?.Invoke(GatewayUsageReader.Read(response, default, step, model));
            throw new ChatException(503, "upstream_unavailable", "The model service is unavailable. Try again shortly.");
        }

        using (json)
        {
            var call = GatewayUsageReader.Read(response, json.RootElement, step, model);
            usage?.Invoke(call);
            var choice = json.RootElement.GetProperty("choices")[0];
            var text = choice.GetProperty("message").TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : "";
            var finish = choice.TryGetProperty("finish_reason", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString()! : "stop";
            return new GatewayAnswer(text, (int)call.InputTokens, (int)call.OutputTokens, finish);
        }
    }

    /// <summary>
    /// Opens the stream with <c>x-ume-stream-cost: 1</c> (§8.1): status and residency are checked when this returns; the caller
    /// then writes its first byte.
    /// </summary>
    public Task<HttpResponseMessage> OpenStreamAsync(string model, Residency ceiling, IReadOnlyList<ChatMessageIn> messages, int? maxTokens, double? temperature,
        Guid runId, CancellationToken ct) =>
        SendAsync(model, ceiling, messages, maxTokens, temperature, runId, stream: true, AnswerStep, null,
            TimeSpan.FromSeconds(options.Value.GatewayHeadersTimeoutSeconds), ct);

    /// <summary>
    /// The stream's events, read through <see cref="SseReader"/> (the cost comment is taken, never passed on), ending with
    /// <see cref="GatewayStreamEvent.End"/> and its cost; an idle gap over <c>Chat:StreamIdleTimeoutSeconds</c> or the total limit
    /// ends it with <c>stream_interrupted</c> instead.
    /// </summary>
    public async IAsyncEnumerable<GatewayStreamEvent> ReadAsync(HttpResponseMessage response, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(response);
        using var total = CancellationTokenSource.CreateLinkedTokenSource(ct);
        total.CancelAfter(TimeSpan.FromSeconds(options.Value.MaxAnswerDurationSeconds));
        await using var body = await response.Content.ReadAsStreamAsync(total.Token);
        using var lines = new StreamReader(body);
        var reader = new SseReader(lines);
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
                throw new ChatException(502, "stream_interrupted", "The model stopped answering.") { TimedOut = true };
            }

            if (line is null || !GatewayStreamLine.Read(line, events))
            {
                // The comment comes just before [DONE] (gateway-changes §5.4), so the cost is known here.
                yield return new GatewayStreamEvent.End(reader.CostUsd);
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
        Guid runId, bool stream, string step, string? reasoningEffort, TimeSpan headersLimit, CancellationToken ct)
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
        if (stream)
        {
            request.WithStreamCost();
        }

        request.Headers.Add("x-ume-step", step);
        request.Headers.Add("x-ume-run-id", runId.ToString());
        using var headers = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headers.CancelAfter(headersLimit);
        HttpResponseMessage response;
        try
        {
            response = await clients.CreateClient(HttpClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headers.Token);
        }
        catch (GatewayResidencyException)
        {
            throw new ChatException(502, "upstream_error", "The model service answered in a way that could not be accepted.") { ResidencyRefused = true };
        }
        catch (GatewayKeyRefusedException refused)
        {
            throw ChatRefusals.Of(refused); // §3.6 (B-44): 503 assistant_unavailable or 402 budget_exceeded, before the first byte
        }
        catch (GatewayDelegationEchoException)
        {
            throw new ChatException(502, "upstream_error", "The model service answered in a way that could not be accepted."); // §3.6: a failed key-echo check
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
