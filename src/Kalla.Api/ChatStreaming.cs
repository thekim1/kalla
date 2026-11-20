using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Kalla.Answering.Chat;
using Kalla.Domain.Access;

namespace Kalla.Api;

/// <summary>
/// A streamed answer (chat design §9.1, §9.3, §9.6): <c>text/event-stream</c>, one flush per event. The role chunk, the
/// caveat line of a degraded or general-knowledge answer, then the model's text through the <see cref="AnswerTransducer"/>: for Open WebUI one
/// <c>source</c> event per cited document just before the text that first cites it, for other clients <c>delta.annotations</c>
/// (<c>url_citation</c>), then the contact links of a general-knowledge answer. A fixed text (not found, unavailable) is one content chunk without a model call. The events come from
/// <see cref="ChatAnswer"/>, the same answer path the JSON answer folds. With <paramref name="kalla"/> (§3.8) the first <c>kalla</c> chunk
/// follows the final chunk, made from the documents the answer cited, at once; with <paramref name="later"/> (§9.8, §9.9) the writer
/// keeps the delivered text and a second <c>kalla</c> chunk with <c>follow_ups</c> and <c>escalation</c> follows when their calls return or their
/// budget runs out; then the usage chunk and <c>[DONE]</c>. After the first byte an error is one error chunk and <c>[DONE]</c>,
/// and no <c>kalla</c> chunk; after a disconnect nothing more is written. A turn that ends in either of those calls
/// <paramref name="aborted"/> instead of <paramref name="finished"/>, once the answer is disposed (its broken-off stream
/// recorded, chat design §8.3), so its usage is still written (§12.1).
/// </summary>
public sealed class ChatSse(ChatAnswer answer, bool openWebUi, bool includeUsage,
    Func<IReadOnlyList<PromptSource>, int, int, ValueTask>? finished = null, Func<IReadOnlyList<PromptSource>, KallaAnswer>? kalla = null,
    Func<IReadOnlyList<PromptSource>, string, CancellationToken, Task<KallaLater>>? later = null,
    Func<string, IReadOnlyList<PromptSource>, ValueTask>? aborted = null) : IResult
{
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    private ChatPlan Plan => answer.Plan;

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        var ct = httpContext.RequestAborted;
        var response = httpContext.Response;
        httpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        response.StatusCode = 200;
        response.ContentType = "text/event-stream; charset=utf-8";
        response.Headers.CacheControl = "no-cache, no-store";
        response.Headers["X-Accel-Buffering"] = "no";
        await response.StartAsync(ct);

        var id = "chatcmpl-" + Plan.MessageId.ToString("N");
        var created = Plan.Created.ToUnixTimeSeconds();
        object Chunk(object delta, string? finish) => new
        {
            id, @object = "chat.completion.chunk", created, model = Plan.Model, choices = new[] { new { index = 0, delta, finish_reason = finish } },
        };

        // The per-token content events, written without a string or an object per token (#333 finding 4).
        using var content = new SseChunk();
        // §9.8: the delivered text, kept only when a call follows the answer (the follow-up call reads it).
        var delivered = later is null ? null : new StringBuilder();
        var completed = false;
        // B-48 (§8.3, §12): a disconnect or the idle or total timeout ends the turn as cancelled; any other failure as error.
        var outcome = Domain.Entities.MessageOutcomes.Cancelled;
        try
        {
            using (answer)
            {
                try
                {
                    await WriteAsync(response, Chunk(new { role = "assistant", content = "" }, null), ct);
                    AnswerEvent.End end = new("stop", 0, 0);
                    await foreach (var item in answer.ReadAsync(ct))
                    {
                        switch (item)
                        {
                            case AnswerEvent.Own own:
                                delivered?.Append(own.Text);
                                await WriteAsync(response, Chunk(new { content = own.Text }, null), ct);
                                break;
                            case AnswerEvent.Pieces pieces:
                                if (delivered is not null)
                                {
                                    foreach (var text in pieces.Items.OfType<AnswerPiece.Text>())
                                    {
                                        delivered.Append(text.Content);
                                    }
                                }

                                await PiecesAsync(response, pieces.Items, pieces.Shift, Chunk, content, id, created, ct);
                                break;
                            case AnswerEvent.End e:
                                end = e;
                                break;
                        }
                    }

                    // A client that left while the gateway's stream ended did not get the answer: the turn is cancelled (B-48).
                    ct.ThrowIfCancellationRequested();
                    await WriteAsync(response, Chunk(new { }, end.FinishReason), ct);
                    if (kalla is not null)
                    {
                        await KallaAsync(response, id, created, kalla(answer.Cited), ct);
                    }

                    if (later is not null)
                    {
                        await KallaAsync(response, id, created, await later(answer.Cited, delivered!.ToString(), ct), ct);
                    }

                    await UsageAsync(response, id, created, end.PromptTokens, end.CompletionTokens, ct);
                    await DoneAsync(response, ct);
                    answer.Finish();
                    completed = true;
                    if (finished is not null)
                    {
                        await finished(answer.Cited, end.PromptTokens, end.CompletionTokens);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // The client went away; nothing more to write (§8.3).
                }
                catch (Exception ex) when (ex is ChatException or HttpRequestException or IOException or JsonException)
                {
                    outcome = ex is ChatException { TimedOut: true } ? Domain.Entities.MessageOutcomes.Cancelled : Domain.Entities.MessageOutcomes.Error;
                    // §9.6: after the first byte only an error chunk is possible.
                    await WriteAsync(response, new { error = new { message = "The answer was interrupted.", type = "api_error", code = "stream_interrupted" } }, CancellationToken.None);
                    await DoneAsync(response, CancellationToken.None);
                }
            }
        }
        finally
        {
            if (!completed && aborted is not null)
            {
                await aborted(outcome, answer.Cited);
            }
        }
    }

    private async Task PiecesAsync(HttpResponse response, IReadOnlyList<AnswerPiece> pieces, int shift, Func<object, string?, object> chunk, SseChunk content,
        string id, long created, CancellationToken ct)
    {
        foreach (var piece in pieces)
        {
            switch (piece)
            {
                case AnswerPiece.FirstCitation first when openWebUi:
                    await WriteAsync(response, SourceEvent(first.Source, Plan.Language), ct);
                    break;
                case AnswerPiece.Text text when text.Content.Length > 0 || text.Annotations.Count > 0:
                    if (openWebUi || text.Annotations.Count == 0)
                    {
                        await response.Body.WriteAsync(content.Content(id, created, Plan.Model, text.Content), ct);
                        await response.Body.FlushAsync(ct);
                        break;
                    }

                    object delta = new
                        {
                            content = text.Content,
                            annotations = text.Annotations.Select(a => new
                            {
                                type = "url_citation",
                                url_citation = new { url = a.Url, title = a.Title, start_index = a.StartIndex + shift, end_index = a.EndIndex + shift },
                            }),
                        };
                    await WriteAsync(response, chunk(delta, null), ct);
                    break;
            }
        }
    }

    /// <summary>The Open WebUI <c>source</c> event of one document (§9.3): link and label; the first chunk's text as the snippet.</summary>
    private static object SourceEvent(PromptSource source, string language)
    {
        var link = Citations.Link(source);
        var label = Citations.Label(source, language);
        var first = source.Chunks[0];
        var text = first.Text.Length > 1000 ? first.Text[..1000] : first.Text;
        return new
        {
            @event = new
            {
                type = "source",
                data = new
                {
                    source = new { id = link, name = label, url = link },
                    document = new[] { text },
                    metadata = new[] { new { source = link, name = label, external = true, page = first.Page is { } page ? page - 1 : (int?)null } },
                },
            },
        };
    }

    /// <summary>
    /// A <c>kalla</c> chunk (§3.8): a <c>chat.completion.chunk</c> with <c>choices: []</c> and the <c>kalla</c> member, the usage
    /// chunk's shape. <paramref name="value"/> holds the members this chunk delivers; each member goes in exactly one chunk.
    /// </summary>
    private Task KallaAsync(HttpResponse response, string id, long created, object value, CancellationToken ct) =>
        WriteAsync(response, new { id, @object = "chat.completion.chunk", created, model = Plan.Model, choices = Array.Empty<object>(), kalla = value }, ct);

    private async Task UsageAsync(HttpResponse response, string id, long created, int prompt, int completion, CancellationToken ct)
    {
        if (includeUsage)
        {
            await WriteAsync(response, new
            {
                id, @object = "chat.completion.chunk", created, model = Plan.Model, choices = Array.Empty<object>(),
                usage = new { prompt_tokens = prompt, completion_tokens = completion, total_tokens = prompt + completion },
            }, ct);
        }
    }

    private static async Task WriteAsync(HttpResponse response, object value, CancellationToken ct)
    {
        await response.WriteAsync("data: " + JsonSerializer.Serialize(value, Json) + "\n\n", Encoding.UTF8, ct);
        await response.Body.FlushAsync(ct);
    }

    private static async Task DoneAsync(HttpResponse response, CancellationToken ct)
    {
        await response.WriteAsync("data: [DONE]\n\n", Encoding.UTF8, ct);
        await response.Body.FlushAsync(ct);
    }
}

/// <summary>
/// The message limit (chat design §4.1 step 4) per client and subject and minute, in process (P1 runs one slot; Redis from
/// P2): the per-user value (<c>Chat:MessagesPerMinutePerUser</c>, 20), or the key's own <c>Keys.RequestsPerMinute</c> when
/// the key itself is the subject (Decisions "Chat rate limit per key"). Over the limit: 429 <c>rate_limited</c> with
/// <c>Retry-After</c>.
/// </summary>
public sealed class ChatRateLimiter(IOptions<ChatOptions> options, TimeProvider time)
{
    private const int MaxTracked = 100_000;
    private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.Ordinal);

    /// <summary>
    /// <paramref name="perUser"/>: <c>Settings.Chat.messagesPerMinutePerUser</c>, else <c>Chat:MessagesPerMinutePerUser</c>.
    /// </summary>
    public void Check(RagPrincipal who, int? perUser = null)
    {
        ArgumentNullException.ThrowIfNull(who);
        var limit = LimitFor(who, perUser ?? options.Value.MessagesPerMinutePerUser);
        if (limit <= 0)
        {
            return;
        }

        var now = time.GetUtcNow();
        if (_windows.Count >= MaxTracked)
        {
            _windows.Clear();
        }

        var window = _windows.GetOrAdd($"{who.Client.Id:N}|{who.SubjectId}", _ => new Window { Start = now });
        lock (window)
        {
            if (now - window.Start >= TimeSpan.FromMinutes(1))
            {
                window.Start = now;
                window.Count = 0;
            }

            if (++window.Count > limit)
            {
                var retry = (int)Math.Ceiling((window.Start + TimeSpan.FromMinutes(1) - now).TotalSeconds);
                throw new ChatException(429, "rate_limited", "Too many messages. Wait a moment and try again.") { RetryAfterSeconds = Math.Max(1, retry) };
            }
        }
    }

    /// <summary>
    /// The key's <c>RequestsPerMinute</c> when the key itself is the subject (<see cref="SubjectKind.Application"/>), else
    /// <paramref name="perUser"/>. A user subject (Open WebUI JWT, token) never gets its connection key's value.
    /// </summary>
    public static int LimitFor(RagPrincipal who, int perUser)
    {
        ArgumentNullException.ThrowIfNull(who);
        return who.Kind == SubjectKind.Application && who.Key?.RequestsPerMinute is { } own ? own : perUser;
    }

    private sealed class Window
    {
        public DateTimeOffset Start;
        public int Count;
    }
}
