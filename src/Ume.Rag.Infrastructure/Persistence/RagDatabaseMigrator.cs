using Microsoft.EntityFrameworkCore;

namespace Ume.Rag.Infrastructure.Persistence;

/// <summary>
/// The migration steps of data-model §10, shared by the migration service and the test kit so tests run exactly them:
/// EF Core migrations (preceded, when no roles path is given, by <c>CREATE EXTENSION IF NOT EXISTS vector</c>: the dev
/// superuser path), then <c>app-roles.sql</c> when a path is given, then the run-item partition maintenance.
/// </summary>
public static class RagDatabaseMigrator
{
    public static async Task MigrateAsync(RagDbContext db, string? rolesSqlPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (string.IsNullOrWhiteSpace(rolesSqlPath))
        {
            // Development (#168): the AppHost's Postgres runs no init.sql, and the migrator is its superuser. Production
            // and the tests create the extension in deploy/postgres/init.sql; there this is skipped.
            await db.Database.ExecuteSqlRawAsync("CREATE EXTENSION IF NOT EXISTS vector", cancellationToken);
        }

        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(() => db.Database.MigrateAsync(cancellationToken));

        if (!string.IsNullOrWhiteSpace(rolesSqlPath))
        {
            await db.Database.ExecuteSqlRawAsync(await File.ReadAllTextAsync(rolesSqlPath, cancellationToken), cancellationToken);
        }

        await db.Database.ExecuteSqlRawAsync("SELECT * FROM content.rag_partition_maintain()", cancellationToken);
    }
}
