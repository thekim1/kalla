using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Kalla.Infrastructure.Jobs;
using Kalla.TestKit;
using Kalla.Worker.Jobs;

namespace Kalla.Worker.Tests;

/// <summary>
/// The worker's content retention (data-model §8, A-39): <c>content.rag_retention_content_step</c>, as
/// <c>ume_rag_worker</c>, removes only the rows past their age, one batch per table and call, and the run is audited as
/// <c>system:retention</c>. Each table gets a row just inside its rule and two just outside it (the older one goes first).
/// </summary>
[Collection(RagDatabaseGroup3.Name)]
public sealed class ContentRetentionTests(RagDatabaseFixture database) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await database.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Only_the_rows_past_their_age_go_one_batch_per_table_and_call_and_the_run_is_audited()
    {
        await DrainAsync();
        var seed = await SeedAsync();
        var worker = database.Connection(RagDatabase.Worker);

        // Batch size 1: one row per table (the six tables with an expired row), the oldest first.
        (await Sql.ScalarAsync<long>(worker, "SELECT content.rag_retention_content_step(1)")).ShouldBe(6);
        (await RemainingAsync(seed)).ShouldBe(new Remaining(
            "in,out-1", "awaiting,in,only,out-1,scheduled", "in,out-1", "in,out-1", "in,out-1", "2,3,4,5,6,7,8,9,10,11,12,13", "1,2"));

        var audited = await Sql.ScalarAsync<long>(database.Connection(null), """SELECT coalesce(max("Id"), 0) FROM audit."AuditLog" """);
        var result = await new ContentMaintenance(database.Database(RagDatabase.Worker)).DeleteExpiredContentAsync(Ct);

        result.ShouldBe(new ContentRetentionResult(6, Skipped: false));
        (await RemainingAsync(seed)).ShouldBe(new Remaining(
            "in", "awaiting,in,only,scheduled", "in", "in", "in", "3,4,5,6,7,8,9,10,11,12,13", "1,2"));
        // The expired runs' crawl and deletion-proposal rows went with them.
        (await Sql.ScalarAsync<long>(database.Connection(null), """
            SELECT (SELECT count(*) FROM content."CrawlRuns" WHERE "SourceId" = $1) + (SELECT count(*) FROM content."DeletionProposals" WHERE "SourceId" = $1)
            """, seed.Marker)).ShouldBe(0);
        (await Sql.ScalarAsync<string>(database.Connection(null), """
            SELECT string_agg("Actor" || ' ' || "Action" || ' ' || "EntityType" || ' ' || "Service" || ' ' || ("Details" ->> 'deleted'), ';')
            FROM audit."AuditLog" WHERE "Id" > $1 AND "Actor" = 'system:retention'
            """, audited)).ShouldBe("system:retention retention content rag-worker 6");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10001)]
    public async Task The_batch_size_is_bounded_inside_the_function(int batchSize)
    {
        var refused = await Should.ThrowAsync<PostgresException>(() =>
            Sql.ScalarAsync<long>(database.Connection(RagDatabase.Worker), $"SELECT content.rag_retention_content_step({batchSize})"));
        refused.MessageText.ShouldBe("batch_size must be between 1 and 10000");
    }

    [Theory]
    [InlineData(RagDatabase.Api)]
    [InlineData(RagDatabase.Admin)]
    public async Task Only_the_worker_may_run_the_step(string role)
    {
        var refused = await Should.ThrowAsync<PostgresException>(() =>
            Sql.ScalarAsync<long>(database.Connection(role), "SELECT content.rag_retention_content_step(1)"));
        refused.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task The_maintenance_run_calls_the_step_and_audits_it()
    {
        var profile = Guid.NewGuid();
        await Sql.ExecuteAsync(database.Connection(null), $$"""
            INSERT INTO content."EmbeddingCache" ("Classification", "EmbeddingProfileId", "TextHash", "LastUsedAt", "Embedding")
            VALUES ('internal', '{{profile}}', '\x01', now() - interval '31 days', '[1,0]'), ('internal', '{{profile}}', '\x02', now() - interval '1 day', '[1,0]')
            """);
        var audited = await Sql.ScalarAsync<long>(database.Connection(null), """SELECT coalesce(max("Id"), 0) FROM audit."AuditLog" """);
        var logs = new CapturingLoggerProvider();
        await using var worker = await WorkerHost.StartAsync(database, logs: logs);

        await worker.Services.GetRequiredService<MaintenanceService>().RunAsync(Ct);

        (await Sql.ScalarAsync<long>(database.Connection(null), """SELECT count(*) FROM content."EmbeddingCache" WHERE "EmbeddingProfileId" = $1""", profile))
            .ShouldBe(1);
        logs.Entries.ShouldContain(e => e.Message.StartsWith("Content retention removed", StringComparison.Ordinal));
        (await Sql.ScalarAsync<long>(database.Connection(null), """
            SELECT count(*) FROM audit."AuditLog" WHERE "Id" > $1 AND "Actor" = 'system:retention' AND "Service" = 'rag-worker'
            """, audited)).ShouldBe(1);
    }

    /// <summary>Runs the step until nothing is left, so other tests' expired rows in this database do not count.</summary>
    private async Task DrainAsync()
    {
        var worker = database.Connection(RagDatabase.Worker);
        for (var i = 0; i < 100 && await Sql.ScalarAsync<long>(worker, "SELECT content.rag_retention_content_step(10000)") > 0; i++)
        {
        }
    }

    /// <summary>
    /// Per table: <c>in</c> just inside the rule, <c>out-1</c> just outside, <c>out-2</c> further outside. Runs (source
    /// <see cref="Seed.Marker"/>): the newest applied sync run is <c>in</c>; older ones that stay are a run awaiting a deletion
    /// decision and the source's newest scheduled run; <c>only</c> is another source's single, old applied run. Site profile
    /// versions (labelled by number): profile one has 13, of which 1 and 2 are superseded and over a year old, 3 is superseded
    /// and just under a year old with 10 newer, 13 is applied; profile two has an old superseded version within its last 10.
    /// </summary>
    private async Task<Seed> SeedAsync()
    {
        var seed = new Seed(Guid.NewGuid(), Guid.NewGuid());
        var (m, other) = (seed.Marker, seed.Other);
        var out1 = Guid.NewGuid();
        var out2 = Guid.NewGuid();
        await Sql.ExecuteAsync(database.Connection(null), $$"""
            INSERT INTO content."CollectionDailyUsage" ("Day", "CollectionId", "Classification", "Step", "DepartmentId", "Calls")
            SELECT (now() AT TIME ZONE 'Europe/Stockholm')::date - v.age, '{{m}}', 'internal', v.label, gen_random_uuid(), 1
            FROM (VALUES (interval '3 years' - interval '1 day', 'in'), (interval '3 years 1 day', 'out-1'), (interval '3 years 2 days', 'out-2')) AS v(age, label);

            INSERT INTO content."Runs" ("Id", "Classification", "SourceId", "Kind", "Trigger", "State", "SettingsHashes", "Report", "ScheduledFor", "StartedAt", "FinishedAt")
            SELECT v.id, 'internal', v.source, 'sync', v.trigger, v.state, v.hashes::jsonb, jsonb_build_object('label', v.label), v.scheduled,
                   now() - v.age - interval '1 minute', now() - v.age
            FROM (VALUES
                (gen_random_uuid(), '{{m}}'::uuid, 'manual', 'succeeded', '{}', 'in', NULL::timestamptz, interval '13 months' - interval '1 hour'),
                ('{{out1}}'::uuid, '{{m}}'::uuid, 'manual', 'failed', NULL, 'out-1', NULL, interval '13 months 1 hour'),
                ('{{out2}}'::uuid, '{{m}}'::uuid, 'manual', 'failed', NULL, 'out-2', NULL, interval '13 months 2 hours'),
                (gen_random_uuid(), '{{m}}'::uuid, 'manual', 'awaiting-deletion-confirmation', '{}', 'awaiting', NULL, interval '2 years'),
                (gen_random_uuid(), '{{m}}'::uuid, 'schedule', 'failed', NULL, 'scheduled', now() - interval '2 years 1 hour', interval '2 years'),
                (gen_random_uuid(), '{{other}}'::uuid, 'manual', 'succeeded', '{}', 'only', NULL, interval '2 years')
            ) AS v(id, source, trigger, state, hashes, label, scheduled, age);
            INSERT INTO content."CrawlRuns" ("RunId", "Classification", "SourceId", "Admitted", "MaxPages", "MaxDepth", "LimitHit", "Seeds", "SeedsFailed", "Incremental", "Complete")
            VALUES ('{{out1}}', 'internal', '{{m}}', 1, 10, 2, false, 1, 0, false, true), ('{{out2}}', 'internal', '{{m}}', 1, 10, 2, false, 1, 0, false, true);
            INSERT INTO content."DeletionProposals" ("RunId", "DocumentId", "Classification", "SourceId", "Reason", "Exempt")
            VALUES ('{{out1}}', gen_random_uuid(), 'internal', '{{m}}', 'gone', false), ('{{out2}}', gen_random_uuid(), 'internal', '{{m}}', 'gone', false);

            INSERT INTO content."ParseObservations" ("Id", "Classification", "DocumentId", "Domain", "PathPattern", "Score", "ObservedAt")
            SELECT gen_random_uuid(), 'internal', gen_random_uuid(), '{{m}}', v.label, 0.5, now() - v.age
            FROM (VALUES (interval '180 days' - interval '1 hour', 'in'), (interval '180 days 1 hour', 'out-1'), (interval '180 days 2 hours', 'out-2')) AS v(age, label);

            INSERT INTO content."RetrievalStats" ("Day", "DocumentId", "TextHash", "CollectionId", "Classification", "Retrieved", "Cited")
            SELECT (now() AT TIME ZONE 'Europe/Stockholm')::date - v.age, gen_random_uuid(), convert_to(v.label, 'UTF8'), '{{m}}', 'internal', 1, 0
            FROM (VALUES (interval '13 months' - interval '1 day', 'in'), (interval '13 months 1 day', 'out-1'), (interval '13 months 2 days', 'out-2')) AS v(age, label);

            INSERT INTO content."EmbeddingCache" ("Classification", "EmbeddingProfileId", "TextHash", "LastUsedAt", "Embedding")
            SELECT 'internal', '{{m}}', convert_to(v.label, 'UTF8'), now() - v.age, '[1,0]'::vector
            FROM (VALUES (interval '30 days' - interval '1 hour', 'in'), (interval '30 days 1 hour', 'out-1'), (interval '30 days 2 hours', 'out-2')) AS v(age, label);

            INSERT INTO content."SiteProfiles" ("Id", "Classification", "Host", "UpdatedAt")
            VALUES ('{{m}}', 'internal', 'one-{{m}}.test', now()), ('{{other}}', 'internal', 'two-{{other}}.test', now());
            INSERT INTO content."SiteProfileVersions" ("Id", "SiteProfileId", "Number", "State", "Origin", "Profile", "CreatedBy", "CreatedAt")
            SELECT gen_random_uuid(), '{{m}}', n, CASE WHEN n = 13 THEN 'applied' ELSE 'superseded' END, 'manual', '{}', 'test',
                   now() - CASE n WHEN 1 THEN interval '1 year 2 hours' WHEN 2 THEN interval '1 year 1 hour'
                                  WHEN 3 THEN interval '1 year' - interval '1 hour' ELSE interval '1 day' END
            FROM generate_series(1, 13) AS n;
            INSERT INTO content."SiteProfileVersions" ("Id", "SiteProfileId", "Number", "State", "Origin", "Profile", "CreatedBy", "CreatedAt")
            VALUES (gen_random_uuid(), '{{other}}', 1, 'superseded', 'manual', '{}', 'test', now() - interval '2 years'),
                   (gen_random_uuid(), '{{other}}', 2, 'applied', 'manual', '{}', 'test', now() - interval '1 day');
            """);
        return seed;
    }

    /// <summary>The labels left per table, in order.</summary>
    private async Task<Remaining> RemainingAsync(Seed seed)
    {
        var owner = database.Connection(null);
        Task<string> Labels(string sql) => Sql.ScalarAsync<string>(owner, sql, seed.Marker);
        return new Remaining(
            await Labels("""SELECT string_agg("Step", ',' ORDER BY "Step") FROM content."CollectionDailyUsage" WHERE "CollectionId" = $1"""),
            await Sql.ScalarAsync<string>(owner, """
                SELECT string_agg("Report" ->> 'label', ',' ORDER BY "Report" ->> 'label') FROM content."Runs" WHERE "SourceId" IN ($1, $2)
                """, seed.Marker, seed.Other),
            await Labels("""SELECT string_agg("PathPattern", ',' ORDER BY "PathPattern") FROM content."ParseObservations" WHERE "Domain" = $1::text"""),
            await Labels("""
                SELECT string_agg(convert_from("TextHash", 'UTF8'), ',' ORDER BY convert_from("TextHash", 'UTF8')) FROM content."RetrievalStats" WHERE "CollectionId" = $1
                """),
            await Labels("""
                SELECT string_agg(convert_from("TextHash", 'UTF8'), ',' ORDER BY convert_from("TextHash", 'UTF8')) FROM content."EmbeddingCache" WHERE "EmbeddingProfileId" = $1
                """),
            await Labels("""SELECT string_agg("Number"::text, ',' ORDER BY "Number") FROM content."SiteProfileVersions" WHERE "SiteProfileId" = $1"""),
            await Sql.ScalarAsync<string>(owner, """
                SELECT string_agg("Number"::text, ',' ORDER BY "Number") FROM content."SiteProfileVersions" WHERE "SiteProfileId" = $1
                """, seed.Other));
    }

    private sealed record Seed(Guid Marker, Guid Other);

    private sealed record Remaining(
        string DailyUsage, string Runs, string ParseObservations, string RetrievalStats, string EmbeddingCache, string SiteProfileOne, string SiteProfileTwo);
}
