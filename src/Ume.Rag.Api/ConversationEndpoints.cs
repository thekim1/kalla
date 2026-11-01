using System.Globalization;
using System.Text;
using Npgsql;
using Ume.Rag.Answering.Chat;
using Ume.Rag.Domain.Access;
using Ume.Rag.Infrastructure.Access;

namespace Ume.Rag.Api;

/// <summary>
/// The caller's conversations (chat design §3.4, §13.2): list, detail and delete, for this client and subject only. In P1
/// every conversation is history mode <c>none</c>: titles and message texts come back null, metadata only. A path id is
/// the issued id (<c>c1.…</c>, verified without SQL) or the conversation's UUID with the owner check; anything not owned
/// is 404 <c>conversation_not_found</c>. Off the chat hot path (Postgres is allowed).
/// </summary>
public static class ConversationEndpoints
{
    public const int MaxLimit = 100;

    public static IEndpointRouteBuilder MapConversations(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var conversations = app.MapGroup("/v1/conversations").WithTags("Chat");

        conversations.MapGet("", (HttpContext http, int? limit, string? after, ChatCallers callers, ConversationIds ids, NpgsqlDataSource data,
                IPolicySnapshotSource snapshots, CancellationToken ct) =>
            ChatEndpoints.OpenAiAsync(http, async () =>
            {
                var who = await callers.AuthenticateAsync(http.Request, userRequired: true, ct);
                var take = Math.Clamp(limit ?? 20, 1, MaxLimit);
                var cursor = Cursor(after);
                await using var command = data.CreateCommand("""
                    SELECT "Id", "AssistantId", "CreatedAt", "LastMessageAt", "HistoryMode" FROM chat."Conversations"
                    WHERE "ClientId" = $1 AND "SubjectId" = $2 AND ($3::timestamptz IS NULL OR ("LastMessageAt", "Id") < ($3, $4))
                    ORDER BY "LastMessageAt" DESC, "Id" DESC LIMIT $5
                    """);
                command.Parameters.Add(new() { Value = who.Client.Id });
                command.Parameters.Add(new() { Value = who.SubjectId });
                command.Parameters.Add(new() { Value = (object?)cursor?.At ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz });
                command.Parameters.Add(new() { Value = cursor?.Id ?? Guid.Empty });
                command.Parameters.Add(new() { Value = take + 1 });
                var rows = new List<(Guid Id, Guid Assistant, DateTimeOffset Created, DateTimeOffset Last, string Mode)>();
                await using (var reader = await command.ExecuteReaderAsync(ct))
                {
                    while (await reader.ReadAsync(ct))
                    {
                        rows.Add((reader.GetGuid(0), reader.GetGuid(1), reader.GetFieldValue<DateTimeOffset>(2), reader.GetFieldValue<DateTimeOffset>(3), reader.GetString(4)));
                    }
                }

                var snapshot = snapshots.Current;
                var page = rows.Take(take).Select(r => new
                {
                    id = ids.HeaderFor(r.Id, who.Client.Id, who.SubjectId),
                    assistant = snapshot.Assistants.TryGetValue(r.Assistant, out var a) ? a.Slug : null,
                    title = (string?)null,
                    created_at = r.Created.ToUnixTimeSeconds(),
                    last_message_at = r.Last.ToUnixTimeSeconds(),
                    history_mode = r.Mode,
                }).ToList();
                var next = rows.Count > take ? CursorOf(rows[take - 1].Last, rows[take - 1].Id) : null;
                return Results.Json(new { data = page, next });
            }))
            .WithName("ListConversations").WithSummary("The caller's conversations for this client (keyset, newest first)");

        conversations.MapGet("/{id}", (HttpContext http, string id, ChatCallers callers, ConversationIds ids, NpgsqlDataSource data, CancellationToken ct) =>
            ChatEndpoints.OpenAiAsync(http, async () =>
            {
                var who = await callers.AuthenticateAsync(http.Request, userRequired: true, ct);
                var conversationId = await OwnedAsync(id, who, ids, data, ct) ?? throw NotFound();
                await using var command = data.CreateCommand("""
                    SELECT m."Id", m."Seq", m."Role", m."Outcome", m."CreatedAt",
                           coalesce((SELECT json_agg(json_build_object('number', s."Number", 'document_id', s."DocumentId") ORDER BY s."Number")
                                     FROM chat."MessageSources" s WHERE s."MessageId" = m."Id" AND s."Used"), '[]')::text,
                           (SELECT f."Rating" FROM chat."Feedback" f WHERE f."MessageId" = m."Id" ORDER BY f."CreatedAt" DESC LIMIT 1)
                    FROM chat."Messages" m WHERE m."ConversationId" = $1 ORDER BY m."Seq"
                    """);
                command.Parameters.Add(new() { Value = conversationId });
                var messages = new List<object>();
                await using (var reader = await command.ExecuteReaderAsync(ct))
                {
                    while (await reader.ReadAsync(ct))
                    {
                        messages.Add(new
                        {
                            id = reader.GetGuid(0), seq = reader.GetInt32(1), role = reader.GetString(2), outcome = reader.GetString(3),
                            created_at = reader.GetFieldValue<DateTimeOffset>(4).ToUnixTimeSeconds(), content = (string?)null,
                            sources = System.Text.Json.JsonDocument.Parse(reader.GetString(5)).RootElement.Clone(),
                            rating = reader.IsDBNull(6) ? (short?)null : reader.GetInt16(6),
                        });
                    }
                }

                return Results.Json(new { id = ids.HeaderFor(conversationId, who.Client.Id, who.SubjectId), messages });
            }))
            .WithName("GetConversation").WithSummary("A conversation's messages (metadata only in P1) with their cited sources and feedback");

        conversations.MapDelete("/{id}", (HttpContext http, string id, ChatCallers callers, ConversationIds ids, NpgsqlDataSource data, CancellationToken ct) =>
            ChatEndpoints.OpenAiAsync(http, async () =>
            {
                var who = await callers.AuthenticateAsync(http.Request, userRequired: true, ct);
                var conversationId = await OwnedAsync(id, who, ids, data, ct, missingIsOwned: true) ?? throw NotFound();
                // §13.2: one transaction; children first (feedback references the conversation with RESTRICT), then the row.
                await using var connection = await data.OpenConnectionAsync(ct);
                await using var transaction = await connection.BeginTransactionAsync(ct);
                await using var delete = new NpgsqlCommand("""
                    DELETE FROM chat."Messages" WHERE "ConversationId" = $1;
                    """, connection, transaction) { Parameters = { new() { Value = conversationId } } };
                await delete.ExecuteNonQueryAsync(ct);
                await using var row = new NpgsqlCommand("""
                    DELETE FROM chat."Conversations" WHERE "Id" = $1 AND "ClientId" = $2 AND "SubjectId" = $3 RETURNING "Classification"
                    """, connection, transaction)
                {
                    Parameters = { new() { Value = conversationId }, new() { Value = who.Client.Id }, new() { Value = who.SubjectId } },
                };
                if (await row.ExecuteScalarAsync(ct) is string level)
                {
                    await using var audit = new NpgsqlCommand("""
                        INSERT INTO audit."AuditLog" ("Timestamp", "RecordedAt", "Actor", "Action", "EntityType", "EntityId", "Service", "Classification", "RequestId")
                        VALUES (now(), now(), $1, 'conversation.delete', 'conversation', $2, 'rag-api', $3, $4)
                        """, connection, transaction)
                    {
                        Parameters = { new() { Value = who.SubjectId }, new() { Value = conversationId.ToString("D") }, new() { Value = level }, new() { Value = http.TraceIdentifier } },
                    };
                    await audit.ExecuteNonQueryAsync(ct);
                }

                await transaction.CommitAsync(ct);
                return Results.NoContent();
            }))
            .WithName("DeleteConversation").WithSummary("Delete a conversation and everything under it (idempotent)");

        return app;
    }

    /// <summary>
    /// The conversation id if the caller owns it. An issued id is verified by its MAC (no SQL); a UUID is checked against
    /// the row. <paramref name="missingIsOwned"/>: a delete of a UUID that no longer exists answers 204 (idempotent).
    /// </summary>
    private static async Task<Guid?> OwnedAsync(string id, RagPrincipal who, ConversationIds ids, NpgsqlDataSource data, CancellationToken ct,
        bool missingIsOwned = false)
    {
        if (id.StartsWith(ConversationIds.Prefix, StringComparison.Ordinal))
        {
            return ids.Verify(id, who.Client.Id, who.SubjectId);
        }

        if (!Guid.TryParse(id, out var uuid))
        {
            return null;
        }

        await using var command = data.CreateCommand("""SELECT "ClientId" = $2 AND "SubjectId" = $3 FROM chat."Conversations" WHERE "Id" = $1""");
        command.Parameters.Add(new() { Value = uuid });
        command.Parameters.Add(new() { Value = who.Client.Id });
        command.Parameters.Add(new() { Value = who.SubjectId });
        return await command.ExecuteScalarAsync(ct) switch
        {
            true => uuid,
            null when missingIsOwned => uuid,
            _ => null,
        };
    }

    private static ChatException NotFound() => new(404, "conversation_not_found", "The conversation does not exist.", "id");

    private static (DateTimeOffset At, Guid Id)? Cursor(string? after)
    {
        if (string.IsNullOrEmpty(after))
        {
            return null;
        }

        try
        {
            var parts = Encoding.ASCII.GetString(Convert.FromBase64String(after.Replace('-', '+').Replace('_', '/') + new string('=', (4 - (after.Length % 4)) % 4))).Split(':');
            return (new DateTimeOffset(long.Parse(parts[0], CultureInfo.InvariantCulture), TimeSpan.Zero), Guid.Parse(parts[1]));
        }
        catch (Exception ex) when (ex is FormatException or IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            throw new ChatException(400, "invalid_request", "The cursor is not valid.", "after");
        }
    }

    private static string CursorOf(DateTimeOffset at, Guid id) =>
        Convert.ToBase64String(Encoding.ASCII.GetBytes(at.UtcTicks.ToString(CultureInfo.InvariantCulture) + ":" + id.ToString("N"))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
