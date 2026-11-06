using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Access;
using Ume.Rag.Domain.Retrieval;
using Ume.Rag.Infrastructure.Access;
using Ume.Rag.Infrastructure.Gateway;

namespace Ume.Rag.Answering.Chat;

/// <summary>
/// Chat deployment knobs (chat design §8.3, §9.4, §4.1 step 4): <c>Chat:GatewayHeadersTimeoutSeconds</c> (15),
/// <c>Chat:StreamIdleTimeoutSeconds</c> (60), <c>Chat:MaxAnswerDurationSeconds</c> (240), <c>Chat:MessagesPerMinutePerUser</c>
/// (20) and <c>Chat:AllowedLinkHosts</c> (<c>umea.se</c>, <c>*.umea.se</c>). The last two move to <c>Settings.Chat</c> with
/// part c of item 15.
/// </summary>
public sealed class ChatOptions
{
    public const string SectionName = "Chat";

    public int GatewayHeadersTimeoutSeconds { get; set; } = 15;
    public int StreamIdleTimeoutSeconds { get; set; } = 60;
    public int MaxAnswerDurationSeconds { get; set; } = 240;
    public int MessagesPerMinutePerUser { get; set; } = 20;
    public string[] AllowedLinkHosts { get; set; } = ["umea.se", "*.umea.se"];

    /// <summary>How long the condense call may take before the raw question is used (chat design §5; #287).</summary>
    public TimeSpan CondenseTimeout { get; set; } = Condenser.DefaultTimeout;
}

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
    /// among them); a null property is left out of the request, as is <c>stream_options</c> on a non-streamed call.
    /// </summary>
    private static readonly JsonSerializerOptions RequestJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<GatewayAnswer> CompleteAsync(string model, Residency ceiling, IReadOnlyList<ChatMessageIn> messages, int maxTokens, double? temperature,
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
    public Task<HttpResponseMessage> OpenStreamAsync(string model, Residency ceiling, IReadOnlyList<ChatMessageIn> messages, int maxTokens, double? temperature,
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

    private async Task<HttpResponseMessage> SendAsync(string model, Residency ceiling, IReadOnlyList<ChatMessageIn> messages, int maxTokens, double? temperature,
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
            throw new ChatException(502, "upstream_error", "The model service answered in a way that could not be accepted.");
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

/// <summary>
/// The outcome of a turn (chat design §6.2): an answer, a general-knowledge answer without sources (§10.4), the fixed
/// not-found text, or the unavailable text.
/// </summary>
public enum ChatOutcome
{
    Answered,
    AnsweredGeneral,
    NotFound,
    Unavailable,
}

/// <summary>The stored and reported names of <see cref="ChatOutcome"/> (<c>Messages.Outcome</c>, <c>rag.chat.requests{outcome}</c>).</summary>
public static class ChatOutcomes
{
    public static string Name(ChatOutcome outcome) => outcome switch
    {
        ChatOutcome.AnsweredGeneral => "answered-general",
        ChatOutcome.NotFound => "not-found",
        ChatOutcome.Unavailable => "unavailable",
        _ => "answered",
    };
}

/// <summary>A completed turn as the JSON endpoint writes it.</summary>
public sealed record ChatCompletion(Guid MessageId, string Model, string Content, IReadOnlyList<UrlCitation> Annotations, IReadOnlyList<PromptSource> Cited,
    int PromptTokens, int CompletionTokens, string FinishReason, ChatOutcome Outcome, DateTimeOffset Created)
{
    /// <summary>The plan the turn ran (for the conversation writer, chat design §12).</summary>
    public ChatPlan? Plan { get; init; }
}

/// <summary>
/// A turn decided up to the gateway call (chat design §4.1 steps 5–12): either a fixed text (<see cref="Fixed"/>, no model
/// call) or the prompt with everything the call and the citations need.
/// </summary>
public sealed record ChatPlan(Guid MessageId, string Model, DateTimeOffset Created, AssistantEntry Assistant, Residency Ceiling, AssistantChatSettings Settings,
    int MaxTokens, string Language, string? Fixed, ChatOutcome Outcome, BuiltPrompt? Prompt, RetrievalResult? Retrieval, bool Degraded,
    PlatformChatSettings Platform, CondenseOutcome? Condense = null)
{
    /// <summary>The caveat line of a degraded answer (§10.2) or of a general-knowledge answer (§10.4), or null; written by us, first.</summary>
    public string? Caveat => Outcome == ChatOutcome.AnsweredGeneral
        ? (Language == LanguageGuess.English ? FixedTexts.GeneralCaveatEn : FixedTexts.GeneralCaveatSv) + "\n\n"
        : Degraded ? (Language == LanguageGuess.English ? FixedTexts.CaveatEn : FixedTexts.CaveatSv) + "\n\n" : null;

    /// <summary>What follows the model's text: the contact links of a general-knowledge answer (§10.4), else empty.</summary>
    public string Trailer => Outcome == ChatOutcome.AnsweredGeneral ? FixedTexts.ContactLinks(Assistant.Published?.NotFound, Platform.NotFound) : "";
}

/// <summary>
/// One chat turn of a RAG assistant (chat design §4.1): policy decision on the assistant (404 <c>model_not_found</c> for
/// anything not allowed), effective level and ceiling, language, the conversational check, retrieval through the knowledge
/// role in process (the assistant's published collections), coverage handling (§6.2), the prompt (§6.3, §7), then the gateway
/// call as JSON (<see cref="CompleteAsync"/>) or a stream the endpoint relays (<see cref="PlanAsync"/> + <see cref="GatewayChatClient"/>),
/// through the <see cref="AnswerTransducer"/> (§9.2–§9.4). Condense is part c of item 15.
/// </summary>
public sealed partial class ChatService(IPolicySnapshotSource snapshots, IAccessPolicy policy, KnowledgeService knowledge, GatewayChatClient gateway,
    IOptions<ChatOptions> options, IOptions<GatewayOptions> gatewayOptions, TimeProvider time, ILogger<ChatService> logger)
{
    private SnapshotChat? _resolved;

    public GatewayChatClient Gateway => gateway;

    /// <summary><c>Settings.Chat</c> at the current snapshot, over the deployment knobs.</summary>
    public PlatformChatSettings Platform => Resolved(snapshots.Current).Platform;

    /// <summary>The id of the assistant with slug <paramref name="model"/> at the current snapshot, or <see cref="Guid.Empty"/>.</summary>
    public Guid AssistantId(string model) => Resolved(snapshots.Current).Assistant(model)?.Id ?? Guid.Empty;

    public async Task<ChatPlan> PlanAsync(RagPrincipal who, ParsedChat chat, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(who);
        ArgumentNullException.ThrowIfNull(chat);
        var created = time.GetUtcNow();
        var snapshot = snapshots.Current;
        var resolved = Resolved(snapshot);
        var assistant = resolved.Assistant(chat.Model);
        if (assistant?.Published is null || !policy.Decide(who, ResourceRef.Assistant(assistant.Id), RagAction.Chat, snapshot, snapshots.IsConfirmed).Allowed)
        {
            throw NotFound(chat.Model);
        }

        var settings = resolved.Settings(assistant);
        if (assistant.Classification == Classification.Public)
        {
            // V36 holds at run time too: a public assistant is always off (chat design §7.3).
            settings = settings with { GeneralKnowledge = GeneralKnowledge.Off };
        }

        var language = settings.AlwaysSwedish ? LanguageGuess.Swedish : LanguageGuess.Detect(chat.Question);
        var ceiling = gatewayOptions.Value.Cap(Ceiling(who, assistant, snapshot)); // Gateway:MaxResidency (M2, #237)
        var messageId = Guid.CreateVersion7();
        var maxTokens = Math.Min(chat.MaxTokens ?? settings.MaxOutputTokens, settings.MaxOutputTokens);
        var platform = resolved.Platform;
        CondenseOutcome? condense = null;
        ChatPlan Plan(string? fixedText, ChatOutcome outcome, BuiltPrompt? prompt, RetrievalResult? retrieval, bool degraded) =>
            new(messageId, chat.Model, created, assistant, ceiling, settings, maxTokens, language, fixedText, outcome, prompt, retrieval, degraded, platform, condense);

        RetrievalResult? retrieval = null;
        var conversational = MessageKind.IsConversational(chat.Question);
        if (!conversational)
        {
            // §5: follow-ups that are not self-contained are rewritten for retrieval only; the prompt keeps the user's question.
            var query = chat.Question;
            (query, condense) = await CondenseAsync(chat, settings, platform, ceiling, messageId, ct);
            try
            {
                // The rerank call (when the assistant reranks) runs at the turn's ceiling, as the answer does (§6.7).
                retrieval = await knowledge.RetrieveAsync(who, new RetrievalRequest(query, assistant.Id, null, settings.TopK, Expand: ExpandMode.Section,
                    Purpose: RetrievalPurpose.Chat, AllowDegraded: true, RunId: messageId,
                    RerankCeiling: settings.Rerank ? ceiling : null), ct);
            }
            catch (RetrievalDeniedException denied) when (denied.Status == 503)
            {
                throw new ChatException(503, "search_unavailable", "Search is unavailable. Try again shortly.");
            }
            catch (RetrievalDeniedException)
            {
                throw NotFound(chat.Model);
            }

            // A failed rewrite of a follow-up must not turn into "not found": then the answer comes from what was found (§6.2).
            var condenseFailed = condense is CondenseOutcome.Timeout or CondenseOutcome.Error or CondenseOutcome.Rejected;
            if (retrieval.Coverage.Status == CoverageStatus.Low && !(condenseFailed && retrieval.Results.Count > 0))
            {
                if (!GeneralKnowledgeLevels.AnswersWithoutSources(settings.GeneralKnowledge))
                {
                    return Plan(FixedTexts.NotFound(assistant.Published.NotFound, platform.NotFound, language), ChatOutcome.NotFound, null, retrieval, false);
                }

                // §10.4: fallback and open answer from general knowledge, without the weak hits, behind our caveat line.
                var general = PromptBuilder.Build(assistant.Name, assistant.Published.SystemPrompt, language, settings.AlwaysSwedish, chat.Question, chat.History,
                    null, settings, general: true);
                return Plan(null, ChatOutcome.AnsweredGeneral, general, retrieval, false);
            }

            if (retrieval.Coverage.Status == CoverageStatus.Unknown && retrieval.Results.Count == 0)
            {
                return Plan(FixedTexts.Unavailable(assistant.Published.NotFound, language), ChatOutcome.Unavailable, null, retrieval, false);
            }
        }

        var degraded = retrieval is not null && retrieval.Coverage.Status == CoverageStatus.Unknown;
        var prompt = PromptBuilder.Build(assistant.Name, assistant.Published.SystemPrompt, language, settings.AlwaysSwedish, chat.Question, chat.History,
            conversational ? null : retrieval!.Results, settings, degraded, retrieval?.Sections);
        return Plan(null, ChatOutcome.Answered, prompt, retrieval, degraded);
    }

    /// <summary>
    /// §5: rewrites a follow-up on the platform condense model at <c>Eu</c> (never above the turn's ceiling), ≤ 96 tokens,
    /// 600 ms; anything but an accepted rewrite keeps the raw question.
    /// </summary>
    private async Task<(string Query, CondenseOutcome Outcome)> CondenseAsync(ParsedChat chat, AssistantChatSettings settings, PlatformChatSettings platform,
        Residency ceiling, Guid messageId, CancellationToken ct)
    {
        if (Condenser.Skip(settings.CondenseEnabled && platform.CondenseModel is not null, chat.History, chat.Question) is { } skipped)
        {
            return (chat.Question, skipped);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.Value.CondenseTimeout);
        try
        {
            var answer = await gateway.CompleteAsync(platform.CondenseModel!, Condenser.Ceiling(ceiling), Condenser.Messages(chat.History, chat.Question),
                Condenser.MaxOutputTokens, null, messageId, "condense", timeout.Token, platform.CondenseReasoningEffort);
            return Condenser.Accept(answer.Text) is { } rewritten ? (rewritten, CondenseOutcome.Rewritten) : (chat.Question, CondenseOutcome.Rejected);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (chat.Question, CondenseOutcome.Timeout);
        }
        catch (Exception ex) when (ex is ChatException or GatewayCallRefusedException or HttpRequestException or JsonException)
        {
            return (chat.Question, CondenseOutcome.Error);
        }
    }

    public async Task<ChatCompletion> CompleteAsync(RagPrincipal who, ParsedChat chat, CancellationToken ct)
    {
        var plan = await PlanAsync(who, chat, ct);
        if (plan.Fixed is { } text)
        {
            return new ChatCompletion(plan.MessageId, plan.Model, text, [], [], 0, 0, "stop", plan.Outcome, plan.Created) { Plan = plan };
        }

        var answer = await gateway.CompleteAsync(plan.Assistant.Published!.GatewayModel, plan.Ceiling, plan.Prompt!.Messages, plan.MaxTokens,
            plan.Settings.Temperature, plan.MessageId, "chat-answer", ct);
        var transducer = new AnswerTransducer(plan.Prompt.Sources, plan.Platform.AllowedLinkHosts);
        var caveat = plan.Caveat ?? "";
        var pieces = transducer.Feed(answer.Text).Concat(transducer.Flush()).OfType<AnswerPiece.Text>().ToList();
        var content = caveat + string.Concat(pieces.Select(p => p.Content)) + plan.Trailer;
        var annotations = pieces.SelectMany(p => p.Annotations).Select(a => a with { StartIndex = a.StartIndex + caveat.Length, EndIndex = a.EndIndex + caveat.Length }).ToList();
        Finish(plan, transducer);
        return new ChatCompletion(plan.MessageId, plan.Model, content, annotations, transducer.Cited, answer.PromptTokens, answer.CompletionTokens, answer.FinishReason,
            plan.Outcome, plan.Created) { Plan = plan };
    }

    /// <summary>After the answer (§9.5): the counters with only the cited ranks, and a log line.</summary>
    public void Finish(ChatPlan plan, AnswerTransducer transducer)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(transducer);
        if (plan.Retrieval is not null && transducer.CitedRanks.Count > 0)
        {
            knowledge.MarkCited(plan.Retrieval, transducer.CitedRanks);
        }

        LogAnswered(logger, plan.Model, plan.MessageId, plan.Prompt?.Sources.Count ?? 0, transducer.Cited.Count, plan.Condense?.ToString() ?? "none");
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

    /// <summary>
    /// The slug index and <c>Settings.Chat</c> of <paramref name="snapshot"/>, built once per snapshot version (the assistants'
    /// settings are parsed on first use, so an unreadable <c>QuerySteps</c> fails only its own assistant's turns).
    /// </summary>
    private SnapshotChat Resolved(PolicySnapshot snapshot)
    {
        if (_resolved is { } cached && cached.Version == snapshot.Version)
        {
            return cached;
        }

        var resolved = new SnapshotChat(snapshot, PlatformChatSettings.Parse(snapshot.ChatSettings, options.Value));
        _resolved = resolved;
        return resolved;
    }

    /// <summary>What every turn at one snapshot version reads: the assistants by slug, their parsed settings and the platform's.</summary>
    private sealed class SnapshotChat
    {
        private readonly Dictionary<string, AssistantEntry> _bySlug = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<Guid, AssistantChatSettings> _settings = new();

        public SnapshotChat(PolicySnapshot snapshot, PlatformChatSettings platform)
        {
            Version = snapshot.Version;
            Platform = platform;
            foreach (var assistant in snapshot.Assistants.Values)
            {
                _bySlug.TryAdd(assistant.Slug, assistant); // the first of a (never expected) duplicate slug, as a linear search finds it
            }
        }

        public long Version { get; }

        public PlatformChatSettings Platform { get; }

        public AssistantEntry? Assistant(string slug) => _bySlug.GetValueOrDefault(slug);

        /// <summary>The published version's settings (<see cref="AssistantChatSettings.Parse"/>), for an assistant of this snapshot.</summary>
        public AssistantChatSettings Settings(AssistantEntry assistant) =>
            _settings.GetOrAdd(assistant.Id, static (_, a) => AssistantChatSettings.Parse(a.Published?.QuerySteps), assistant);
    }

    private static int Rank(string level) => level switch { Classification.Public => 0, Classification.Internal => 1, _ => 2 };

    private static ChatException NotFound(string model) => new(404, "model_not_found", $"The model '{model}' does not exist or you do not have access to it.", "model");

    [LoggerMessage(LogLevel.Information, "Chat answer for {Assistant}, message {MessageId}: {Sources} sources in the prompt, {Cited} cited, condense {Condense}")]
    private static partial void LogAnswered(ILogger logger, string assistant, Guid messageId, int sources, int cited, string condense);
}
