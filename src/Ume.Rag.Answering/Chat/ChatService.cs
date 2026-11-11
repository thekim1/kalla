using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Access;
using Ume.Rag.Domain.Retrieval;
using Ume.Rag.Infrastructure.Access;
using Ume.Rag.Infrastructure.Gateway;
using Ume.Rag.Retrieval;

namespace Ume.Rag.Answering.Chat;

/// <summary>
/// One chat turn of a RAG assistant (chat design §4.1): policy decision on the assistant (404 <c>model_not_found</c> for
/// anything not allowed), effective level and ceiling, language, the conversational check, retrieval through the knowledge
/// role in process (the assistant's published collections), coverage handling (§6.2), the prompt (§6.3, §7), then the gateway
/// call (<see cref="OpenAnswerAsync"/>) and one answer path through the <see cref="AnswerTransducer"/> (<see cref="ChatAnswer"/>,
/// §9.2–§9.4), which the stream writer relays and <see cref="CompleteAsync"/> folds into the JSON answer. Condense is part c of item 15;
/// the long-question condense is §5.1; the follow-up suggestions after the answer are §9.8 (<see cref="FollowUpsAsync"/>). Without
/// <paramref name="meters"/> (the admin test panel) the condense and follow-up calls are not counted.
/// </summary>
public sealed partial class ChatService(IPolicySnapshotSource snapshots, IAccessPolicy policy, KnowledgeService knowledge, GatewayChatClient gateway,
    IOptions<ChatOptions> options, IOptions<GatewayOptions> gatewayOptions, TimeProvider time, ILogger<ChatService> logger, IMeterFactory? meters = null)
{
    /// <summary>The <c>kind</c> of <c>rag.chat.condense</c> (§17).</summary>
    private const string FollowUpKind = "follow-up";
    private const string LongKind = "long";

    private SnapshotChat? _resolved;

    private readonly Counter<long>? _condense = meters?.Create(Microsoft.Extensions.Hosting.Extensions.TelemetryName)
        .CreateCounter<long>("rag.chat.condense", description: "Condense calls and skips by kind and outcome");

    private readonly Counter<long>? _tokens = meters?.Create(Microsoft.Extensions.Hosting.Extensions.TelemetryName)
        .CreateCounter<long>("rag.chat.tokens", description: "Model tokens of the chat role's gateway calls by direction and step");

    private readonly Counter<long>? _followUps = meters?.Create(Microsoft.Extensions.Hosting.Extensions.TelemetryName)
        .CreateCounter<long>("rag.chat.follow_ups", description: "Follow-up suggestion calls and skips by outcome");

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

        // The published level runs, public included: V36 is a confirmed warning at publish (#445, chat design §7.3).
        var settings = resolved.Settings(assistant);

        var language = settings.AlwaysSwedish ? LanguageGuess.Swedish : LanguageGuess.Detect(chat.Question);
        var ceiling = gatewayOptions.Value.Cap(Ceiling(who, assistant, snapshot)); // Gateway:MaxResidency (M2, #237)
        var messageId = Guid.CreateVersion7();
        // §6.3 (Decisions "Answer length from the model"): the overrides, else the model's limits from the mirror.
        var limits = AnswerLimits.Resolve(settings, snapshot.ModelLimits.GetValueOrDefault(assistant.Published.GatewayModel));
        var platform = resolved.Platform;
        // §7.1: the answer model reads the question's prompt form, line breaks kept; everything before it reads the query form.
        var context = new PromptContext(assistant.Name, assistant.Published.SystemPrompt, language, chat.PromptQuestion, chat.History, settings, limits);
        CondenseOutcome? condense = null;
        string? query = null;
        ChatPlan Plan(string? fixedText, ChatOutcome outcome, BuiltPrompt? prompt, RetrievalResult? retrieval, bool degraded)
        {
            // §3.2: the client's max_tokens lowers the turn's limit, never raises it; with no known limit it is the limit.
            var (maxTokens, source) = limits.MaxTokens(prompt?.EstimatedTokens ?? 0);
            if (chat.MaxTokens is { } asked)
            {
                maxTokens = maxTokens is { } limit ? Math.Min(asked, limit) : asked;
            }

            return new(messageId, chat.Model, created, assistant, ceiling, settings, maxTokens, language, fixedText, outcome, prompt, retrieval, degraded, platform,
                condense)
            {
                Query = query,
                MaxTokensSource = source,
            };
        }

        RetrievalResult? retrieval = null;
        var conversational = MessageKind.IsConversational(chat.Question);
        if (!conversational)
        {
            // §5, §5.1: a follow-up that is not self-contained, or any question over 2,000 characters, is rewritten for retrieval
            // only; the prompt keeps the user's question.
            (query, condense, var condenseFailed) = await CondenseAsync(chat, settings, platform, ceiling, messageId, ct);
            try
            {
                // The rerank call (when the assistant reranks) runs at the turn's ceiling, as the answer does (§6.7).
                retrieval = await knowledge.RetrieveAsync(who, new RetrievalRequest(query, assistant.Id, null, settings.TopK, Expand: ExpandMode.Section,
                    Purpose: RetrievalPurpose.Chat, AllowDegraded: true, RunId: messageId,
                    RerankCeiling: settings.Rerank is not null ? ceiling : null, Reranker: settings.Rerank ?? RerankerKind.ChatModel), ct);
            }
            catch (RetrievalDeniedException denied) when (denied.Status == 503)
            {
                throw new ChatException(503, "search_unavailable", "Search is unavailable. Try again shortly.");
            }
            catch (RetrievalDeniedException)
            {
                throw NotFound(chat.Model);
            }

            // A failed rewrite must not turn into "not found": then the answer comes from what was found (§6.2, §5.1).
            if (retrieval.Coverage.Status == CoverageStatus.Low && !(condenseFailed && retrieval.Results.Count > 0))
            {
                if (!GeneralKnowledgeLevels.AnswersWithoutSources(settings.GeneralKnowledge))
                {
                    return Plan(FixedTexts.NotFound(assistant.Published.NotFound, platform.NotFound, language), ChatOutcome.NotFound, null, retrieval, false);
                }

                // §10.4: fallback and open answer from general knowledge, without the weak hits, behind our caveat line.
                var general = PromptBuilder.Build(PromptKind.General, context);
                return Plan(null, ChatOutcome.AnsweredGeneral, general, retrieval, false);
            }

            if (retrieval.Coverage.Status == CoverageStatus.Unknown && retrieval.Results.Count == 0)
            {
                return Plan(FixedTexts.Unavailable(assistant.Published.NotFound, language), ChatOutcome.Unavailable, null, retrieval, false);
            }
        }

        var degraded = retrieval is not null && retrieval.Coverage.Status == CoverageStatus.Unknown;
        var prompt = conversational
            ? PromptBuilder.Build(PromptKind.Conversational, context)
            : PromptBuilder.Build(PromptKind.Sources, context, retrieval!.Results, retrieval.Sections, degraded);
        return Plan(null, ChatOutcome.Answered, prompt, retrieval, degraded);
    }

    /// <summary>
    /// The retrieval query (§5, §5.1), whether it comes from a failed condense (§6.2), and the outcome. A question whose query
    /// form is over 2,000 characters takes the long-question condense; otherwise a follow-up that is not self-contained is
    /// rewritten, and anything but an accepted rewrite keeps the question.
    /// </summary>
    private async Task<(string Query, CondenseOutcome Outcome, bool Failed)> CondenseAsync(ParsedChat chat, AssistantChatSettings settings,
        PlatformChatSettings platform, Residency ceiling, Guid messageId, CancellationToken ct)
    {
        if (QueryNormalizer.Length(chat.Question) > QueryNormalizer.MaxLength)
        {
            var (longQuery, longOutcome) = await CondenseLongAsync(chat, settings, platform, ceiling, messageId, ct);
            return (longQuery, longOutcome, longOutcome != CondenseOutcome.Rewritten);
        }

        if (Condenser.Skip(settings.CondenseEnabled && platform.CondenseModel is not null, chat.History, chat.Question) is { } skipped)
        {
            Count(FollowUpKind, skipped);
            return (chat.Question, skipped, false);
        }

        var (rewritten, outcome) = await CallCondenseAsync(Condenser.Messages(chat.History, chat.Question), Condenser.Step, options.Value.CondenseTimeout, platform,
            ceiling, messageId, ct);
        Count(FollowUpKind, outcome);
        return (rewritten ?? chat.Question, outcome, rewritten is null);
    }

    /// <summary>
    /// §5.1: the long question's search query from the condense model, or its first 2,000 characters cut at a word
    /// (<see cref="Condenser.Fallback"/>) when condense is off (no model, turned off, an <c>OnPrem</c> turn: no condense model
    /// until M2), times out, fails or is rejected; a fallback is logged with its reason, never the text.
    /// </summary>
    private async Task<(string Query, CondenseOutcome Outcome)> CondenseLongAsync(ParsedChat chat, AssistantChatSettings settings, PlatformChatSettings platform,
        Residency ceiling, Guid messageId, CancellationToken ct)
    {
        string? rewritten = null;
        var outcome = CondenseOutcome.Disabled;
        if (settings.CondenseEnabled && platform.CondenseModel is not null && ceiling != Residency.OnPrem)
        {
            (rewritten, outcome) = await CallCondenseAsync(Condenser.LongMessages(chat.History, chat.Question), Condenser.LongStep,
                options.Value.LongQuestionCondenseTimeout, platform, ceiling, messageId, ct);
        }

        Count(LongKind, outcome);
        if (rewritten is not null)
        {
            return (QueryNormalizer.Normalize(rewritten), outcome);
        }

        LogLongQuestionFallback(logger, messageId, Condenser.OutcomeName(outcome));
        return (Condenser.Fallback(chat.Question), outcome);
    }

    /// <summary>
    /// One condense call (§5 "Call", §5.1 "Model and call"), ≤ 96 tokens (<see cref="CallTaskModelAsync"/>). The accepted one-line
    /// rewrite, or null with the outcome.
    /// </summary>
    private async Task<(string? Rewritten, CondenseOutcome Outcome)> CallCondenseAsync(IReadOnlyList<ChatMessageIn> messages, string step, TimeSpan budget,
        PlatformChatSettings platform, Residency ceiling, Guid messageId, CancellationToken ct)
    {
        var (answer, failure) = await CallTaskModelAsync(messages, step, Condenser.MaxOutputTokens, budget, platform, ceiling, messageId, ct);
        if (answer is null)
        {
            return (null, failure!.Value);
        }

        return Condenser.Accept(answer.Text) is { } rewritten ? (rewritten, CondenseOutcome.Rewritten) : (null, CondenseOutcome.Rejected);
    }

    /// <summary>
    /// One call to the platform's task model (§5 "Call", §5.1 "Model and call", §9.8 "Model and call"): non-streaming, the condense
    /// model at the condense ceiling (<see cref="Condenser.Ceiling"/>, never above the turn's <paramref name="ceiling"/>), no
    /// temperature, the reasoning effort when set, a hard <paramref name="budget"/> on <see cref="TimeProvider"/> without retry;
    /// its tokens counted under <paramref name="step"/>. The answer, or null with why (<c>timeout</c>, <c>residency</c>, <c>error</c>).
    /// The caller's own cancellation (a disconnect) is not caught.
    /// </summary>
    private async Task<(GatewayAnswer? Answer, CondenseOutcome? Failure)> CallTaskModelAsync(IReadOnlyList<ChatMessageIn> messages, string step, int maxTokens,
        TimeSpan budget, PlatformChatSettings platform, Residency ceiling, Guid messageId, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(budget, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            var answer = await gateway.CompleteAsync(platform.CondenseModel!, Condenser.Ceiling(ceiling), messages, maxTokens, null, messageId, step, linked.Token,
                platform.CondenseReasoningEffort);
            CountTokens(step, answer);
            return (answer, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, CondenseOutcome.Timeout);
        }
        catch (ChatException ex) when (ex.ResidencyRefused)
        {
            return (null, CondenseOutcome.Residency);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Any other failure, a malformed 200 body included: a task-model call never fails the turn (§5.1, §9.8).
            return (null, CondenseOutcome.Error);
        }
    }

    /// <summary>
    /// How many follow-up questions the turn's call asks for (§9.8 "When"), 0 for no call: asked for, after a model answer
    /// (<c>answered</c> or <c>answered-general</c>; not a fixed text, not a conversational turn), with <c>Chat:FollowUpTimeout</c>
    /// above zero, a <c>Settings.Chat.condenseModel</c>, and a turn ceiling above <c>OnPrem</c> (no condense model until M2, §5).
    /// </summary>
    public int FollowUpCount(ChatPlan plan, UmeRequest? asked)
    {
        ArgumentNullException.ThrowIfNull(plan);
        // A conversational turn has no search query (no retrieval).
        var answered = plan.Fixed is null && (plan.Outcome is ChatOutcome.Answered or ChatOutcome.AnsweredGeneral) && plan.Query is not null;
        return asked is { FollowUps: > 0 } && answered && options.Value.FollowUpTimeout > TimeSpan.Zero && plan.Platform.CondenseModel is not null &&
            plan.Ceiling != Residency.OnPrem
            ? asked.FollowUps
            : 0;
    }

    /// <summary>
    /// The answer's <c>ume</c> member as the first <c>ume</c> chunk carries it (§3.8): <c>follow_ups</c> is <c>[]</c> there when
    /// they were asked for and are skipped before any call (counted <c>skipped</c>), and left for the second chunk when a call follows.
    /// </summary>
    public UmeAnswer Ume(ChatPlan plan, IReadOnlyList<PromptSource> cited, UmeRequest asked)
    {
        ArgumentNullException.ThrowIfNull(asked);
        var pending = FollowUpCount(plan, asked) > 0;
        if (asked.FollowUps > 0 && !pending)
        {
            _followUps?.Add(1, new KeyValuePair<string, object?>("outcome", FollowUps.Skipped));
        }

        return UmeAnswer.For(plan, cited, asked, pending);
    }

    /// <summary>
    /// The follow-up suggestions (§9.8): one call at the condense ceiling with <c>x-ume-step: follow-ups</c> and <c>max_tokens</c>
    /// 40 × <paramref name="count"/>, from the turn's search query, the <paramref name="delivered"/> answer and the prompt's titles
    /// (<paramref name="cited"/> first), bounded by <c>Chat:FollowUpTimeout</c>; then the accept rule against the
    /// <paramref name="question"/> and the query. Timeout, error, residency refusal or nothing accepted give <c>[]</c>, counted,
    /// never retried; the suggestions are never logged. The caller's cancellation (a disconnect) cancels the call and is thrown.
    /// </summary>
    public async Task<IReadOnlyList<string>> FollowUpsAsync(ChatPlan plan, string question, int count, string delivered, IReadOnlyList<PromptSource> cited,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(cited);
        var messages = FollowUps.Messages(count, plan.Language, plan.Query!, FollowUps.Answer(plan, delivered), FollowUps.Titles(plan.Prompt?.Sources ?? [], cited));
        var (answer, failure) = await CallTaskModelAsync(messages, FollowUps.Step, FollowUps.TokensPerQuestion * count, options.Value.FollowUpTimeout,
            plan.Platform, plan.Ceiling, plan.MessageId, ct);
        IReadOnlyList<string> suggestions = answer is null ? [] : FollowUps.Accept(answer.Text, count, question, plan.Query);
        var outcome = answer is null ? Condenser.OutcomeName(failure!.Value) : suggestions.Count > 0 ? FollowUps.Used : FollowUps.Empty;
        _followUps?.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
        return suggestions;
    }

    /// <summary><c>rag.chat.condense{kind, outcome}</c> (§17); for <c>long</c> every outcome but <c>used</c> is the fallback.</summary>
    private void Count(string kind, CondenseOutcome outcome) =>
        _condense?.Add(1, new TagList { { "kind", kind }, { "outcome", Condenser.OutcomeName(outcome) } });

    /// <summary><c>rag.chat.tokens{direction, step}</c> (§17) of a condense call; never in the response's usage (§3.2).</summary>
    private void CountTokens(string step, GatewayAnswer answer)
    {
        _tokens?.Add(answer.PromptTokens, new TagList { { "direction", "input" }, { "step", step } });
        _tokens?.Add(answer.CompletionTokens, new TagList { { "direction", "output" }, { "step", step } });
    }

    /// <summary>
    /// Opens the answer of <paramref name="plan"/>: no call for a fixed text, else the gateway call at the plan's model,
    /// ceiling, max tokens and temperature, as a stream (§8.2: status and residency checked when this returns, before the
    /// caller's first byte) or as one JSON answer.
    /// </summary>
    public async Task<ChatAnswer> OpenAnswerAsync(ChatPlan plan, bool stream, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Fixed is not null)
        {
            return new ChatAnswer(plan, this, gateway, null, null);
        }

        var model = plan.Assistant.Published!.GatewayModel;
        if (stream)
        {
            var response = await gateway.OpenStreamAsync(model, plan.Ceiling, plan.Prompt!.Messages, plan.MaxTokens, plan.Settings.Temperature, plan.MessageId, ct);
            return new ChatAnswer(plan, this, gateway, response, null);
        }

        var answer = await gateway.CompleteAsync(model, plan.Ceiling, plan.Prompt!.Messages, plan.MaxTokens, plan.Settings.Temperature, plan.MessageId, "chat-answer", ct);
        return new ChatAnswer(plan, this, gateway, null, answer);
    }

    /// <summary>The turn as one JSON answer: the events of <see cref="ChatAnswer.ReadAsync"/> folded into the content and its annotations.</summary>
    public async Task<ChatCompletion> CompleteAsync(RagPrincipal who, ParsedChat chat, CancellationToken ct)
    {
        var plan = await PlanAsync(who, chat, ct);
        using var answer = await OpenAnswerAsync(plan, stream: false, ct);
        var content = new System.Text.StringBuilder();
        var annotations = new List<UrlCitation>();
        AnswerEvent.End end = new("stop", 0, 0);
        await foreach (var item in answer.ReadAsync(ct))
        {
            switch (item)
            {
                case AnswerEvent.Own own:
                    content.Append(own.Text);
                    break;
                case AnswerEvent.Pieces pieces:
                    foreach (var text in pieces.Items.OfType<AnswerPiece.Text>())
                    {
                        content.Append(text.Content);
                        annotations.AddRange(text.Annotations.Select(a => a with { StartIndex = a.StartIndex + pieces.Shift, EndIndex = a.EndIndex + pieces.Shift }));
                    }

                    break;
                case AnswerEvent.End e:
                    end = e;
                    break;
            }
        }

        answer.Finish();
        return new ChatCompletion(plan.MessageId, plan.Model, content.ToString(), annotations, answer.Cited, end.PromptTokens, end.CompletionTokens, end.FinishReason,
            plan.Outcome, plan.Created) { Plan = plan };
    }

    /// <summary>After the answer (§9.5): the counters with only the cited ranks, and a log line.</summary>
    internal void Finish(ChatPlan plan, AnswerTransducer transducer)
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

    [LoggerMessage(LogLevel.Information, "Long question of message {MessageId} searched by its first 2,000 characters: condense {Reason}")]
    private static partial void LogLongQuestionFallback(ILogger logger, Guid messageId, string reason);
}
