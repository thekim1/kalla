using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Access;
using Ume.Rag.Domain.Retrieval;
using Ume.Rag.Infrastructure.Access;
using Ume.Rag.Infrastructure.Gateway;

namespace Ume.Rag.Answering.Chat;

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
        var context = new PromptContext(assistant.Name, assistant.Published.SystemPrompt, language, chat.Question, chat.History, settings);
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
