using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Kalla.TestKit;

namespace Kalla.Api.Tests;

/// <summary>
/// Downvotes per document (chat design §14.1; Decisions "Downvotes per document"; data-model §2.3): a rating through
/// <c>POST /v1/feedback</c> counts once in <c>DocumentDailyFeedback</c> for each document the answer cited (<c>Used</c>), however
/// many of its chunks were cited, on the answer's day; a changed rating moves one count, the same rating again none, and a count
/// never goes below 0. The Open WebUI import writes through the same delta (<c>FeedbackImporterTests</c>).
/// </summary>
public sealed partial class ChatApiTests
{
    /// <summary>The columns of a document feedback row, and nothing else (data-model §2.3): no question, answer, comment or subject.</summary>
    private static readonly string[] DocumentFeedbackColumns =
        ["Day", "AssistantId", "ClientId", "Classification", "CollectionId", "DocumentId", "DepartmentId", "ThumbsUp", "ThumbsDown"];

    [Fact]
    public async Task A_rating_counts_once_per_cited_document_and_a_changed_rating_moves_one_count()
    {
        await using var api = Host();
        using var client = Client(api, db.IntegrationKey);
        using var response = await Ask(client, AltanQuestion);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var answerId = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetString()!;
        var messageId = Guid.ParseExact(answerId["chatcmpl-".Length..], "N");

        // The answer's citations, set by the test so the documents are its own: two chunks of one document and one of another
        // cited, a fourth document retrieved but not cited.
        var (collection, twoChunks, oneChunk, uncited) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        await CiteAsync(api, messageId, collection, (twoChunks, true), (twoChunks, true), (oneChunk, true), (uncited, false));

        async Task Rate(int rating) =>
            (await client.PostAsJsonAsync("/v1/feedback", new { message_id = answerId, rating }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await Rate(-1);
        var rows = await DocumentFeedbackAsync(collection);
        rows.ShouldBe(new Dictionary<Guid, (int, int)> { [twoChunks] = (0, 1), [oneChunk] = (0, 1) }, ignoreOrder: true);

        // A changed rating moves one count each way; the same rating again moves none.
        await Rate(1);
        (await DocumentFeedbackAsync(collection)).ShouldBe(new Dictionary<Guid, (int, int)> { [twoChunks] = (1, 0), [oneChunk] = (1, 0) }, ignoreOrder: true);
        await Rate(1);
        (await DocumentFeedbackAsync(collection)).ShouldBe(new Dictionary<Guid, (int, int)> { [twoChunks] = (1, 0), [oneChunk] = (1, 0) }, ignoreOrder: true);

        // Never below 0: a count already at 0 stays there when a rating leaves it.
        await using var connection = new NpgsqlConnection(db.Connection(RagDatabase.Api));
        await connection.OpenAsync(Ct);
        await using (var zero = new NpgsqlCommand("""UPDATE chat."DocumentDailyFeedback" SET "ThumbsUp" = 0 WHERE "DocumentId" = $1""", connection)
                     { Parameters = { new() { Value = oneChunk } } })
        {
            await zero.ExecuteNonQueryAsync(Ct);
        }

        await Rate(-1);
        (await DocumentFeedbackAsync(collection)).ShouldBe(new Dictionary<Guid, (int, int)> { [twoChunks] = (0, 1), [oneChunk] = (0, 1) }, ignoreOrder: true);

        // The row is the answer's day (Europe/Stockholm), the caller's client, the message's level and the assistant's department.
        await using (var key = new NpgsqlCommand("""
            SELECT f."Day" = (m."CreatedAt" AT TIME ZONE 'Europe/Stockholm')::date, f."AssistantId", f."ClientId", f."Classification" = m."Classification", f."DepartmentId"
            FROM chat."DocumentDailyFeedback" f, chat."Messages" m WHERE f."DocumentId" = $1 AND m."Id" = $2
            """, connection) { Parameters = { new() { Value = twoChunks }, new() { Value = messageId } } })
        await using (var reader = await key.ExecuteReaderAsync(Ct))
        {
            (await reader.ReadAsync(Ct)).ShouldBeTrue();
            (reader.GetBoolean(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetBoolean(3), reader.GetGuid(4))
                .ShouldBe((true, db.AssistantId, db.IntegrationClient, true, Snapshot().Assistants[db.AssistantId].DepartmentId));
            (await reader.ReadAsync(Ct)).ShouldBeFalse();
        }

        await using var columns = new NpgsqlCommand("""SELECT to_jsonb(f)::text FROM chat."DocumentDailyFeedback" f WHERE "CollectionId" = $1""", connection)
        {
            Parameters = { new() { Value = collection } },
        };
        using var row = JsonDocument.Parse((string)(await columns.ExecuteScalarAsync(Ct))!);
        row.RootElement.EnumerateObject().Select(p => p.Name).ShouldBe(DocumentFeedbackColumns, ignoreOrder: true);
    }

    [Fact]
    public async Task A_rating_of_an_answer_without_a_citation_counts_no_document()
    {
        await using var api = Host();
        using var client = Client(api, db.IntegrationKey);
        using var response = await Ask(client, AltanQuestion);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var answerId = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetString()!;
        var collection = Guid.CreateVersion7();
        await CiteAsync(api, Guid.ParseExact(answerId["chatcmpl-".Length..], "N"), collection, (Guid.CreateVersion7(), false));

        (await client.PostAsJsonAsync("/v1/feedback", new { message_id = answerId, rating = -1 }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await DocumentFeedbackAsync(collection)).ShouldBeEmpty();
    }

    /// <summary>Replaces the written answer's source rows with <paramref name="documents"/> in <paramref name="collection"/>, one chunk each.</summary>
    private async Task CiteAsync(WebApplicationFactory<Program> api, Guid messageId, Guid collection, params (Guid Document, bool Used)[] documents)
    {
        await api.Services.GetRequiredService<ConversationWriter>().FlushAsync(Ct);
        await using var connection = new NpgsqlConnection(db.Connection(RagDatabase.Api));
        await connection.OpenAsync(Ct);
        await using (var clear = new NpgsqlCommand("""DELETE FROM chat."MessageSources" WHERE "MessageId" = $1""", connection) { Parameters = { new() { Value = messageId } } })
        {
            await clear.ExecuteNonQueryAsync(Ct);
        }

        for (var rank = 0; rank < documents.Length; rank++)
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO chat."MessageSources" ("MessageId", "Rank", "Classification", "DocumentId", "ChunkId", "CollectionId", "Number", "Used")
                SELECT "Id", $2, "Classification", $3, $4, $5, $6, $7 FROM chat."Messages" WHERE "Id" = $1
                """, connection)
            {
                Parameters =
                {
                    new() { Value = messageId }, new() { Value = rank }, new() { Value = documents[rank].Document }, new() { Value = Guid.CreateVersion7() },
                    new() { Value = collection }, new() { Value = documents[rank].Used ? (object)(short)(rank + 1) : DBNull.Value }, new() { Value = documents[rank].Used },
                },
            };
            (await insert.ExecuteNonQueryAsync(Ct)).ShouldBe(1);
        }
    }

    /// <summary>The document feedback rows of <paramref name="collection"/>, summed per document as (up, down).</summary>
    private async Task<Dictionary<Guid, (int Up, int Down)>> DocumentFeedbackAsync(Guid collection)
    {
        await using var connection = new NpgsqlConnection(db.Connection(RagDatabase.Api));
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            SELECT "DocumentId", sum("ThumbsUp")::int, sum("ThumbsDown")::int FROM chat."DocumentDailyFeedback" WHERE "CollectionId" = $1 GROUP BY 1
            """, connection) { Parameters = { new() { Value = collection } } };
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new Dictionary<Guid, (int, int)>();
        while (await reader.ReadAsync(Ct))
        {
            rows[reader.GetGuid(0)] = (reader.GetInt32(1), reader.GetInt32(2));
        }

        return rows;
    }
}
