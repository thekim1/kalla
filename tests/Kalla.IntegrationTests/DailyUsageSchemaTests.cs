using Npgsql;
using static Kalla.TestKit.RagDatabase;

namespace Kalla.IntegrationTests;

/// <summary>
/// The per-step usage tables (data-model §2.2, §2.3, §8; cost-attribution §5): <c>chat."AssistantDailyUsage"</c> written by
/// the api (and the admin's test panel), <c>content."CollectionDailyUsage"</c> by the worker; every column takes a value,
/// the checks refuse a negative counter, more estimated calls than calls, more cached than input tokens, an empty step and
/// an unknown level, the nightly chat step removes assistant usage older than 3 years, and the daily totals keep no token
/// or cost column (B-38). <c>chat."AssistantDailyOutcomes"</c> (Decisions "Cost per outcome") likewise: its checks, its grants
/// and its 3 years. <c>chat."AssistantDailyGaps"</c> (Decisions "Content-gap report") too, and it has no column that could hold
/// a question, an answer or who asked. <c>chat."DocumentDailyFeedback"</c> (Decisions "Downvotes per document") as well: its
/// checks, its grants, its 3 years, and no column for content or a subject.
/// </summary>
[Collection(RagDatabaseGroup.Name)]
public sealed class DailyUsageSchemaTests(RagDatabaseFixture database)
{
    private const string AssistantColumns =
        """("Day", "AssistantId", "ClientId", "Classification", "Step", "DepartmentId", "Calls", "InputTokens", "CachedInputTokens", "OutputTokens", "CostUsd", "EstimatedCalls", "EstimatedCostUsd")""";

    private const string CollectionColumns =
        """("Day", "CollectionId", "Classification", "Step", "DepartmentId", "Calls", "InputTokens", "CachedInputTokens", "OutputTokens", "CostUsd", "EstimatedCalls", "EstimatedCostUsd")""";

    [Fact]
    public async Task An_assistant_usage_row_takes_every_column_and_the_checks_refuse_what_they_forbid()
    {
        var api = database.Connection(Api);
        var assistant = Guid.NewGuid();
        await Sql.ExecuteAsync(api, $"""
            INSERT INTO chat."AssistantDailyUsage" {AssistantColumns}
            VALUES (current_date, '{assistant}', gen_random_uuid(), 'internal', 'chat-answer', gen_random_uuid(), 3, 1200, 400, 300, 0.00123456, 1, 0.00004500)
            """);
        (await Sql.ScalarAsync<decimal>(api, $"""SELECT "CostUsd" + "EstimatedCostUsd" FROM chat."AssistantDailyUsage" WHERE "AssistantId" = '{assistant}'"""))
            .ShouldBe(0.00127956m);

        // The counters default to 0, so a writer may name only what it adds.
        await Sql.ExecuteAsync(api, $"""
            INSERT INTO chat."AssistantDailyUsage" ("Day", "AssistantId", "ClientId", "Classification", "Step", "DepartmentId")
            VALUES (current_date, '{assistant}', gen_random_uuid(), 'public', 'rerank', gen_random_uuid())
            """);
        (await Sql.ScalarAsync<long>(api, $"""
            SELECT "Calls" + "InputTokens" + "CachedInputTokens" + "OutputTokens" + "EstimatedCalls" FROM chat."AssistantDailyUsage"
            WHERE "AssistantId" = '{assistant}' AND "Step" = 'rerank'
            """)).ShouldBe(0);

        foreach (var (values, state) in Refused())
        {
            var sql = $"""INSERT INTO chat."AssistantDailyUsage" {AssistantColumns} VALUES (current_date, gen_random_uuid(), gen_random_uuid(), {values})""";
            (await Should.ThrowAsync<PostgresException>(() => Sql.ExecuteAsync(api, sql))).SqlState.ShouldBe(state, sql);
        }

        // One row per (day, assistant, client, level, step): the writer's upsert key.
        var client = Guid.NewGuid();
        var duplicate = $"""
            INSERT INTO chat."AssistantDailyUsage" ("Day", "AssistantId", "ClientId", "Classification", "Step", "DepartmentId")
            VALUES (current_date, '{assistant}', '{client}', 'internal', 'condense', gen_random_uuid())
            """;
        await Sql.ExecuteAsync(api, duplicate);
        (await Should.ThrowAsync<PostgresException>(() => Sql.ExecuteAsync(api, duplicate))).SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);

        // The admin upserts the test panel's usage (admin design §4.9) but deletes nothing; the worker cannot see the schema.
        var admin = database.Connection(Admin);
        await Sql.ExecuteAsync(admin, $"""
            INSERT INTO chat."AssistantDailyUsage" AS u ("Day", "AssistantId", "ClientId", "Classification", "Step", "DepartmentId", "Calls")
            VALUES (current_date, '{assistant}', '{client}', 'internal', 'condense', gen_random_uuid(), 1)
            ON CONFLICT ("Day", "AssistantId", "ClientId", "Classification", "Step") DO UPDATE SET "Calls" = u."Calls" + excluded."Calls"
            """);
        (await Sql.ScalarAsync<int>(admin, $"""SELECT "Calls" FROM chat."AssistantDailyUsage" WHERE "ClientId" = '{client}'""")).ShouldBe(1);
        await Sql.ShouldBeDeniedAsync(admin, $"""DELETE FROM chat."AssistantDailyUsage" WHERE "AssistantId" = '{assistant}'""");
        await Sql.ShouldBeDeniedAsync(database.Connection(Worker), """SELECT count(*) FROM chat."AssistantDailyUsage" """);
    }

    [Fact]
    public async Task A_collection_usage_row_takes_every_column_and_the_checks_refuse_what_they_forbid()
    {
        var worker = database.Connection(Worker);
        var collection = Guid.NewGuid();
        await Sql.ExecuteAsync(worker, $"""
            INSERT INTO content."CollectionDailyUsage" {CollectionColumns}
            VALUES (current_date, '{collection}', 'public', 'embed', gen_random_uuid(), 40, 52000, 0, 0, 0.00104000, 2, 0.00000800)
            """);
        (await Sql.ScalarAsync<long>(worker, $"""SELECT "InputTokens" FROM content."CollectionDailyUsage" WHERE "CollectionId" = '{collection}'"""))
            .ShouldBe(52000);

        foreach (var (values, state) in Refused())
        {
            var sql = $"""INSERT INTO content."CollectionDailyUsage" {CollectionColumns} VALUES (current_date, gen_random_uuid(), {values})""";
            (await Should.ThrowAsync<PostgresException>(() => Sql.ExecuteAsync(worker, sql))).SqlState.ShouldBe(state, sql);
        }

        // The admin reads it for the cost report and writes nothing; the api has no access.
        var admin = database.Connection(Admin);
        (await Sql.ScalarAsync<long>(admin, $"""SELECT count(*) FROM content."CollectionDailyUsage" WHERE "CollectionId" = '{collection}'""")).ShouldBe(1);
        await Sql.ShouldBeDeniedAsync(admin, $"""UPDATE content."CollectionDailyUsage" SET "Calls" = 0 WHERE "CollectionId" = '{collection}'""");
        await Sql.ShouldBeDeniedAsync(database.Connection(Api), """SELECT count(*) FROM content."CollectionDailyUsage" """);
    }

    [Fact]
    public async Task The_chat_retention_step_removes_assistant_usage_older_than_three_years()
    {
        // Data-model §8: 3 years, as AssistantDailyTotals, by chat.rag_retention_chat_step (the admin's RetentionService).
        var assistant = Guid.NewGuid();
        await Sql.ExecuteAsync(database.Connection(Api), $"""
            INSERT INTO chat."AssistantDailyUsage" ("Day", "AssistantId", "ClientId", "Classification", "Step", "DepartmentId", "Calls")
            VALUES ((now() AT TIME ZONE 'Europe/Stockholm')::date - interval '3 years 1 day', '{assistant}', gen_random_uuid(), 'internal', 'chat-answer', gen_random_uuid(), 1),
                   ((now() AT TIME ZONE 'Europe/Stockholm')::date - interval '1 day', '{assistant}', gen_random_uuid(), 'internal', 'chat-answer', gen_random_uuid(), 1)
            """);

        var admin = database.Connection(Admin);
        // One batch per table and call; other tests' expired rows go too, so call until nothing is left (bounded).
        for (var i = 0; i < 100 && await Sql.ScalarAsync<long>(admin, "SELECT chat.rag_retention_chat_step(1000)") > 0; i++)
        {
        }

        (await Sql.ListAsync(admin, $"""SELECT ("Day" > current_date - 7)::text FROM chat."AssistantDailyUsage" WHERE "AssistantId" = '{assistant}'"""))
            .ShouldBe(["true"]);
    }

    [Fact]
    public async Task An_assistant_outcome_row_takes_every_column_and_the_checks_refuse_what_they_forbid()
    {
        const string columns = """("Day", "AssistantId", "ClientId", "Classification", "Outcome", "DepartmentId", "Turns", "CostUsd", "EstimatedCostUsd")""";
        var api = database.Connection(Api);
        var assistant = Guid.NewGuid();
        var client = Guid.NewGuid();
        await Sql.ExecuteAsync(api, $"""
            INSERT INTO chat."AssistantDailyOutcomes" {columns}
            VALUES (current_date, '{assistant}', '{client}', 'internal', 'answered-general', gen_random_uuid(), 3, 0.00123456, 0.00004500)
            """);
        (await Sql.ScalarAsync<decimal>(api, $"""SELECT "CostUsd" + "EstimatedCostUsd" FROM chat."AssistantDailyOutcomes" WHERE "AssistantId" = '{assistant}'"""))
            .ShouldBe(0.00127956m);

        // The counters default to 0; every value of Messages.Outcome is taken.
        foreach (var outcome in new[] { "answered", "not-found", "unavailable", "refused", "error", "cancelled" })
        {
            await Sql.ExecuteAsync(api, $"""
                INSERT INTO chat."AssistantDailyOutcomes" ("Day", "AssistantId", "ClientId", "Classification", "Outcome", "DepartmentId")
                VALUES (current_date, '{assistant}', '{client}', 'internal', '{outcome}', gen_random_uuid())
                """);
        }

        (await Sql.ScalarAsync<long>(api, $"""SELECT count(*) FROM chat."AssistantDailyOutcomes" WHERE "AssistantId" = '{assistant}' AND "Turns" = 0""")).ShouldBe(6);

        foreach (var (values, state) in new[]
                 {
                     ("'secret', 'answered', 1, 0, 0", PostgresErrorCodes.CheckViolation),
                     ("'public', 'answered-later', 1, 0, 0", PostgresErrorCodes.CheckViolation),
                     ("'public', 'answered', -1, 0, 0", PostgresErrorCodes.CheckViolation),
                     ("'public', 'answered', 1, -0.1, 0", PostgresErrorCodes.CheckViolation),
                     ("'public', 'answered', 1, 0, -0.1", PostgresErrorCodes.CheckViolation),
                     ("'public', NULL, 1, 0, 0", PostgresErrorCodes.NotNullViolation),
                 })
        {
            var sql = $"""
                INSERT INTO chat."AssistantDailyOutcomes" ("Day", "AssistantId", "ClientId", "DepartmentId", "Classification", "Outcome", "Turns", "CostUsd", "EstimatedCostUsd")
                VALUES (current_date, gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), {values})
                """;
            (await Should.ThrowAsync<PostgresException>(() => Sql.ExecuteAsync(api, sql))).SqlState.ShouldBe(state, sql);
        }

        // One row per (day, assistant, client, level, outcome): the writers' upsert key. The admin upserts the test panel's turns
        // (admin design §4.9) but deletes nothing; the worker cannot see the schema.
        var admin = database.Connection(Admin);
        await Sql.ExecuteAsync(admin, $"""
            INSERT INTO chat."AssistantDailyOutcomes" AS o ("Day", "AssistantId", "ClientId", "Classification", "Outcome", "DepartmentId", "Turns")
            VALUES (current_date, '{assistant}', '{client}', 'internal', 'error', gen_random_uuid(), 1)
            ON CONFLICT ("Day", "AssistantId", "ClientId", "Classification", "Outcome") DO UPDATE SET "Turns" = o."Turns" + excluded."Turns"
            """);
        (await Sql.ScalarAsync<int>(admin, $"""SELECT "Turns" FROM chat."AssistantDailyOutcomes" WHERE "AssistantId" = '{assistant}' AND "Outcome" = 'error'""")).ShouldBe(1);
        await Sql.ShouldBeDeniedAsync(admin, $"""DELETE FROM chat."AssistantDailyOutcomes" WHERE "AssistantId" = '{assistant}'""");
        await Sql.ShouldBeDeniedAsync(database.Connection(Worker), """SELECT count(*) FROM chat."AssistantDailyOutcomes" """);
    }

    [Fact]
    public async Task The_chat_retention_step_removes_assistant_outcomes_older_than_three_years()
    {
        // Data-model §8: 3 years, as AssistantDailyUsage, by chat.rag_retention_chat_step.
        var assistant = Guid.NewGuid();
        await Sql.ExecuteAsync(database.Connection(Api), $"""
            INSERT INTO chat."AssistantDailyOutcomes" ("Day", "AssistantId", "ClientId", "Classification", "Outcome", "DepartmentId", "Turns")
            VALUES ((now() AT TIME ZONE 'Europe/Stockholm')::date - interval '3 years 1 day', '{assistant}', gen_random_uuid(), 'internal', 'answered', gen_random_uuid(), 1),
                   ((now() AT TIME ZONE 'Europe/Stockholm')::date - interval '1 day', '{assistant}', gen_random_uuid(), 'internal', 'answered', gen_random_uuid(), 1)
            """);

        var admin = database.Connection(Admin);
        for (var i = 0; i < 100 && await Sql.ScalarAsync<long>(admin, "SELECT chat.rag_retention_chat_step(1000)") > 0; i++)
        {
        }

        (await Sql.ListAsync(admin, $"""SELECT ("Day" > current_date - 7)::text FROM chat."AssistantDailyOutcomes" WHERE "AssistantId" = '{assistant}'"""))
            .ShouldBe(["true"]);
    }

    [Fact]
    public async Task An_assistant_gap_row_takes_every_column_and_the_checks_refuse_what_they_forbid()
    {
        var api = database.Connection(Api);
        var assistant = Guid.NewGuid();
        var client = Guid.NewGuid();
        await Sql.ExecuteAsync(api, $"""
            INSERT INTO chat."AssistantDailyGaps" ("Day", "AssistantId", "ClientId", "Classification", "Outcome", "CollectionId", "DocumentId", "DepartmentId", "Turns")
            VALUES (current_date, '{assistant}', '{client}', 'internal', 'not-found', gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), 3)
            """);

        // No hit is the zero ids; the counter defaults to 0; answered-general is the other outcome.
        await Sql.ExecuteAsync(api, $"""
            INSERT INTO chat."AssistantDailyGaps" ("Day", "AssistantId", "ClientId", "Classification", "Outcome", "CollectionId", "DocumentId", "DepartmentId")
            VALUES (current_date, '{assistant}', '{client}', 'internal', 'answered-general', '{Guid.Empty}', '{Guid.Empty}', gen_random_uuid())
            """);
        (await Sql.ScalarAsync<long>(api, $"""SELECT sum("Turns") FROM chat."AssistantDailyGaps" WHERE "AssistantId" = '{assistant}'""")).ShouldBe(3);

        foreach (var (values, state) in new[]
                 {
                     ("'secret', 'not-found', 1", PostgresErrorCodes.CheckViolation),
                     ("'public', 'answered', 1", PostgresErrorCodes.CheckViolation), // only the two gap outcomes
                     ("'public', 'unavailable', 1", PostgresErrorCodes.CheckViolation),
                     ("'public', 'not-found', -1", PostgresErrorCodes.CheckViolation),
                     ("'public', NULL, 1", PostgresErrorCodes.NotNullViolation),
                 })
        {
            var sql = $"""
                INSERT INTO chat."AssistantDailyGaps" ("Day", "AssistantId", "ClientId", "CollectionId", "DocumentId", "DepartmentId", "Classification", "Outcome", "Turns")
                VALUES (current_date, gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), {values})
                """;
            (await Should.ThrowAsync<PostgresException>(() => Sql.ExecuteAsync(api, sql))).SqlState.ShouldBe(state, sql);
        }

        // Only the api writes it (never the test panel, data-model §2.3); the admin reads it for the report; the worker cannot see it.
        var admin = database.Connection(Admin);
        (await Sql.ScalarAsync<long>(admin, $"""SELECT count(*) FROM chat."AssistantDailyGaps" WHERE "AssistantId" = '{assistant}'""")).ShouldBe(2);
        await Sql.ShouldBeDeniedAsync(admin, $"""
            INSERT INTO chat."AssistantDailyGaps" ("Day", "AssistantId", "ClientId", "Classification", "Outcome", "CollectionId", "DocumentId", "DepartmentId", "Turns")
            VALUES (current_date, '{assistant}', '{client}', 'internal', 'not-found', '{Guid.Empty}', '{Guid.Empty}', gen_random_uuid(), 1)
            """);
        await Sql.ShouldBeDeniedAsync(admin, $"""UPDATE chat."AssistantDailyGaps" SET "Turns" = 0 WHERE "AssistantId" = '{assistant}'""");
        await Sql.ShouldBeDeniedAsync(database.Connection(Worker), """SELECT count(*) FROM chat."AssistantDailyGaps" """);
    }

    [Fact]
    public async Task The_gap_table_has_no_column_for_a_question_an_answer_or_who_asked()
    {
        // Decisions "Content-gap report": a count per day and nearest document; the only text columns are the level and the outcome.
        var columns = await Sql.ListAsync(database.Connection(Api), """
            SELECT column_name::text || ':' || data_type::text || ':' || coalesce(character_maximum_length::text, '')
            FROM information_schema.columns WHERE table_schema = 'chat' AND table_name = 'AssistantDailyGaps'
            """);
        columns.ShouldBe(
        [
            "Day:date:", "AssistantId:uuid:", "ClientId:uuid:", "Classification:character varying:20", "Outcome:character varying:20", "CollectionId:uuid:",
            "DocumentId:uuid:", "DepartmentId:uuid:", "Turns:integer:",
        ], ignoreOrder: true);
    }

    [Fact]
    public async Task The_chat_retention_step_removes_assistant_gaps_older_than_three_years()
    {
        // Data-model §8: 3 years, as the other daily tables, by chat.rag_retention_chat_step.
        var assistant = Guid.NewGuid();
        await Sql.ExecuteAsync(database.Connection(Api), $"""
            INSERT INTO chat."AssistantDailyGaps" ("Day", "AssistantId", "ClientId", "Classification", "Outcome", "CollectionId", "DocumentId", "DepartmentId", "Turns")
            VALUES ((now() AT TIME ZONE 'Europe/Stockholm')::date - interval '3 years 1 day', '{assistant}', gen_random_uuid(), 'internal', 'not-found', '{Guid.Empty}', '{Guid.Empty}', gen_random_uuid(), 1),
                   ((now() AT TIME ZONE 'Europe/Stockholm')::date - interval '1 day', '{assistant}', gen_random_uuid(), 'internal', 'not-found', '{Guid.Empty}', '{Guid.Empty}', gen_random_uuid(), 1)
            """);

        var admin = database.Connection(Admin);
        for (var i = 0; i < 100 && await Sql.ScalarAsync<long>(admin, "SELECT chat.rag_retention_chat_step(1000)") > 0; i++)
        {
        }

        (await Sql.ListAsync(admin, $"""SELECT ("Day" > current_date - 7)::text FROM chat."AssistantDailyGaps" WHERE "AssistantId" = '{assistant}'"""))
            .ShouldBe(["true"]);
    }

    [Fact]
    public async Task A_document_feedback_row_takes_every_column_and_the_checks_refuse_what_they_forbid()
    {
        var api = database.Connection(Api);
        var document = Guid.NewGuid();
        await Sql.ExecuteAsync(api, $"""
            INSERT INTO chat."DocumentDailyFeedback" ("Day", "AssistantId", "ClientId", "Classification", "CollectionId", "DocumentId", "DepartmentId", "ThumbsUp", "ThumbsDown")
            VALUES (current_date, gen_random_uuid(), gen_random_uuid(), 'internal', gen_random_uuid(), '{document}', gen_random_uuid(), 2, 3)
            """);

        // The counters default to 0.
        await Sql.ExecuteAsync(api, $"""
            INSERT INTO chat."DocumentDailyFeedback" ("Day", "AssistantId", "ClientId", "Classification", "CollectionId", "DocumentId", "DepartmentId")
            VALUES (current_date, gen_random_uuid(), gen_random_uuid(), 'public', gen_random_uuid(), '{document}', gen_random_uuid())
            """);
        (await Sql.ScalarAsync<long>(api, $"""SELECT sum("ThumbsUp" + "ThumbsDown") FROM chat."DocumentDailyFeedback" WHERE "DocumentId" = '{document}'""")).ShouldBe(5);

        foreach (var (values, state) in new[]
                 {
                     ("'secret', 0, 0", PostgresErrorCodes.CheckViolation),
                     ("'public', -1, 0", PostgresErrorCodes.CheckViolation), // never below 0
                     ("'public', 0, -1", PostgresErrorCodes.CheckViolation),
                     ("NULL, 0, 0", PostgresErrorCodes.NotNullViolation),
                 })
        {
            var sql = $"""
                INSERT INTO chat."DocumentDailyFeedback" ("Day", "AssistantId", "ClientId", "CollectionId", "DocumentId", "DepartmentId", "Classification", "ThumbsUp", "ThumbsDown")
                VALUES (current_date, gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), {values})
                """;
            (await Should.ThrowAsync<PostgresException>(() => Sql.ExecuteAsync(api, sql))).SqlState.ShouldBe(state, sql);
        }

        // Only the api writes it (POST /v1/feedback and the import); the admin reads it for the report; the worker cannot see it.
        var admin = database.Connection(Admin);
        (await Sql.ScalarAsync<long>(admin, $"""SELECT count(*) FROM chat."DocumentDailyFeedback" WHERE "DocumentId" = '{document}'""")).ShouldBe(2);
        await Sql.ShouldBeDeniedAsync(admin, $"""
            INSERT INTO chat."DocumentDailyFeedback" ("Day", "AssistantId", "ClientId", "Classification", "CollectionId", "DocumentId", "DepartmentId")
            VALUES (current_date, gen_random_uuid(), gen_random_uuid(), 'internal', gen_random_uuid(), '{document}', gen_random_uuid())
            """);
        await Sql.ShouldBeDeniedAsync(admin, $"""UPDATE chat."DocumentDailyFeedback" SET "ThumbsDown" = 0 WHERE "DocumentId" = '{document}'""");
        await Sql.ShouldBeDeniedAsync(database.Connection(Worker), """SELECT count(*) FROM chat."DocumentDailyFeedback" """);
    }

    [Fact]
    public async Task The_document_feedback_table_has_no_column_for_a_question_an_answer_a_comment_or_who_asked()
    {
        // Decisions "Downvotes per document": counts per day and cited document; the only text column is the level.
        var columns = await Sql.ListAsync(database.Connection(Api), """
            SELECT column_name::text || ':' || data_type::text || ':' || coalesce(character_maximum_length::text, '')
            FROM information_schema.columns WHERE table_schema = 'chat' AND table_name = 'DocumentDailyFeedback'
            """);
        columns.ShouldBe(
        [
            "Day:date:", "AssistantId:uuid:", "ClientId:uuid:", "Classification:character varying:20", "CollectionId:uuid:", "DocumentId:uuid:",
            "DepartmentId:uuid:", "ThumbsUp:integer:", "ThumbsDown:integer:",
        ], ignoreOrder: true);
    }

    [Fact]
    public async Task The_chat_retention_step_removes_document_feedback_older_than_three_years_beside_the_earlier_tables()
    {
        // Data-model §8: 3 years, as the other daily tables, by chat.rag_retention_chat_step; the gaps' and outcomes' deletes
        // before it still run in the same calls.
        var assistant = Guid.NewGuid();
        await Sql.ExecuteAsync(database.Connection(Api), $"""
            INSERT INTO chat."DocumentDailyFeedback" ("Day", "AssistantId", "ClientId", "Classification", "CollectionId", "DocumentId", "DepartmentId", "ThumbsDown")
            VALUES ((now() AT TIME ZONE 'Europe/Stockholm')::date - interval '3 years 1 day', '{assistant}', gen_random_uuid(), 'internal', gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), 1),
                   ((now() AT TIME ZONE 'Europe/Stockholm')::date - interval '1 day', '{assistant}', gen_random_uuid(), 'internal', gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), 1);
            INSERT INTO chat."AssistantDailyGaps" ("Day", "AssistantId", "ClientId", "Classification", "Outcome", "CollectionId", "DocumentId", "DepartmentId", "Turns")
            VALUES ((now() AT TIME ZONE 'Europe/Stockholm')::date - interval '3 years 1 day', '{assistant}', gen_random_uuid(), 'internal', 'not-found', '{Guid.Empty}', '{Guid.Empty}', gen_random_uuid(), 1),
                   ((now() AT TIME ZONE 'Europe/Stockholm')::date - interval '1 day', '{assistant}', gen_random_uuid(), 'internal', 'not-found', '{Guid.Empty}', '{Guid.Empty}', gen_random_uuid(), 1);
            INSERT INTO chat."AssistantDailyOutcomes" ("Day", "AssistantId", "ClientId", "Classification", "Outcome", "DepartmentId", "Turns")
            VALUES ((now() AT TIME ZONE 'Europe/Stockholm')::date - interval '3 years 1 day', '{assistant}', gen_random_uuid(), 'internal', 'answered', gen_random_uuid(), 1),
                   ((now() AT TIME ZONE 'Europe/Stockholm')::date - interval '1 day', '{assistant}', gen_random_uuid(), 'internal', 'answered', gen_random_uuid(), 1);
            """);

        var admin = database.Connection(Admin);
        for (var i = 0; i < 100 && await Sql.ScalarAsync<long>(admin, "SELECT chat.rag_retention_chat_step(1000)") > 0; i++)
        {
        }

        foreach (var table in new[] { "DocumentDailyFeedback", "AssistantDailyGaps", "AssistantDailyOutcomes" })
        {
            (await Sql.ListAsync(admin, $"""SELECT ("Day" > current_date - 7)::text FROM chat."{table}" WHERE "AssistantId" = '{assistant}'"""))
                .ShouldBe(["true"], table);
        }
    }

    [Fact]
    public async Task The_daily_totals_keep_no_token_or_cost_column()
    {
        // Data-model §2.3 (B-38): tokens and cost have one home, AssistantDailyUsage; the totals row keeps the turn counters.
        var columns = await Sql.ListAsync(database.Connection(Api), """
            SELECT column_name::text FROM information_schema.columns WHERE table_schema = 'chat' AND table_name = 'AssistantDailyTotals'
            """);
        columns.ShouldContain("Messages");
        columns.ShouldNotContain(c => c.Contains("Tokens", StringComparison.Ordinal) || c.Contains("Cost", StringComparison.Ordinal));
    }

    /// <summary>The values after the key's ids that the checks refuse (level, step, department, the counters, the costs).</summary>
    private static IEnumerable<(string Values, string State)> Refused() =>
    [
        ("'secret', 'embed', gen_random_uuid(), 1, 10, 0, 0, 0, 0, 0", PostgresErrorCodes.CheckViolation),
        ("'public', '', gen_random_uuid(), 1, 10, 0, 0, 0, 0, 0", PostgresErrorCodes.CheckViolation),
        ("'public', 'embed', gen_random_uuid(), -1, 10, 0, 0, 0, 0, 0", PostgresErrorCodes.CheckViolation),
        ("'public', 'embed', gen_random_uuid(), 1, -10, 0, 0, 0, 0, 0", PostgresErrorCodes.CheckViolation),
        ("'public', 'embed', gen_random_uuid(), 1, 10, 11, 0, 0, 0, 0", PostgresErrorCodes.CheckViolation),
        ("'public', 'embed', gen_random_uuid(), 1, 10, 0, -1, 0, 0, 0", PostgresErrorCodes.CheckViolation),
        ("'public', 'embed', gen_random_uuid(), 1, 10, 0, 0, -0.1, 0, 0", PostgresErrorCodes.CheckViolation),
        ("'public', 'embed', gen_random_uuid(), 1, 10, 0, 0, 0, 2, 0", PostgresErrorCodes.CheckViolation),
        ("'public', 'embed', gen_random_uuid(), 1, 10, 0, 0, 0, 1, -0.1", PostgresErrorCodes.CheckViolation),
        ("'public', NULL, gen_random_uuid(), 1, 10, 0, 0, 0, 0, 0", PostgresErrorCodes.NotNullViolation),
        ("'public', repeat('x', 101), gen_random_uuid(), 1, 10, 0, 0, 0, 0, 0", PostgresErrorCodes.StringDataRightTruncation),
    ];
}
