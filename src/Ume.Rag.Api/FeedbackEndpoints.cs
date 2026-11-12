using System.Text.Json;
using Npgsql;
using Ume.Rag.Answering.Chat;
using Ume.Rag.Domain.Access;
using Ume.Rag.Infrastructure.Access;

namespace Ume.Rag.Api;

/// <summary>
/// <c>POST /v1/feedback</c> (chat design §3.5, §14.1): a rating of an assistant message of the caller's own conversation,
/// upserted by (message, <c>api</c>) under a lock on the message row, with the daily totals' thumbs moved by the delta. History mode <c>none</c> (all of
/// P1, weakness review §4): a comment is refused with 400 <c>comment_not_stored</c>. <c>ReasonCategory</c> is the reason
/// when it is one of Open WebUI's fixed keys or the API's own reasons, else <c>other</c>.
/// </summary>
public static class FeedbackEndpoints
{
    /// <summary>The reasons the API accepts (§3.5).</summary>
    public static IReadOnlySet<string> Reasons { get; } = new HashSet<string>(["incorrect", "incomplete", "wrong_sources", "not_relevant", "other"], StringComparer.Ordinal);

    /// <summary>
    /// Open WebUI's fixed reason keys (v0.12.5, unchanged since v0.12.3: <c>src/lib/components/chat/Messages/RateComment.svelte:17-35</c>, read from the
    /// source): <c>LIKE_REASONS</c> and <c>DISLIKE_REASONS</c>.
    /// </summary>
    public static IReadOnlySet<string> OpenWebUiReasons { get; } = new HashSet<string>(
    [
        "accurate_information", "followed_instructions_perfectly", "showcased_creativity", "positive_attitude", "attention_to_detail", "thorough_explanation",
        "dont_like_the_style", "too_verbose", "not_helpful", "not_factually_correct", "didnt_fully_follow_instructions", "refused_when_it_shouldnt_have",
        "being_lazy", "other",
    ], StringComparer.Ordinal);

    private static readonly TimeZoneInfo Stockholm = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm");

    /// <summary>
    /// The plaintext category (main-session decision on #264): an Open WebUI key or one of the API's own reasons is kept,
    /// anything else is <c>other</c>.
    /// </summary>
    public static string CategoryOf(string? reason) => reason is not null && (OpenWebUiReasons.Contains(reason) || Reasons.Contains(reason)) ? reason : "other";

    /// <summary>The body limit: a rating with a 2,000-character comment fits in far less.</summary>
    public const int MaxBodyBytes = 16 * 1024;

    public static IEndpointRouteBuilder MapFeedback(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapPost("/v1/feedback", (HttpContext http, ChatCallers callers, NpgsqlDataSource data, IPolicySnapshotSource snapshots, IAccessPolicy policy,
                CancellationToken ct) =>
            ChatEndpoints.OpenAiAsync(http, async () =>
            {
                var who = await callers.AuthenticateAsync(http.Request, userRequired: true, ct);
                JsonElement body;
                try
                {
                    if (http.Request.ContentLength > MaxBodyBytes)
                    {
                        throw new ChatException(413, "request_too_large", "The body is too large.");
                    }

                    using var limited = new MemoryStream();
                    var buffer = new byte[4096];
                    int read;
                    while ((read = await http.Request.Body.ReadAsync(buffer, ct)) > 0)
                    {
                        if (limited.Length + read > MaxBodyBytes)
                        {
                            throw new ChatException(413, "request_too_large", "The body is too large.");
                        }

                        limited.Write(buffer, 0, read);
                    }

                    limited.Position = 0;
                    body = (await JsonDocument.ParseAsync(limited, cancellationToken: ct)).RootElement;
                }
                catch (JsonException)
                {
                    throw new ChatException(400, "invalid_request", "The body is not valid JSON.");
                }

                var (messageId, rating, reason) = Parse(body);
                var snapshot = snapshots.Current;

                await using var connection = await data.OpenConnectionAsync(ct);
                await using var transaction = await connection.BeginTransactionAsync(ct);
                await using var find = new NpgsqlCommand("""
                    SELECT m."ConversationId", m."Classification", m."CreatedAt", c."AssistantId", c."HistoryMode"
                    FROM chat."Messages" m JOIN chat."Conversations" c ON c."Id" = m."ConversationId"
                    WHERE m."Id" = $1 AND m."Role" = 'assistant' AND c."ClientId" = $2 AND c."SubjectId" = $3
                    FOR UPDATE OF m
                    """, connection, transaction)
                {
                    Parameters = { new() { Value = messageId }, new() { Value = who.Client.Id }, new() { Value = who.SubjectId } },
                };
                Guid conversationId, assistantId;
                string level, mode;
                DateTimeOffset at;
                short? previous;
                await using (var reader = await find.ExecuteReaderAsync(ct))
                {
                    if (!await reader.ReadAsync(ct))
                    {
                        throw NotFound();
                    }

                    (conversationId, level, at, assistantId, mode) = (reader.GetGuid(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2), reader.GetGuid(3),
                        reader.GetString(4));
                }

                // After the lock, in its own statement: READ COMMITTED gives it a fresh snapshot, so a request that waited sees the
                // rating the other one committed (a sub-select of the locking statement would not, #264 review).
                await using (var current = new NpgsqlCommand("""SELECT "Rating" FROM chat."Feedback" WHERE "MessageId" = $1 AND "Source" = 'api'""",
                                 connection, transaction) { Parameters = { new() { Value = messageId } } })
                {
                    previous = await current.ExecuteScalarAsync(ct) is short value ? value : null;
                }

                // Identity §4.3 step 4: the Feedback permission on the message's assistant; anything else looks like a missing message.
                if (!snapshot.Assistants.TryGetValue(assistantId, out var assistant) ||
                    !policy.Decide(who, ResourceRef.Assistant(assistantId), RagAction.Feedback, snapshot, snapshots.IsConfirmed).Allowed)
                {
                    throw NotFound();
                }

                if (body.TryGetProperty("comment", out var comment) && comment.ValueKind == JsonValueKind.String && comment.GetString()!.Trim().Length > 0 &&
                    mode == Domain.Entities.HistoryModes.None)
                {
                    throw new ChatException(400, "comment_not_stored", "This assistant does not store conversation content, so a comment cannot be saved. Send the rating without it.",
                        "comment");
                }

                await using (var upsert = new NpgsqlCommand("""
                    INSERT INTO chat."Feedback" ("Id", "MessageId", "ConversationId", "Classification", "ClientId", "Source", "Rating", "ReasonCategory", "ReviewState",
                        "CreatedAt", "UpdatedAt")
                    VALUES ($1, $2, $3, $4, $5, 'api', $6, $7, 'open', now(), now())
                    ON CONFLICT ("MessageId", "Source") WHERE "Source" = 'api' DO UPDATE SET "Rating" = excluded."Rating",
                        "ReasonCategory" = excluded."ReasonCategory", "UpdatedAt" = now()
                    """, connection, transaction)
                {
                    Parameters =
                    {
                        new() { Value = Guid.CreateVersion7() }, new() { Value = messageId }, new() { Value = conversationId }, new() { Value = level },
                        new() { Value = who.Client.Id }, new() { Value = rating }, new() { Value = (object?)(reason is null ? null : CategoryOf(reason)) ?? DBNull.Value },
                    },
                })
                {
                    await upsert.ExecuteNonQueryAsync(ct);
                }

                // §14.1: the totals move by the delta (a changed rating moves one count), on the rated message's day.
                var up = (rating == 1 ? 1 : 0) - (previous == 1 ? 1 : 0);
                var down = (rating == -1 ? 1 : 0) - (previous == -1 ? 1 : 0);
                if (up != 0 || down != 0)
                {
                    await using var totals = new NpgsqlCommand("""
                        INSERT INTO chat."AssistantDailyTotals" AS t ("Day", "AssistantId", "ClientId", "Classification", "DepartmentId", "Conversations", "Messages",
                            "NotFound", "Unavailable", "Degraded", "Errors", "Cancelled", "UncitedAnswers", "ThumbsUp", "ThumbsDown", "ToolCalls", "ToolErrors",
                            "AgentLimitHits")
                        VALUES ($1, $2, $3, $4, $5, 0, 0, 0, 0, 0, 0, 0, 0, GREATEST($6, 0), GREATEST($7, 0), 0, 0, 0)
                        ON CONFLICT ("Day", "AssistantId", "ClientId", "Classification") DO UPDATE SET
                            "ThumbsUp" = GREATEST(t."ThumbsUp" + $6, 0), "ThumbsDown" = GREATEST(t."ThumbsDown" + $7, 0)
                        """, connection, transaction)
                    {
                        Parameters =
                        {
                            new() { Value = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, Stockholm).DateTime) }, new() { Value = assistantId },
                            new() { Value = who.Client.Id }, new() { Value = level }, new() { Value = assistant.DepartmentId }, new() { Value = up }, new() { Value = down },
                        },
                    };
                    await totals.ExecuteNonQueryAsync(ct);
                }

                await transaction.CommitAsync(ct);
                return Results.NoContent();
            }))
            .WithTags("Chat").WithName("Feedback").WithSummary("Rate an assistant message of your own conversation (ratings only in P1)");
        return app;
    }

    private static (Guid MessageId, short Rating, string? Reason) Parse(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            throw new ChatException(400, "invalid_request", "The body must be an object.");
        }

        var id = body.TryGetProperty("message_id", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()! : "";
        if (id.StartsWith("chatcmpl-", StringComparison.Ordinal))
        {
            id = id["chatcmpl-".Length..];
        }

        if (!Guid.TryParse(id, out var messageId))
        {
            throw NotFound();
        }

        if (!body.TryGetProperty("rating", out var r) || r.ValueKind != JsonValueKind.Number || !r.TryGetInt16(out var rating) || rating is not (1 or -1))
        {
            throw new ChatException(400, "invalid_request", "rating must be 1 or -1.", "rating");
        }

        string? reason = null;
        if (body.TryGetProperty("reason", out var reasonValue) && reasonValue.ValueKind != JsonValueKind.Null)
        {
            reason = reasonValue.ValueKind == JsonValueKind.String ? reasonValue.GetString() : null;
            if (reason is null || !Reasons.Contains(reason))
            {
                throw new ChatException(400, "invalid_request", "reason must be one of " + string.Join(", ", Reasons) + ".", "reason");
            }
        }

        if (body.TryGetProperty("comment", out var c) && c.ValueKind == JsonValueKind.String && c.GetString()!.Length > 2000)
        {
            throw new ChatException(400, "invalid_request", "comment is at most 2,000 characters.", "comment");
        }

        return (messageId, rating, reason);
    }

    private static ChatException NotFound() => new(404, "message_not_found", "The message does not exist.", "message_id");
}
