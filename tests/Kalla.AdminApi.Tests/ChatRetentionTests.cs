using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Kalla.TestKit;

namespace Kalla.AdminApi.Tests;

/// <summary>
/// Chat retention (chat design §13.1, data-model §8; the condition on #223): <c>chat.rag_retention_chat_step</c> through the
/// admin's <see cref="RetentionService"/> removes expired messages (with their sources) and then the conversations left
/// without messages whose <c>ExpiresAt</c> passed; a live conversation, and an expired one that still has a live message,
/// stay.
/// </summary>
[Collection(AdminDatabaseGroup1.Name)]
public sealed class ChatRetentionTests(AdminDatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_expired_conversation_is_gone_after_the_retention_run()
    {
        var expired = Guid.CreateVersion7();
        var live = Guid.CreateVersion7();
        var stillUsed = Guid.CreateVersion7();
        var expiredMessage = Guid.CreateVersion7();
        await using (var connection = new NpgsqlConnection(database.Connection(RagDatabase.Api)))
        {
            await connection.OpenAsync(Ct);
            // As the api's writer leaves them (mode none, 90 days): expired 91 days ago, written yesterday, and an expired
            // conversation whose newest message is still within retention.
            await using var insert = new NpgsqlCommand($"""
                INSERT INTO chat."Conversations" ("Id", "Classification", "AssistantId", "ClientId", "SubjectKind", "SubjectId", "HistoryMode", "RetentionDays",
                    "CreatedAt", "LastMessageAt", "ExpiresAt")
                VALUES ('{expired}', 'internal', gen_random_uuid(), gen_random_uuid(), 'entra-user', 'retention-test', 'none', 90, now() - interval '181 days', now() - interval '181 days', now() - interval '91 days'),
                       ('{live}', 'internal', gen_random_uuid(), gen_random_uuid(), 'entra-user', 'retention-test', 'none', 90, now() - interval '1 day', now() - interval '1 day', now() + interval '89 days'),
                       ('{stillUsed}', 'internal', gen_random_uuid(), gen_random_uuid(), 'entra-user', 'retention-test', 'none', 90, now() - interval '181 days', now() - interval '181 days', now() - interval '1 day');
                INSERT INTO chat."Messages" ("Id", "ConversationId", "Classification", "Seq", "Role", "ExpiresAt", "Outcome", "CreatedAt", "CondenseUsed")
                VALUES ('{expiredMessage}', '{expired}', 'internal', 1, 'user', now() - interval '91 days', 'answered', now() - interval '181 days', false),
                       (gen_random_uuid(), '{live}', 'internal', 1, 'user', now() + interval '89 days', 'answered', now() - interval '1 day', false),
                       (gen_random_uuid(), '{stillUsed}', 'internal', 1, 'user', now() + interval '10 days', 'answered', now() - interval '80 days', false);
                INSERT INTO chat."MessageSources" ("MessageId", "Rank", "Classification", "DocumentId", "ChunkId", "CollectionId", "Used")
                VALUES ('{expiredMessage}', 1, 'internal', gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), true);
                """, connection);
            await insert.ExecuteNonQueryAsync(Ct);
        }

        await using var factory = database.HeaderFactory();
        var retention = new RetentionService(factory.Services.GetRequiredService<IServiceScopeFactory>(), Options.Create(new AdminOptions()), TimeProvider.System,
            NullLogger<RetentionService>.Instance);

        // Another test's run can hold a step's lock for a moment: run until the expired rows are gone (bounded).
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (await CountAsync("""SELECT count(*) FROM chat."Conversations" WHERE "Id" = $1""", expired) > 0 && waited.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (await retention.RunOnceAsync(Ct) <= 0)
            {
                await Task.Delay(50, Ct);
            }
        }

        (await CountAsync("""SELECT count(*) FROM chat."Conversations" WHERE "Id" = $1""", expired)).ShouldBe(0);
        (await CountAsync("""SELECT count(*) FROM chat."Messages" WHERE "Id" = $1""", expiredMessage)).ShouldBe(0);
        (await CountAsync("""SELECT count(*) FROM chat."MessageSources" WHERE "MessageId" = $1""", expiredMessage)).ShouldBe(0);
        (await CountAsync("""SELECT count(*) FROM chat."Conversations" WHERE "Id" = $1""", live)).ShouldBe(1);
        (await CountAsync("""SELECT count(*) FROM chat."Conversations" WHERE "Id" = $1""", stillUsed)).ShouldBe(1);
    }

    private async Task<long> CountAsync(string sql, Guid id)
    {
        await using var connection = new NpgsqlConnection(database.Connection(RagDatabase.Admin));
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection) { Parameters = { new() { Value = id } } };
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }
}
