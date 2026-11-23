using Npgsql;
using static Kalla.TestKit.RagDatabase;

namespace Kalla.IntegrationTests;

/// <summary>
/// The production roles (deploy/postgres/init.sql and app-roles.sql) against the real schema: each service gets only
/// what data-model §1.2 lists, with the caps and timeouts of §1.1. Copied from the gateway's DatabaseRoleTests.
/// </summary>
[Collection(RagDatabaseGroup.Name)]
public sealed class DatabaseRoleTests(RagDatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Each_role_reads_and_writes_exactly_the_tables_it_needs()
    {
        // A new table must be added to app-roles.sql on purpose: until then no service can read it and this test fails.
        string[] all = [.. await Sql.ListAsync(database.Connection(null), """
            SELECT n.nspname || '.' || c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname IN ('config', 'content', 'chat', 'audit') AND c.relkind IN ('r', 'p') AND NOT c.relispartition
            """)];
        all.ShouldContain("content.RunItems");

        string[] configRead = ["config.ConfigVersion", "config.ConfigOutbox", "config.DataProtectionKeys", "config.Departments", "config.Teams"];
        string[] organisation = ["config.Departments", "config.Teams"];

        // The assigned gateway keys' mirror (B-35, data-model §1.2): the admin's sync writes it, the api reads it, the worker never.
        string[] gatewayKeys = ["config.GatewayKeys"];
        string[] workerTables = ["content.Jobs", "content.WorkerInstances", "content.RunItems"];

        string[] keys = ["config.Clients", "config.Keys"];

        // Ingestion (P1 item 2, data-model §1.2): the admin manages the config, the worker writes the content.
        string[] retrievalConfig = ["config.Settings", "config.DataProtectionPolicies", "config.EmbeddingProfiles", "config.Collections"];

        // Organisation (P1b item 33, data-model §1.2): the admin manages collection shares, the api reads them, the worker never.
        string[] shares = ["config.CollectionShares"];
        string[] ingestionConfig = [.. retrievalConfig, "config.Sources", "config.Pipelines", "config.PipelineVersions"];
        string[] ingestionContent = [.. IngestionContentTables.Select(t => "content." + t)];

        // Serving (P1 item 3, data-model §1.2): the api reads published versions only through the view; the worker reads
        // no assistant table whole (column grants below); the admin also writes evaluation runs and results (answer-level runs in kalla-admin).
        // Assistant templates (P2) are admin-only: copied into a new assistant's first draft, never read by the api.
        string[] servingConfig = ["config.Assistants", "config.AssistantVersions", "config.AssistantAudiences", "config.GatewayModels", "config.AssistantTemplates",
            "config.AssistantTestQuestions"];
        string[] apiServingConfig = ["config.Assistants", "config.AssistantAudiences", "config.PublishedAssistantVersions", "config.GatewayModels"];
        string[] evaluationWrites = ["content.EvaluationRuns", "content.EvaluationResults"];
        string[] servingContent = ["content.EvaluationSets", "content.EvaluationQuestions", .. evaluationWrites, "content.RetrievalStats"];

        // Chat (P1b item 1, data-model §2.3): the api owns it, the admin reads it (and marks feedback reviewed, a column
        // grant), the worker has no access. The content-gap counts (Decisions "Content-gap report") and the ratings per cited document
        // (Decisions "Downvotes per document") are the api's alone: no test-panel write.
        string[] chat = [.. new[] { "Conversations", "DataKeys", "Messages", "MessageSources", "Feedback", "ToolCalls", "AssistantDailyTotals", "FeedbackImportState", "AssistantDailyUsage", "AssistantDailyOutcomes", "AssistantDailyGaps", "DocumentDailyFeedback" }
            .Select(t => "chat." + t)];

        // Cost per step (data-model §1.2, §2.2, §2.3; cost-attribution §5): the worker writes its knowledge bases' usage, the
        // admin reads it; the admin also upserts the test panel's assistant usage (I U, no D), the api owns the assistant's.
        string[] collectionUsage = ["content.CollectionDailyUsage"];

        // Setup mode (identity design §19): only the admin has the setup sessions and test sign-ins, which hold candidate values.
        string[] setup = ["config.SetupSessions", "config.SetupTests"];
        string[] assistantUsage = ["chat.AssistantDailyUsage", "chat.AssistantDailyOutcomes"]; // and the test panel's turns per outcome (Decisions "Cost per outcome")

        // config.Sources and content.Documents are column grants for the api (below), so they are not table privileges.
        (await PrivilegesAsync(Api, "SELECT")).ShouldBe([.. configRead, .. keys, "config.KeyLastUse", .. retrievalConfig, .. shares, .. apiServingConfig, .. gatewayKeys, "content.ChunkSets", "content.Chunks",
            "content.KeywordStatistics", .. chat], ignoreOrder: true); // the keyword stop list's statistics (#499)
        (await PrivilegesAsync(Api, "INSERT")).ShouldBe(["config.KeyLastUse", "audit.AuditLog", .. chat], ignoreOrder: true);
        (await PrivilegesAsync(Api, "UPDATE")).ShouldBe(["config.KeyLastUse", .. chat], ignoreOrder: true);
        (await PrivilegesAsync(Api, "DELETE")).ShouldBe(chat, ignoreOrder: true);

        (await PrivilegesAsync(Admin, "SELECT")).ShouldBe([.. all, "config.PublishedAssistantVersions"], ignoreOrder: true); // explain access (identity §4.9)
        (await PrivilegesAsync(Admin, "INSERT")).ShouldBe([.. keys, .. organisation, .. gatewayKeys, .. ingestionConfig, .. shares, .. servingConfig, .. setup, "config.AdminSessions", "config.DataProtectionKeys", "content.Jobs", "content.BlobTombstones", .. evaluationWrites, "audit.AuditLog", .. assistantUsage], ignoreOrder: true);
        (await PrivilegesAsync(Admin, "UPDATE")).ShouldBe([.. keys, .. organisation, .. gatewayKeys, .. ingestionConfig, .. shares, .. servingConfig, .. setup, "config.AdminSessions", "config.ConfigVersion", .. evaluationWrites, .. assistantUsage], ignoreOrder: true);
        (await PrivilegesAsync(Admin, "DELETE")).ShouldBe([.. keys, .. organisation, .. gatewayKeys, .. ingestionConfig, .. shares, .. servingConfig, .. setup, "config.AdminSessions"], ignoreOrder: true);

        // The worker also reads the model mirror (the evaluation's residency checks, #130).
        (await PrivilegesAsync(Worker, "SELECT")).ShouldBe([.. configRead, .. ingestionConfig, .. workerTables, .. ingestionContent, .. servingContent, "config.GatewayModels", .. collectionUsage],
            ignoreOrder: true);
        (await PrivilegesAsync(Worker, "INSERT")).ShouldBe([.. workerTables, .. ingestionContent, .. servingContent, "audit.AuditLog", .. collectionUsage], ignoreOrder: true);
        (await PrivilegesAsync(Worker, "UPDATE")).ShouldBe([.. workerTables, .. ingestionContent, .. servingContent, .. collectionUsage], ignoreOrder: true);
        (await PrivilegesAsync(Worker, "DELETE")).ShouldBe([.. workerTables, .. ingestionContent, .. servingContent, .. collectionUsage], ignoreOrder: true);

        // Column grants exist only where data-model §1.2 lists columns: the api's reads of Sources and Documents.
        (await ColumnPrivilegesAsync(Api)).ShouldBe([.. ApiSourceColumns.Select(c => "config.Sources." + c), .. ApiDocumentColumns.Select(c => "content.Documents." + c)], ignoreOrder: true);
        (await ColumnPrivilegesAsync(Admin)).ShouldBe(["chat.Feedback.ReviewState:UPDATE"]);
        // The worker: the links of active assistants' published versions, for the index-health check (Decisions "M-2 open design calls").
        (await ColumnPrivilegesAsync(Worker)).ShouldBe([.. WorkerAssistantColumns], ignoreOrder: true);
    }

    /// <summary>The content tables of P1 item 2 (data-model §2.2, issue #34).</summary>
    private static readonly string[] IngestionContentTables =
    [
        "KeywordStatistics", // kept by the worker (knowledge design §6.2, #499)
        "NodeManifest", "WorkerNodeSupport", "CrawlHosts", "SiteProfiles", "SiteProfileVersions", "CrawlFrontier", "CrawlRuns",
        "ConnectorCheckpoints", "Uploads", "DeletionProposals", "QuarantinedFiles", "Documents", "NodeOutputs", "ChunkSets",
        "Chunks", "EmbeddingCache", "Runs", "ParseObservations", "ParseProfiles", "BlobTombstones",
    ];

    /// <summary>data-model §1.2: what retrieval reads of a source (knowledge design §6.3, §6.5); never its settings.</summary>
    /// <summary>The index-health check's scopes (knowledge design §6.4): which collections and topics active assistants link.</summary>
    private static readonly string[] WorkerAssistantColumns =
        ["config.PublishedAssistantVersions.AssistantId", "config.PublishedAssistantVersions.CollectionLinks", "config.Assistants.Id", "config.Assistants.State"];

    private static readonly string[] ApiSourceColumns = ["Id", "CollectionId", "Type", "Classification", "State", "DeletedAt", "ContainsThirdPartyText", "DemoteRules"];

    /// <summary>data-model §1.2: the citation and download columns of a document (knowledge design §3.5).</summary>
    private static readonly string[] ApiDocumentColumns =
    [
        "Id", "SourceId", "Classification", "IdentityKind", "IdentityHash", "Title", "Url", "MediaType", "DisplayName",
        "SourceModifiedAt", "PageCount", "LiveChunkSetId", "DeletedAt", "Language", "FetchedAt", "CanonicalIdentity", "ContentHash",
    ];

    [Fact]
    public async Task The_api_reads_only_the_granted_columns_of_sources_and_documents()
    {
        var api = database.Connection(Api);
        await Sql.ExecuteAsync(api, $"""SELECT {Columns(ApiSourceColumns)} FROM config."Sources" """);
        await Sql.ExecuteAsync(api, $"""SELECT {Columns(ApiDocumentColumns)} FROM content."Documents" """);
        await Sql.ShouldBeDeniedAsync(api, """SELECT * FROM config."Sources" """);
        await Sql.ShouldBeDeniedAsync(api, """SELECT "Settings" FROM config."Sources" """);
        await Sql.ShouldBeDeniedAsync(api, """SELECT "Schedule" FROM config."Sources" """);
        await Sql.ShouldBeDeniedAsync(api, """SELECT * FROM content."Documents" """);
        await Sql.ShouldBeDeniedAsync(api, """SELECT "ParseCacheKey" FROM content."Documents" """);
        await Sql.ShouldBeDeniedAsync(api, """SELECT "PiiClearedBy" FROM content."Documents" """);
        await Sql.ShouldBeDeniedAsync(api, """UPDATE content."Documents" SET "Title" = 'x' """);
        // The api never reads pipelines, connector state, uploads or the crawl frontier.
        foreach (var table in new[] { "config.\"Pipelines\"", "config.\"PipelineVersions\"", "content.\"Uploads\"", "content.\"CrawlFrontier\"", "content.\"Runs\"" })
        {
            await Sql.ShouldBeDeniedAsync(api, $"SELECT count(*) FROM {table}");
        }

        static string Columns(IEnumerable<string> columns) => string.Join(", ", columns.Select(c => $"\"{c}\""));
    }

    [Fact]
    public async Task Only_the_admin_writes_ingestion_config_and_only_the_worker_writes_content()
    {
        await Sql.ShouldBeDeniedAsync(database.Connection(Worker), """UPDATE config."Collections" SET "Name" = 'x' """);
        await Sql.ShouldBeDeniedAsync(database.Connection(Api), """UPDATE config."Collections" SET "Name" = 'x' """);
        await Sql.ShouldBeDeniedAsync(database.Connection(Worker), """DELETE FROM config."Settings" """);
        await Sql.ShouldBeDeniedAsync(database.Connection(Admin), """UPDATE content."Documents" SET "Title" = 'x' """);
        await Sql.ShouldBeDeniedAsync(database.Connection(Admin), """DELETE FROM content."Chunks" """);
        await Sql.ShouldBeDeniedAsync(database.Connection(Admin), """INSERT INTO content."CrawlHosts" ("Host", "RequestsPerSecond", "MaxConnections", "NextAllowedAt", "Kind", "BackoffFactor") VALUES ('x.example', 1, 1, now(), 'external', 1)""");
    }

    [Fact]
    public async Task The_api_reads_published_versions_only_through_the_view_and_the_worker_only_their_links()
    {
        // data-model §1.2: never AssistantVersions itself, so a draft cannot reach chat.
        var admin = database.Connection(Admin);
        var assistantId = Guid.CreateVersion7();
        var publishedId = Guid.CreateVersion7();
        var draftId = Guid.CreateVersion7();
        await Sql.ExecuteAsync(admin, $"""
            BEGIN;
            INSERT INTO config."Assistants" ("Id", "Slug", "Name", "DepartmentId", "Classification", "HistoryMode", "RetentionDays", "AllowedOrigins", "State", "UpdatedAt")
            VALUES ('{assistantId}', 'roles-{assistantId:N}', 'Roller', '{Guid.NewGuid()}', 'public', 'none', 90, '{"{}"}', 'active', now());
            INSERT INTO config."AssistantVersions" ("Id", "AssistantId", "Number", "State", "GatewayModel", "SystemPrompt", "QuerySteps", "NotFound", "CollectionLinks", "CreatedAt", "UpdatedAt", "PublishedBy", "PublishedAt")
            VALUES ('{publishedId}', '{assistantId}', 1, 'published', 'gpt-test', 'publicerad', '{"{}"}', '{"{}"}', '[]', now(), now(), 'test', now()),
                   ('{draftId}', '{assistantId}', 2, 'draft', NULL, 'utkast', '{"{}"}', '{"{}"}', '[]', now(), now(), NULL, NULL);
            UPDATE config."Assistants" SET "PublishedVersionId" = '{publishedId}', "DraftVersionId" = '{draftId}' WHERE "Id" = '{assistantId}';
            COMMIT;
            """);

        var api = database.Connection(Api);
        (await Sql.ListAsync(api, $"""SELECT "SystemPrompt" FROM config."PublishedAssistantVersions" WHERE "AssistantId" = '{assistantId}'""")).ShouldBe(["publicerad"]);
        (await Sql.ScalarAsync<string>(api, $"""SELECT "DraftVersionId"::text FROM config."Assistants" WHERE "Id" = '{assistantId}'""")).ShouldBe(draftId.ToString());
        await Sql.ShouldBeDeniedAsync(api, """SELECT count(*) FROM config."AssistantVersions" """);
        await Sql.ShouldBeDeniedAsync(api, $"""SELECT "SystemPrompt" FROM config."AssistantVersions" WHERE "Id" = '{draftId}'""");
        await Sql.ShouldBeDeniedAsync(api, $"""UPDATE config."Assistants" SET "Name" = 'x' WHERE "Id" = '{assistantId}'""");
        await Sql.ShouldBeDeniedAsync(api, """DELETE FROM config."AssistantAudiences" """);
        await Sql.ShouldBeDeniedAsync(api, """INSERT INTO config."GatewayModels" ("Name", "Kind", "Residencies", "Source", "SupportsTools", "SupportsToolsSource") VALUES ('x', 'model', ARRAY['Eu'], 'manual', false, 'manual')""");

        var worker = database.Connection(Worker);
        foreach (var table in new[] { "AssistantVersions", "AssistantAudiences" })
        {
            await Sql.ShouldBeDeniedAsync(worker, $"""SELECT count(*) FROM config."{table}" """);
        }

        // The index-health check (knowledge design §6.4): an active assistant's published links, nothing else of it.
        (await Sql.ListAsync(worker, $"""
            SELECT v."CollectionLinks"::text FROM config."PublishedAssistantVersions" v JOIN config."Assistants" a ON a."Id" = v."AssistantId"
            WHERE a."State" = 'active' AND v."AssistantId" = '{assistantId}'
            """)).ShouldBe(["[]"]);
        await Sql.ShouldBeDeniedAsync(worker, """SELECT * FROM config."PublishedAssistantVersions" """);
        await Sql.ShouldBeDeniedAsync(worker, """SELECT "SystemPrompt" FROM config."PublishedAssistantVersions" """);
        await Sql.ShouldBeDeniedAsync(worker, """SELECT "Name" FROM config."Assistants" """);

        await Sql.ExecuteAsync(worker, """SELECT count(*) FROM config."GatewayModels" """); // the model mirror only (#130)

        // The admin edits versions in the table, and reads the data plane's view only to explain access exactly as the
        // api decides (identity design §4.9, P1b item 9): the published version, never a draft.
        (await Sql.ScalarAsync<long>(admin, $"""SELECT count(*) FROM config."AssistantVersions" WHERE "AssistantId" = '{assistantId}'""")).ShouldBe(2);
        (await Sql.ScalarAsync<long>(admin, $"""SELECT count(*) FROM config."PublishedAssistantVersions" WHERE "AssistantId" = '{assistantId}'""")).ShouldBe(1);
    }

    [Fact]
    public async Task The_admin_manages_collection_shares_the_api_reads_them_and_the_worker_has_no_access()
    {
        var profileId = Guid.CreateVersion7();
        var collectionId = Guid.CreateVersion7();
        var departmentId = Guid.CreateVersion7();
        var otherDepartmentId = Guid.CreateVersion7();
        await Sql.ExecuteAsync(database.Connection(null), $"""
            INSERT INTO config."EmbeddingProfiles" ("Id", "Name", "GatewayModel", "Dimensions", "Distance", "MaxInputTokens", "IsDefault", "State", "MaxResidency", "UpdatedAt")
            VALUES ('{profileId}', 'shares-{profileId:N}', 'bge-m3', 3, 'cosine', 10, false, 'active', 'Eu', now());
            INSERT INTO config."Collections" ("Id", "Slug", "Name", "DepartmentId", "Classification", "EmbeddingProfileId", "RetrievalSettings", "UpdatedAt")
            VALUES ('{collectionId}', 'shares-{collectionId:N}', 'Delad', '{departmentId}', 'public', '{profileId}', '{"{}"}', now());
            INSERT INTO config."Departments" ("Id", "Name", "CostCenterCode", "MemberGroups", "IsActive", "SyncedAt")
            VALUES ('{departmentId}', 'Ägare', 'cc-{departmentId:N}', '{"{}"}', true, now()),
                   ('{otherDepartmentId}', 'Mottagare', 'cc-{otherDepartmentId:N}', '{"{}"}', true, now());
            """);

        var admin = database.Connection(Admin);
        await Sql.ExecuteAsync(admin, $"""
            INSERT INTO config."CollectionShares" ("CollectionId", "DepartmentId", "SharedBy", "SharedAt") VALUES ('{collectionId}', '{otherDepartmentId}', 'admin', now());
            UPDATE config."CollectionShares" SET "SharedBy" = 'admin2' WHERE "CollectionId" = '{collectionId}';
            """);
        var api = database.Connection(Api);
        (await Sql.ListAsync(api, $"""SELECT "DepartmentId"::text FROM config."CollectionShares" WHERE "CollectionId" = '{collectionId}'""")).ShouldBe([otherDepartmentId.ToString()]);
        await Sql.ShouldBeDeniedAsync(api, $"""INSERT INTO config."CollectionShares" ("CollectionId", "DepartmentId", "SharedBy", "SharedAt") VALUES ('{collectionId}', '{departmentId}', 'api', now())""");
        await Sql.ShouldBeDeniedAsync(api, $"""UPDATE config."CollectionShares" SET "SharedBy" = 'api' WHERE "CollectionId" = '{collectionId}'""");
        await Sql.ShouldBeDeniedAsync(api, $"""DELETE FROM config."CollectionShares" WHERE "CollectionId" = '{collectionId}'""");
        await Sql.ShouldBeDeniedAsync(database.Connection(Worker), """SELECT count(*) FROM config."CollectionShares" """);
        await Sql.ExecuteAsync(admin, $"""DELETE FROM config."CollectionShares" WHERE "CollectionId" = '{collectionId}'""");
    }

    [Fact]
    public async Task The_admin_writes_evaluation_runs_and_results_but_only_the_worker_writes_sets_and_counters()
    {
        var worker = database.Connection(Worker);
        var admin = database.Connection(Admin);
        var setId = Guid.CreateVersion7();
        var runId = Guid.CreateVersion7();
        await Sql.ExecuteAsync(worker, $"""
            INSERT INTO content."EvaluationSets" ("Id", "CollectionId", "Classification") VALUES ('{setId}', '{Guid.NewGuid()}', 'public')
            """);
        await Sql.ExecuteAsync(admin, $"""
            INSERT INTO content."EvaluationRuns" ("Id", "SetId", "Classification", "Kind", "K", "Reranked", "StartedAt")
            VALUES ('{runId}', '{setId}', 'public', 'on-demand', 10, false, now());
            INSERT INTO content."EvaluationResults" ("RunId", "QuestionId", "Classification", "ExpectedRank")
            VALUES ('{runId}', '{Guid.NewGuid()}', 'public', 1);
            UPDATE content."EvaluationRuns" SET "QuestionCount" = 1, "Found" = 1, "FinishedAt" = now() WHERE "Id" = '{runId}';
            UPDATE content."EvaluationResults" SET "Hit" = true WHERE "RunId" = '{runId}';
            """);
        await Sql.ShouldBeDeniedAsync(admin, $"""DELETE FROM content."EvaluationRuns" WHERE "Id" = '{runId}'""");
        await Sql.ShouldBeDeniedAsync(admin, $"""DELETE FROM content."EvaluationResults" WHERE "RunId" = '{runId}'""");
        await Sql.ShouldBeDeniedAsync(admin, $"""INSERT INTO content."EvaluationSets" ("Id", "CollectionId", "Classification") VALUES ('{Guid.CreateVersion7()}', '{Guid.NewGuid()}', 'public')""");
        await Sql.ShouldBeDeniedAsync(admin, $"""UPDATE content."EvaluationQuestions" SET "State" = 'retired' WHERE "SetId" = '{setId}'""");
        await Sql.ShouldBeDeniedAsync(admin, """DELETE FROM content."RetrievalStats" """);
        await Sql.ShouldBeDeniedAsync(database.Connection(Api), """SELECT count(*) FROM content."EvaluationRuns" """);
        await Sql.ShouldBeDeniedAsync(database.Connection(Api), """SELECT count(*) FROM content."RetrievalStats" """);
        await Sql.ExecuteAsync(worker, $"""DELETE FROM content."EvaluationSets" WHERE "Id" = '{setId}'""");
    }

    [Fact]
    public async Task The_admin_tombstones_blobs_but_only_the_worker_purges_them()
    {
        // Issue #34: the deleting transaction inserts the tombstone, idempotently; the worker's sweep deletes the row.
        var key = $"uploads/{Guid.NewGuid()}/{Guid.NewGuid()}/{new string('a', 64)}";
        var insert = $"""INSERT INTO content."BlobTombstones" ("Key", "Reason") VALUES ('{key}', 'upload.replaced') ON CONFLICT DO NOTHING""";
        await Sql.ExecuteAsync(database.Connection(Admin), insert);
        await Sql.ExecuteAsync(database.Connection(Admin), insert);
        await Sql.ExecuteAsync(database.Connection(Worker), insert);
        await Sql.ShouldBeDeniedAsync(database.Connection(Admin), $"""DELETE FROM content."BlobTombstones" WHERE "Key" = '{key}'""");
        await Sql.ShouldBeDeniedAsync(database.Connection(Api), """SELECT count(*) FROM content."BlobTombstones" """);
        (await Sql.ScalarAsync<long>(database.Connection(Worker), $"""SELECT count(*) FROM content."BlobTombstones" WHERE "Key" = '{key}' AND "DeletedAt" <= now()""")).ShouldBe(1);
        await Sql.ExecuteAsync(database.Connection(Worker), $"""DELETE FROM content."BlobTombstones" WHERE "Key" = '{key}'""");
    }

    [Fact]
    public async Task Init_sql_makes_the_ddl_role_own_the_database_without_superuser_rights()
    {
        var superuser = database.Connection(null);
        (await Sql.ScalarAsync<string>(superuser, "SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname = 'ragdb'")).ShouldBe(Migrator);
        (await Sql.ScalarAsync<long>(superuser, $"""
            SELECT count(*) FROM pg_roles WHERE rolname LIKE 'kalla_%' AND (rolsuper OR rolcreaterole OR rolcreatedb)
            """)).ShouldBe(0);
        (await Sql.ScalarAsync<string>(superuser, "SELECT extversion FROM pg_extension WHERE extname = 'vector'")).ShouldNotBeNull();
        foreach (var schema in new[] { "config", "content", "chat", "audit", "public" })
        {
            (await Sql.ScalarAsync<string>(superuser, $"SELECT pg_get_userbyid(nspowner) FROM pg_namespace WHERE nspname = '{schema}'"))
                .ShouldBe(Migrator, schema);
        }
    }

    [Theory]
    [InlineData(Migrator, 3, "0", "10s", "0")]
    [InlineData(Api, 26, "5s", "1s", "10s")]
    [InlineData(Admin, 12, "30s", "5s", "1min")]
    [InlineData(Worker, 12, "1min", "5s", "1min")]
    public async Task Each_role_has_its_connection_cap_and_timeouts(string role, int limit, string statementTimeout, string lockTimeout, string idleTimeout)
    {
        (await Sql.ScalarAsync<int>(database.Connection(null), $"SELECT rolconnlimit FROM pg_roles WHERE rolname = '{role}'")).ShouldBe(limit);

        await using var connection = new NpgsqlConnection(database.Connection(role, pooling: false));
        await connection.OpenAsync(Ct);
        (await ShowAsync(connection, "statement_timeout")).ShouldBe(statementTimeout);
        (await ShowAsync(connection, "lock_timeout")).ShouldBe(lockTimeout);
        (await ShowAsync(connection, "idle_in_transaction_session_timeout")).ShouldBe(idleTimeout);
    }

    [Theory]
    [InlineData(Api)]
    [InlineData(Admin)]
    [InlineData(Worker)]
    public async Task Hnsw_defaults_apply_before_and_after_pgvector_is_loaded(string role)
    {
        // to-verify #2: the role defaults were stored before the session loads the vector library.
        await using var connection = new NpgsqlConnection(database.Connection(role, pooling: false));
        await connection.OpenAsync(Ct);
        await AssertHnswDefaultsAsync(connection);

        await using (var load = new NpgsqlCommand("SELECT '[1,2,3]'::vector <=> '[3,2,1]'::vector", connection))
        {
            await load.ExecuteScalarAsync(Ct);
        }

        await AssertHnswDefaultsAsync(connection);
    }

    [Fact]
    public async Task The_ddl_role_cannot_change_the_service_roles_caps()
    {
        // So init.sql (bootstrap superuser) sets them, not app-roles.sql (run as the DDL role): see the PR's design deviation.
        await Sql.ShouldBeDeniedAsync(database.Connection(Migrator), "ALTER ROLE kalla_api CONNECTION LIMIT 100");
        await Sql.ShouldBeDeniedAsync(database.Connection(Migrator), "ALTER ROLE kalla_api IN DATABASE ragdb SET statement_timeout = 0");
    }

    [Fact]
    public async Task Api_statements_are_cancelled_after_five_seconds()
    {
        var error = await Should.ThrowAsync<PostgresException>(() => Sql.ExecuteAsync(database.Connection(Api), "SELECT pg_sleep(6)"));
        error.SqlState.ShouldBe(PostgresErrorCodes.QueryCanceled);
    }

    [Fact]
    public async Task Api_connection_limit_fits_two_instances_and_refuses_one_more()
    {
        // Budget rule (data-model §1.1): two instances × (pool 12 + 1 LISTEN) = 26 = the role's CONNECTION LIMIT.
        NpgsqlConnection.ClearAllPools(); // idle pooled connections of other tests count against the limit
        var connections = new List<NpgsqlConnection>();
        try
        {
            for (var i = 0; i < 26; i++)
            {
                var connection = new NpgsqlConnection(database.Connection(Api, pooling: false));
                connections.Add(connection);
                await connection.OpenAsync(Ct);
            }

            await using var extra = new NpgsqlConnection(database.Connection(Api, pooling: false));
            var error = await Should.ThrowAsync<PostgresException>(() => extra.OpenAsync(Ct));
            error.SqlState.ShouldBe(PostgresErrorCodes.TooManyConnections);
        }
        finally
        {
            foreach (var connection in connections)
            {
                await connection.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task Only_the_admin_locks_the_config_version_row()
    {
        await Sql.ExecuteAsync(database.Connection(Admin), """
            BEGIN; SELECT "Version" FROM config."ConfigVersion" WHERE "Id" = 1 FOR UPDATE; COMMIT;
            """);
        await Sql.ShouldBeDeniedAsync(database.Connection(Api), """SELECT "Version" FROM config."ConfigVersion" FOR UPDATE""");
        await Sql.ShouldBeDeniedAsync(database.Connection(Worker), """SELECT "Version" FROM config."ConfigVersion" FOR UPDATE""");
        // The version itself moves only through the trigger function.
        await Sql.ShouldBeDeniedAsync(database.Connection(Admin), """INSERT INTO config."ConfigOutbox" ("Version", "EntityType", "Operation", "SecurityRelevant") VALUES (1, 'x', 'I', false)""");
    }

    [Fact]
    public async Task Admin_enqueues_jobs_but_only_the_worker_changes_them()
    {
        var departmentId = Guid.NewGuid();
        const string payload = """{"kind":"noop"}""";
        await Sql.ExecuteAsync(database.Connection(Admin), $"""
            INSERT INTO content."Jobs" ("Id", "Class", "DepartmentId", "Classification", "Payload", "MaxAttempts")
            VALUES ('{Guid.CreateVersion7()}', 'command', '{departmentId}', 'internal', '{payload}', 3)
            """);
        await Sql.ShouldBeDeniedAsync(database.Connection(Admin), $"""UPDATE content."Jobs" SET "State" = 'cancelled' WHERE "DepartmentId" = '{departmentId}'""");
        await Sql.ShouldBeDeniedAsync(database.Connection(Admin), $"""DELETE FROM content."Jobs" WHERE "DepartmentId" = '{departmentId}'""");
        await Sql.ShouldBeDeniedAsync(database.Connection(Api), """SELECT count(*) FROM content."Jobs" """);
        await Sql.ExecuteAsync(database.Connection(Worker), $"""DELETE FROM content."Jobs" WHERE "DepartmentId" = '{departmentId}'""");
    }

    [Fact]
    public async Task Every_service_appends_audit_entries_and_only_the_admin_reads_them()
    {
        foreach (var role in new[] { Api, Admin, Worker })
        {
            await Sql.ExecuteAsync(database.Connection(role), $"""
                INSERT INTO audit."AuditLog" ("Timestamp", "Actor", "Action", "EntityType", "Service")
                VALUES (now(), 'system:test', 'role-test', 'Test', '{role}')
                """);
        }

        (await Sql.ScalarAsync<long>(database.Connection(Admin), """SELECT count(*) FROM audit."AuditLog" WHERE "Action" = 'role-test'""")).ShouldBeGreaterThanOrEqualTo(3);
        await Sql.ShouldBeDeniedAsync(database.Connection(Api), """SELECT count(*) FROM audit."AuditLog" """);
        await Sql.ShouldBeDeniedAsync(database.Connection(Worker), """SELECT count(*) FROM audit."AuditLog" """);
        await Sql.ShouldBeDeniedAsync(database.Connection(Admin), """DELETE FROM audit."AuditLog" """);
    }

    [Fact]
    public async Task Functions_run_only_for_the_roles_that_need_them()
    {
        foreach (var role in new[] { Api, Admin, Worker })
        {
            (await Sql.ScalarAsync<object>(database.Connection(role), "SELECT content.rag_or_query(to_tsvector('swedish', 'bygglov'))")).ShouldNotBeNull(role);
        }

        await Sql.ExecuteAsync(database.Connection(Worker), "SELECT * FROM content.rag_partition_maintain()");
        await Sql.ShouldBeDeniedAsync(database.Connection(Api), "SELECT * FROM content.rag_partition_maintain()");
        await Sql.ShouldBeDeniedAsync(database.Connection(Admin), "SELECT * FROM content.rag_partition_maintain()");
        // The per-profile HNSW index (data-model §3.2): only the worker creates it; an unknown profile is refused inside.
        var unknown = Guid.NewGuid();
        var refused = await Should.ThrowAsync<PostgresException>(() => Sql.ExecuteAsync(database.Connection(Worker), $"SELECT content.rag_ensure_profile_index('{unknown}', 3)"));
        refused.SqlState.ShouldBe(PostgresErrorCodes.RaiseException);
        await Sql.ShouldBeDeniedAsync(database.Connection(Api), $"SELECT content.rag_ensure_profile_index('{unknown}', 3)");
        await Sql.ShouldBeDeniedAsync(database.Connection(Admin), $"SELECT content.rag_ensure_profile_index('{unknown}', 3)");
        await Sql.ShouldBeDeniedAsync(database.Connection(Admin), "SELECT config.rag_config_changed()");
        // Retrieval counters (data-model §12.3): only the api flushes them, and only through the function.
        const string count = "SELECT content.rag_count_retrievals(current_date, '{}', '{}', '{}', '{}', '{}', '{}')";
        (await Sql.ScalarAsync<int>(database.Connection(Api), count)).ShouldBe(0);
        await Sql.ShouldBeDeniedAsync(database.Connection(Admin), count);
        await Sql.ShouldBeDeniedAsync(database.Connection(Worker), count);
    }

    [Fact]
    public async Task Applying_the_roles_again_keeps_the_same_grants()
    {
        var before = await PrivilegesAsync(Worker, "SELECT");
        await database.ApplyRolesAsync(Ct);
        (await PrivilegesAsync(Worker, "SELECT")).ShouldBe(before, ignoreOrder: true);
    }

    private static async Task AssertHnswDefaultsAsync(NpgsqlConnection connection)
    {
        (await ShowAsync(connection, "hnsw.ef_search")).ShouldBe("100");
        (await ShowAsync(connection, "hnsw.iterative_scan")).ShouldBe("relaxed_order");
        (await ShowAsync(connection, "hnsw.max_scan_tuples")).ShouldBe("100000");
    }

    private static async Task<string?> ShowAsync(NpgsqlConnection connection, string setting)
    {
        await using var command = new NpgsqlCommand($"SHOW {setting}", connection);
        return (string?)await command.ExecuteScalarAsync(Ct);
    }

    /// <summary>Column grants (schema.table.column) of <paramref name="role"/> on tables where it has no table-wide grant of the same privilege.</summary>
    private async Task<List<string>> ColumnPrivilegesAsync(string role) => await Sql.ListAsync(database.Connection(null), $"""
        SELECT c.table_schema || '.' || c.table_name || '.' || c.column_name || CASE c.privilege_type WHEN 'SELECT' THEN '' ELSE ':' || c.privilege_type END
        FROM information_schema.column_privileges c
        WHERE c.table_schema IN ('config', 'content', 'chat', 'audit') AND c.grantee = '{role}'
          AND NOT EXISTS (SELECT 1 FROM information_schema.table_privileges t
                          WHERE t.table_schema = c.table_schema AND t.table_name = c.table_name
                            AND t.grantee = c.grantee AND t.privilege_type = c.privilege_type)
        """);

    /// <summary>Tables (schema.table) on which <paramref name="role"/> holds <paramref name="privilege"/> for the whole table.</summary>
    private async Task<List<string>> PrivilegesAsync(string role, string privilege) => await Sql.ListAsync(database.Connection(null), $"""
        SELECT table_schema || '.' || table_name FROM information_schema.table_privileges
        WHERE table_schema IN ('config', 'content', 'chat', 'audit') AND grantee = '{role}' AND privilege_type = '{privilege}'
        """);
}
