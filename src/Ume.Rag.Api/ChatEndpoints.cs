using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Metadata;
using Ume.Rag.Answering.Chat;
using Ume.Rag.Domain.Access;
using Ume.Rag.Infrastructure.Access;

namespace Ume.Rag.Api;

/// <summary>
/// The chat role's routes (chat design §3; P1a item 15, part a): <c>GET /v1/models</c> and <c>POST /v1/chat/completions</c>
/// (non-streaming in this part). Errors in the OpenAI shape (§3.6), also for authentication. Open WebUI clients present
/// the user's JWT (identity §5, <see cref="ChatCallers"/>).
/// </summary>
public static class ChatEndpoints
{
    /// <summary>The chat body limit (§3.2: Open WebUI resends the whole chat); every other rag-api route keeps 16 KB.</summary>
    public const long BodyLimit = 2 * 1024 * 1024;

    public static IEndpointRouteBuilder MapChat(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var chat = app.MapGroup("/v1").WithTags("Chat");

        chat.MapGet("/models", async (HttpContext http, ChatCallers callers, IPolicySnapshotSource snapshots, IAccessPolicy policy, CancellationToken ct) =>
            await OpenAiAsync(http, async () =>
            {
                var who = await callers.AuthenticateAsync(http.Request, userRequired: false, ct);
                var snapshot = snapshots.Current;
                var candidates = snapshot.Assistants.Values.Where(a => a.State == "active" && a.Published is not null).ToList();
                var allowed = policy.Filter(who, candidates.Select(a => ResourceRef.Assistant(a.Id)), RagAction.ListModels, snapshot, snapshots.IsConfirmed)
                    .Select(r => r.Id).ToHashSet();
                return Results.Json(new
                {
                    @object = "list",
                    data = candidates.Where(a => allowed.Contains(a.Id)).OrderBy(a => a.Slug, StringComparer.Ordinal).Select(a =>
                    {
                        var description = Description(a);
                        return new
                        {
                            id = a.Slug, @object = "model", created = (a.Published!.PublishedAt ?? DateTimeOffset.UnixEpoch).ToUnixTimeSeconds(), owned_by = "umea-kommun",
                            name = a.Name, description, info = new { meta = new { description } },
                        };
                    }),
                });
            }))
            .WithName("ListModels").WithSummary("The assistants this caller may use, as OpenAI models");

        chat.MapPost("/chat/completions", async (HttpContext http, ChatCallers callers, ChatService service, ChatRateLimiter limiter, ConversationIds ids,
                ConversationWriter writer, IPolicySnapshotSource snapshots, KnowledgeSecurity security, ChatMetrics metrics, CancellationToken ct) =>
            await OpenAiAsync(http, async () =>
            {
                var who = await callers.AuthenticateAsync(http.Request, userRequired: true, ct);
                JsonElement body;
                try
                {
                    body = (await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: ct)).RootElement;
                }
                catch (JsonException)
                {
                    throw new ChatException(400, "invalid_request", "The body is not valid JSON.");
                }

                // §3.8: Open WebUI renders no ume chunk, so its requests ignore the member without validating it.
                var parsed = ChatRequestParser.Parse(body, readUme: who.Client.Kind != "open-webui");
                limiter.Check(who, service.Platform.MessagesPerMinutePerUser); // §4.1 step 4: per user (or per key, when the key is the subject) and minute, before any model call
                // §11.1: an issued id is verified before any model call, bound to the asked assistant (#266); an unknown model is
                // the plan's 404, so Guid.Empty only ever signs or verifies an id that is never used.
                var assistantId = service.Assistant(parsed.Model)?.Id ?? Guid.Empty;
                (Guid? Issued, string? External) conversation;
                try
                {
                    conversation = ConversationKey(http, who, assistantId, ids);
                }
                catch (ChatException refused) when (refused.Code == "conversation_not_found")
                {
                    security.ConversationNotOwned(http, who, http.Request.Headers[ConversationIds.HeaderName].ToString(), "Chat", snapshots.Current.Version);
                    throw;
                }
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                // §8.3, §12.1: every model call of the turn adds its usage record here; each record is written once, with the turn
                // or, when the turn fails or breaks off after a call (or a call follows the turn's write), as a usage-only turn.
                var usage = service.Usage();
                ValueTask Enqueue(ChatPlan plan, IReadOnlyList<PromptSource> cited, int prompt, int completion)
                {
                    metrics.Completed(plan, who, parsed.Stream);
                    return writer.EnqueueAsync(Turn(http, plan, who, conversation, cited, prompt, completion,
                        (int)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds) with { Usage = usage.Take() });
                }
                ValueTask EnqueueUsage(bool unavailable = false)
                {
                    var records = usage.Take();
                    return (records.Count > 0 || unavailable) && service.Assistant(parsed.Model) is { } assistant
                        ? writer.EnqueueAsync(ChatTurn.UsageOnlyOf(assistant.Id, assistant.DepartmentId, who.Client.Id, assistant.Classification, usage.Started, records)
                            with { Unavailable = unavailable })
                        : ValueTask.CompletedTask;
                }
                UmeAnswer? Ume(ChatPlan plan, IReadOnlyList<PromptSource> cited)
                {
                    if (parsed.Ume is not { } asked)
                    {
                        return null;
                    }

                    var ume = service.Ume(plan, cited, asked);
                    metrics.Ume(asked, ume, who);
                    return ume;
                }
                try
                {
                    if (!parsed.Stream)
                    {
                        var answer = await service.CompleteAsync(who, parsed, usage, ct);
                        await Enqueue(answer.Plan!, answer.Cited, answer.PromptTokens, answer.CompletionTokens);
                        var ume = Ume(answer.Plan!, answer.Cited);
                        // §3.8, §9.8: the JSON answer is written when the follow-up call has returned or its budget has run out.
                        if (ume is not null && service.FollowUpCount(answer.Plan!, parsed.Ume) is > 0 and var count)
                        {
                            ume = ume with { FollowUps = await service.FollowUpsAsync(answer.Plan!, parsed.Question, count, answer.Content, answer.Cited, ct) };
                            await EnqueueUsage(); // the follow-up call came after the turn's write
                        }

                        return Json(answer, ume);
                    }

                    var plan = await service.PlanAsync(who, parsed, usage, ct);
                    // §8.2: status and residency are checked here, before our first byte; a refusal is still a JSON error.
                    var streamed = await service.OpenAnswerAsync(plan, stream: true, ct);
                    var followUps = service.FollowUpCount(plan, parsed.Ume);
                    return new ChatSse(streamed, who.Client.Kind == "open-webui", parsed.IncludeUsage, (c, p, o) => Enqueue(plan, c, p, o),
                        parsed.Ume is null ? null : cited => Ume(plan, cited)!,
                        followUps > 0 ? (cited, delivered, token) => service.FollowUpsAsync(plan, parsed.Question, followUps, delivered, cited, token) : null,
                        () => EnqueueUsage());
                }
                catch (ChatException refused) when (refused.Unavailable)
                {
                    // §3.6 (B-44): an unusable key or a spent budget: the turn's outcome is unavailable, counted in the day's totals.
                    await EnqueueUsage(unavailable: true);
                    throw;
                }
                catch (Exception) when (usage.HasRecords)
                {
                    // A turn that fails after a model call (an answer call refused after condense and the query embedding) is usage-only.
                    await EnqueueUsage();
                    throw;
                }
            }))
            .WithMetadata(new ChatBodyLimit())
            .WithName("ChatCompletions").WithSummary("Ask an assistant (OpenAI chat completions, JSON or stream)");

        return app;
    }

    /// <summary>Open WebUI's temporary, local and channel chats (§11.1, <c>NON_SAVED_CHAT_ID_PREFIXES</c>): never kept beyond mode none.</summary>
    private static readonly string[] NonSavedPrefixes = ["temporary:", "local:", "channel:"];

    /// <summary>
    /// The turn's conversation (chat design §11.1): Open WebUI's chat id (<c>X-OpenWebUI-Chat-Id</c>), or for other clients
    /// the issued id sent back (verified, else 404) or a new one, returned in <c>x-rag-conversation-id</c>.
    /// </summary>
    private static (Guid? Issued, string? External) ConversationKey(HttpContext http, RagPrincipal who, Guid assistantId, ConversationIds ids)
    {
        if (who.Client.Kind == "open-webui" && http.Request.Headers["X-OpenWebUI-Chat-Id"].ToString() is { Length: > 0 and <= 200 } chatId)
        {
            return (null, chatId);
        }

        var sent = http.Request.Headers[ConversationIds.HeaderName].ToString();
        if (sent.Length > 0)
        {
            var id = ids.Verify(sent, who.Client.Id, assistantId, who.SubjectId);
            http.Response.Headers[ConversationIds.HeaderName] = sent;
            return (id, null);
        }

        var (issued, header) = ids.Issue(who.Client.Id, assistantId, who.SubjectId);
        http.Response.Headers[ConversationIds.HeaderName] = header;
        return (issued, null);
    }

    /// <summary>The metadata-only turn for the writer (history mode none, all of P1): no text, the sources with their prompt numbers.</summary>
    private static string? Header(HttpContext http, string name) => http.Request.Headers[name].ToString() is { Length: > 0 and <= 200 } value ? value : null;

    private static ChatTurn Turn(HttpContext http, ChatPlan plan, RagPrincipal who, (Guid? Issued, string? External) conversation, IReadOnlyList<PromptSource> cited, int prompt,
        int completion, int latencyMs)
    {
        var numbers = (plan.Prompt?.Sources ?? []).SelectMany(s => s.Chunks.Select(c => (c.ChunkId, s.Number))).GroupBy(x => x.ChunkId)
            .ToDictionary(g => g.Key, g => g.First().Number);
        var used = cited.SelectMany(s => s.Chunks.Select(c => c.ChunkId)).ToHashSet();
        var sources = (plan.Retrieval?.Results ?? []).Select(r => new TurnSource(r.Rank, r.DocumentId, r.ChunkId, r.CollectionId,
            numbers.TryGetValue(r.ChunkId, out var n) ? (short)n : null, r.Page, r.PageEnd, used.Contains(r.ChunkId))).ToList();
        var answered = plan.Fixed is null;
        // §11.1: a temporary/local/channel chat is mode none; in P1 every assistant is mode none anyway (weakness review §4).
        var mode = conversation.External is { } external && NonSavedPrefixes.Any(p => external.StartsWith(p, StringComparison.Ordinal))
            ? Domain.Entities.HistoryModes.None
            : plan.Assistant.HistoryMode;
        return new ChatTurn(plan.MessageId, conversation.Issued, conversation.External, plan.Assistant.Id, plan.Assistant.Published?.Id, plan.Assistant.DepartmentId, who.Client.Id,
            ChatTurn.SubjectKindOf(who.Kind), who.SubjectId, plan.Assistant.Classification, mode, plan.Assistant.RetentionDays, ChatTurn.OutcomeOf(plan.Outcome),
            ChatTurn.CoverageOf(plan.Retrieval), plan.Retrieval?.Degraded, plan.Language, plan.Condense is CondenseOutcome.Rewritten,
            answered ? plan.Assistant.Published?.GatewayModel : null, answered ? Domain.ResidencyCeiling.ToHeader(plan.Ceiling) : null,
            answered ? prompt : null, answered ? completion : null, latencyMs, null, sources, plan.Created)
        {
            // §11.4, §14.2: Open WebUI's message ids from its connection headers (OPENAI_API_CONFIGS), for feedback mapping.
            UserExternalId = who.Client.Kind == "open-webui" ? Header(http, "X-Rag-Owui-User-Message-Id") : null,
            AnswerExternalId = who.Client.Kind == "open-webui" ? Header(http, "X-Rag-Owui-Message-Id") : null,
        };
    }

    /// <summary>The <c>chat.completion</c> object (§3.3); with <paramref name="ume"/> (§3.8) a top-level <c>ume</c> member after <c>usage</c>.</summary>
    private static IResult Json(ChatCompletion answer, UmeAnswer? ume)
    {
        var body = new
        {
            id = "chatcmpl-" + answer.MessageId.ToString("N"),
            @object = "chat.completion",
            created = answer.Created.ToUnixTimeSeconds(),
            model = answer.Model,
            choices = new[]
            {
                new
                {
                    index = 0, finish_reason = answer.FinishReason,
                    message = new
                    {
                        role = "assistant", content = answer.Content,
                        annotations = answer.Annotations.Select(a => new
                        {
                            type = "url_citation", url_citation = new { url = a.Url, title = a.Title, start_index = a.StartIndex, end_index = a.EndIndex },
                        }),
                    },
                },
            },
            usage = new { prompt_tokens = answer.PromptTokens, completion_tokens = answer.CompletionTokens, total_tokens = answer.PromptTokens + answer.CompletionTokens },
        };
        return ume is null ? Results.Json(body) : Results.Json(new { body.id, body.@object, body.created, body.model, body.choices, body.usage, ume });
    }

    /// <summary>The description Open WebUI shows (§3.1): the assistant's description and its contact.</summary>
    private static string? Description(AssistantEntry assistant)
    {
        var contact = string.Join(", ", new[] { assistant.ContactName, assistant.ContactEmail }.Where(x => !string.IsNullOrWhiteSpace(x)));
        var text = string.Join(" ", new[] { assistant.Description, contact.Length > 0 ? "Kontakt: " + contact : null }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return text.Length > 0 ? text : null;
    }

    /// <summary>Runs the handler and writes refusals as OpenAI errors (§3.6, identity §4.4).</summary>
    public static async Task<IResult> OpenAiAsync(HttpContext http, Func<Task<IResult>> handler)
    {
        try
        {
            return await handler();
        }
        catch (KnowledgeFaultException fault)
        {
            if (fault.Status == 401)
            {
                http.Response.Headers.WWWAuthenticate = "Bearer";
            }

            return Error(http, fault.Status, fault.Code, fault.Message, null, fault.Status == 429 ? 60 : null);
        }
        catch (ChatException refusal)
        {
            return Error(http, refusal.Status, refusal.Code, refusal.Message, refusal.Param, refusal.RetryAfterSeconds);
        }
    }

    public static IResult Error(HttpContext http, int status, string code, string message, string? param, int? retryAfterSeconds = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (retryAfterSeconds is { } seconds)
        {
            http.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var type = status switch
        {
            400 or 413 => "invalid_request_error",
            401 => "authentication_error",
            402 => "insufficient_quota",
            403 => "permission_error",
            404 => "not_found_error",
            429 => "rate_limit_error",
            _ => "api_error",
        };
        return Results.Json(new { error = new { message, type, code, param, request_id = http.TraceIdentifier } }, statusCode: status);
    }

    private sealed class ChatBodyLimit : IRequestSizeLimitMetadata
    {
        public long? MaxRequestBodySize => BodyLimit;
    }
}

/// <summary>
/// <c>rag.chat.requests</c> (chat design §17) for completed turns: <c>outcome</c> (<c>answered</c>, <c>answered-general</c>,
/// <c>not-found</c>, <c>unavailable</c>), <c>level</c>, <c>client_kind</c>, <c>stream</c>, <c>conversational</c>; for answers
/// with the <c>ume</c> extension (§3.8) <c>rag.chat.ume</c> (<c>members</c>, <c>client_kind</c>) and
/// <c>rag.chat.related_documents</c> (entries per answer that asked for the list, §9.7).
/// </summary>
public sealed class ChatMetrics
{
    private readonly Counter<long> _requests;
    private readonly Counter<long> _ume;
    private readonly Histogram<int> _related;

    public ChatMetrics(IMeterFactory meters)
    {
        ArgumentNullException.ThrowIfNull(meters);
        var meter = meters.Create(Microsoft.Extensions.Hosting.Extensions.TelemetryName);
        _requests = meter.CreateCounter<long>("rag.chat.requests", description: "Chat turns by outcome");
        _ume = meter.CreateCounter<long>("rag.chat.ume", description: "Chat answers with the ume extension by the members asked for");
        _related = meter.CreateHistogram<int>("rag.chat.related_documents", description: "Related documents listed per answer that asked for them");
    }

    public void Completed(ChatPlan plan, RagPrincipal who, bool stream)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(who);
        _requests.Add(1, new TagList
        {
            { "outcome", ChatOutcomes.Name(plan.Outcome) }, { "level", plan.Assistant.Classification }, { "client_kind", who.Client.Kind },
            { "stream", stream }, { "conversational", plan.Retrieval is null },
        });
    }

    /// <summary>One answer with a <c>ume</c> member: <c>members</c> is <c>outcome</c> (only), <c>related</c>, <c>follow_ups</c> or <c>both</c>.</summary>
    public void Ume(UmeRequest asked, UmeAnswer answer, RagPrincipal who)
    {
        ArgumentNullException.ThrowIfNull(asked);
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(who);
        var members = (asked.RelatedDocuments > 0, asked.FollowUps > 0) switch
        {
            (true, true) => "both",
            (true, false) => "related",
            (false, true) => "follow_ups",
            _ => "outcome",
        };
        _ume.Add(1, new TagList { { "members", members }, { "client_kind", who.Client.Kind } });
        if (answer.RelatedDocuments is { } related)
        {
            _related.Record(related.Count);
        }
    }
}
