using static Ume.Rag.TestKit.RagDatabase;

namespace Ume.Rag.IntegrationTests;

/// <summary>
/// P1b schema part 3 (P1b item 1): crawl freshness columns (#102), the parse-rule key and its outbox trigger (#134),
/// evaluation columns and the worker's read of the model mirror (#129, #130), <c>PiiFlaggedAt</c> (#175) and the
/// generated <c>DemoteRules</c> the api reads without <c>Settings</c>, and the chunks' content kind and large-table flag (#129). #114 is in <see cref="ServingSchemaTests"/>.
/// </summary>
[Collection(RagDatabaseGroup.Name)]
public sealed class P1bSchemaPart3Tests(RagDatabaseFixture database)
{
    [Fact]
    public async Task The_new_columns_exist_with_their_types_and_defaults()
    {
        var superuser = database.Connection(null);
        (await Sql.ListAsync(superuser, """
            SELECT table_schema || '.' || table_name || '.' || column_name || ':' || data_type || ':' || is_nullable || ':' || coalesce(column_default, '-')
            FROM information_schema.columns
            WHERE (table_name, column_name) IN (('CrawlRuns', 'Incremental'), ('CrawlFrontier', 'RemovalCheck'), ('ChunkSets', 'ParseRuleKey'),
                  ('ChunkSets', 'PiiFlaggedAt'), ('EvaluationRuns', 'Candidate'), ('EvaluationRuns', 'ThresholdProposal'), ('EvaluationSets', 'Round'),
                  ('Chunks', 'ContentKind'), ('Chunks', 'LargeTable'), ('CrawlFrontier', 'FetchUrl'))
            ORDER BY 1
            """)).ShouldBe([
            "content.ChunkSets.ParseRuleKey:character varying:YES:-",
            "content.ChunkSets.PiiFlaggedAt:timestamp with time zone:YES:-",
            "content.Chunks.ContentKind:character varying:NO:'text'::character varying",
            "content.Chunks.LargeTable:boolean:NO:false",
            "content.CrawlFrontier.FetchUrl:character varying:YES:-",
            "content.CrawlFrontier.RemovalCheck:boolean:NO:false",
            "content.CrawlRuns.Incremental:boolean:NO:false",
            "content.EvaluationRuns.Candidate:boolean:NO:false",
            "content.EvaluationRuns.ThresholdProposal:jsonb:YES:-",
            "content.EvaluationSets.Round:integer:NO:1",
        ]);
        (await Sql.ScalarAsync<string>(superuser, """
            SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'CK_Chunks_ContentKind'
            """)).ShouldContain("'text'::character varying, 'table'::character varying, 'list'::character varying");
    }

    [Fact]
    public async Task The_parse_rule_key_is_part_of_the_chunk_set_key_and_parse_profiles_feed_the_outbox()
    {
        var superuser = database.Connection(null);
        var index = await Sql.ScalarAsync<string>(superuser, "SELECT indexdef FROM pg_indexes WHERE indexname = 'IX_ChunkSets_Key'");
        index.ShouldContain("\"ContentHash\", \"ParseRuleKey\")");
        index.ShouldContain("NULLS NOT DISTINCT");
        (await Sql.ScalarAsync<string>(superuser, """SELECT pg_get_triggerdef(oid) FROM pg_trigger WHERE tgname = 'TR_ParseProfiles_ConfigChanged'"""))
            .ShouldBe("""CREATE TRIGGER "TR_ParseProfiles_ConfigChanged" AFTER INSERT OR DELETE OR UPDATE ON content."ParseProfiles" FOR EACH ROW EXECUTE FUNCTION config.rag_config_changed('false')""");
    }

    [Fact]
    public async Task Demote_rules_are_generated_from_the_settings_and_the_api_reads_them_but_not_the_settings()
    {
        var superuser = database.Connection(null);
        (await Sql.ScalarAsync<string>(superuser, """
            SELECT is_generated || ':' || generation_expression FROM information_schema.columns
            WHERE table_schema = 'config' AND table_name = 'Sources' AND column_name = 'DemoteRules'
            """)).ShouldBe("""ALWAYS:("Settings" -> 'demote'::text)""");
        var api = database.Connection(Api);
        await Sql.ExecuteAsync(api, """SELECT "DemoteRules" FROM config."Sources" LIMIT 1""");
        await Sql.ShouldBeDeniedAsync(api, """SELECT "Settings" FROM config."Sources" LIMIT 1""");
    }

    [Fact]
    public async Task The_worker_reads_the_gateway_model_mirror_and_of_assistants_only_id_and_state()
    {
        var worker = database.Connection(Worker);
        await Sql.ExecuteAsync(worker, """SELECT count(*) FROM config."GatewayModels" """);
        // The index-health check skips archived assistants (knowledge design §6.4; Decisions "M-2 open design calls").
        await Sql.ExecuteAsync(worker, """SELECT "Id", "State" FROM config."Assistants" """);
        await Sql.ShouldBeDeniedAsync(worker, """SELECT * FROM config."Assistants" """);
        await Sql.ShouldBeDeniedAsync(worker, """SELECT "Name" FROM config."Assistants" """);
        await Sql.ShouldBeDeniedAsync(worker, """INSERT INTO config."GatewayModels" DEFAULT VALUES""");
    }
}
