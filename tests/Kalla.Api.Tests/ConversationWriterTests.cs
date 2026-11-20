using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Kalla.Answering.Chat;
using Kalla.TestKit;

namespace Kalla.Api.Tests;

/// <summary>
/// Conversation ids and the writer (chat design §11.1, §12; #266): an issued id is bound to client, assistant and subject;
/// the writer refuses to append to a conversation of another owner or assistant on its own, whatever the MAC said; Open
/// WebUI chats are one conversation per (user, chat id, assistant), and a resent user message id (a regeneration) is kept once.
/// </summary>
public sealed class ConversationWriterTests(KnowledgeFixture db) : IClassFixture<KnowledgeFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Meters : IMeterFactory
    {
        public Meter Create(MeterOptions options) => new(options);

        public void Dispose()
        {
        }
    }

    [Fact]
    public void An_issued_id_verifies_only_for_its_client_assistant_and_subject_and_across_key_rotation()
    {
        var (client, assistant, other) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var old = new ConversationIds(new Dictionary<string, byte[]> { ["2026a"] = new byte[32] });
        var (id, header) = old.Issue(client, assistant, "user-1");
        var rotated = new ConversationIds(new Dictionary<string, byte[]> { ["2026a"] = new byte[32], ["2026b"] = Enumerable.Repeat((byte)7, 32).ToArray() });

        rotated.Verify(header, client, assistant, "user-1").ShouldBe(id);
        rotated.HeaderFor(id, client, assistant, "user-1").ShouldNotBe(header); // the newest key signs
        foreach (var (c, a, s) in new[] { (client, other, "user-1"), (other, assistant, "user-1"), (client, assistant, "user-2") })
        {
            Should.Throw<ChatException>(() => rotated.Verify(header, c, a, s)).Code.ShouldBe("conversation_not_found");
        }
    }

    private static ChatTurn Turn(Guid? issued, string? chat, Guid assistant, Guid client, string subject, string level = "public") => new(
        Guid.CreateVersion7(), issued, chat, assistant, Guid.CreateVersion7(), Guid.CreateVersion7(), client, chat is null ? "application" : "owui-user", subject, level,
        "none", 90, "answered", "ok", null, "sv", false, "fors/chat-eu", "Eu", 10, 5, 100, null, [], DateTimeOffset.UtcNow);

    [Fact]
    public async Task The_writer_refuses_another_owners_or_assistants_conversation_and_keeps_open_webui_chats_apart()
    {
        await using var api = NpgsqlDataSource.Create(db.Connection(RagDatabase.Api));
        using var meters = new Meters();
        var writer = new ConversationWriter(api, meters, NullLogger<ConversationWriter>.Instance);
        var (client, assistant, internalAssistant) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var issued = Guid.CreateVersion7();

        await writer.WriteAsync([Turn(issued, null, assistant, client, "user-C")], Ct);
        // Another subject, and another assistant (of another level), with the same id: refused by the upsert's WHERE.
        await writer.WriteAsync([Turn(issued, null, assistant, client, "user-D"), Turn(issued, null, internalAssistant, client, "user-C", "internal")], Ct);
        (await CountAsync(api, $"""SELECT count(*) FROM chat."Messages" WHERE "ConversationId" = '{issued}'""")).ShouldBe(2);
        (await CountAsync(api, $"""SELECT count(*) FROM chat."Messages" WHERE "ConversationId" = '{issued}' AND "Classification" <> 'public'""")).ShouldBe(0);

        // Open WebUI: two turns of one chat are one conversation; the same chat id of another user is its own.
        var chat = "chat-" + Guid.CreateVersion7().ToString("N");
        var second = Turn(null, chat, assistant, client, "owui:u-1");
        await writer.WriteAsync([Turn(null, chat, assistant, client, "owui:u-1")], Ct);
        await writer.WriteAsync([second, Turn(null, chat, assistant, client, "owui:u-2")], Ct);
        (await CountAsync(api, $"""SELECT count(*) FROM chat."Conversations" WHERE "ExternalChatId" = '{chat}'""")).ShouldBe(2);
        (await CountAsync(api, $"""
            SELECT count(*) FROM chat."Messages" m JOIN chat."Conversations" c ON c."Id" = m."ConversationId"
            WHERE c."ExternalChatId" = '{chat}' AND c."SubjectId" = 'owui:u-1'
            """)).ShouldBe(4);
        (await CountAsync(api, $"""SELECT count(*) FROM chat."Messages" WHERE "Id" = '{second.MessageId}' AND "Role" = 'assistant'""")).ShouldBe(1); // chatcmpl id kept

        // A regeneration resends the user message id: one user message, both answers with their Open WebUI ids.
        var regenChat = "chat-" + Guid.CreateVersion7().ToString("N");
        await writer.WriteAsync([Turn(null, regenChat, assistant, client, "owui:u-1") with { UserExternalId = "um-1", AnswerExternalId = "am-1" }], Ct);
        await writer.WriteAsync([Turn(null, regenChat, assistant, client, "owui:u-1") with { UserExternalId = "um-1", AnswerExternalId = "am-2" }], Ct);
        (await CountAsync(api, $"""
            SELECT count(*) FROM chat."Messages" m JOIN chat."Conversations" c ON c."Id" = m."ConversationId"
            WHERE c."ExternalChatId" = '{regenChat}' AND m."ExternalMessageId" IN ('um-1', 'am-1', 'am-2')
            """)).ShouldBe(3);
    }

    private static TurnSource Source(int rank) => new(rank, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), (short)rank, null, null, true);

    [Fact]
    public async Task A_reused_open_webui_answer_id_writes_no_second_answer_and_no_orphan_sources()
    {
        // #698: the header is unsigned, so a user can send the same MESSAGE_ID twice; the answer insert does nothing, and its
        // sources must not be inserted against a row that isn't there (a foreign-key error that poisoned the batch).
        await using var api = NpgsqlDataSource.Create(db.Connection(RagDatabase.Api));
        using var meters = new Meters();
        var writer = new ConversationWriter(api, meters, NullLogger<ConversationWriter>.Instance);
        var (client, assistant) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        var chat = "chat-" + Guid.CreateVersion7().ToString("N");
        var answer = Guid.NewGuid().ToString();
        var first = Turn(null, chat, assistant, client, "owui:u-1") with { UserExternalId = Guid.NewGuid().ToString(), AnswerExternalId = answer, Sources = [Source(1)] };
        var reused = Turn(null, chat, assistant, client, "owui:u-1") with { UserExternalId = Guid.NewGuid().ToString(), AnswerExternalId = answer, Sources = [Source(1), Source(2)] };

        await writer.WriteAsync([first], Ct);
        await writer.WriteAsync([reused], Ct);

        (await CountAsync(api, $"""SELECT count(*) FROM chat."Messages" WHERE "Id" = '{reused.MessageId}'""")).ShouldBe(0);
        (await CountAsync(api, $"""SELECT count(*) FROM chat."MessageSources" WHERE "MessageId" = '{first.MessageId}'""")).ShouldBe(1);
        (await CountAsync(api, $"""SELECT count(*) FROM chat."MessageSources" WHERE "MessageId" = '{reused.MessageId}'""")).ShouldBe(0);
    }

    [Fact]
    public async Task A_turn_that_fails_on_its_data_is_dropped_at_once_without_holding_up_the_batch()
    {
        // #698: a data error (here 22001, a value too long for its column) fails the same way on every attempt, so the batch is
        // split at once and the bad turn dropped, without the 2 s retry delays; the good turn of the batch is written.
        await using var api = NpgsqlDataSource.Create(db.Connection(RagDatabase.Api));
        using var meters = new Meters();
        var writer = new ConversationWriter(api, meters, NullLogger<ConversationWriter>.Instance);
        var (client, assistant) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        var good = Turn(Guid.CreateVersion7(), null, assistant, client, "user-G");
        var bad = Turn(null, "chat-" + Guid.CreateVersion7().ToString("N"), assistant, client, "owui:u-1") with { AnswerExternalId = new string('a', 101) };
        await writer.StartAsync(Ct);
        try
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            await writer.EnqueueAsync(bad);
            await writer.EnqueueAsync(good);
            await writer.FlushAsync(Ct);
            started.Elapsed.ShouldBeLessThan(ConversationWriter.RetryInterval);
        }
        finally
        {
            await writer.StopAsync(CancellationToken.None);
        }

        (await CountAsync(api, $"""SELECT count(*) FROM chat."Messages" WHERE "Id" = '{good.MessageId}'""")).ShouldBe(1);
        (await CountAsync(api, $"""SELECT count(*) FROM chat."Messages" WHERE "Id" = '{bad.MessageId}'""")).ShouldBe(0);
    }

    private static async Task<long> CountAsync(NpgsqlDataSource data, string sql)
    {
        await using var command = data.CreateCommand(sql);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }
}
