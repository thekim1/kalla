using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Kalla.Answering.Chat;
using Kalla.Domain;
using Kalla.Infrastructure.Gateway;
using Kalla.TestKit;

namespace Kalla.Api.Tests;

/// <summary>
/// Conversation ids and the writer (chat design §11.1, §12; #266): an issued id is bound to client, assistant and subject;
/// the writer refuses to append to a conversation of another owner or assistant on its own, whatever the MAC said; Open
/// WebUI chats are one conversation per (user, chat id, assistant), and a resent user message id (a regeneration) is kept once.
/// Every turn adds one turn and its full cost to its outcome's row of <c>AssistantDailyOutcomes</c> (Decisions "Cost per outcome").
/// A written not-found or general-knowledge turn adds one to its nearest hit's row of <c>AssistantDailyGaps</c> (Decisions
/// "Content-gap report"), where the day's totals count it.
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
            // A retried data error costs two retry delays (attempts 1 to 3) before the split, so under two delays means none was
            // taken; one delay's margin is for a loaded runner's database (2.007 s was seen with no retry).
            started.Elapsed.ShouldBeLessThan(2 * ConversationWriter.RetryInterval);
        }
        finally
        {
            await writer.StopAsync(CancellationToken.None);
        }

        (await CountAsync(api, $"""SELECT count(*) FROM chat."Messages" WHERE "Id" = '{good.MessageId}'""")).ShouldBe(1);
        (await CountAsync(api, $"""SELECT count(*) FROM chat."Messages" WHERE "Id" = '{bad.MessageId}'""")).ShouldBe(0);
    }

    private static UsageRecord Charged(string step, decimal cost) => new(step, "fors/chat-eu", 100, 0, 10, cost, 0m, Estimated: false);

    private static UsageRecord Estimated(string step, decimal cost) => new(step, "fake-no-cost", 100, 0, 10, 0m, cost, Estimated: true);

    [Fact]
    public async Task Cost_per_outcome_counts_each_turn_under_its_outcome_with_its_usage_and_sums_to_the_days_usage()
    {
        // Chat design §18.9: two answered turns, a not-found turn and a usage-only error turn in one batch give AssistantDailyOutcomes
        // rows answered 2, not-found 1, error 1, each with the summed cost of its turns' usage records; the rows' cost summed equals
        // the day's AssistantDailyUsage cost summed, gateway and estimated apart.
        await using var api = NpgsqlDataSource.Create(db.Connection(RagDatabase.Api));
        using var meters = new Meters();
        var writer = new ConversationWriter(api, meters, NullLogger<ConversationWriter>.Instance);
        var (client, assistant, department) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var at = DateTimeOffset.UtcNow;
        ChatTurn Asked(string outcome, params UsageRecord[] usage) =>
            Turn(Guid.CreateVersion7(), null, assistant, client, "user-O", "internal") with { Outcome = outcome, DepartmentId = department, Usage = usage, At = at };

        await writer.WriteAsync(
        [
            Asked("answered", Charged("query-embed", 0.0001m), Estimated("condense", 0.0003m), Charged("chat-answer", 0.002m)),
            Asked("answered", Charged("query-embed", 0.0001m), Estimated("chat-answer", 0.0015m)),
            Asked("not-found", Charged("query-embed", 0.0001m)),
            ChatTurn.UsageOnlyOf(assistant, department, client, "internal", at, [Charged("condense", 0.0003m), Charged("query-embed", 0.0001m)]) with { Error = true },
        ], Ct);

        var rows = await OutcomesAsync(api, assistant);
        rows.ShouldBe(new Dictionary<string, (int, decimal, decimal)>
        {
            ["answered"] = (2, 0.0022m, 0.0018m), ["not-found"] = (1, 0.0001m, 0m), ["error"] = (1, 0.0004m, 0m),
        }, ignoreOrder: true);
        (await CountAsync(api, $"""SELECT count(*) FROM chat."AssistantDailyOutcomes" WHERE "AssistantId" = '{assistant}' AND "DepartmentId" = '{department}'"""))
            .ShouldBe(3);

        await using var usage = api.CreateCommand("""
            SELECT sum("CostUsd"), sum("EstimatedCostUsd") FROM chat."AssistantDailyUsage" WHERE "AssistantId" = $1 AND "Day" = $2
            """);
        usage.Parameters.Add(new NpgsqlParameter { Value = assistant });
        usage.Parameters.Add(new NpgsqlParameter { Value = StockholmTime.DayOf(at) });
        await using var reader = await usage.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).ShouldBeTrue();
        (reader.GetDecimal(0), reader.GetDecimal(1)).ShouldBe((rows.Values.Sum(r => r.CostUsd), rows.Values.Sum(r => r.EstimatedCostUsd)));
    }

    [Fact]
    public async Task Calls_after_a_turns_write_add_their_cost_to_its_outcome_and_a_failure_after_a_call_counts_as_an_error_turn()
    {
        // Decisions "Cost per outcome": the follow-up and escalation calls of a JSON answer come after its turn was enqueued (§9.8,
        // §9.9): their cost is the turn's, under its outcome, with no turn of their own. A turn that fails after a model call
        // without a 5xx or an unavailable key is still a usage-only turn, so it counts under error (§12.2); an unavailable one
        // under unavailable.
        await using var api = NpgsqlDataSource.Create(db.Connection(RagDatabase.Api));
        using var meters = new Meters();
        var writer = new ConversationWriter(api, meters, NullLogger<ConversationWriter>.Instance);
        var (client, assistant, department) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var at = DateTimeOffset.UtcNow;
        ChatTurn UsageOnly(params UsageRecord[] usage) => ChatTurn.UsageOnlyOf(assistant, department, client, "public", at, usage);

        await writer.WriteAsync(
        [
            Turn(Guid.CreateVersion7(), null, assistant, client, "user-L") with { Outcome = "answered-general", DepartmentId = department, At = at, Usage = [Charged("chat-answer", 0.002m)] },
        ], Ct);
        await writer.WriteAsync(
        [
            UsageOnly(Charged("follow-ups", 0.0004m)) with { AfterTurn = true, Outcome = "answered-general" },
            UsageOnly(Charged("condense", 0.0003m)),
            UsageOnly() with { Unavailable = true },
        ], Ct);

        (await OutcomesAsync(api, assistant)).ShouldBe(new Dictionary<string, (int, decimal, decimal)>
        {
            ["answered-general"] = (1, 0.0024m, 0m), ["error"] = (1, 0.0003m, 0m), ["unavailable"] = (1, 0m, 0m),
        }, ignoreOrder: true);
    }

    [Fact]
    public async Task Gaps_count_each_written_low_coverage_turn_under_its_nearest_hit_where_the_totals_count_it()
    {
        // §12.2: not-found and answered-general turns per (day, assistant, client, level, outcome, nearest collection, nearest
        // document), no hit as the zero ids. Answered, cancelled, usage-only, after-turn and duplicate turns add nothing, so the
        // gap rows add up to the day's NotFound and GeneralAnswers.
        await using var api = NpgsqlDataSource.Create(db.Connection(RagDatabase.Api));
        using var meters = new Meters();
        var writer = new ConversationWriter(api, meters, NullLogger<ConversationWriter>.Instance);
        var (client, assistant, department) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var (collection, document, other) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var at = DateTimeOffset.UtcNow;
        var chat = "chat-" + Guid.CreateVersion7().ToString("N");
        ChatTurn Asked(string outcome, NearestHit? nearest) =>
            Turn(Guid.CreateVersion7(), null, assistant, client, "user-N", "internal") with { Outcome = outcome, DepartmentId = department, At = at, Nearest = nearest };
        var answer = Guid.NewGuid().ToString();
        var first = Turn(null, chat, assistant, client, "owui:u-1", "internal") with
        {
            Outcome = "not-found", DepartmentId = department, At = at, Nearest = new NearestHit(collection, other), UserExternalId = Guid.NewGuid().ToString(), AnswerExternalId = answer,
        };

        await writer.WriteAsync(
        [
            Asked("not-found", new NearestHit(collection, document)),
            Asked("not-found", new NearestHit(collection, document)),
            Asked("answered-general", new NearestHit(collection, document)),
            Asked("not-found", null),
            Asked("answered", new NearestHit(collection, document)),
            Asked("cancelled", new NearestHit(collection, document)), // an aborted general-knowledge stream is a cancelled turn
            first,
            ChatTurn.UsageOnlyOf(assistant, department, client, "internal", at, [Charged("follow-ups", 0.0004m)]) with { AfterTurn = true, Outcome = "not-found" },
            ChatTurn.UsageOnlyOf(assistant, department, client, "internal", at, [Charged("condense", 0.0003m)]) with { Error = true },
        ], Ct);
        // #698: the same Open WebUI answer id again writes no row, so the totals and the gaps leave it out.
        await writer.WriteAsync([first with { MessageId = Guid.CreateVersion7(), UserExternalId = Guid.NewGuid().ToString() }], Ct);

        var gaps = await GapsAsync(api, assistant);
        gaps.ShouldBe(new Dictionary<(string, Guid, Guid), int>
        {
            [("not-found", collection, document)] = 2, [("answered-general", collection, document)] = 1, [("not-found", Guid.Empty, Guid.Empty)] = 1,
            [("not-found", collection, other)] = 1,
        }, ignoreOrder: true);
        (await CountAsync(api, $"""
            SELECT count(*) FROM chat."AssistantDailyGaps"
            WHERE "AssistantId" = '{assistant}' AND "ClientId" = '{client}' AND "DepartmentId" = '{department}' AND "Classification" = 'internal' AND "Day" = '{StockholmTime.DayOf(at):yyyy-MM-dd}'
            """)).ShouldBe(4);
        (await CountAsync(api, $"""SELECT sum("NotFound") FROM chat."AssistantDailyTotals" WHERE "AssistantId" = '{assistant}'""")).ShouldBe(4);
        (await CountAsync(api, $"""SELECT sum("GeneralAnswers") FROM chat."AssistantDailyTotals" WHERE "AssistantId" = '{assistant}'""")).ShouldBe(1);

        // A later batch adds to the same rows.
        await writer.WriteAsync([Asked("not-found", new NearestHit(collection, document))], Ct);
        (await GapsAsync(api, assistant))[("not-found", collection, document)].ShouldBe(3);
    }

    /// <summary>The assistant's <c>AssistantDailyGaps</c> rows by (outcome, collection, document).</summary>
    private static async Task<Dictionary<(string, Guid, Guid), int>> GapsAsync(NpgsqlDataSource data, Guid assistant)
    {
        await using var command = data.CreateCommand("""
            SELECT "Outcome", "CollectionId", "DocumentId", "Turns" FROM chat."AssistantDailyGaps" WHERE "AssistantId" = $1
            """);
        command.Parameters.Add(new NpgsqlParameter { Value = assistant });
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new Dictionary<(string, Guid, Guid), int>();
        while (await reader.ReadAsync(Ct))
        {
            rows[(reader.GetString(0), reader.GetGuid(1), reader.GetGuid(2))] = reader.GetInt32(3);
        }

        return rows;
    }

    /// <summary>The assistant's <c>AssistantDailyOutcomes</c> rows by outcome: turns, the gateway's cost and the estimate.</summary>
    private static async Task<Dictionary<string, (int Turns, decimal CostUsd, decimal EstimatedCostUsd)>> OutcomesAsync(NpgsqlDataSource data, Guid assistant)
    {
        await using var command = data.CreateCommand("""
            SELECT "Outcome", "Turns", "CostUsd", "EstimatedCostUsd" FROM chat."AssistantDailyOutcomes" WHERE "AssistantId" = $1
            """);
        command.Parameters.Add(new NpgsqlParameter { Value = assistant });
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new Dictionary<string, (int, decimal, decimal)>(StringComparer.Ordinal);
        while (await reader.ReadAsync(Ct))
        {
            rows[reader.GetString(0)] = (reader.GetInt32(1), reader.GetDecimal(2), reader.GetDecimal(3));
        }

        return rows;
    }

    private static async Task<long> CountAsync(NpgsqlDataSource data, string sql)
    {
        await using var command = data.CreateCommand(sql);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }
}
