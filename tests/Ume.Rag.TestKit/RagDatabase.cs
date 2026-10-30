using System.Text;
using Npgsql;
using Testcontainers.PostgreSql;
using Ume.Rag.Infrastructure.Persistence;

namespace Ume.Rag.TestKit;

/// <summary>
/// ragdb as production builds it: the real <c>deploy/postgres/init.sql</c> run by the bootstrap superuser with the role
/// passwords as secret files, then the migrations and <c>app-roles.sql</c> applied as the DDL role (data-model §1, §10).
/// A test project makes it an xUnit fixture with <c>sealed class RagDatabaseFixture : RagDatabase, IAsyncLifetime;</c>
/// (TestKit does not reference xUnit).
/// </summary>
public class RagDatabase : IAsyncDisposable
{
    public const string Migrator = "ume_rag_migrator";
    public const string Api = "ume_rag_api";
    public const string Admin = "ume_rag_admin";
    public const string Worker = "ume_rag_worker";

    private const string Password = "role-test-password";
    private static readonly string[] SecretFiles = ["migrator", "api", "admin", "worker"];
    private readonly PostgreSqlContainer _postgres;

    public RagDatabase()
    {
        var builder = TestPostgres.Builder();
        foreach (var name in SecretFiles)
        {
            builder = builder.WithResourceMapping(Encoding.UTF8.GetBytes(Password + "\n"), $"/run/postgres-secrets/{name}_password");
        }

        _postgres = builder.Build();
    }

    public static string RolesSqlPath => TestPostgres.RepositoryFile(Path.Combine("deploy", "postgres", "app-roles.sql"));

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();
        var initSql = await File.ReadAllTextAsync(TestPostgres.RepositoryFile(Path.Combine("deploy", "postgres", "init.sql")));
        var result = await _postgres.ExecScriptAsync("\\set ON_ERROR_STOP on\n" + initSql);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"init.sql failed ({result.ExitCode}): {result.Stderr}");
        }

        await using var db = TestPostgres.Context(Connection(Migrator));
        await RagDatabaseMigrator.MigrateAsync(db, RolesSqlPath, CancellationToken.None);
    }

    /// <summary>Runs app-roles.sql again as the migration service does.</summary>
    public async Task ApplyRolesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = TestPostgres.Context(Connection(Migrator));
        await RagDatabaseMigrator.MigrateAsync(db, RolesSqlPath, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _postgres.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    /// <summary>A connection string to ragdb as <paramref name="role"/>, or as the bootstrap superuser when null.</summary>
    public string Connection(string? role, bool pooling = true)
    {
        var builder = new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = "ragdb", Pooling = pooling };
        if (role is not null)
        {
            builder.Username = role;
            builder.Password = Password;
        }

        return builder.ConnectionString;
    }
}
