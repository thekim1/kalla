using Microsoft.EntityFrameworkCore;
using Npgsql;
using Kalla.Infrastructure.Persistence;

namespace Kalla.MigrationService;

/// <summary>
/// The HNSW retrieval defaults that <c>deploy/postgres/init.sql</c> sets per service role (knowledge design §6.4). The
/// development stack connects every service as the bootstrap superuser, so the role defaults never apply there; in
/// Development they are set on the database instead and reach every new connection.
/// </summary>
public static class DevDatabaseSettings
{
    public static readonly IReadOnlyList<(string Name, string Value)> Hnsw =
    [
        ("hnsw.ef_search", "100"),
        ("hnsw.iterative_scan", "relaxed_order"),
        ("hnsw.max_scan_tuples", "100000"),
    ];

    public static async Task ApplyAsync(RagDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var database = new NpgsqlConnectionStringBuilder(db.Database.GetConnectionString()).Database
            ?? throw new InvalidOperationException("The connection string names no database.");
        var quoted = "\"" + database.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        foreach (var (name, value) in Hnsw)
        {
#pragma warning disable EF1002 // identifiers and fixed values only: ALTER DATABASE takes no parameters
            await db.Database.ExecuteSqlRawAsync($"ALTER DATABASE {quoted} SET {name} = {value}", cancellationToken);
#pragma warning restore EF1002
        }
    }
}
