using Microsoft.EntityFrameworkCore;

namespace Ume.Rag.Infrastructure.Persistence;

/// <summary>
/// The migration steps of data-model §10, shared by the migration service and the test kit so tests run exactly them:
/// EF Core migrations, then <c>app-roles.sql</c> when a path is given, then the run-item partition maintenance.
/// </summary>
public static class RagDatabaseMigrator
{
    public static async Task MigrateAsync(RagDbContext db, string? rolesSqlPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(() => db.Database.MigrateAsync(cancellationToken));

        if (!string.IsNullOrWhiteSpace(rolesSqlPath))
        {
            await db.Database.ExecuteSqlRawAsync(await File.ReadAllTextAsync(rolesSqlPath, cancellationToken), cancellationToken);
        }

        await db.Database.ExecuteSqlRawAsync("SELECT * FROM content.rag_partition_maintain()", cancellationToken);
    }
}
