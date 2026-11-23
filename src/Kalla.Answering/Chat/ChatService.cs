using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Kalla.Domain;
using Kalla.Domain.Access;
using Kalla.Domain.Retrieval;
using Kalla.Infrastructure.Access;
using Kalla.Infrastructure.Gateway;
using Kalla.Retrieval;

namespace Kalla.Answering.Chat;

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
    IOptions<ChatOptions> options, GatewayCeiling serviceCeiling, TimeProvider time, ILogger<ChatService> logger, IMeterFactory? meters = null)
{
    /// <summary>The <c>kind</c> of <c>rag.chat.condense</c> (§17).</summary>
    private const string FollowUpKind = "follow-up";
    private const string LongKind = "long";

    private SnapshotChat? _resolved;

    private readonly Counter<long>? _condense = meters?.Create(Microsoft.Extensions.Hosting.Extensions.TelemetryName)
        .CreateCounter<long>("rag.chat.condense", description: "Condense calls and skips by kind and outcome");

    private readonly Counter<long>? _tokens = meters?.Create(Microsoft.Extensions.Hosting.Extensions.TelemetryName)
        .CreateCounter<long>("rag.chat.tokens", description: "Model tokens of the chat role's gateway calls by direction and step");

    private readonly Counter<double>? _cost = meters?.Create(Microsoft.Extensions.Hosting.Extensions.TelemetryName)
        .CreateCounter<double>("rag.chat.cost", unit: "USD", description: "Cost of the chat role's gateway calls by step and source (gateway or estimate)");

    private readonly Counter<long>? _followUps = meters?.Create(Microsoft.Extensions.Hosting.Extensions.TelemetryName)
        .CreateCounter<long>("rag.chat.follow_ups", description: "Follow-up suggestion calls and skips by outcome");

    private readonly Counter<long>? _escalation = meters?.Create(Microsoft.Extensions.Hosting.Extensions.TelemetryName)
        .CreateCounter<long>("rag.chat.escalation", description: "Escalation hand-offs by reason and what became of the summary");

    private readonly Histogram<double>? _estimateRatio = meters?.Create(Microsoft.Extensions.Hosting.Extensions.TelemetryName)
        .CreateHistogram<double>("rag.chat.prompt.estimate_ratio",
            description: "The prompt's estimated tokens (characters / 3 + 4 per message) over the prompt_tokens the gateway reported, per answer");

    /// <summary>
    /// Chat design §6.3, §17: the prompt's estimate (<see cref="BuiltPrompt.EstimatedTokens"/>) over the <c>prompt_tokens</c> the
    /// gateway reported, once per answer that reports them; above 1 the estimate errs high (as meant), below 1 it errs low.
    /// <c>Chat:CharsPerToken</c> is tuned from it (to-verify row 103). Nothing for a fixed text, or an answer without usage.
    /// </summary>
    internal void RecordPromptEstimate(ChatPlan plan, int promptTokens)
    {
        if (_estimateRatio is not null && promptTokens > 0 && plan.Prompt is { EstimatedTokens: > 0 } prompt)
        {
            _estimateRatio.Record((double)prompt.EstimatedTokens / promptTokens);
        }
    }

    /// <summary><c>Settings.Chat</c> at the current snapshot, over the deployment knobs.</summary>
    public PlatformChatSettings Platform => Resolved(snapshots.Current).Platform;

    /// <summary>The assistant with slug <paramref name="model"/> at the current snapshot, or null.</summary>
    public AssistantEntry? Assistant(string model) => Resolved(snapshots.Current).Assistant(model);

    /// <summary>
    /// A new turn's usage records (§8.3), priced from the current snapshot's model mirror and counted in <c>rag.chat.tokens</c>
    /// and <c>rag.chat.cost</c> (§17) when this service has meters.
    /// </summary>
    public TurnUsage Usage() => new(snapshots.Current.ModelPrices, time.GetUtcNow(), _tokens is null ? null : new Action<UsageRecord>(CountUsage));

    /// <summary>The plan of a turn whose usage records nobody writes (tests; chat and the admin test panel pass their own).</summary>
    public Task<ChatPlan> PlanAsync(RagPrincipal who, ParsedChat chat, CancellationToken ct) => PlanAsync(who, chat, Usage(), ct);

    /// <summary>
    /// Steps 5–12 of §4.1 up to the answer call. Condense, the query embedding and rerank add their usage to
    /// <paramref name="usage"/> as they answer, so a turn that fails afterwards keeps it (§12.1); the plan carries it on.
    /// </summary>
    public async Task<ChatPlan> PlanAsync(RagPrincipal who, ParsedChat chat, TurnUsage usage, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(usage);
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

        // §3.6, step 7 (B-44): an assistant whose gateway key is missing from the mirror or unusable answers before any model call.
        if (assistant.GatewayKeyId is { } keyId)
        {
            var reason = snapshot.GatewayKeys.TryGetValue(keyId, out var key) ? key.UnusableReason : "missing";
            if (reason is not null)
            {
                throw ChatRefusals.AssistantUnavailable(reason);
            }
        }

        // The published level runs, public included: V36 is a confirmed warning at publish (#445, chat design §7.3). A request's
        // kalla.general_knowledge only tightens it (§3.8); the routing, the frame and the plan read this effective level.
        var settings = resolved.Settings(assistant);
        if (chat.Kalla?.GeneralKnowledge is { } requested)
        {
            settings = settings with { GeneralKnowledge = GeneralKnowledgeLevels.Effective(settings.GeneralKnowledge, requested) };
        }
        // B-42 (identity §4.7, cost-attribution §4.1): every call of the turn runs as the assistant's gateway key.
        using var acting = GatewayCallScope.Begin(Acting(assistant));

        var language = settings.AlwaysSwedish ? LanguageGuess.Swedish : LanguageGuess.Detect(chat.Question);
        // Lowered to the credential key's residencies (M2 keeps them at Eu; Decisions "Residency per level", M-17 C9).
        var ceiling = await serviceCeiling.CapAsync(Ceiling(who, assistant, snapshot), ct);
        var messageId = Guid.CreateVersion7();
        // §6.3 (Decisions "Answer length from the model"): the overrides, else the model's limits from the mirror.
        var platform = resolved.Platform;
        var limits = AnswerLimits.Resolve(settings, snapshot.ModelLimits.GetValueOrDefault(assistant.Published.GatewayModel), platform.MaxAnswerTokens);
        // §7.1: the answer model reads the question's prompt form, line breaks kept; everything before it reads the query form.
        var context = new PromptContext(assistant.Name, assistant.Published.SystemPrompt, language, chat.PromptQuestion, chat.History, settings, limits, platform.Texts);
        CondenseOutcome? condense = null;
        string? query = null;

        // §3.8 (Decisions "Topics per request"): the asked topics narrow the published links; with none of the live links left the
        // turn ends not-found before any condense call or search, and a turn without retrieval reports what its links would search.
        var askedTopics = chat.Kalla?.Topics is { Count: > 0 } asked ? asked : null;
        IReadOnlyList<string>? linkTopics = null;
        var noLinkLeft = false;
        if (askedTopics is not null)
        {
            var live = assistant.Published.CollectionLinks
                .Where(l => snapshot.Collections.TryGetValue(l.CollectionId, out var collection) && collection.IsLive).Select(l => l.Tags).ToList();
            linkTopics = TopicNarrowing.Searched(live, askedTopics);
            noLinkLeft = live.All(l => TopicNarrowing.Narrow(l, askedTopics) is null);
        }

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
                SearchedTopics = retrieval?.SearchedTopics ?? linkTopics,
                MaxTokensSource = source,
                Usage = usage,
            };
        }

        RetrievalResult? retrieval = null;
        var conversational = MessageKind.IsConversational(chat.Question);
        if (!conversational && noLinkLeft)
        {
            return Plan(FixedTexts.NotFound(assistant.Published.NotFound, platform.NotFound, language), ChatOutcome.NotFound, null, null, false);
        }

        if (!conversational)
        {
            // §5, §5.1: a follow-up that is not self-contained, or any question over 2,000 characters, is rewritten for retrieval
            // only; the prompt keeps the user's question.
            (query, condense, var condenseFailed) = await CondenseAsync(chat, settings, platform, ceiling, messageId, usage, ct);
            try
            {
                // The rerank call (when the assistant reranks) runs at the turn's ceiling, as the answer does (§6.7).
                retrieval = await knowledge.RetrieveAsync(who, new RetrievalRequest(query, assistant.Id, null, settings.TopK, Expand: ExpandMode.Section,
                    Purpose: RetrievalPurpose.Chat, AllowDegraded: true, RunId: messageId,
                    RerankCeiling: settings.Rerank is not null ? ceiling : null, Reranker: settings.Rerank ?? RerankerKind.ChatModel, Topics: askedTopics),
                    usage.Add, ct);
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
                return Plan(FixedTexts.Unavailable(assistant.Published.NotFound, platform.NotFound, language), ChatOutcome.Unavailable, null, retrieval, false);
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
        PlatformChatSettings platform, Residency ceiling, Guid messageId, TurnUsage usage, CancellationToken ct)
    {
        if (QueryNormalizer.Length(chat.Question) > QueryNormalizer.MaxLength)
        {
            var (longQuery, longOutcome) = await CondenseLongAsync(chat, settings, platform, ceiling, messageId, usage, ct);
            return (longQuery, longOutcome, longOutcome != CondenseOutcome.Rewritten);
        }

        if (Condenser.Skip(settings.CondenseEnabled && platform.CondenseModel is not null, chat.History, chat.Question) is { } skipped)
        {
            Count(FollowUpKind, skipped);
            return (chat.Question, skipped, false);
        }

        var (rewritten, outcome) = await CallCondenseAsync(Condenser.Messages(chat.History, chat.Question, platform.Texts), Condenser.Step, options.Value.CondenseTimeout, platform,
            ceiling, messageId, usage, ct);
        Count(FollowUpKind, outcome);
        return (rewritten ?? chat.Question, outcome, rewritten is null);
    }

    /// <summary>
    /// §5.1: the long question's search query from the condense model, or its first 2,000 characters cut at a word
    /// (<see cref="Condenser.Fallback"/>) when condense is off (no model, turned off, an <c>OnPrem</c> turn: no condense model
    /// until M2), times out, fails or is rejected; a fallback is logged with its reason, never the text.
    /// </summary>
    private async Task<(string Query, CondenseOutcome Outcome)> CondenseLongAsync(ParsedChat chat, AssistantChatSettings settings, PlatformChatSettings platform,
        Residency ceiling, Guid messageId, TurnUsage usage, CancellationToken ct)
    {
        string? rewritten = null;
        var outcome = CondenseOutcome.Disabled;
        if (settings.CondenseEnabled && platform.CondenseModel is not null && ceiling != Residency.OnPrem)
        {
            (rewritten, outcome) = await CallCondenseAsync(Condenser.LongMessages(chat.History, chat.Question, platform.Texts), Condenser.LongStep,
                options.Value.LongQuestionCondenseTimeout, platform, ceiling, messageId, usage, ct);
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
        PlatformChatSettings platform, Residency ceiling, Guid messageId, TurnUsage usage, CancellationToken ct)
    {
        var (answer, failure) = await CallTaskModelAsync(messages, step, Condenser.MaxOutputTokens, budget, platform, ceiling, messageId, usage, ct);
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
    /// its usage record under <paramref name="step"/> goes to <paramref name="usage"/> once it answers <c>2xx</c> (a malformed body
    /// included: the gateway charged it). The answer, or null with why (<c>timeout</c>, <c>residency</c>, <c>error</c>). The
    /// caller's own cancellation (a disconnect) is not caught.
    /// </summary>
    private async Task<(GatewayAnswer? Answer, CondenseOutcome? Failure)> CallTaskModelAsync(IReadOnlyList<ChatMessageIn> messages, string step, int maxTokens,
        TimeSpan budget, PlatformChatSettings platform, Residency ceiling, Guid messageId, TurnUsage usage, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(budget, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            var answer = await gateway.CompleteAsync(platform.CondenseModel!, Condenser.Ceiling(ceiling), messages, maxTokens, null, messageId, step, linked.Token,
                platform.CondenseReasoningEffort, usage.Add);
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
    public int FollowUpCount(ChatPlan plan, KallaRequest? asked)
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
    /// The answer's <c>kalla</c> member as the first <c>kalla</c> chunk carries it (§3.8): <c>follow_ups</c> is <c>[]</c> there when
    /// they were asked for and are skipped before any call (counted <c>skipped</c>), and left for the second chunk when a call follows.
    /// </summary>
    public KallaAnswer Kalla(ChatPlan plan, IReadOnlyList<PromptSource> cited, KallaRequest asked)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(asked);
        var pending = FollowUpCount(plan, asked) > 0;
        if (asked.FollowUps > 0 && !pending)
        {
            _followUps?.Add(1, new KeyValuePair<string, object?>("outcome", FollowUps.Skipped));
        }

        // §9.9: null for a turn that needs no hand-off; the route without a summary when the call is skipped before it starts;
        // left for the second chunk when the call follows.
        EscalationMember? escalation = null;
        if (asked.Escalation && !Escalation.Applies(plan.Outcome))
        {
            escalation = new EscalationMember(null);
        }
        else if (asked.Escalation && !EscalationPending(plan, asked))
        {
            CountEscalation(plan, Escalation.Skipped);
            escalation = new EscalationMember(Route(plan, null));
        }

        return KallaAnswer.For(plan, cited, asked, pending, escalation);
    }

    /// <summary>
    /// Whether the escalation summary's call follows the answer (§9.9 "Summary call"): asked for, a turn that gets the hand-off
    /// (<see cref="Escalation.Applies"/>), <c>Chat:EscalationTimeout</c> above zero, a condense model, and a ceiling above
    /// <c>OnPrem</c> (no condense model there, §5).
    /// </summary>
    public bool EscalationPending(ChatPlan plan, KallaRequest? asked)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return asked is { Escalation: true } && Escalation.Applies(plan.Outcome) && options.Value.EscalationTimeout > TimeSpan.Zero &&
            plan.Platform.CondenseModel is not null && plan.Ceiling != Residency.OnPrem;
    }

    /// <summary>
    /// The escalation hand-off with its summary (§9.9): one call at the condense ceiling as the assistant's key with
    /// <c>x-fors-step: escalation</c> and <c>max_tokens</c> 200, from the user's last <paramref name="question"/>, the search query
    /// when condensed and the outcome's line, bounded by <c>Chat:EscalationTimeout</c>; then the accept rule. Timeout, error,
    /// residency refusal or a rejected text give a null summary, counted, never retried; the route still comes. The summary
    /// is never logged. The caller's cancellation (a disconnect) cancels the call and is thrown.
    /// </summary>
    public async Task<EscalationMember> EscalationAsync(ChatPlan plan, string question, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(question);
        var messages = Escalation.Messages(plan.Outcome, plan.Language, question, plan.Query, plan.Platform.Texts);
        using var acting = GatewayCallScope.Begin(Acting(plan.Assistant));
        var (answer, failure) = await CallTaskModelAsync(messages, Escalation.Step, Escalation.MaxTokens, options.Value.EscalationTimeout,
            plan.Platform, plan.Ceiling, plan.MessageId, plan.Usage, ct);
        var summary = answer is null ? null : Escalation.Accept(answer.Text);
        CountEscalation(plan, answer is null ? Condenser.OutcomeName(failure!.Value) : summary is null ? Escalation.Rejected : Escalation.Used);
        return new EscalationMember(Route(plan, summary));
    }

    /// <summary>
    /// The second <c>kalla</c> chunk (§3.8 Delivery): the follow-up call (when <paramref name="followUps"/> is above zero) and the
    /// escalation summary's call (when pending) at the same time, so the wait is the longer budget, not their sum.
    /// </summary>
    public async Task<KallaLater> LaterAsync(ChatPlan plan, KallaRequest? asked, string question, int followUps, string delivered,
        IReadOnlyList<PromptSource> cited, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var suggestions = followUps > 0 ? FollowUpsAsync(plan, question, followUps, delivered, cited, ct) : null;
        var escalation = EscalationPending(plan, asked) ? EscalationAsync(plan, question, ct) : null;
        return new KallaLater(suggestions is null ? null : await suggestions, escalation is null ? null : await escalation);
    }

    /// <summary>The escalation route of <paramref name="plan"/> (§9.9 "Object"): its outcome, the contact links, the summary.</summary>
    private static EscalationAnswer Route(ChatPlan plan, string? summary) =>
        new(ChatOutcomes.Name(plan.Outcome), FixedTexts.Contacts(plan.Assistant.Published?.NotFound, plan.Platform.NotFound), summary);

    /// <summary><c>rag.chat.escalation{reason, summary}</c> (§17).</summary>
    private void CountEscalation(ChatPlan plan, string summary) =>
        _escalation?.Add(1, new TagList { { "reason", ChatOutcomes.Name(plan.Outcome) }, { "summary", summary } });

    /// <summary>
    /// The follow-up suggestions (§9.8): one call at the condense ceiling with <c>x-fors-step: follow-ups</c> and <c>max_tokens</c>
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
        var messages = FollowUps.Messages(count, plan.Language, plan.Query!, FollowUps.Answer(plan, delivered), FollowUps.Titles(plan.Prompt?.Sources ?? [], cited),
            plan.Platform.Texts);
        using var acting = GatewayCallScope.Begin(Acting(plan.Assistant));
        var (answer, failure) = await CallTaskModelAsync(messages, FollowUps.Step, FollowUps.TokensPerQuestion * count, options.Value.FollowUpTimeout,
            plan.Platform, plan.Ceiling, plan.MessageId, plan.Usage, ct);
        IReadOnlyList<string> suggestions = answer is null ? [] : FollowUps.Accept(answer.Text, count, question, plan.Query);
        var outcome = answer is null ? Condenser.OutcomeName(failure!.Value) : suggestions.Count > 0 ? FollowUps.Used : FollowUps.Empty;
        _followUps?.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
        return suggestions;
    }

    /// <summary>The turn's calls as the assistant's gateway key (null: the service's credential key; B-42).</summary>
    private static GatewayCall Acting(AssistantEntry assistant) => new(assistant.GatewayKeyId, AssistantId: assistant.Id);

    /// <summary><c>rag.chat.condense{kind, outcome}</c> (§17); for <c>long</c> every outcome but <c>used</c> is the fallback.</summary>
    private void Count(string kind, CondenseOutcome outcome) =>
        _condense?.Add(1, new TagList { { "kind", kind }, { "outcome", Condenser.OutcomeName(outcome) } });

    /// <summary>
    /// <c>rag.chat.tokens{direction, step}</c> and <c>rag.chat.cost{step, source}</c> (§17) of one model call of a turn; never in
    /// the response's usage (§3.2). <c>input</c> is every input token, cached ones included (as the gateway reports them),
    /// <c>cached-input</c> the cached part; the cost is the gateway's (<c>gateway</c>) or the mirror's estimate (<c>estimate</c>).
    /// </summary>
    private void CountUsage(UsageRecord record)
    {
        _tokens?.Add(record.InputTokens, new TagList { { "direction", "input" }, { "step", record.Step } });
        _tokens?.Add(record.CachedInputTokens, new TagList { { "direction", "cached-input" }, { "step", record.Step } });
        _tokens?.Add(record.OutputTokens, new TagList { { "direction", "output" }, { "step", record.Step } });
        _cost?.Add((double)(record.Estimated ? record.EstimatedCostUsd : record.CostUsd),
            new TagList { { "step", record.Step }, { "source", record.Estimated ? "estimate" : "gateway" } });
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
        using var acting = GatewayCallScope.Begin(Acting(plan.Assistant));
        if (stream)
        {
            var response = await gateway.OpenStreamAsync(model, plan.Ceiling, plan.Prompt!.Messages, plan.MaxTokens, plan.Settings.Temperature, plan.MessageId, ct);
            return new ChatAnswer(plan, this, gateway, response, null);
        }

        var answer = await gateway.CompleteAsync(model, plan.Ceiling, plan.Prompt!.Messages, plan.MaxTokens, plan.Settings.Temperature, plan.MessageId,
            GatewayChatClient.AnswerStep, ct, usage: plan.Usage.Add);
        return new ChatAnswer(plan, this, gateway, null, answer);
    }

    /// <summary>
    /// The turn as one JSON answer: the events of <see cref="ChatAnswer.ReadAsync"/> folded into the content and its annotations;
    /// every call's usage in <paramref name="usage"/>.
    /// </summary>
    public async Task<ChatCompletion> CompleteAsync(RagPrincipal who, ParsedChat chat, TurnUsage usage, CancellationToken ct)
    {
        var plan = await PlanAsync(who, chat, usage, ct);
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
