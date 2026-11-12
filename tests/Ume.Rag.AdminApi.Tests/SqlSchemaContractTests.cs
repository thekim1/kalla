using Ume.Rag.TestKit;

namespace Ume.Rag.AdminApi.Tests;

/// <summary>
/// The schema contract of rag-admin's raw SQL (B-34, #510): its EF <c>SqlQuery</c>/<c>ExecuteSql</c> statements (the
/// interpolated format strings, holes as parameters) and any SQL constant or literal of <c>Ume.Rag.AdminApi</c> must prepare
/// against the migrated database, and the result columns of each that returns rows must equal <c>sql-shapes.json</c> next
/// to this file: <c>SqlQuery&lt;T&gt;</c> maps columns by name (<c>"Value"</c> for a scalar), so a renamed column fails
/// here, not at run time. rag-admin's shared stores (<c>Ume.Rag.Infrastructure</c>) are in rag-api's contract.
/// </summary>
public sealed class SqlSchemaContractTests(SqlContractDatabase database) : IClassFixture<SqlContractDatabase>
{
    private static readonly string ShapesFile =
        TestPostgres.RepositoryFile(Path.Combine("tests", "Ume.Rag.AdminApi.Tests", "sql-shapes.json"));

    /// <summary>Parameter types Postgres cannot infer from the statement alone (the code binds them typed), by 1-based position.</summary>
    private static readonly Dictionary<string, Dictionary<int, string>> ParameterTypes = new(StringComparer.Ordinal);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_admin_statement_prepares_and_returns_the_shape_its_reader_expects()
    {
        var statements = SqlContract.Statements(typeof(OrganisationSync).Assembly, "Ume.Rag.");
        statements.Count.ShouldBeGreaterThan(10);

        var described = await SqlContract.DescribeAsync(database.Connection(null), statements, ParameterTypes, Ct);

        described.Failures.ShouldBeEmpty();
        var (expected, actual) = await SqlContract.CompareAsync(ShapesFile, described.Shapes, Ct);
        expected.ShouldBe(actual,
            "A statement's result columns changed: check its reader, then run with UME_RAG_UPDATE_CONTRACTS=1 to regenerate.");
    }
}

/// <summary>A migrated ragdb of its own for the schema contract.</summary>
public sealed class SqlContractDatabase : RagDatabase, IAsyncLifetime;
