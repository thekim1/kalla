using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Kalla.Infrastructure.Persistence;

namespace Kalla.TestKit;

/// <summary>The Postgres container every test project runs against.</summary>
public static class TestPostgres
{
    /// <summary>
    /// The stock pgvector image ume-rag/postgres is built from (repo-and-deployment §5.2), pinned by digest; the same pin
    /// as the AppHost. <c>UME_RAG_TEST_POSTGRES_IMAGE</c> overrides it, so CI can run against the built ume-rag/postgres
    /// image (hand-off H3).
    /// </summary>
    public const string DefaultImage =
        "pgvector/pgvector:0.8.7-pg17-trixie@sha256:89947206f52eac3a9df9b663df7e76987cbdc623be6576b3a524953d568700d7";

    public static string Image =>
        Environment.GetEnvironmentVariable("UME_RAG_TEST_POSTGRES_IMAGE") is { Length: > 0 } image ? image : DefaultImage;

    /// <summary>
    /// The container builder. <c>UME_RAG_TEST_POSTGRES_TMPFS=1</c> keeps the data directory on a tmpfs, for the explicit
    /// measurements that load millions of chunks (no gigabytes written to the developer's disk).
    /// </summary>
    public static PostgreSqlBuilder Builder() =>
        Environment.GetEnvironmentVariable("UME_RAG_TEST_POSTGRES_TMPFS") == "1"
            ? new PostgreSqlBuilder(Image).WithTmpfsMount("/var/lib/postgresql/data")
            : new(Image);

    /// <summary>A context with the connection's rights, outside any host.</summary>
    public static RagDbContext Context(string connectionString) =>
        new(new DbContextOptionsBuilder<RagDbContext>().UseNpgsql(connectionString, RagDbContext.ConfigureNpgsql).Options);

    /// <summary>A file of the repository, found by walking up from the test output directory.</summary>
    public static string RepositoryFile(string relativePath)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(path))
            {
                return path;
            }
        }

        throw new FileNotFoundException($"{relativePath} not found above the test output directory.");
    }
}
