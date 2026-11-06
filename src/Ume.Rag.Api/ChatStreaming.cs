using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Ume.Rag.Answering.Chat;
using Ume.Rag.Domain.Access;

namespace Ume.Rag.Api;

/// <summary>
/// A streamed answer (chat design §9.1, §9.3, §9.6): <c>text/event-stream</c>, one flush per event. The role chunk, the
/// caveat line of a degraded or general-knowledge answer, then the model's text through the <see cref="AnswerTransducer"/>: for Open WebUI one
/// <c>source</c> event per cited document just before the text that first cites it, for other clients <c>delta.annotations</c>
/// (<c>url_citation</c>), then the contact links of a general-knowledge answer. A fixed text (not found, unavailable) is one content chunk without a model call. The events come from
/// <see cref="ChatAnswer"/>, the same answer path the JSON answer folds. After the first
/// byte an error is one error chunk and <c>[DONE]</c>.
/// </summary>
public sealed class ChatSse(ChatAnswer answer, bool openWebUi, bool includeUsage,
    Func<IReadOnlyList<PromptSource>, int, int, ValueTask>? finished = null) : IResult
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
                            await WriteAsync(response, Chunk(new { content = own.Text }, null), ct);
                            break;
                        case AnswerEvent.Pieces pieces:
                            await PiecesAsync(response, pieces.Items, pieces.Shift, Chunk, content, id, created, ct);
                            break;
                        case AnswerEvent.End e:
                            end = e;
                            break;
                    }
                }

                await WriteAsync(response, Chunk(new { }, end.FinishReason), ct);
                await UsageAsync(response, id, created, end.PromptTokens, end.CompletionTokens, ct);
                await DoneAsync(response, ct);
                answer.Finish();
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
                // §9.6: after the first byte only an error chunk is possible.
                await WriteAsync(response, new { error = new { message = "The answer was interrupted.", type = "api_error", code = "stream_interrupted" } }, CancellationToken.None);
                await DoneAsync(response, CancellationToken.None);
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
                    await WriteAsync(response, SourceEvent(first.Source), ct);
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
    private static object SourceEvent(PromptSource source)
    {
        var link = Citations.Link(source);
        var label = Citations.Label(source);
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
/// The per-user message limit (chat design §4.1 step 4): <c>Chat:MessagesPerMinutePerUser</c> (20) per client and subject
/// and minute, in process (P1 runs one slot; Redis from P2). Over the limit: 429 <c>rate_limited</c> with <c>Retry-After</c>.
/// </summary>
public sealed class ChatRateLimiter(IOptions<ChatOptions> options, TimeProvider time)
{
    private const int MaxTracked = 100_000;
    private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.Ordinal);

    /// <summary><paramref name="limit"/>: <c>Settings.Chat.messagesPerMinutePerUser</c>, else <c>Chat:MessagesPerMinutePerUser</c>.</summary>
    public void Check(RagPrincipal who, int? limit = null)
    {
        ArgumentNullException.ThrowIfNull(who);
        limit ??= options.Value.MessagesPerMinutePerUser;
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

    private sealed class Window
    {
        public DateTimeOffset Start;
        public int Count;
    }
}
