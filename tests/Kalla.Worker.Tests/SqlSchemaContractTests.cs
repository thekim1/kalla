using System.Data;
using System.Reflection;
using System.Text;
using Npgsql;
using Kalla.TestKit;

namespace Kalla.Worker.Tests;

/// <summary>
/// The schema contract of the worker's raw SQL (A-28, #510). The stores read rows by position (<c>reader.GetGuid(0)</c>),
/// so a select list that changes order, or a column renamed in the schema, breaks a reader the compiler cannot see. Every
/// SQL constant of the worker assembly is described with <see cref="CommandBehavior.SchemaOnly"/> against the migrated
/// database (Parse and Describe, nothing runs): it must prepare, and the result columns of each statement that returns rows
/// (name and Postgres type, in order) must equal <c>sql-shapes.json</c> next to this file, the shapes the readers were
/// written against. A changed shape fails here; check the statement's reader, then regenerate the file with
/// <c>KALLA_UPDATE_CONTRACTS=1</c> and commit it.
/// </summary>
[Collection(RagDatabaseGroup1.Name)]
public sealed class SqlSchemaContractTests(RagDatabaseFixture database)
{
    private static readonly string ShapesFile =
        TestPostgres.RepositoryFile(Path.Combine("tests", "Kalla.Worker.Tests", "sql-shapes.json"));

    /// <summary>
    /// Parameter types Postgres cannot infer from the statement alone (the code binds them typed), by 1-based position;
    /// the other parameters are left for Postgres to infer.
    /// </summary>
    private static readonly Dictionary<string, Dictionary<int, string>> ParameterTypes = new(StringComparer.Ordinal)
    {
        ["Engine.EngineCommit.UploadSlotSql"] = new() { [4] = "text" },
        ["Engine.IndexWriter.StateSql"] = new() { [2] = "text" },
        ["Engine.IndexWriter.SweepSql"] = new() { [4] = "timestamp with time zone" },
        ["Engine.KeywordStatistics.InsertSql"] = new() { [3] = "integer", [4] = "double precision" },
        ["Processing.Persistence.PostgresEmbeddingCache.TouchSql"] = new() { [4] = "timestamp with time zone" },
        ["Processing.Persistence.PostgresNodeOutputStore.TouchSql"] = new() { [2] = "timestamp with time zone" },
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_worker_statement_prepares_and_returns_the_shape_its_reader_expects()
    {
        var statements = Statements(typeof(Kalla.Worker.Engine.KeywordStatistics).Assembly);
        statements.Count.ShouldBeGreaterThan(150);

        var failures = new List<string>();
        var shapes = new SortedDictionary<string, string[]>(StringComparer.Ordinal);
        await using var connection = new NpgsqlConnection(database.Connection(null));
        await connection.OpenAsync(Ct);
        foreach (var (name, sql) in statements)
        {
            try
            {
                var columns = await DescribeAsync(connection, sql, ParameterTypes.GetValueOrDefault(name));
                if (columns.Length > 0)
                {
                    shapes[name] = columns;
                }
            }
            catch (PostgresException e)
            {
                failures.Add($"{name}: {e.SqlState} {e.MessageText}");
            }
        }

        failures.ShouldBeEmpty();
        var json = Render(shapes);
        if (Environment.GetEnvironmentVariable("KALLA_UPDATE_CONTRACTS") == "1")
        {
            await File.WriteAllTextAsync(ShapesFile, json, Ct);
        }

        (await File.ReadAllTextAsync(ShapesFile, Ct)).ReplaceLineEndings("\n").ShouldBe(json,
            "A statement's result columns changed: check its reader, then run with KALLA_UPDATE_CONTRACTS=1 to regenerate.");
    }

    /// <summary>The result columns (<c>name type</c>) of <paramref name="sql"/>, described without running it.</summary>
    private static async Task<string[]> DescribeAsync(NpgsqlConnection connection, string sql, Dictionary<int, string>? types)
    {
        await using var transaction = await connection.BeginTransactionAsync(Ct);
#pragma warning disable CA2100 // The worker's own SQL constants.
        await using var command = new NpgsqlCommand(sql, connection, transaction);
#pragma warning restore CA2100
        for (var position = 1; types is not null && position <= types.Keys.Max(); position++)
        {
            command.Parameters.Add(types.TryGetValue(position, out var type)
                ? new NpgsqlParameter { DataTypeName = type, Value = DBNull.Value }
                : new NpgsqlParameter { Value = DBNull.Value });
        }

        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SchemaOnly, Ct);
        var columns = (await reader.GetColumnSchemaAsync(Ct)).Select(c => $"{c.ColumnName} {c.DataTypeName}").ToArray();
        await reader.CloseAsync();
        await transaction.RollbackAsync(Ct);
        return columns;
    }

    /// <summary>Every <c>const string</c> of <paramref name="assembly"/> that reads as SQL, by <c>Type.Field</c>.</summary>
    private static List<(string Name, string Sql)> Statements(Assembly assembly) =>
        [.. from type in assembly.GetTypes()
            from field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            where field is { IsLiteral: true } && field.FieldType == typeof(string)
            let sql = (string)field.GetRawConstantValue()!
            let name = $"{type.FullName!.Replace("Kalla.Worker.", "", StringComparison.Ordinal)}.{field.Name}"
            where IsSql(sql)
            orderby name
            select (name, sql)];

    private static bool IsSql(string text)
    {
        var start = text.TrimStart();
        return new[] { "SELECT ", "SELECT\n", "WITH ", "INSERT ", "UPDATE ", "DELETE ", "MERGE " }
            .Any(k => start.StartsWith(k, StringComparison.OrdinalIgnoreCase));
    }

    private static string Render(SortedDictionary<string, string[]> shapes)
    {
        var json = new StringBuilder("{\n");
        var first = true;
        foreach (var (name, columns) in shapes)
        {
            json.Append(first ? "" : ",\n").Append("  \"").Append(name).Append("\": [")
                .AppendJoin(", ", columns.Select(c => $"\"{c.Replace("\"", "\\\"", StringComparison.Ordinal)}\"")).Append(']');
            first = false;
        }

        return json.Append("\n}\n").ToString();
    }
}
