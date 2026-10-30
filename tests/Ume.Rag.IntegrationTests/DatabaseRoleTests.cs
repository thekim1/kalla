using Npgsql;
using static Ume.Rag.TestKit.RagDatabase;

namespace Ume.Rag.IntegrationTests;

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

        string[] configRead = ["config.ConfigVersion", "config.ConfigOutbox", "config.DataProtectionKeys"];
        string[] workerTables = ["content.Jobs", "content.WorkerInstances", "content.RunItems"];

        (await PrivilegesAsync(Api, "SELECT")).ShouldBe(configRead, ignoreOrder: true);
        (await PrivilegesAsync(Api, "INSERT")).ShouldBe(["audit.AuditLog"]);
        (await PrivilegesAsync(Api, "UPDATE")).ShouldBeEmpty();
        (await PrivilegesAsync(Api, "DELETE")).ShouldBeEmpty();

        (await PrivilegesAsync(Admin, "SELECT")).ShouldBe(all, ignoreOrder: true);
        (await PrivilegesAsync(Admin, "INSERT")).ShouldBe(["config.DataProtectionKeys", "content.Jobs", "audit.AuditLog"], ignoreOrder: true);
        (await PrivilegesAsync(Admin, "UPDATE")).ShouldBe(["config.ConfigVersion"]);
        (await PrivilegesAsync(Admin, "DELETE")).ShouldBeEmpty();

        (await PrivilegesAsync(Worker, "SELECT")).ShouldBe([.. configRead, .. workerTables], ignoreOrder: true);
        (await PrivilegesAsync(Worker, "INSERT")).ShouldBe([.. workerTables, "audit.AuditLog"], ignoreOrder: true);
        (await PrivilegesAsync(Worker, "UPDATE")).ShouldBe(workerTables, ignoreOrder: true);
        (await PrivilegesAsync(Worker, "DELETE")).ShouldBe(workerTables, ignoreOrder: true);
    }

    [Fact]
    public async Task Init_sql_makes_the_ddl_role_own_the_database_without_superuser_rights()
    {
        var superuser = database.Connection(null);
        (await Sql.ScalarAsync<string>(superuser, "SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname = 'ragdb'")).ShouldBe(Migrator);
        (await Sql.ScalarAsync<long>(superuser, $"""
            SELECT count(*) FROM pg_roles WHERE rolname LIKE 'ume_rag_%' AND (rolsuper OR rolcreaterole OR rolcreatedb)
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
        await Sql.ShouldBeDeniedAsync(database.Connection(Migrator), "ALTER ROLE ume_rag_api CONNECTION LIMIT 100");
        await Sql.ShouldBeDeniedAsync(database.Connection(Migrator), "ALTER ROLE ume_rag_api IN DATABASE ragdb SET statement_timeout = 0");
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
        await Sql.ShouldBeDeniedAsync(database.Connection(Admin), "SELECT config.rag_config_changed()");
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

    /// <summary>Tables (schema.table) on which <paramref name="role"/> holds <paramref name="privilege"/> for the whole table.</summary>
    private async Task<List<string>> PrivilegesAsync(string role, string privilege) => await Sql.ListAsync(database.Connection(null), $"""
        SELECT table_schema || '.' || table_name FROM information_schema.table_privileges
        WHERE table_schema IN ('config', 'content', 'chat', 'audit') AND grantee = '{role}' AND privilege_type = '{privilege}'
        """);
}
