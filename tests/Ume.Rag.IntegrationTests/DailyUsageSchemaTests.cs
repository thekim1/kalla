using Npgsql;
using static Ume.Rag.TestKit.RagDatabase;

namespace Ume.Rag.IntegrationTests;

/// <summary>
/// The per-step usage tables (data-model §2.2, §2.3, §8; cost-attribution §5): <c>chat."AssistantDailyUsage"</c> written by
/// the api (and the admin's test panel), <c>content."CollectionDailyUsage"</c> by the worker; every column takes a value,
/// the checks refuse a negative counter, more estimated calls than calls, more cached than input tokens, an empty step and
/// an unknown level, the nightly chat step removes assistant usage older than 3 years, and the daily totals keep no token
/// or cost column (B-38).
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
