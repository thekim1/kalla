using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Ume.Rag.Infrastructure;
using Ume.Rag.Infrastructure.Persistence;
using Ume.Rag.TestKit;
using static Ume.Rag.TestKit.RagDatabase;

namespace Ume.Rag.IntegrationTests;

/// <summary>The schema of P0 #6: migrations, the job queue's dedup index, run-item partitions and the config outbox.</summary>
[Collection(RagDatabaseGroup.Name)]
public sealed class SchemaTests(RagDatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Migrations_match_the_model_and_keep_their_history_in_the_config_schema()
    {
        await using var db = TestPostgres.Context(database.Connection(Migrator));
        db.Database.HasPendingModelChanges().ShouldBeFalse();
        (await db.Database.GetPendingMigrationsAsync(Ct)).ShouldBeEmpty();
        (await Sql.ScalarAsync<string>(database.Connection(null), """SELECT to_regclass('config."__EFMigrationsHistory"')::text""")).ShouldNotBeNull();
        (await Sql.ScalarAsync<string>(database.Connection(null), """SELECT to_regclass('public."__EFMigrationsHistory"')::text""")).ShouldBeNull();
    }

    [Fact]
    public async Task The_service_registration_finds_the_history_in_the_config_schema()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:ragdb"] = database.Connection(Admin),
        });
        builder.AddRagDatabase();
        using var host = builder.Build();
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RagDbContext>();

        (await db.Database.GetAppliedMigrationsAsync(Ct)).ShouldBe(db.Database.GetMigrations());
    }

    [Fact]
    public async Task EF_and_raw_Npgsql_share_one_connection_pool()
    {
        // Issue #22: one NpgsqlDataSource per process, so EF and raw SQL together stay within Maximum Pool Size.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:ragdb"] = new NpgsqlConnectionStringBuilder(database.Connection(Worker)) { MaxPoolSize = 2, Timeout = 2 }.ConnectionString,
        });
        builder.AddRagDatabase();
        using var host = builder.Build();
        var dataSource = host.Services.GetRequiredService<NpgsqlDataSource>();
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RagDbContext>();

        await db.Database.OpenConnectionAsync(Ct);
        await using var raw = await dataSource.OpenConnectionAsync(Ct);

        // Both pool slots are taken by EF and raw SQL together: a third connection waits for the 2 s timeout.
        var exhausted = await Should.ThrowAsync<NpgsqlException>(async () => await dataSource.OpenConnectionAsync(Ct));
        exhausted.Message.ShouldContain("pool", Case.Insensitive);
        await db.Database.CloseConnectionAsync();
        await using var third = await dataSource.OpenConnectionAsync(Ct);

        // Aspire's enrichment still adds the context's health check.
        host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckServiceOptions>>()
            .Value.Registrations.ShouldContain(r => r.Name.Contains(nameof(RagDbContext), StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_shared_data_source_maps_pgvector_for_binary_copy()
    {
        // #110 (to-verify #21, #119): the index writer can COPY embeddings in binary on the process's one data source
        // (superuser only for the temp table; the mapping is the same for every role).
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:ragdb"] = database.Connection(null) });
        builder.AddRagDatabase();
        using var host = builder.Build();
        await using var connection = await host.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync(Ct);
        await using (var create = new NpgsqlCommand("""CREATE TEMP TABLE "VectorCopy" ("Id" integer, "Embedding" vector(3))""", connection))
        {
            await create.ExecuteNonQueryAsync(Ct);
        }

        var embedding = new Pgvector.Vector(new[] { 0.25f, -1f, 3.5f });
        await using (var writer = await connection.BeginBinaryImportAsync("""COPY "VectorCopy" ("Id", "Embedding") FROM STDIN (FORMAT BINARY)""", Ct))
        {
            await writer.StartRowAsync(Ct);
            await writer.WriteAsync(1, Ct);
            await writer.WriteAsync(embedding, "vector", Ct);
            await writer.CompleteAsync(Ct);
        }

        await using var read = new NpgsqlCommand("""SELECT "Embedding" FROM "VectorCopy" WHERE "Id" = 1""", connection);
        ((Pgvector.Vector)(await read.ExecuteScalarAsync(Ct))!).ToArray().ShouldBe(embedding.ToArray());
    }

    [Fact]
    public async Task Concurrent_enqueues_with_one_dedup_key_insert_one_job_until_it_finishes()
    {
        // data-model §4.2 and §14: ON CONFLICT infers the partial unique index under concurrent enqueues.
        var key = "fetch:public:" + Guid.NewGuid().ToString("N");
        var departmentId = Guid.NewGuid();
        const string payload = "{}";
        string Enqueue() => $"""
            INSERT INTO content."Jobs" ("Id", "Class", "DepartmentId", "Classification", "DedupKey", "Payload", "MaxAttempts")
            VALUES ('{Guid.CreateVersion7()}', 'fetch', '{departmentId}', 'public', '{key}', '{payload}', 5)
            ON CONFLICT ("DedupKey") WHERE "State" IN ('queued','running') DO NOTHING
            """;

        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Sql.ExecuteAsync(database.Connection(Worker), Enqueue())));
        (await CountAsync()).ShouldBe(1);

        await Sql.ExecuteAsync(database.Connection(Worker), $"""UPDATE content."Jobs" SET "State" = 'running' WHERE "DedupKey" = '{key}'""");
        await Sql.ExecuteAsync(database.Connection(Worker), Enqueue());
        (await CountAsync()).ShouldBe(1);

        await Sql.ExecuteAsync(database.Connection(Worker), $"""UPDATE content."Jobs" SET "State" = 'succeeded', "CompletedAt" = now() WHERE "DedupKey" = '{key}'""");
        await Sql.ExecuteAsync(database.Connection(Worker), Enqueue());
        (await CountAsync()).ShouldBe(2);

        Task<long> CountAsync() => Sql.ScalarAsync<long>(database.Connection(Worker), $"""SELECT count(*) FROM content."Jobs" WHERE "DedupKey" = '{key}'""");
    }

    [Fact]
    public async Task Run_items_land_in_the_partition_of_their_utc_day()
    {
        var superuser = database.Connection(null);
        var today = DateTime.UtcNow.Date;
        var partitions = await Sql.ListAsync(superuser, """
            SELECT c.relname FROM pg_inherits h JOIN pg_class c ON c.oid = h.inhrelid WHERE h.inhparent = 'content."RunItems"'::regclass
            """);
        for (var i = 0; i <= 7; i++)
        {
            partitions.ShouldContain(PartitionName(today.AddDays(i)));
        }

        partitions.ShouldContain("RunItems_default");

        var runId = Guid.NewGuid();
        await Sql.ExecuteAsync(database.Connection(Worker), $"""
            INSERT INTO content."RunItems" ("RunId", "Classification", "Identity", "Outcome") VALUES ('{runId}', 'public', 'https://www.umea.se/', 'added')
            """);
        (await Sql.ScalarAsync<string>(database.Connection(Worker), $"""SELECT tableoid::regclass::text FROM content."RunItems" WHERE "RunId" = '{runId}'"""))
            .ShouldBe($"content.\"{PartitionName(today)}\"");
    }

    [Fact]
    public async Task Maintenance_drops_partitions_past_retention_and_is_idempotent()
    {
        var migrator = database.Connection(Migrator);
        var expired = DateTime.UtcNow.Date.AddDays(-91);
        var kept = DateTime.UtcNow.Date.AddDays(-89);
        foreach (var day in new[] { expired, kept })
        {
            await Sql.ExecuteAsync(migrator, $"""
                CREATE TABLE content."{PartitionName(day)}" PARTITION OF content."RunItems"
                FOR VALUES FROM ('{day:yyyy-MM-dd}T00:00:00Z') TO ('{day.AddDays(1):yyyy-MM-dd}T00:00:00Z')
                """);
        }

        (await MaintainAsync()).ShouldBe((0, 1, 0L));

        (await Sql.ScalarAsync<string>(migrator, $"SELECT to_regclass('content.\"{PartitionName(expired)}\"')::text")).ShouldBeNull();
        (await Sql.ScalarAsync<string>(migrator, $"SELECT to_regclass('content.\"{PartitionName(kept)}\"')::text")).ShouldNotBeNull();
        await Sql.ExecuteAsync(migrator, $"""DROP TABLE content."{PartitionName(kept)}" """);
    }

    [Fact]
    public async Task Maintenance_moves_rows_that_landed_in_the_default_partition_into_their_new_day()
    {
        // Issue #10: a row in DEFAULT for a day without a partition must not block creating that day's partition.
        var migrator = database.Connection(Migrator);
        var day = DateTime.UtcNow.Date.AddDays(7);
        await Sql.ExecuteAsync(migrator, $"""DROP TABLE content."{PartitionName(day)}" """);
        var runId = Guid.NewGuid();
        await Sql.ExecuteAsync(database.Connection(Worker), $"""
            INSERT INTO content."RunItems" ("CreatedAt", "RunId", "Classification", "Identity", "Outcome")
            VALUES ('{day:yyyy-MM-dd}T12:00:00Z', '{runId}', 'public', 'https://www.umea.se/', 'added')
            """);
        (await PartitionOfAsync()).ShouldBe("content.\"RunItems_default\"");
        var id = await Sql.ScalarAsync<long>(database.Connection(Worker), $"""SELECT "Id" FROM content."RunItems" WHERE "RunId" = '{runId}'""");

        (await MaintainAsync()).ShouldBe((1, 0, 0L));
        (await PartitionOfAsync()).ShouldBe($"content.\"{PartitionName(day)}\"");
        (await Sql.ScalarAsync<long>(database.Connection(Worker), $"""SELECT "Id" FROM content."RunItems" WHERE "RunId" = '{runId}'""")).ShouldBe(id);
        (await MaintainAsync()).ShouldBe((0, 0, 0L));

        // Later rows of that day go to the attached partition, and its indexes came with it.
        await Sql.ExecuteAsync(database.Connection(Worker), $"""
            INSERT INTO content."RunItems" ("CreatedAt", "RunId", "Classification", "Identity", "Outcome")
            VALUES ('{day:yyyy-MM-dd}T13:00:00Z', '{runId}', 'public', 'https://www.umea.se/a', 'added')
            """);
        (await Sql.ScalarAsync<long>(database.Connection(null), $"""
            SELECT count(*) FROM pg_indexes WHERE schemaname = 'content' AND tablename = '{PartitionName(day)}'
            """)).ShouldBe(2);

        Task<string> PartitionOfAsync() => Sql.ScalarAsync<string>(database.Connection(Worker),
            $"""SELECT tableoid::regclass::text FROM content."RunItems" WHERE "RunId" = '{runId}' ORDER BY "Id" LIMIT 1""");
    }

    private async Task<(int Created, int Dropped, long DefaultRows)> MaintainAsync()
    {
        await using var connection = new NpgsqlConnection(database.Connection(Worker));
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT created, dropped, default_rows FROM content.rag_partition_maintain()", connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).ShouldBeTrue();
        return (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt64(2));
    }

    [Fact]
    public async Task A_config_transaction_bumps_the_version_once_writes_the_outbox_and_notifies()
    {
        // A probe table with the trigger (config tables add theirs in their own migrations), in a schema of its own so
        // the grant checks of DatabaseRoleTests do not see it.
        var migrator = database.Connection(Migrator);
        await Sql.ExecuteAsync(migrator, """
            CREATE SCHEMA IF NOT EXISTS probe;
            CREATE TABLE IF NOT EXISTS probe."Probes" ("Id" uuid PRIMARY KEY, "Name" text);
            CREATE OR REPLACE TRIGGER "TR_Probes_ConfigChanged" AFTER INSERT OR UPDATE OR DELETE ON probe."Probes"
                FOR EACH ROW EXECUTE FUNCTION config.rag_config_changed('true');
            """);

        await using var listener = new NpgsqlConnection(database.Connection(Api, pooling: false));
        await listener.OpenAsync(Ct);
        var notified = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.Notification += (_, e) => notified.TrySetResult(e.Payload);
        await using (var listen = new NpgsqlCommand("LISTEN rag_config", listener))
        {
            await listen.ExecuteNonQueryAsync(Ct);
        }

        var before = await VersionAsync();
        Guid[] ids = [Guid.NewGuid(), Guid.NewGuid()];
        await Sql.ExecuteAsync(migrator, $"""
            BEGIN;
            INSERT INTO probe."Probes" VALUES ('{ids[0]}', 'a');
            INSERT INTO probe."Probes" VALUES ('{ids[1]}', 'b');
            COMMIT;
            """);

        var version = await VersionAsync();
        version.ShouldBe(before + 1);
        (await Sql.ListAsync(database.Connection(Api), $"""
            SELECT "EntityType" || ':' || "EntityId" || ':' || "Operation" || ':' || "SecurityRelevant"
            FROM config."ConfigOutbox" WHERE "Version" = {version}
            """)).ShouldBe([$"Probes:{ids[0]}:I:true", $"Probes:{ids[1]}:I:true"], ignoreOrder: true);

        await listener.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        (await notified.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct)).ShouldBe(version.ToString(System.Globalization.CultureInfo.InvariantCulture));

        await Sql.ExecuteAsync(migrator, $"""DELETE FROM probe."Probes" WHERE "Id" = '{ids[0]}'""");
        (await VersionAsync()).ShouldBe(version + 1);
        (await Sql.ScalarAsync<string>(database.Connection(Api), $"""SELECT "Operation" FROM config."ConfigOutbox" WHERE "Version" = {version + 1}""")).ShouldBe("D");

        await Sql.ExecuteAsync(migrator, "DROP SCHEMA probe CASCADE");

        Task<long> VersionAsync() => Sql.ScalarAsync<long>(database.Connection(Api), """SELECT "Version" FROM config."ConfigVersion" WHERE "Id" = 1""");
    }

    private static string PartitionName(DateTime day) => $"RunItems_{day:yyyyMMdd}";
}
