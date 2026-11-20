using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Kalla.Domain;
using Kalla.Domain.Entities;
using Kalla.MigrationService;
using Kalla.TestKit;
using static Kalla.TestKit.RagDatabase;

namespace Kalla.IntegrationTests;

/// <summary>
/// Schema part 1, ingestion (P1 item 2; data-model §2.1, §2.2, §3.2, §5, §9): the pieces EF does not express or that the
/// worker and retrieval rely on: per-profile HNSW and partial GIN indexes, the generated search vector, partial and
/// nulls-not-distinct unique keys, CHECK constraints, the outbox triggers, <c>xmin</c> concurrency (to-verify #22), and
/// the P2 tables and columns left out.
/// </summary>
[Collection(RagDatabaseGroup.Name)]
public sealed class IngestionSchemaTests(RagDatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task P2_tables_and_columns_are_absent()
    {
        var superuser = database.Connection(null);
        // data-model §13, §15 and the "(P2)" rows of §2.1, §2.2.
        foreach (var table in new[]
                 {
                     "content.CrawlClaims", "content.LeaderLeases", "config.CollectionAudiences",
                     "config.DepartmentSettings", "config.AssistantTestQuestions", "config.AssistantTemplates",
                     "config.SkillShares", "config.SkillVersionFiles", "config.ToolDepartmentGrants",
                 })
        {
            var parts = table.Split('.');
            (await Sql.ScalarAsync<string>(superuser, $"SELECT to_regclass('{parts[0]}.\"{parts[1]}\"')::text")).ShouldBeNull(table);
        }

        string[] columns = [.. await Sql.ListAsync(superuser, """
            SELECT table_schema || '.' || table_name || '.' || column_name FROM information_schema.columns
            WHERE table_schema IN ('config', 'content')
            """)];
        foreach (var column in new[]
                 {
                     "content.SiteProfiles.LastLearnedAt", "content.SiteProfiles.NextLearnAfter",
                     "content.SiteProfileVersions.LearnedFromRunId", "content.SiteProfileVersions.Sample",
                     "config.Settings.ChatShreddingEnabled", "config.Settings.Tools", "config.DataProtectionPolicies.ChatMasterKeyName",
                 })
        {
            columns.ShouldNotContain(column);
        }

        // P1 keys the frontier per source (connectors design §6.0).
        (await Sql.ListAsync(superuser, """
            SELECT a.attname::text FROM pg_index i JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = ANY (i.indkey)
            WHERE i.indrelid = 'content."CrawlFrontier"'::regclass AND i.indisprimary ORDER BY array_position(i.indkey, a.attnum)
            """)).ShouldBe(["Classification", "SourceId", "UrlHash"]);

        // P2 states are refused until P2 adds them.
        var level = await Sql.ScalarAsync<string>(superuser, """SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'CK_Sources_Type'""");
        level.ShouldNotContain("sharepoint");
        (await Sql.ScalarAsync<string>(superuser, """SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'CK_SiteProfileVersions_State'"""))
            .ShouldNotContain("proposed");
        (await Sql.ScalarAsync<string>(superuser, """SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'CK_DataProtectionPolicies_HistoryModes'"""))
            .ShouldNotContain("stored");
    }

    [Fact]
    public async Task Every_row_that_carries_a_level_checks_it()
    {
        var tables = await Sql.ListAsync(database.Connection(null), """
            SELECT c.table_schema || '.' || c.table_name FROM information_schema.columns c
            JOIN pg_class r ON r.relname = c.table_name JOIN pg_namespace n ON n.oid = r.relnamespace AND n.nspname = c.table_schema
            WHERE c.table_schema IN ('config', 'content') AND c.column_name = 'Classification' AND r.relkind IN ('r', 'p') AND NOT r.relispartition
              AND NOT EXISTS (SELECT 1 FROM pg_constraint k WHERE k.conrelid = r.oid AND k.contype = 'c'
                              AND pg_get_constraintdef(k.oid) LIKE '%"Classification"%''sensitive''%')
            """);
        tables.ShouldBeEmpty();

        var error = await Should.ThrowAsync<PostgresException>(() => Sql.ExecuteAsync(database.Connection(Worker), $"""
            INSERT INTO content."ParseObservations" ("Id", "Classification", "DocumentId", "Domain", "PathPattern", "Score", "ObservedAt")
            VALUES ('{Guid.CreateVersion7()}', 'secret', '{Guid.NewGuid()}', 'www.umea.se', '/', 0.5, now())
            """));
        error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task Each_new_config_table_bumps_the_config_version_with_its_security_flag()
    {
        // data-model §5: Collections, Sources and DataProtectionPolicies are security-relevant; the rest is not.
        var triggers = await Sql.ListAsync(database.Connection(null), """
            SELECT c.relname || ':' || encode(t.tgargs, 'escape') FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'config' AND NOT t.tgisinternal AND t.tgfoid = 'config.rag_config_changed'::regproc
            """);
        string[] expected =
        [
            "Settings:false\\000", "DataProtectionPolicies:true\\000", "EmbeddingProfiles:false\\000", "Collections:true\\000",
            "Sources:true\\000", "Pipelines:false\\000", "PipelineVersions:false\\000",
        ];
        foreach (var trigger in expected)
        {
            triggers.ShouldContain(trigger);
        }

        // Every config table has the trigger except the ones §5 names.
        (await Sql.ListAsync(database.Connection(null), """
            SELECT c.relname::text FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'config' AND c.relkind = 'r'
              AND c.relname NOT IN ('KeyLastUse', 'DataProtectionKeys', 'AdminSessions', 'SetupSessions', 'SetupTests', 'ConfigVersion', 'ConfigOutbox', '__EFMigrationsHistory')
              AND NOT EXISTS (SELECT 1 FROM pg_trigger t WHERE t.tgrelid = c.oid AND t.tgfoid = 'config.rag_config_changed'::regproc)
            """)).ShouldBeEmpty();

        await using var db = TestPostgres.Context(database.Connection(Admin));
        var profile = await AddProfileAsync(db, 3);
        var collection = await AddCollectionAsync(db, profile.Id);
        var outbox = await Sql.ListAsync(database.Connection(Admin), $"""
            SELECT "EntityType" || ':' || "Operation" || ':' || "SecurityRelevant" FROM config."ConfigOutbox"
            WHERE "EntityId" IN ('{profile.Id}', '{collection.Id}')
            """);
        outbox.ShouldBe(["EmbeddingProfiles:I:false", "Collections:I:true"], ignoreOrder: true);
    }

    [Fact]
    public async Task Every_editable_root_maps_xmin_as_its_concurrency_token()
    {
        // to-verify #22 (admin design §4.3): Npgsql EF Core 10.0.3 maps a uint row version to the xmin system column.
        await using var db = TestPostgres.Context(database.Connection(Admin));
        Type[] roots = [typeof(PlatformSettings), typeof(DataProtectionPolicy), typeof(EmbeddingProfile), typeof(Collection), typeof(Source), typeof(Pipeline), typeof(Client)];
        foreach (var root in roots)
        {
            var entity = db.Model.FindEntityType(root).ShouldNotBeNull(root.Name);
            var token = entity.GetProperties().Single(p => p.IsConcurrencyToken);
            token.GetColumnName().ShouldBe("xmin", root.Name);
            token.ClrType.ShouldBe(typeof(uint), root.Name);
            entity.FindProperty("UpdatedAt").ShouldNotBeNull(root.Name);
            entity.FindProperty("UpdatedBy").ShouldNotBeNull(root.Name);
        }
    }

    [Fact]
    public async Task A_stale_version_is_refused_and_ExecuteUpdate_bypasses_but_moves_it()
    {
        // to-verify #22: SaveChanges checks xmin; ExecuteUpdate does not check it, but its write changes xmin, so an
        // entity loaded before it can no longer be saved.
        await using var setup = TestPostgres.Context(database.Connection(Admin));
        var profile = await AddProfileAsync(setup, 3);
        var id = (await AddCollectionAsync(setup, profile.Id)).Id;

        await using var first = TestPostgres.Context(database.Connection(Admin));
        await using var second = TestPostgres.Context(database.Connection(Admin));
        var a = await first.Collections.SingleAsync(c => c.Id == id, Ct);
        var b = await second.Collections.SingleAsync(c => c.Id == id, Ct);
        a.Version.ShouldBe(b.Version);
        a.Version.ShouldNotBe(0u);

        a.Name = "Första";
        await first.SaveChangesAsync(Ct);
        a.Version.ShouldNotBe(b.Version);
        b.Name = "Andra";
        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync(Ct));

        await using var third = TestPostgres.Context(database.Connection(Admin));
        var c = await third.Collections.SingleAsync(x => x.Id == id, Ct);
        var before = c.Version;
        // A version check with ExecuteUpdate must be an explicit filter: the stale version matches no row.
        (await first.Collections.Where(x => x.Id == id && x.Version == b.Version)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Name, "Ingen"), Ct)).ShouldBe(0);
        (await first.Collections.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Name, "Tredje"), Ct)).ShouldBe(1);
        (await first.Collections.AsNoTracking().SingleAsync(x => x.Id == id, Ct)).Version.ShouldNotBe(before);
        c.Name = "Fjärde";
        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => third.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task Embedding_profiles_store_their_ceiling_and_allow_one_default()
    {
        await using var db = TestPostgres.Context(database.Connection(Admin));
        var profile = await AddProfileAsync(db, 1024, Residency.Eu);
        (await Sql.ScalarAsync<string>(database.Connection(Api), $"""SELECT "MaxResidency" FROM config."EmbeddingProfiles" WHERE "Id" = '{profile.Id}'""")).ShouldBe("Eu");

        var error = await Should.ThrowAsync<PostgresException>(() => Sql.ExecuteAsync(database.Connection(Admin), $"""
            UPDATE config."EmbeddingProfiles" SET "MaxResidency" = 'eu' WHERE "Id" = '{profile.Id}'
            """));
        error.ConstraintName.ShouldBe("CK_EmbeddingProfiles_MaxResidency");
        error = await Should.ThrowAsync<PostgresException>(() => Sql.ExecuteAsync(database.Connection(Admin), $"""
            UPDATE config."EmbeddingProfiles" SET "Dimensions" = 4001 WHERE "Id" = '{profile.Id}'
            """));
        error.ConstraintName.ShouldBe("CK_EmbeddingProfiles_Dimensions");

        // One default at most (partial unique index), in a transaction that leaves the shared database as it was.
        var other = await AddProfileAsync(db, 3);
        error = await Should.ThrowAsync<PostgresException>(() => Sql.ExecuteAsync(database.Connection(Admin), $"""
            BEGIN;
            UPDATE config."EmbeddingProfiles" SET "IsDefault" = true WHERE "Id" = '{profile.Id}';
            UPDATE config."EmbeddingProfiles" SET "IsDefault" = true WHERE "Id" = '{other.Id}';
            COMMIT;
            """));
        error.ConstraintName.ShouldBe("IX_EmbeddingProfiles_IsDefault");
    }

    [Fact]
    public async Task The_profile_index_is_created_once_on_the_half_precision_cast_and_serves_the_vector_query()
    {
        await using var db = TestPostgres.Context(database.Connection(Admin));
        var profile = await AddProfileAsync(db, 3);
        var worker = database.Connection(Worker);

        var wrongDims = await Should.ThrowAsync<PostgresException>(() => Sql.ExecuteAsync(worker, $"SELECT content.rag_ensure_profile_index('{profile.Id}', 4)"));
        wrongDims.MessageText.ShouldContain("has 3 dimensions");
        (await Sql.ScalarAsync<bool>(worker, $"SELECT content.rag_ensure_profile_index('{profile.Id}', 3)")).ShouldBeTrue();
        (await Sql.ScalarAsync<bool>(worker, $"SELECT content.rag_ensure_profile_index('{profile.Id}', 3)")).ShouldBeFalse();

        var name = "IX_Chunks_Embedding_" + profile.Id.ToString("N");
        var definition = await Sql.ScalarAsync<string>(database.Connection(null), $"SELECT indexdef FROM pg_indexes WHERE indexname = '{name}'");
        definition.ShouldContain("USING hnsw");
        definition.ShouldContain("halfvec(3)");
        definition.ShouldContain("halfvec_cosine_ops");
        definition.ShouldContain($"\"EmbeddingProfileId\" = '{profile.Id}'::uuid");
        definition.ShouldContain("\"DeletedAt\" IS NULL");

        var (documentId, setId, collectionId) = await AddChunkSetAsync(worker, profile.Id);
        for (var i = 0; i < 3; i++)
        {
            await InsertChunkAsync(worker, setId, documentId, collectionId, i, "Bygglov för altan", profile.Id, $"[{i + 1},1,0]");
        }

        // With sequential scans and sorts off, only an index that yields the distance order can serve the query.
        var plan = await ExplainAsync(Worker, ["enable_seqscan", "enable_sort"], $"""
            SELECT "Id" FROM content."Chunks"
            WHERE "EmbeddingProfileId" = '{profile.Id}' AND "DeletedAt" IS NULL
            ORDER BY ("Embedding"::halfvec(3)) <=> '[1,1,0]'::halfvec(3) LIMIT 10
            """);
        plan.ShouldContain(name);
    }

    [Fact]
    public async Task Chunks_get_a_weighted_search_vector_for_children_only_and_keyword_search_uses_the_partial_gin_index()
    {
        await using var db = TestPostgres.Context(database.Connection(Admin));
        var profile = await AddProfileAsync(db, 3);
        var worker = database.Connection(Worker);
        var (documentId, setId, collectionId) = await AddChunkSetAsync(worker, profile.Id);
        await InsertChunkAsync(worker, setId, documentId, collectionId, 0, "ansökan om bygglov bygglov ansökan", profile.Id, "[1,0,0]", heading: "Bygga > Bygglov");
        await InsertChunkAsync(worker, setId, documentId, collectionId, 1, "förälder", null, null, kind: "parent");

        (await Sql.ScalarAsync<string>(worker, $"""SELECT "SearchVector"::text FROM content."Chunks" WHERE "ChunkSetId" = '{setId}' AND "Ordinal" = 0"""))
            .ShouldContain("'bygg':1A");
        (await Sql.ScalarAsync<string>(worker, $"""SELECT "SearchVector"::text FROM content."Chunks" WHERE "ChunkSetId" = '{setId}' AND "Ordinal" = 1""")).ShouldBeNull();
        await Should.ThrowAsync<PostgresException>(() => Sql.ExecuteAsync(worker, $"""UPDATE content."Chunks" SET "SearchVector" = NULL WHERE "ChunkSetId" = '{setId}'"""));

        (await Sql.ScalarAsync<string>(database.Connection(null), """SELECT indexdef FROM pg_indexes WHERE indexname = 'IX_Chunks_SearchVector'"""))
            .ShouldBe("""CREATE INDEX "IX_Chunks_SearchVector" ON content."Chunks" USING gin ("SearchVector") WHERE ((("Kind")::text = 'child'::text) AND ("DeletedAt" IS NULL))""");
        // GIN serves only bitmap scans. With a few thousand other children analysed, the keyword condition is selective, and
        // with sequential and plain index scans off the planner shows that it can use the partial GIN index.
        await Sql.ExecuteAsync(worker, $"""
            INSERT INTO content."Chunks" ("Id", "ChunkSetId", "DocumentId", "CollectionId", "Classification", "Kind", "Ordinal", "Text", "SearchText", "OverlapChars", "TextHash", "TokenCount")
            SELECT gen_random_uuid(), '{setId}', '{documentId}', '{collectionId}', 'public', 'child', g, 'sida ' || g, 'avfall' || g || ' sophämtning', 0, int4send(g), 3
            FROM generate_series(10, 5000) g
            """);
        await Sql.ExecuteAsync(database.Connection(null), """ANALYZE content."Chunks" """);
        var plan = await ExplainAsync(Api, ["enable_seqscan", "enable_indexscan"], """
            SELECT "Id" FROM content."Chunks"
            WHERE "Kind" = 'child' AND "DeletedAt" IS NULL
              AND "SearchVector" @@ content.rag_or_query(to_tsvector('swedish', 'pelargon'))
            """); // a word no test writes: other tests' chunks in the shared database must not make it common
        plan.ShouldContain("IX_Chunks_SearchVector", customMessage: plan);

        // (ChunkSetId, Ordinal) is unique: section expansion reads neighbours by it.
        var duplicate = await Should.ThrowAsync<PostgresException>(() => InsertChunkAsync(worker, setId, documentId, collectionId, 0, "igen", profile.Id, "[1,0,0]"));
        duplicate.ConstraintName.ShouldBe("IX_Chunks_ChunkSetId_Ordinal");
    }

    [Fact]
    public async Task Document_and_chunk_set_keys_are_unique_with_a_null_site_profile_version()
    {
        await using var db = TestPostgres.Context(database.Connection(Admin));
        var profile = await AddProfileAsync(db, 3);
        var worker = database.Connection(Worker);
        var (documentId, setId, _) = await AddChunkSetAsync(worker, profile.Id);
        var key = await Sql.ScalarAsync<string>(worker, $"""
            SELECT "PipelineVersionId" || ',' || encode("ContentHash", 'hex') FROM content."ChunkSets" WHERE "Id" = '{setId}'
            """);
        var parts = key.Split(',');

        // engine design §14: one set per key, also when the page has no site profile version (nulls not distinct).
        var error = await Should.ThrowAsync<PostgresException>(() => Sql.ExecuteAsync(worker, $"""
            INSERT INTO content."ChunkSets" ("Id", "DocumentId", "Classification", "CollectionId", "PipelineVersionId", "EmbeddingProfileId", "ContentHash", "InputHash", "State", "PiiFlagged", "ChunkCount")
            VALUES ('{Guid.CreateVersion7()}', '{documentId}', 'public', '{Guid.NewGuid()}', '{parts[0]}', '{profile.Id}', '\x{parts[1]}', '\x00', 'writing', false, 0)
            """));
        error.ConstraintName.ShouldBe("IX_ChunkSets_Key");

        var identity = await Sql.ScalarAsync<string>(worker, $"""SELECT "SourceId" || ',' || encode("IdentityHash", 'hex') FROM content."Documents" WHERE "Id" = '{documentId}'""");
        var ids = identity.Split(',');
        error = await Should.ThrowAsync<PostgresException>(() => Sql.ExecuteAsync(worker, $"""
            INSERT INTO content."Documents" ("Id", "SourceId", "Classification", "IdentityKind", "CanonicalIdentity", "IdentityHash", "ContentHashVersion", "ConsecutiveFailures", "FirstSeenAt", "LastSeenAt")
            VALUES ('{Guid.CreateVersion7()}', '{ids[0]}', 'public', 'url', 'https://www.umea.se/igen', '\x{ids[1]}', 1, 0, now(), now())
            """));
        error.ConstraintName.ShouldBe("IX_Documents_SourceId_IdentityKind_IdentityHash");

        // Purging a document removes its sets and chunks (data-model §8: cascade from Documents).
        await Sql.ExecuteAsync(worker, $"""DELETE FROM content."Documents" WHERE "Id" = '{documentId}'""");
        (await Sql.ScalarAsync<long>(worker, $"""SELECT count(*) FROM content."ChunkSets" WHERE "Id" = '{setId}'""")).ShouldBe(0);
    }

    [Fact]
    public async Task Frontier_depth_is_never_negative_now_that_removal_checks_have_their_own_flag()
    {
        // #102: the removal check is CrawlFrontier.RemovalCheck; the Depth = -1 sentinel is gone, so a CHECK holds the rule.
        var definition = await Sql.ScalarAsync<string>(database.Connection(null), """
            SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conrelid = 'content."CrawlFrontier"'::regclass AND conname = 'CK_CrawlFrontier_Depth'
            """);
        definition.ShouldBe("""CHECK (("Depth" >= 0))""");
    }

    [Fact]
    public async Task Hot_crawl_tables_leave_room_for_hot_updates()
    {
        foreach (var table in new[] { "CrawlHosts", "CrawlRuns" })
        {
            var options = await Sql.ScalarAsync<string[]>(database.Connection(null), $"""SELECT reloptions FROM pg_class WHERE oid = 'content."{table}"'::regclass""");
            options.ShouldContain("fillfactor=50", table);
            options.ShouldContain("autovacuum_vacuum_scale_factor=0.02", table);
        }
    }

    [Fact]
    public async Task The_dev_seeder_adds_the_eu_profile_and_its_index_once()
    {
        await using var db = TestPostgres.Context(database.Connection(Migrator));
        await DevSeeder.SeedAsync(db, TimeProvider.System, Ct);
        await DevSeeder.SeedAsync(db, TimeProvider.System, Ct);

        var profile = await db.EmbeddingProfiles.AsNoTracking().SingleAsync(p => p.Id == DevSeeder.EmbeddingProfileId, Ct);
        profile.Dimensions.ShouldBe(1024);
        profile.MaxResidency.ShouldBe(Residency.Eu);
        profile.State.ShouldBe(EmbeddingProfileStates.Active);
        profile.CanaryText.ShouldBe(EmbeddingCanaryText.Default);
        (await Sql.ScalarAsync<string>(database.Connection(null), $"""
            SELECT indexdef FROM pg_indexes WHERE indexname = 'IX_Chunks_Embedding_{DevSeeder.EmbeddingProfileId:N}'
            """)).ShouldContain("halfvec(1024)");
    }

    [Fact]
    public async Task The_dev_seeder_adds_the_open_webui_client_and_its_key_once()
    {
        // repo-and-deployment §4 "Seed": client Open WebUI (dev) at ceiling internal with key kalla-sk- + dev-key (#164).
        await using var db = TestPostgres.Context(database.Connection(Migrator));
        var hasher = new Domain.Services.KeyHasher(System.Text.Encoding.UTF8.GetBytes(new string('p', 32)));
        var devKey = new string('D', 43);
        await DevSeeder.SeedDevClientAsync(db, hasher, devKey, "http://localhost:7410", TimeProvider.System, Ct);
        await DevSeeder.SeedDevClientAsync(db, hasher, devKey, "http://elsewhere.invalid", TimeProvider.System, Ct); // insert-if-absent

        var client = await db.Clients.AsNoTracking().Include(c => c.Keys).SingleAsync(c => c.Id == DevSeeder.DevClientId, Ct);
        (client.Name, client.Kind, client.ClassificationCeiling, client.StoresHistory).ShouldBe(
            (DevSeeder.DevClientName, ClientKinds.OpenWebUi, Classification.Internal, false));
        client.PortalUrl.ShouldBe("http://localhost:7410"); // the assistant page's "Open in" link (admin design §5.4 step 13)
        client.IsManagedByDeployment.ShouldBeTrue(); // the development deployment's own Open WebUI client (identity §5.2)
        var key = client.Keys.ShouldHaveSingleItem();
        hasher.Verify(Domain.Services.KeyHasher.SecretPrefix + devKey, key.KeyHash).ShouldBeTrue();
        key.Prefix.ShouldBe("kalla-sk-DDDD");
        await Should.ThrowAsync<InvalidOperationException>(() => DevSeeder.SeedDevClientAsync(db, hasher, "too-short", null, TimeProvider.System, Ct));
    }

    [Fact]
    public async Task Sources_stamp_their_creation_and_runs_start_their_counters_at_zero()
    {
        // #110: the scheduler counts a never-run source's first schedule from CreatedAt; writers may leave the counters out.
        await using var db = TestPostgres.Context(database.Connection(Admin));
        var collection = await AddCollectionAsync(db, (await AddProfileAsync(db, 3)).Id);
        var pipeline = new Pipeline { Name = "test-" + Guid.NewGuid().ToString("N"), TemplateId = "url-list", UpdatedAt = DateTimeOffset.UtcNow };
        db.Pipelines.Add(pipeline);
        await db.SaveChangesAsync(Ct);
        var before = await Sql.ScalarAsync<DateTime>(database.Connection(null), "SELECT now()");
        var source = new Source
        {
            CollectionId = collection.Id, Type = SourceTypes.UrlList, Name = "Test", Classification = Classification.Public,
            PipelineId = pipeline.Id, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Sources.Add(source);
        await db.SaveChangesAsync(Ct);
        (await db.Sources.AsNoTracking().Where(x => x.Id == source.Id).Select(x => x.CreatedAt).SingleAsync(Ct)).UtcDateTime
            .ShouldBeGreaterThanOrEqualTo(before.ToUniversalTime());

        var worker = database.Connection(Worker);
        var runId = Guid.CreateVersion7();
        await Sql.ExecuteAsync(worker, $"""
            INSERT INTO content."Runs" ("Id", "SourceId", "Classification", "Kind", "Trigger", "State")
            VALUES ('{runId}', '{source.Id}', 'public', '{RunKinds.Sync}', '{RunTriggers.Manual}', '{RunStates.Running}')
            """);
        (await Sql.ScalarAsync<string>(worker, $"""
            SELECT concat_ws(',', "Added", "Updated", "Unchanged", "Deleted", "Failed", "ProposedDeletions", "CostUsd"::int)
            FROM content."Runs" WHERE "Id" = '{runId}'
            """)).ShouldBe("0,0,0,0,0,0,0");

        // A continue run names the run it goes on from (#804); the retention step deleting that run leaves the reference null.
        var continueId = Guid.CreateVersion7();
        await Sql.ExecuteAsync(worker, $"""
            INSERT INTO content."Runs" ("Id", "SourceId", "Classification", "Kind", "Trigger", "State", "ContinuesRunId")
            VALUES ('{continueId}', '{source.Id}', 'public', '{RunKinds.Continue}', '{RunTriggers.Manual}', '{RunStates.Running}', '{runId}')
            """);
        await Sql.ExecuteAsync(database.Connection(null), $"""DELETE FROM content."Runs" WHERE "Id" = '{runId}'""");
        (await Sql.ScalarAsync<bool>(worker, $"""SELECT "ContinuesRunId" IS NULL FROM content."Runs" WHERE "Id" = '{continueId}'""")).ShouldBeTrue();
    }

    private static async Task<EmbeddingProfile> AddProfileAsync(Kalla.Infrastructure.Persistence.RagDbContext db, int dimensions, Residency ceiling = Residency.Eu)
    {
        var profile = new EmbeddingProfile
        {
            Name = "test-" + Guid.NewGuid().ToString("N"),
            GatewayModel = "bge-m3",
            Dimensions = dimensions,
            MaxInputTokens = 8192,
            MaxResidency = ceiling,
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = "test",
        };
        db.EmbeddingProfiles.Add(profile);
        await db.SaveChangesAsync(Ct);
        return profile;
    }

    private static async Task<Collection> AddCollectionAsync(Kalla.Infrastructure.Persistence.RagDbContext db, Guid profileId)
    {
        var collection = new Collection
        {
            Slug = "test-" + Guid.NewGuid().ToString("N"),
            Name = "Test",
            DepartmentId = Guid.NewGuid(),
            Classification = Classification.Public,
            EmbeddingProfileId = profileId,
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = "test",
        };
        db.Collections.Add(collection);
        await db.SaveChangesAsync(Ct);
        return collection;
    }

    /// <summary>A document with one writing chunk set, as the worker writes them (content references config by id only).</summary>
    private static async Task<(Guid DocumentId, Guid SetId, Guid CollectionId)> AddChunkSetAsync(string worker, Guid profileId)
    {
        var documentId = Guid.CreateVersion7();
        var setId = Guid.CreateVersion7();
        var collectionId = Guid.NewGuid();
        var identity = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray());
        await Sql.ExecuteAsync(worker, $"""
            INSERT INTO content."Documents" ("Id", "SourceId", "Classification", "IdentityKind", "CanonicalIdentity", "IdentityHash", "ContentHashVersion", "ConsecutiveFailures", "FirstSeenAt", "LastSeenAt")
            VALUES ('{documentId}', '{Guid.NewGuid()}', 'public', 'url', 'https://www.umea.se/{identity}', '\x{identity}', 1, 0, now(), now());
            INSERT INTO content."ChunkSets" ("Id", "DocumentId", "Classification", "CollectionId", "PipelineVersionId", "EmbeddingProfileId", "ContentHash", "InputHash", "State", "PiiFlagged", "ChunkCount")
            VALUES ('{setId}', '{documentId}', 'public', '{collectionId}', '{Guid.NewGuid()}', '{profileId}', '\x01', '\x02', 'live', false, 0);
            """);
        return (documentId, setId, collectionId);
    }

    private static Task InsertChunkAsync(string worker, Guid setId, Guid documentId, Guid collectionId, int ordinal, string text, Guid? profileId,
        string? embedding, string kind = "child", string? heading = null) => Sql.ExecuteAsync(worker, $"""
            INSERT INTO content."Chunks" ("Id", "ChunkSetId", "DocumentId", "CollectionId", "Classification", "Kind", "Ordinal", "HeadingPath",
                "Text", "SearchText", "OverlapChars", "TextHash", "TokenCount", "EmbeddingProfileId", "Embedding")
            VALUES ('{Guid.CreateVersion7()}', '{setId}', '{documentId}', '{collectionId}', 'public', '{kind}', {ordinal.ToString(CultureInfo.InvariantCulture)},
                {(heading is null ? "NULL" : $"'{heading}'")}, '{text}', '{text}', 0, '\x0{ordinal.ToString(CultureInfo.InvariantCulture)}', 5,
                {(profileId is null ? "NULL" : $"'{profileId}'")}, {(embedding is null ? "NULL" : $"'{embedding}'")})
            """);

    /// <summary>The plan of <paramref name="query"/> as <paramref name="role"/> with the given planner methods off, so a usable index shows.</summary>
    private async Task<string> ExplainAsync(string role, string[] off, string query)
    {
        await using var connection = new NpgsqlConnection(database.Connection(role));
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        foreach (var setting in off)
        {
            await using var command = new NpgsqlCommand($"SET LOCAL {setting} = off", connection, transaction);
            await command.ExecuteNonQueryAsync(Ct);
        }

        await using var explain = new NpgsqlCommand("EXPLAIN " + query, connection, transaction);
        await using var reader = await explain.ExecuteReaderAsync(Ct);
        var lines = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            lines.Add(reader.GetString(0));
        }

        return string.Join('\n', lines);
    }
}
