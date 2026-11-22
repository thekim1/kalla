using Microsoft.EntityFrameworkCore;
using Npgsql;
using Kalla.Domain;
using Kalla.Domain.Entities;
using Kalla.Infrastructure.Persistence;
using Kalla.TestKit;
using static Kalla.TestKit.RagDatabase;

namespace Kalla.IntegrationTests;

/// <summary>
/// Schema part 2, serving (P1 item 3; data-model §2.1, §2.2, §5, §12.3; weakness review §2): assistant versions with one
/// draft and one published version, the view of published versions, outbox triggers that skip drafts (to-verify #164,
/// test part), audiences, the gateway model mirror, the evaluation tables with variant and coverage fields, the retrieval
/// counters and their function, and the P2 tables, columns and values left out.
/// </summary>
[Collection(RagDatabaseGroup.Name)]
public sealed class ServingSchemaTests(RagDatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task P2_tables_columns_and_values_are_absent()
    {
        var superuser = database.Connection(null);
        // data-model §15 (weakness review §1): built in P2. AssistantTemplates came with B-54.
        foreach (var table in new[] { "AssistantTestQuestions", "AssistantCollections", "AssistantSkills" })
        {
            (await Sql.ScalarAsync<string>(superuser, $"SELECT to_regclass('config.\"{table}\"')::text")).ShouldBeNull(table);
        }

        string[] columns = [.. await Sql.ListAsync(superuser, """
            SELECT table_name || '.' || column_name FROM information_schema.columns WHERE table_schema IN ('config', 'content')
            """)];
        foreach (var column in new[]
                 {
                     "Assistants.Mode", "AssistantVersions.Skills", "AssistantVersions.TestRunId", "EvaluationQuestions.SkillVersionId",
                     "EvaluationRuns.Faithfulness", "EvaluationRuns.CitationPrecision", "EvaluationResults.Faithfulness",
                     "EvaluationResults.CitationPrecision", "EvaluationResults.Verdict", "EvaluationResults.Question",
                     "EvaluationResults.Answer", "EvaluationResults.JudgeNote",
                 })
        {
            columns.ShouldNotContain(column);
        }

        // P2 values are refused until P2 adds them; P1 assistants keep history mode none (weakness review §4).
        (await ConstraintAsync("CK_EvaluationRuns_Kind")).ShouldNotContain("answers");
        (await ConstraintAsync("CK_EvaluationRuns_Kind")).ShouldNotContain("assistant-tests");
        (await ConstraintAsync("CK_EvaluationRuns_Kind")).ShouldNotContain("skill-gate");
        (await ConstraintAsync("CK_EvaluationQuestions_Layer")).ShouldNotContain("feedback");
        (await ConstraintAsync("CK_EvaluationQuestions_Layer")).ShouldNotContain("skill");
        (await ConstraintAsync("CK_AssistantAudiences_Kind")).ShouldNotContain("department");
        (await ConstraintAsync("CK_Assistants_HistoryMode")).ShouldNotContain("stored");
        // The partial index of assistant-tests runs comes with that kind.
        (await Sql.ScalarAsync<long>(superuser, """
            SELECT count(*) FROM pg_indexes WHERE schemaname = 'content' AND tablename = 'EvaluationRuns' AND indexdef LIKE '%SubjectId%'
            """)).ShouldBe(0);
    }

    [Fact]
    public async Task Serving_config_tables_bump_the_config_version_and_version_triggers_skip_drafts()
    {
        var triggers = await Sql.ListAsync(database.Connection(null), """
            SELECT t.tgname || ':' || encode(t.tgargs, 'escape') || ':' || CASE WHEN pg_get_triggerdef(t.oid) LIKE '%WHEN%' THEN 'when' ELSE 'all' END
            FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'config' AND NOT t.tgisinternal AND t.tgfoid = 'config.rag_config_changed'::regproc
              AND c.relname IN ('Assistants', 'AssistantVersions', 'AssistantAudiences', 'GatewayModels', 'CollectionShares')
            """);
        triggers.ShouldBe(
        [
            "TR_Assistants_ConfigChanged:true\\000:all", "TR_AssistantVersions_ConfigChanged:true\\000:when",
            "TR_AssistantVersions_ConfigDeleted:true\\000:when", "TR_AssistantAudiences_ConfigChanged:true\\000:all",
            "TR_GatewayModels_ConfigChanged:false\\000:all", "TR_CollectionShares_ConfigChanged:true\\000:all",
        ], ignoreOrder: true);
        var superuser = database.Connection(null);
        (await Sql.ScalarAsync<string>(superuser, """SELECT pg_get_triggerdef(oid) FROM pg_trigger WHERE tgname = 'TR_AssistantVersions_ConfigChanged'"""))
            .ShouldContain("AFTER INSERT OR UPDATE ON config.\"AssistantVersions\" FOR EACH ROW WHEN (((new.\"State\")::text <> 'draft'::text))");
        (await Sql.ScalarAsync<string>(superuser, """SELECT pg_get_triggerdef(oid) FROM pg_trigger WHERE tgname = 'TR_AssistantVersions_ConfigDeleted'"""))
            .ShouldContain("AFTER DELETE ON config.\"AssistantVersions\" FOR EACH ROW WHEN (((old.\"State\")::text <> 'draft'::text))");

        await using var db = TestPostgres.Context(database.Connection(Admin));
        db.GatewayModels.Add(new GatewayModel { Name = "test-" + Guid.NewGuid().ToString("N"), Residencies = ["Eu"], SyncedAt = DateTimeOffset.UtcNow });
        var before = await ConfigVersionAsync();
        await db.SaveChangesAsync(Ct);
        (await ConfigVersionAsync()).ShouldBe(before + 1);
        (await OutboxAsync(before)).ShouldBe(["GatewayModels:I:False:"]);
    }

    [Fact]
    public async Task Draft_saves_do_not_bump_the_config_version_and_publish_does()
    {
        // to-verify #164 (test part), data-model §5: editing a draft never bumps the version; publish bumps it once.
        await using var db = TestPostgres.Context(database.Connection(Admin));
        var assistant = await AddAssistantAsync(db);
        var draft = await AddDraftAsync(db, assistant);

        var before = await ConfigVersionAsync();
        draft.SystemPrompt = "Svara kort.";
        draft.CollectionLinks = [CollectionLink.Whole(Guid.NewGuid())];
        draft.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(Ct);
        draft.SystemPrompt = "Svara kort och sakligt.";
        await db.SaveChangesAsync(Ct);
        (await ConfigVersionAsync()).ShouldBe(before);
        (await OutboxAsync(before)).ShouldBeEmpty();

        // Publish: one config transaction (data-model §2.1).
        await using (var transaction = await db.Database.BeginTransactionAsync(Ct))
        {
            Publish(assistant, draft);
            await db.SaveChangesAsync(Ct);
            await transaction.CommitAsync(Ct);
        }

        (await ConfigVersionAsync()).ShouldBe(before + 1);
        (await OutboxAsync(before)).ShouldBe([$"AssistantVersions:U:True:{draft.Id}", $"Assistants:U:True:{assistant.Id}"], ignoreOrder: true);

        // A new draft (Number + 1) bumps only through the assistant's DraftVersionId; discarding it likewise. Deleting a
        // draft row on its own does not fire the version trigger.
        var second = await AddDraftAsync(db, assistant);
        var afterDraft = await ConfigVersionAsync();
        (await OutboxAsync(afterDraft - 1)).ShouldBe([$"Assistants:U:True:{assistant.Id}"]);
        assistant.DraftVersionId = null;
        await db.SaveChangesAsync(Ct);
        var afterPointer = await ConfigVersionAsync();
        db.AssistantVersions.Remove(second);
        await db.SaveChangesAsync(Ct);
        (await ConfigVersionAsync()).ShouldBe(afterPointer);
    }

    [Fact]
    public async Task Collections_carry_a_sharing_level_and_contact_and_shares_bump_the_config_version_as_security_relevant()
    {
        // P1b item 33, data-model §2.1, §5.
        await using var db = TestPostgres.Context(database.Connection(Admin));
        var now = DateTimeOffset.UtcNow;
        var profile = new EmbeddingProfile { Name = "p-" + Guid.NewGuid().ToString("N"), GatewayModel = "bge-m3", Dimensions = 3, MaxInputTokens = 10, MaxResidency = Residency.Eu, UpdatedAt = now };
        var owner = new Department { Id = Guid.CreateVersion7(), Name = "Ägare", CostCenterCode = "cc-" + Guid.NewGuid().ToString("N"), SyncedAt = now };
        var reader = new Department { Id = Guid.CreateVersion7(), Name = "Läsare", CostCenterCode = "cc-" + Guid.NewGuid().ToString("N"), SyncedAt = now };
        db.AddRange(profile, owner, reader);
        await db.SaveChangesAsync(Ct);
        var collection = new Collection
        {
            Slug = "c-" + Guid.NewGuid().ToString("N"), Name = "Delad", DepartmentId = owner.Id, Classification = Classification.Public,
            EmbeddingProfileId = profile.Id, ContactName = "Kim Kontakt", ContactEmail = "kunskap@umea.se", UpdatedAt = now,
        };
        db.Collections.Add(collection);
        await db.SaveChangesAsync(Ct);

        var admin = database.Connection(Admin);
        (await Sql.ScalarAsync<string>(admin, $"""SELECT "SharedWith" || ':' || "ContactName" || ':' || "ContactEmail" FROM config."Collections" WHERE "Id" = '{collection.Id}'"""))
            .ShouldBe("department:Kim Kontakt:kunskap@umea.se");
        await Sql.ExecuteAsync(admin, $"""UPDATE config."Collections" SET "SharedWith" = 'municipality' WHERE "Id" = '{collection.Id}'""");
        var error = await Should.ThrowAsync<PostgresException>(() => Sql.ExecuteAsync(admin, $"""UPDATE config."Collections" SET "SharedWith" = 'everyone' WHERE "Id" = '{collection.Id}'"""));
        error.ConstraintName.ShouldBe("CK_Collections_SharedWith");

        var before = await ConfigVersionAsync();
        db.CollectionShares.Add(new CollectionShare { CollectionId = collection.Id, DepartmentId = reader.Id, SharedBy = "test", SharedAt = now });
        await db.SaveChangesAsync(Ct);
        (await ConfigVersionAsync()).ShouldBe(before + 1);
        (await OutboxAsync(before)).ShouldBe(["CollectionShares:I:True:"]);

        // One share per (collection, department); a share names a mirrored department and goes with its collection.
        error = await Should.ThrowAsync<PostgresException>(() => Sql.ExecuteAsync(admin, $"""
            INSERT INTO config."CollectionShares" ("CollectionId", "DepartmentId", "SharedBy", "SharedAt") VALUES ('{collection.Id}', '{reader.Id}', 'test', now())
            """));
        error.ConstraintName.ShouldBe("PK_CollectionShares");
        error = await Should.ThrowAsync<PostgresException>(() => Sql.ExecuteAsync(admin, $"""
            INSERT INTO config."CollectionShares" ("CollectionId", "DepartmentId", "SharedBy", "SharedAt") VALUES ('{collection.Id}', '{Guid.NewGuid()}', 'test', now())
            """));
        error.ConstraintName.ShouldBe("FK_CollectionShares_Departments_DepartmentId");
        await Sql.ExecuteAsync(admin, $"""DELETE FROM config."Collections" WHERE "Id" = '{collection.Id}'""");
        (await Sql.ScalarAsync<long>(admin, $"""SELECT count(*) FROM config."CollectionShares" WHERE "CollectionId" = '{collection.Id}'""")).ShouldBe(0);
    }

    [Fact]
    public async Task Each_assistant_has_at_most_one_draft_and_one_published_version()
    {
        await using var db = TestPostgres.Context(database.Connection(Admin));
        var assistant = await AddAssistantAsync(db);
        var draft = await AddDraftAsync(db, assistant);
        Publish(assistant, draft);
        await db.SaveChangesAsync(Ct);
        await AddDraftAsync(db, assistant);

        var admin = database.Connection(Admin);
        var error = await Should.ThrowAsync<PostgresException>(() => Sql.ExecuteAsync(admin, InsertVersion(assistant.Id, 3, "draft")));
        error.ConstraintName.ShouldBe("IX_AssistantVersions_Draft");
        error = await Should.ThrowAsync<PostgresException>(() => Sql.ExecuteAsync(admin, InsertVersion(assistant.Id, 3, "published")));
        error.ConstraintName.ShouldBe("IX_AssistantVersions_Published");
        error = await Should.ThrowAsync<PostgresException>(() => Sql.ExecuteAsync(admin, InsertVersion(assistant.Id, 1, "superseded")));
        error.ConstraintName.ShouldBe("IX_AssistantVersions_AssistantId_Number");
        // Superseded versions are many.
        await Sql.ExecuteAsync(admin, InsertVersion(assistant.Id, 3, "superseded"));
        await Sql.ExecuteAsync(admin, InsertVersion(assistant.Id, 4, "superseded"));

        // A version that was published has its model and stamps; a draft may lack the model.
        error = await Should.ThrowAsync<PostgresException>(() => Sql.ExecuteAsync(admin, $"""
            UPDATE config."AssistantVersions" SET "GatewayModel" = NULL WHERE "Id" = '{draft.Id}'
            """));
        error.ConstraintName.ShouldBe("CK_AssistantVersions_Published");
        // At most 20 collection links (data-model §2.1), checked in the database.
        string Links(int count) => $"""
            UPDATE config."AssistantVersions"
               SET "CollectionLinks" = (SELECT jsonb_agg(jsonb_build_object('collectionId', gen_random_uuid(), 'tags', '["bygglov"]'::jsonb)) FROM generate_series(1, {count}))
             WHERE "Id" = '{draft.Id}'
            """;
        await Sql.ExecuteAsync(admin, Links(20));
        error = await Should.ThrowAsync<PostgresException>(() => Sql.ExecuteAsync(admin, Links(21)));
        error.ConstraintName.ShouldBe("CK_AssistantVersions_CollectionLinks");

        // A pointer reaches only a version of the same assistant.
        var other = await AddAssistantAsync(db);
        error = await Should.ThrowAsync<PostgresException>(() => Sql.ExecuteAsync(admin, $"""
            UPDATE config."Assistants" SET "PublishedVersionId" = '{draft.Id}' WHERE "Id" = '{other.Id}'
            """));
        error.ConstraintName.ShouldBe("FK_Assistants_AssistantVersions_PublishedVersionId");
        error = await Should.ThrowAsync<PostgresException>(() => Sql.ExecuteAsync(admin, $"""
            UPDATE config."Assistants" SET "DraftVersionId" = '{assistant.DraftVersionId}' WHERE "Id" = '{other.Id}'
            """));
        error.ConstraintName.ShouldBe("FK_Assistants_AssistantVersions_DraftVersionId");
        // The version a pointer names cannot be deleted under it.
        error = await Should.ThrowAsync<PostgresException>(() => Sql.ExecuteAsync(admin, $"""DELETE FROM config."AssistantVersions" WHERE "Id" = '{draft.Id}'"""));
        error.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);

        // Deleting the assistant takes its versions and audiences with it.
        db.AssistantAudiences.Add(new AssistantAudience { AssistantId = assistant.Id, SubjectId = Guid.NewGuid() });
        await db.SaveChangesAsync(Ct);
        await Sql.ExecuteAsync(admin, $"""DELETE FROM config."Assistants" WHERE "Id" = '{assistant.Id}'""");
        (await Sql.ScalarAsync<long>(admin, $"""SELECT count(*) FROM config."AssistantVersions" WHERE "AssistantId" = '{assistant.Id}'""")).ShouldBe(0);
        (await Sql.ScalarAsync<long>(admin, $"""SELECT count(*) FROM config."AssistantAudiences" WHERE "AssistantId" = '{assistant.Id}'""")).ShouldBe(0);
    }

    [Fact]
    public async Task The_view_returns_only_the_published_version_of_each_assistant()
    {
        await using var db = TestPostgres.Context(database.Connection(Admin));
        var assistant = await AddAssistantAsync(db);
        var first = await AddDraftAsync(db, assistant);
        var api = database.Connection(Api);
        (await ViewAsync(api, assistant.Id)).ShouldBeEmpty();

        Publish(assistant, first);
        await db.SaveChangesAsync(Ct);
        var draft = await AddDraftAsync(db, assistant);
        draft.SystemPrompt = "Utkast som inte får nå chatten.";
        await db.SaveChangesAsync(Ct);
        (await ViewAsync(api, assistant.Id)).ShouldBe([$"{first.Id}:1:published"]);
        (await Sql.ScalarAsync<string>(api, $"""SELECT "SystemPrompt" FROM config."PublishedAssistantVersions" WHERE "AssistantId" = '{assistant.Id}'"""))
            .ShouldBe(first.SystemPrompt);

        // Publishing the draft supersedes the first version and moves the view to the second.
        first.State = AssistantVersionStates.Superseded;
        await using (var transaction = await db.Database.BeginTransactionAsync(Ct))
        {
            await db.SaveChangesAsync(Ct);
            Publish(assistant, draft);
            await db.SaveChangesAsync(Ct);
            await transaction.CommitAsync(Ct);
        }

        (await ViewAsync(api, assistant.Id)).ShouldBe([$"{draft.Id}:2:published"]);

        // The pointer and the state must agree: a pointer at a superseded version serves nothing.
        await Sql.ExecuteAsync(database.Connection(Admin), $"""UPDATE config."Assistants" SET "PublishedVersionId" = '{first.Id}' WHERE "Id" = '{assistant.Id}'""");
        (await ViewAsync(api, assistant.Id)).ShouldBeEmpty();

        // The view's columns are the version's (data-model §2.1), and it is owned by the migrator.
        (await Sql.ScalarAsync<string>(database.Connection(null), """
            SELECT pg_get_userbyid(c.relowner) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'config' AND c.relname = 'PublishedAssistantVersions' AND c.relkind = 'v'
            """)).ShouldBe(Migrator);
        var viewColumns = await ColumnsAsync("PublishedAssistantVersions");
        viewColumns.ShouldBe(await ColumnsAsync("AssistantVersions"));
    }

    [Fact]
    public async Task Assistants_and_versions_map_their_change_stamps_and_a_draft_save_leaves_the_assistant_alone()
    {
        await using var db = TestPostgres.Context(database.Connection(Admin));
        foreach (var root in new[] { typeof(Assistant), typeof(AssistantVersion) })
        {
            var entity = db.Model.FindEntityType(root).ShouldNotBeNull(root.Name);
            var token = entity.GetProperties().Single(p => p.IsConcurrencyToken);
            token.GetColumnName().ShouldBe("xmin", root.Name);
            entity.FindProperty("UpdatedAt").ShouldNotBeNull(root.Name);
            entity.FindProperty("UpdatedBy").ShouldNotBeNull(root.Name);
        }

        var assistant = await AddAssistantAsync(db);
        var draft = await AddDraftAsync(db, assistant);
        var assistantVersion = assistant.Version;

        // admin design §4.3: the draft's own version token; a stale draft save is refused, the assistant is untouched.
        await using var other = TestPostgres.Context(database.Connection(Admin));
        var stale = await other.AssistantVersions.SingleAsync(v => v.Id == draft.Id, Ct);
        draft.SystemPrompt = "Första";
        await db.SaveChangesAsync(Ct);
        stale.SystemPrompt = "Andra";
        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => other.SaveChangesAsync(Ct));
        (await other.Assistants.AsNoTracking().SingleAsync(a => a.Id == assistant.Id, Ct)).Version.ShouldBe(assistantVersion);
    }

    [Fact]
    public async Task Audiences_are_unique_per_subject_and_p1_accepts_clients_only()
    {
        await using var db = TestPostgres.Context(database.Connection(Admin));
        var assistant = await AddAssistantAsync(db);
        var client = Guid.NewGuid();
        var admin = database.Connection(Admin);
        await Sql.ExecuteAsync(admin, InsertAudience(assistant.Id, "client", $"'{client}'", "NULL"));

        var error = await Should.ThrowAsync<PostgresException>(() => Sql.ExecuteAsync(admin, InsertAudience(assistant.Id, "client", $"'{client}'", "NULL")));
        error.ConstraintName.ShouldBe("IX_AssistantAudiences_Subject");
        error = await Should.ThrowAsync<PostgresException>(() => Sql.ExecuteAsync(admin, InsertAudience(assistant.Id, "department", $"'{Guid.NewGuid()}'", "NULL")));
        error.ConstraintName.ShouldBe("CK_AssistantAudiences_Kind");
        error = await Should.ThrowAsync<PostgresException>(() => Sql.ExecuteAsync(admin, InsertAudience(assistant.Id, "client", "NULL", "NULL")));
        error.ConstraintName.ShouldBe("CK_AssistantAudiences_Subject");
        error = await Should.ThrowAsync<PostgresException>(() => Sql.ExecuteAsync(admin, InsertAudience(assistant.Id, "client", $"'{Guid.NewGuid()}'", "'grupp'")));
        error.ConstraintName.ShouldBe("CK_AssistantAudiences_Subject");
        error = await Should.ThrowAsync<PostgresException>(() => Sql.ExecuteAsync(admin, InsertAudience(Guid.NewGuid(), "client", $"'{client}'", "NULL")));
        error.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);

        // The assistant's own CHECKs.
        error = await Should.ThrowAsync<PostgresException>(() => Sql.ExecuteAsync(admin, $"""UPDATE config."Assistants" SET "RetentionDays" = 91 WHERE "Id" = '{assistant.Id}'"""));
        error.ConstraintName.ShouldBe("CK_Assistants_RetentionDays");
        error = await Should.ThrowAsync<PostgresException>(() => Sql.ExecuteAsync(admin, $"""UPDATE config."Assistants" SET "HistoryMode" = 'stored' WHERE "Id" = '{assistant.Id}'"""));
        error.ConstraintName.ShouldBe("CK_Assistants_HistoryMode");
        error = await Should.ThrowAsync<PostgresException>(() => Sql.ExecuteAsync(admin, $"""UPDATE config."Assistants" SET "State" = 'deleted' WHERE "Id" = '{assistant.Id}'"""));
        error.ConstraintName.ShouldBe("CK_Assistants_State");
        var other = await AddAssistantAsync(db);
        error = await Should.ThrowAsync<PostgresException>(() => Sql.ExecuteAsync(admin, $"""UPDATE config."Assistants" SET "Slug" = '{other.Slug}' WHERE "Id" = '{assistant.Id}'"""));
        error.ConstraintName.ShouldBe("IX_Assistants_Slug");
    }

    [Fact]
    public async Task Gateway_models_carry_known_residencies()
    {
        var admin = database.Connection(Admin);
        var name = "route-" + Guid.NewGuid().ToString("N");
        await Sql.ExecuteAsync(admin, $"""
            INSERT INTO config."GatewayModels" ("Name", "Kind", "Residencies", "Source", "SupportsTools", "SupportsToolsSource")
            VALUES ('{name}', 'route', ARRAY['OnPrem', 'Eu'], 'manual', false, 'manual')
            """);
        (await Sql.ScalarAsync<string[]>(database.Connection(Api), $"""SELECT "Residencies" FROM config."GatewayModels" WHERE "Name" = '{name}'""")).ShouldBe(["OnPrem", "Eu"]);

        foreach (var (set, constraint) in new[]
                 {
                     ("\"Residencies\" = ARRAY['eu']", "CK_GatewayModels_Residencies"),
                     ("\"Residencies\" = ARRAY[]::varchar(10)[]", "CK_GatewayModels_Residencies"),
                     ("\"Kind\" = 'alias'", "CK_GatewayModels_Kind"),
                     ("\"Source\" = 'gateway'", "CK_GatewayModels_Source"),
                     ("\"ContextWindow\" = 0", "CK_GatewayModels_Limits"),
                     ("\"MaxOutputTokens\" = 0", "CK_GatewayModels_Limits"),
                     ("\"ContextWindow\" = 1000, \"MaxOutputTokens\" = 1001", "CK_GatewayModels_Limits"), // the output within the window
                 })
        {
            var error = await Should.ThrowAsync<PostgresException>(() => Sql.ExecuteAsync(admin, $"""UPDATE config."GatewayModels" SET {set} WHERE "Name" = '{name}'"""));
            error.ConstraintName.ShouldBe(constraint, set);
        }
    }

    [Fact]
    public async Task Evaluation_runs_record_variant_and_coverage_and_go_with_their_set()
    {
        var worker = database.Connection(Worker);
        var setId = Guid.CreateVersion7();
        var questionId = Guid.CreateVersion7();
        var unanswerable = Guid.CreateVersion7();
        var runId = Guid.CreateVersion7();
        await Sql.ExecuteAsync(worker, $"""
            INSERT INTO content."EvaluationSets" ("Id", "CollectionId", "Classification") VALUES ('{setId}', '{Guid.NewGuid()}', 'public');
            INSERT INTO content."EvaluationQuestions" ("Id", "SetId", "Classification", "Layer", "Question", "ExpectedDocumentIds", "State")
            VALUES ('{questionId}', '{setId}', 'public', 'human', 'Hur söker jag bygglov?', ARRAY['{Guid.NewGuid()}'::uuid], 'active'),
                   ('{unanswerable}', '{setId}', 'public', 'human', 'Vem vann melodifestivalen?', '{"{}"}', 'active');
            INSERT INTO content."EvaluationRuns" ("Id", "SetId", "Classification", "Kind", "K", "Reranked", "Variant", "StartedAt")
            VALUES ('{runId}', '{setId}', 'public', 'on-demand', 10, false, 'hybrid-no-decompounder', now());
            INSERT INTO content."EvaluationResults" ("RunId", "QuestionId", "Classification", "ExpectedRank", "Hit", "Coverage", "BestDistance", "BestKeywordScore")
            VALUES ('{runId}', '{questionId}', 'public', 2, true, 'ok', 0.31, 0.08),
                   ('{runId}', '{unanswerable}', 'public', NULL, NULL, 'low', 0.74, NULL);
            """);
        // The default variant is hybrid (data-model §2.2).
        var other = Guid.CreateVersion7();
        await Sql.ExecuteAsync(worker, $"""
            INSERT INTO content."EvaluationRuns" ("Id", "SetId", "Classification", "Kind", "K", "Reranked", "StartedAt")
            VALUES ('{other}', '{setId}', 'public', 'nightly', 10, false, now())
            """);
        (await Sql.ScalarAsync<string>(worker, $"""SELECT "Variant" FROM content."EvaluationRuns" WHERE "Id" = '{other}'""")).ShouldBe("hybrid");

        foreach (var (sql, constraint) in new[]
                 {
                     ($"""UPDATE content."EvaluationRuns" SET "Kind" = 'answers' WHERE "Id" = '{runId}'""", "CK_EvaluationRuns_Kind"),
                     ($"""UPDATE content."EvaluationRuns" SET "Variant" = 'keyword' WHERE "Id" = '{runId}'""", "CK_EvaluationRuns_Variant"),
                     ($"""UPDATE content."EvaluationRuns" SET "QuestionCount" = 2, "Found" = 3 WHERE "Id" = '{runId}'""", "CK_EvaluationRuns_Counts"),
                     ($"""UPDATE content."EvaluationRuns" SET "RecallAtK" = 1.5 WHERE "Id" = '{runId}'""", "CK_EvaluationRuns_Metrics"),
                     ($"""UPDATE content."EvaluationResults" SET "Coverage" = 'high' WHERE "RunId" = '{runId}'""", "CK_EvaluationResults_Coverage"),
                     ($"""UPDATE content."EvaluationResults" SET "ExpectedRank" = 0 WHERE "RunId" = '{runId}'""", "CK_EvaluationResults_ExpectedRank"),
                     ($"""UPDATE content."EvaluationQuestions" SET "Layer" = 'feedback' WHERE "Id" = '{questionId}'""", "CK_EvaluationQuestions_Layer"),
                     ($"""UPDATE content."EvaluationQuestions" SET "State" = 'deleted' WHERE "Id" = '{questionId}'""", "CK_EvaluationQuestions_State"),
                     ($"""INSERT INTO content."EvaluationSets" ("Id", "CollectionId", "Classification") SELECT gen_random_uuid(), "CollectionId", 'public' FROM content."EvaluationSets" WHERE "Id" = '{setId}'""", "IX_EvaluationSets_CollectionId"),
                 })
        {
            var error = await Should.ThrowAsync<PostgresException>(() => Sql.ExecuteAsync(worker, sql));
            error.ConstraintName.ShouldBe(constraint, sql);
        }

        // Retention "until the collection is removed" (data-model §8): the set takes questions, runs and results with it.
        await Sql.ExecuteAsync(worker, $"""DELETE FROM content."EvaluationSets" WHERE "Id" = '{setId}'""");
        (await Sql.ScalarAsync<long>(worker, $"""
            SELECT (SELECT count(*) FROM content."EvaluationQuestions" WHERE "SetId" = '{setId}')
                 + (SELECT count(*) FROM content."EvaluationRuns" WHERE "SetId" = '{setId}')
                 + (SELECT count(*) FROM content."EvaluationResults" WHERE "RunId" = '{runId}')
            """)).ShouldBe(0);
    }

    [Fact]
    public async Task The_api_counts_retrievals_through_the_function_only()
    {
        var api = database.Connection(Api);
        var day = new DateOnly(2026, 10, 31);
        var document = Guid.NewGuid();
        var collection = Guid.NewGuid();
        // One flush with a key twice (folded) and another key; a second flush adds to the counts.
        var call = $"""
            SELECT content.rag_count_retrievals('{day:yyyy-MM-dd}'::date,
                ARRAY['{document}', '{document}', '{document}']::uuid[], ARRAY['\x01', '\x01', '\x02']::bytea[],
                ARRAY['{collection}', '{collection}', '{collection}']::uuid[], ARRAY['public', 'internal', 'public']::varchar[],
                ARRAY[1, 2, 5]::integer[], ARRAY[0, 1, 0]::integer[])
            """;
        (await Sql.ScalarAsync<int>(api, call)).ShouldBe(2);
        (await Sql.ScalarAsync<int>(api, call)).ShouldBe(2);
        (await Sql.ScalarAsync<int>(api, "SELECT content.rag_count_retrievals(current_date, '{}', '{}', '{}', '{}', '{}', '{}')")).ShouldBe(0);
        (await Sql.ListAsync(database.Connection(Worker), $"""
            SELECT encode("TextHash", 'hex') || ':' || "Classification" || ':' || "Retrieved" || ':' || "Cited"
            FROM content."RetrievalStats" WHERE "DocumentId" = '{document}' ORDER BY "TextHash"
            """)).ShouldBe(["01:internal:6:2", "02:public:10:0"]);

        // #114: a later flush at a lower level keeps the stored higher one; a higher level replaces it.
        string Flush(string hash, string level) => $"""
            SELECT content.rag_count_retrievals('{day:yyyy-MM-dd}'::date, ARRAY['{document}']::uuid[], ARRAY['\x{hash}']::bytea[],
                ARRAY['{collection}']::uuid[], ARRAY['{level}']::varchar[], ARRAY[1]::integer[], ARRAY[0]::integer[])
            """;
        await Sql.ExecuteAsync(api, Flush("01", "public"));
        await Sql.ExecuteAsync(api, Flush("02", "sensitive"));
        (await Sql.ListAsync(database.Connection(Worker), $"""
            SELECT encode("TextHash", 'hex') || ':' || "Classification" || ':' || "Retrieved"
            FROM content."RetrievalStats" WHERE "DocumentId" = '{document}' ORDER BY "TextHash"
            """)).ShouldBe(["01:internal:7", "02:sensitive:11"]);

        foreach (var bad in new[]
                 {
                     "SELECT content.rag_count_retrievals(current_date, ARRAY[gen_random_uuid()], ARRAY['\\x01'::bytea], '{}', ARRAY['public'], ARRAY[1], ARRAY[0])",
                     "SELECT content.rag_count_retrievals(current_date, ARRAY[gen_random_uuid()], ARRAY['\\x01'::bytea], ARRAY[gen_random_uuid()], ARRAY['public'], ARRAY[-1], ARRAY[0])",
                     "SELECT content.rag_count_retrievals(current_date, ARRAY[gen_random_uuid()], ARRAY['\\x01'::bytea], ARRAY[gen_random_uuid()], ARRAY['secret'], ARRAY[1], ARRAY[0])",
                     "SELECT content.rag_count_retrievals(NULL, '{}', '{}', '{}', '{}', '{}', '{}')",
                 })
        {
            (await Should.ThrowAsync<PostgresException>(() => Sql.ExecuteAsync(api, bad))).SqlState.ShouldBe(PostgresErrorCodes.RaiseException, bad);
        }

        // SECURITY DEFINER with a fixed search path, executable by no one but the api (data-model §1.2).
        (await Sql.ScalarAsync<string>(database.Connection(null), """
            SELECT p.prosecdef || ':' || array_to_string(p.proconfig, ',') || ':' || pg_get_userbyid(p.proowner)
            FROM pg_proc p WHERE p.oid = 'content.rag_count_retrievals'::regproc
            """)).ShouldBe($"true:search_path=pg_catalog, pg_temp:{Migrator}");
        await Sql.ShouldBeDeniedAsync(api, """SELECT count(*) FROM content."RetrievalStats" """);
        await Sql.ShouldBeDeniedAsync(database.Connection(Admin), call);
        await Sql.ShouldBeDeniedAsync(database.Connection(Worker), call);
    }

    private static async Task<Assistant> AddAssistantAsync(RagDbContext db)
    {
        var assistant = new Assistant
        {
            Slug = "test-" + Guid.NewGuid().ToString("N"),
            Name = "Testassistent",
            DepartmentId = Guid.NewGuid(),
            Classification = Classification.Public,
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = "test",
        };
        db.Assistants.Add(assistant);
        await db.SaveChangesAsync(Ct);
        return assistant;
    }

    /// <summary>The next draft (Number + 1), then the assistant's pointer to it: two saves, as the pointers form a cycle.</summary>
    private static async Task<AssistantVersion> AddDraftAsync(RagDbContext db, Assistant assistant)
    {
        var number = await db.AssistantVersions.Where(v => v.AssistantId == assistant.Id).MaxAsync(v => (int?)v.Number, Ct) ?? 0;
        var now = DateTimeOffset.UtcNow;
        var draft = new AssistantVersion
        {
            AssistantId = assistant.Id,
            Number = number + 1,
            GatewayModel = "gpt-test",
            SystemPrompt = "Du svarar på frågor om Umeå kommun.",
            CreatedBy = "test",
            CreatedAt = now,
            UpdatedAt = now,
            UpdatedBy = "test",
        };
        db.AssistantVersions.Add(draft);
        await db.SaveChangesAsync(Ct);
        assistant.DraftVersionId = draft.Id;
        assistant.UpdatedAt = now;
        await db.SaveChangesAsync(Ct);
        return draft;
    }

    private static void Publish(Assistant assistant, AssistantVersion draft)
    {
        var now = DateTimeOffset.UtcNow;
        draft.State = AssistantVersionStates.Published;
        draft.PublishedAt = now;
        draft.PublishedBy = "test";
        assistant.PublishedVersionId = draft.Id;
        assistant.DraftVersionId = null;
        assistant.UpdatedAt = now;
    }

    private static string InsertVersion(Guid assistantId, int number, string state) => $"""
        INSERT INTO config."AssistantVersions" ("Id", "AssistantId", "Number", "State", "GatewayModel", "SystemPrompt", "QuerySteps", "NotFound",
            "CollectionLinks", "CreatedAt", "UpdatedAt", "PublishedBy", "PublishedAt")
        VALUES ('{Guid.CreateVersion7()}', '{assistantId}', {number}, '{state}', 'gpt-test', '', '{"{}"}', '{"{}"}', '[]', now(), now(), 'test', now())
        """;

    private static string InsertAudience(Guid assistantId, string kind, string subject, string group) => $"""
        INSERT INTO config."AssistantAudiences" ("Id", "AssistantId", "Kind", "SubjectId", "GroupValue")
        VALUES ('{Guid.CreateVersion7()}', '{assistantId}', '{kind}', {subject}, {group})
        """;

    private static Task<List<string>> ViewAsync(string connection, Guid assistantId) => Sql.ListAsync(connection, $"""
        SELECT "Id" || ':' || "Number" || ':' || "State" FROM config."PublishedAssistantVersions" WHERE "AssistantId" = '{assistantId}'
        """);

    private Task<long> ConfigVersionAsync() =>
        Sql.ScalarAsync<long>(database.Connection(null), """SELECT "Version" FROM config."ConfigVersion" WHERE "Id" = 1""");

    /// <summary>Outbox rows after <paramref name="version"/> as EntityType:Operation:SecurityRelevant:EntityId.</summary>
    private Task<List<string>> OutboxAsync(long version) => Sql.ListAsync(database.Connection(null), $"""
        SELECT "EntityType" || ':' || "Operation" || ':' || CASE WHEN "SecurityRelevant" THEN 'True' ELSE 'False' END || ':' || coalesce("EntityId", '')
        FROM config."ConfigOutbox" WHERE "Version" > {version}
        """);

    private Task<string> ConstraintAsync(string name) =>
        Sql.ScalarAsync<string>(database.Connection(null), $"SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = '{name}'");

    private Task<List<string>> ColumnsAsync(string table) => Sql.ListAsync(database.Connection(null), $"""
        SELECT a.attname::text FROM pg_attribute a WHERE a.attrelid = 'config."{table}"'::regclass AND a.attnum > 0 AND NOT a.attisdropped ORDER BY a.attnum
        """);
}
