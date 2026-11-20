using Npgsql;
using static Kalla.TestKit.RagDatabase;

namespace Kalla.IntegrationTests;

/// <summary>
/// The <c>chat</c> schema (data-model §2.3; P1b item 1) as the roles use it: the api writes conversations, messages,
/// sources, feedback and daily totals; the admin reads and only marks feedback reviewed; the worker cannot see the schema.
/// </summary>
[Collection(RagDatabaseGroup.Name)]
public sealed class ChatSchemaTests(RagDatabaseFixture database)
{
    [Fact]
    public async Task The_api_writes_a_conversation_with_messages_sources_and_feedback_and_deleting_it_cascades()
    {
        var api = database.Connection(Api);
        var conversation = Guid.NewGuid();
        var question = Guid.NewGuid();
        var answer = Guid.NewGuid();
        var client = Guid.NewGuid();
        await Sql.ExecuteAsync(api, $"""
            INSERT INTO chat."Conversations" ("Id", "Classification", "AssistantId", "ClientId", "SubjectKind", "SubjectId", "ExternalChatId",
                "HistoryMode", "RetentionDays", "LastMessageAt", "ExpiresAt")
            VALUES ('{conversation}', 'internal', gen_random_uuid(), '{client}', 'owui-user', 'u-1', 'chat-1', 'none', 0, now(), now());
            INSERT INTO chat."Messages" ("Id", "ConversationId", "Classification", "Seq", "Role", "ExpiresAt", "ExternalMessageId", "CondenseUsed", "Outcome")
            VALUES ('{question}', '{conversation}', 'internal', 1, 'user', now(), NULL, false, 'answered'),
                   ('{answer}', '{conversation}', 'internal', 2, 'assistant', now(), 'owui-m-2', false, 'answered');
            INSERT INTO chat."MessageSources" ("MessageId", "Rank", "Classification", "DocumentId", "ChunkId", "CollectionId", "Number", "Used")
            VALUES ('{answer}', 1, 'internal', gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), 1, true);
            INSERT INTO chat."Feedback" ("Id", "MessageId", "ConversationId", "Classification", "ClientId", "Source", "Rating")
            VALUES (gen_random_uuid(), '{answer}', '{conversation}', 'internal', '{client}', 'api', 1);
            INSERT INTO chat."AssistantDailyTotals" ("Day", "AssistantId", "ClientId", "DepartmentId", "Classification", "Conversations", "Messages",
                "NotFound", "Unavailable", "Degraded", "Errors", "Cancelled", "UncitedAnswers", "ThumbsUp", "ThumbsDown", "ToolCalls", "ToolErrors",
                "AgentLimitHits")
            VALUES (current_date, gen_random_uuid(), '{client}', gen_random_uuid(), 'internal', 1, 2, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0);
            """);

        // Regeneration dedup, one api rating per message, the same Seq twice, an unknown outcome and a 0 rating are refused.
        foreach (var (sql, state) in new[]
                 {
                     ($"""INSERT INTO chat."Messages" ("Id", "ConversationId", "Classification", "Seq", "Role", "ExpiresAt", "ExternalMessageId", "CondenseUsed", "Outcome") VALUES (gen_random_uuid(), '{conversation}', 'internal', 3, 'assistant', now(), 'owui-m-2', false, 'answered')""", PostgresErrorCodes.UniqueViolation),
                     ($"""INSERT INTO chat."Messages" ("Id", "ConversationId", "Classification", "Seq", "Role", "ExpiresAt", "CondenseUsed", "Outcome") VALUES (gen_random_uuid(), '{conversation}', 'internal', 2, 'user', now(), false, 'answered')""", PostgresErrorCodes.UniqueViolation),
                     ($"""INSERT INTO chat."Feedback" ("Id", "MessageId", "ConversationId", "Classification", "ClientId", "Source", "Rating") VALUES (gen_random_uuid(), '{answer}', '{conversation}', 'internal', '{client}', 'api', -1)""", PostgresErrorCodes.UniqueViolation),
                     ($"""INSERT INTO chat."Messages" ("Id", "ConversationId", "Classification", "Seq", "Role", "ExpiresAt", "CondenseUsed", "Outcome") VALUES (gen_random_uuid(), '{conversation}', 'internal', 4, 'user', now(), false, 'maybe')""", PostgresErrorCodes.CheckViolation),
                     ($"""INSERT INTO chat."Feedback" ("Id", "MessageId", "ConversationId", "Classification", "ClientId", "Source", "Rating") VALUES (gen_random_uuid(), '{answer}', '{conversation}', 'internal', '{client}', 'owui-import', 0)""", PostgresErrorCodes.CheckViolation),
                     ($"""INSERT INTO chat."Conversations" ("Id", "Classification", "AssistantId", "ClientId", "SubjectKind", "SubjectId", "HistoryMode", "RetentionDays", "LastMessageAt", "ExpiresAt") VALUES (gen_random_uuid(), 'internal', gen_random_uuid(), '{client}', 'owui-user', 'u-1', 'full', 0, now(), now())""", PostgresErrorCodes.CheckViolation),
                     // No stored content until A1 (weakness review §4): every ciphertext column stays null.
                     ($"""UPDATE chat."Messages" SET "ContentCiphertext" = '\x01' WHERE "Id" = '{answer}'""", PostgresErrorCodes.CheckViolation),
                     ($"""UPDATE chat."Conversations" SET "TitleCiphertext" = '\x01' WHERE "Id" = '{conversation}'""", PostgresErrorCodes.CheckViolation),
                     ($"""UPDATE chat."MessageSources" SET "SnippetCiphertext" = '\x01' WHERE "MessageId" = '{answer}'""", PostgresErrorCodes.CheckViolation),
                     ($"""UPDATE chat."Feedback" SET "CommentCiphertext" = '\x01' WHERE "MessageId" = '{answer}'""", PostgresErrorCodes.CheckViolation),
                 })
        {
            (await Should.ThrowAsync<PostgresException>(() => Sql.ExecuteAsync(api, sql))).SqlState.ShouldBe(state, sql);
        }

        // The admin reads and marks feedback reviewed, nothing else; the worker has no access to the schema.
        var admin = database.Connection(Admin);
        (await Sql.ScalarAsync<long>(admin, $"""SELECT count(*) FROM chat."Messages" WHERE "ConversationId" = '{conversation}'""")).ShouldBe(2);
        await Sql.ExecuteAsync(admin, $"""UPDATE chat."Feedback" SET "ReviewState" = 'reviewed' WHERE "MessageId" = '{answer}'""");
        await Sql.ShouldBeDeniedAsync(admin, $"""UPDATE chat."Feedback" SET "Rating" = -1 WHERE "MessageId" = '{answer}'""");
        await Sql.ShouldBeDeniedAsync(admin, $"""DELETE FROM chat."Conversations" WHERE "Id" = '{conversation}'""");
        await Sql.ShouldBeDeniedAsync(database.Connection(Worker), """SELECT count(*) FROM chat."Conversations" """);

        // Deleting the conversation removes its messages, sources and feedback (retention, data-model §8).
        await Sql.ExecuteAsync(api, $"""DELETE FROM chat."Conversations" WHERE "Id" = '{conversation}'""");
        (await Sql.ScalarAsync<long>(api, $"""
            SELECT (SELECT count(*) FROM chat."Messages" WHERE "ConversationId" = '{conversation}')
                 + (SELECT count(*) FROM chat."MessageSources" WHERE "MessageId" = '{answer}')
                 + (SELECT count(*) FROM chat."Feedback" WHERE "MessageId" = '{answer}')
            """)).ShouldBe(0);
    }
}
