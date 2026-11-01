using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Access;
using Ume.Rag.Domain.Retrieval;
using Ume.Rag.Infrastructure.Access;
using Ume.Rag.Infrastructure.Gateway;

namespace Ume.Rag.Answering.Chat;

/// <summary>The gateway's answer: the model text, token usage and finish reason.</summary>
public sealed record GatewayAnswer(string Text, int PromptTokens, int CompletionTokens, string FinishReason);

/// <summary>
/// Chat calls to the gateway (chat design §8): <c>POST /v1/chat/completions</c> with the assistant's gateway model, an explicit
/// <c>x-ume-max-residency</c> (the <see cref="ResidencyCeilingHandler"/> adds that level's key and checks
/// <c>x-ume-residency</c> before a byte is read), <c>x-ume-step: chat-answer</c> and <c>x-ume-run-id</c> = the message id.
/// Gateway errors map to the OpenAI codes of §3.6.
/// </summary>
public sealed class GatewayChatClient(IHttpClientFactory clients)
{
    public const string HttpClientName = "gateway-chat";

    public async Task<GatewayAnswer> CompleteAsync(string model, Residency ceiling, IReadOnlyList<ChatMessageIn> messages, int maxTokens, double temperature,
        Guid runId, CancellationToken ct)
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
                stream = false,
            }),
        }.WithMaxResidency(ceiling);
        request.Headers.Add("x-ume-step", "chat-answer");
        request.Headers.Add("x-ume-run-id", runId.ToString());
        HttpResponseMessage response;
        try
        {
            response = await clients.CreateClient(HttpClientName).SendAsync(request, ct);
        }
        catch (GatewayResidencyException)
        {
            throw new ChatException(502, "upstream_error", "The model service answered in a way that could not be accepted.");
        }
        catch (HttpRequestException)
        {
            throw new ChatException(503, "upstream_unavailable", "The model service is unavailable. Try again shortly.");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw await MapAsync(response, ct);
            }

            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var choice = json.RootElement.GetProperty("choices")[0];
            var text = choice.GetProperty("message").TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : "";
            var finish = choice.TryGetProperty("finish_reason", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString()! : "stop";
            var usage = json.RootElement.TryGetProperty("usage", out var u) ? u : default;
            return new GatewayAnswer(text, Int(usage, "prompt_tokens"), Int(usage, "completion_tokens"), finish);
        }
    }

    private static int Int(JsonElement usage, string name) =>
        usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : 0;

    /// <summary>§3.6: the gateway's status and code to ours; its <c>Retry-After</c> passed on for 429 and 503.</summary>
    private static async Task<ChatException> MapAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string? code = null;
        try
        {
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            code = body?["error"]?["code"]?.GetValue<string>();
        }
        catch (JsonException)
        {
        }

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
}

/// <summary>The outcome of a turn (chat design §6.2): an answer, the fixed not-found text, or the unavailable text.</summary>
public enum ChatOutcome
{
    Answered,
    NotFound,
    Unavailable,
}

/// <summary>A completed turn as the endpoint writes it.</summary>
public sealed record ChatCompletion(Guid MessageId, string Model, string Content, IReadOnlyList<UrlCitation> Annotations, IReadOnlyList<PromptSource> Cited,
    int PromptTokens, int CompletionTokens, string FinishReason, ChatOutcome Outcome, DateTimeOffset Created);

/// <summary>
/// One chat turn of a RAG assistant (chat design §4.1, non-streaming): policy decision on the assistant (404
/// <c>model_not_found</c> for anything not allowed), effective level and ceiling, language, the conversational check,
/// retrieval through the knowledge role in process (the assistant's published collections), coverage handling (§6.2), the
/// prompt (§6.3, §7), the gateway call (§8) and the citations (§9.2–§9.3). P1a: no condense (15c), no streaming (15b).
/// </summary>
public sealed partial class ChatService(IPolicySnapshotSource snapshots, IAccessPolicy policy, KnowledgeService knowledge, GatewayChatClient gateway,
    TimeProvider time, ILogger<ChatService> logger)
{
    public async Task<ChatCompletion> CompleteAsync(RagPrincipal who, ParsedChat chat, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(who);
        ArgumentNullException.ThrowIfNull(chat);
        var started = time.GetUtcNow();
        var snapshot = snapshots.Current;
        var assistant = snapshot.Assistants.Values.FirstOrDefault(a => string.Equals(a.Slug, chat.Model, StringComparison.Ordinal));
        if (assistant?.Published is null || !policy.Decide(who, ResourceRef.Assistant(assistant.Id), RagAction.Chat, snapshot, snapshots.IsConfirmed).Allowed)
        {
            throw new ChatException(404, "model_not_found", $"The model '{chat.Model}' does not exist or you do not have access to it.", "model");
        }

        var settings = AssistantChatSettings.Parse(assistant.Published.QuerySteps);
        var language = settings.AlwaysSwedish ? LanguageGuess.Swedish : LanguageGuess.Detect(chat.Question);
        var ceiling = Ceiling(who, assistant, snapshot);
        var messageId = Guid.CreateVersion7();
        var maxTokens = Math.Min(chat.MaxTokens ?? settings.MaxOutputTokens, settings.MaxOutputTokens);

        RetrievalResult? retrieval = null;
        var conversational = MessageKind.IsConversational(chat.Question);
        if (!conversational)
        {
            try
            {
                retrieval = await knowledge.RetrieveAsync(who, new RetrievalRequest(chat.Question, assistant.Id, null, settings.TopK,
                    Purpose: RetrievalPurpose.Chat, AllowDegraded: true, RunId: messageId), ct);
            }
            catch (RetrievalDeniedException denied) when (denied.Status == 503)
            {
                throw new ChatException(503, "search_unavailable", "Search is unavailable. Try again shortly.");
            }
            catch (RetrievalDeniedException)
            {
                throw new ChatException(404, "model_not_found", $"The model '{chat.Model}' does not exist or you do not have access to it.", "model");
            }

            if (retrieval.Coverage.Status == CoverageStatus.Low)
            {
                return Fixed(messageId, chat, FixedTexts.NotFound(assistant.Published.NotFound, null, language), ChatOutcome.NotFound, started);
            }

            if (retrieval.Coverage.Status == CoverageStatus.Unknown && retrieval.Results.Count == 0)
            {
                return Fixed(messageId, chat, FixedTexts.Unavailable(assistant.Published.NotFound, language), ChatOutcome.Unavailable, started);
            }
        }

        var degraded = retrieval is not null && retrieval.Coverage.Status == CoverageStatus.Unknown;
        var prompt = PromptBuilder.Build(assistant.Name, assistant.Published.SystemPrompt, language, settings.AlwaysSwedish, chat.Question, chat.History,
            conversational ? null : retrieval!.Results, settings, degraded);
        var watch = Stopwatch.StartNew();
        var answer = await gateway.CompleteAsync(assistant.Published.GatewayModel, ceiling, prompt.Messages, maxTokens, settings.Temperature, messageId, ct);
        LogAnswered(logger, assistant.Slug, messageId, prompt.Sources.Count, watch.Elapsed.TotalMilliseconds);
        var cited = Citations.Apply(answer.Text, prompt.Sources);
        if (retrieval is not null && cited.CitedRanks.Count > 0)
        {
            knowledge.MarkCited(retrieval, cited.CitedRanks);
        }

        var content = degraded ? (language == LanguageGuess.English ? FixedTexts.CaveatEn : FixedTexts.CaveatSv) + "\n\n" + cited.Content : cited.Content;
        var shift = content.Length - cited.Content.Length;
        var annotations = shift == 0 ? cited.Annotations : [.. cited.Annotations.Select(a => a with { StartIndex = a.StartIndex + shift, EndIndex = a.EndIndex + shift })];
        return new ChatCompletion(messageId, chat.Model, content, annotations, cited.Cited, answer.PromptTokens, answer.CompletionTokens, answer.FinishReason,
            ChatOutcome.Answered, started);
    }

    /// <summary>
    /// The call's ceiling (chat design §8.2, identity §4.7): the assistant's level, raised to the client's ceiling when the
    /// client stores history (every turn of such a client runs at that level).
    /// </summary>
    public static Residency Ceiling(RagPrincipal who, AssistantEntry assistant, PolicySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(who);
        ArgumentNullException.ThrowIfNull(assistant);
        ArgumentNullException.ThrowIfNull(snapshot);
        var level = assistant.Classification;
        if (snapshot.Clients.TryGetValue(who.Client.Id, out var client) && client.StoresHistory && Rank(client.Ceiling) > Rank(level))
        {
            level = client.Ceiling;
        }

        return ResidencyCeiling.ForLevel(level);
    }

    private static int Rank(string level) => level switch { Classification.Public => 0, Classification.Internal => 1, _ => 2 };

    private static ChatCompletion Fixed(Guid messageId, ParsedChat chat, string text, ChatOutcome outcome, DateTimeOffset started) =>
        new(messageId, chat.Model, text, [], [], 0, 0, "stop", outcome, started);

    [LoggerMessage(LogLevel.Information, "Chat answer for {Assistant}, message {MessageId}: {Sources} sources in the prompt, gateway {GatewayMs} ms")]
    private static partial void LogAnswered(ILogger logger, string assistant, Guid messageId, int sources, double gatewayMs);
}
