using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ume.Rag.Infrastructure.Persistence;
using Ume.Rag.TestKit;

namespace Ume.Rag.IntegrationTests;

/// <summary>
/// #168: the AppHost's Postgres runs no init.sql, so the migrator, as the dev superuser (no roles path), creates the
/// <c>vector</c> extension itself before the migrations need it.
/// </summary>
public sealed class DevMigrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_dev_migrator_creates_the_vector_extension_on_a_bare_database()
    {
        await using var postgres = TestPostgres.Builder().WithDatabase("ragdb").Build();
        await postgres.StartAsync(Ct);
        var connection = postgres.GetConnectionString();
        await using (var bare = new NpgsqlConnection(connection))
        {
            await bare.OpenAsync(Ct);
            await using var check = new NpgsqlCommand("SELECT count(*) FROM pg_extension WHERE extname = 'vector'", bare);
            ((long)(await check.ExecuteScalarAsync(Ct))!).ShouldBe(0);
        }

        await using var db = TestPostgres.Context(connection);
        await RagDatabaseMigrator.MigrateAsync(db, rolesSqlPath: null, Ct);

        (await db.Database.GetPendingMigrationsAsync(Ct)).ShouldBeEmpty();
        await using var after = new NpgsqlConnection(connection);
        await after.OpenAsync(Ct);
        await using var again = new NpgsqlCommand("SELECT count(*) FROM pg_extension WHERE extname = 'vector'", after);
        ((long)(await again.ExecuteScalarAsync(Ct))!).ShouldBe(1);
    }
}
