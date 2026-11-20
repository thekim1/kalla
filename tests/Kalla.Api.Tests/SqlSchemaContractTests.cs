using System.Reflection;
using Kalla.Retrieval.Postgres;
using Kalla.TestKit;

namespace Kalla.Api.Tests;

/// <summary>
/// The schema contract of rag-api's raw SQL (B-34, #510; the worker's is A-28): every SQL constant and inline SQL literal of
/// rag-api and the libraries it runs (<c>Kalla.Api</c>, <c>Answering</c>, <c>Retrieval</c>, <c>Infrastructure</c>), and
/// the retrieval statements K and V built for each filter combination, must prepare against the migrated database, and the
/// result columns of each that returns rows must equal <c>sql-shapes.json</c> next to this file (<see cref="SqlContract"/>).
/// A changed shape fails here: check the statement's reader, then regenerate the file with <c>UME_RAG_UPDATE_CONTRACTS=1</c>.
/// </summary>
public sealed class SqlSchemaContractTests(SqlContractDatabase database) : IClassFixture<SqlContractDatabase>
{
    private static readonly string ShapesFile =
        TestPostgres.RepositoryFile(Path.Combine("tests", "Kalla.Api.Tests", "sql-shapes.json"));

    private static readonly Assembly[] Assemblies =
    [
        typeof(Program).Assembly,
        typeof(Kalla.Answering.KnowledgeService).Assembly,
        typeof(RetrievalStatements).Assembly,
        typeof(Kalla.Infrastructure.Persistence.RagDbContext).Assembly,
    ];

    /// <summary>Parameter types Postgres cannot infer from the statement alone (the code binds them typed), by 1-based position.</summary>
    private static readonly Dictionary<string, Dictionary<int, string>> ParameterTypes = new(StringComparer.Ordinal);

    /// <summary>
    /// Literals that are SQL fragments, not statements (the rest of the text is appended in code); what they build is
    /// described whole below or by the test that runs it.
    /// </summary>
    private static readonly Dictionary<string, string> Fragments = new(StringComparer.Ordinal)
    {
        ["Api.FeedbackImporter.RunOnceAsync#1"] = "SELECT pg_try_advisory_xact_lock({LockKey}): a scalar boolean, no columns read",
        ["Retrieval.Postgres.RetrievalStatements.Keyword#1"] = "K's text, built in Keyword(); described whole in Built()",
        ["Retrieval.Postgres.RetrievalStatements.Vector#1"] = "V's text, built in Vector(); described whole in Built()",
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_api_statement_prepares_and_returns_the_shape_its_reader_expects()
    {
        var statements = Assemblies
            .SelectMany(a => SqlContract.Statements(a, "Källa.", Assemblies))
            .Where(s => !Fragments.ContainsKey(s.Name))
            .Concat(Built())
            .ToList();
        statements.Count.ShouldBeGreaterThan(50);

        var described = await SqlContract.DescribeAsync(database.Connection(null), statements, ParameterTypes, Ct);

        described.Failures.ShouldBeEmpty();
        var (expected, actual) = await SqlContract.CompareAsync(ShapesFile, described.Shapes, Ct);
        expected.ShouldBe(actual,
            "A statement's result columns changed: check its reader, then run with UME_RAG_UPDATE_CONTRACTS=1 to regenerate.");
    }

    [Fact]
    public void Every_fragment_named_here_still_exists()
    {
        var names = Assemblies.SelectMany(a => SqlContract.Statements(a, "Källa.", Assemblies)).Select(s => s.Name).ToHashSet();

        Fragments.Keys.Where(k => !names.Contains(k)).ShouldBeEmpty();
    }

    /// <summary>
    /// K (stored and computed vector) and V (approximate and exact) for every filter combination: each must prepare; the
    /// filters are predicates only, so each statement has one shape.
    /// </summary>
    private static IEnumerable<SqlContract.Statement> Built()
    {
        var profile = Guid.Parse("00000000-0000-0000-0000-0000000000b3");
        for (var filters = FilterFragments.None; filters <= FilterFragments.All; filters++)
        {
            yield return new("Retrieval.Postgres.RetrievalStatements.Keyword()",
                RetrievalStatements.Keyword(profile, 10, filters));
            yield return new("Retrieval.Postgres.RetrievalStatements.Keyword(WithoutDecompounder)",
                RetrievalStatements.Keyword(profile, 10, filters, KeywordVector.WithoutDecompounder));
            yield return new("Retrieval.Postgres.RetrievalStatements.VectorApproximate()",
                RetrievalStatements.VectorApproximate(profile, 3, 10, filters));
            yield return new("Retrieval.Postgres.RetrievalStatements.VectorExact()",
                RetrievalStatements.VectorExact(profile, 3, 10, filters));
        }
    }
}

/// <summary>A migrated ragdb of its own for the schema contract.</summary>
public sealed class SqlContractDatabase : RagDatabase, IAsyncLifetime;
