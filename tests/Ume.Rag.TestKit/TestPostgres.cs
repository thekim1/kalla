using Testcontainers.PostgreSql;

namespace Ume.Rag.TestKit;

/// <summary>The Postgres container every test project runs against.</summary>
public static class TestPostgres
{
    /// <summary>
    /// The stock pgvector image ume-rag/postgres is built from (repo-and-deployment §5.2), pinned by digest; the same pin
    /// as the AppHost. <c>UME_RAG_TEST_POSTGRES_IMAGE</c> overrides it, so CI can run against the built ume-rag/postgres
    /// image (hand-off H3).
    /// </summary>
    public const string DefaultImage =
        "pgvector/pgvector:0.8.7-pg17-trixie@sha256:7a7e9f22015b67edb4bef5c59daeebcd7e74bfa570df6ce60ae01237c8648a84";

    public static string Image =>
        Environment.GetEnvironmentVariable("UME_RAG_TEST_POSTGRES_IMAGE") is { Length: > 0 } image ? image : DefaultImage;

    public static PostgreSqlBuilder Builder() => new(Image);
}
