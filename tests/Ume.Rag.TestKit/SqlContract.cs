using System.Data;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;

namespace Ume.Rag.TestKit;

/// <summary>
/// The schema contract of raw SQL (#510; A-28 for the worker, B-34 for the services). Readers take rows by position
/// (<c>reader.GetGuid(0)</c>) or, with EF's <c>SqlQuery</c>, by column name, so a select list that changes order or a
/// column renamed in the schema breaks a reader the compiler cannot see. <see cref="Statements"/> finds an assembly's SQL:
/// its <c>const string</c> fields and the string literals in its method bodies (inline <c>new NpgsqlCommand("""…""")</c>
/// and EF's interpolated <c>SqlQuery($"…{x}")</c>, whose format string's <c>{0}</c> holes become <c>$1</c>).
/// <see cref="DescribeAsync"/> describes each with <see cref="CommandBehavior.SchemaOnly"/> against the migrated database
/// (Parse and Describe, nothing runs); the result columns (name and Postgres type, in order) are compared with a
/// checked-in shapes file, regenerated with <c>UME_RAG_UPDATE_CONTRACTS=1</c>.
/// </summary>
public static partial class SqlContract
{
    public const string UpdateVariable = "UME_RAG_UPDATE_CONTRACTS";

    /// <summary>One SQL text and where it is: <c>Type.Field</c> for a constant, <c>Type.Method#n</c> for a literal.</summary>
    public sealed record Statement(string Name, string Sql);

    /// <summary>The result of describing statements: the shape of each that returns rows, and each that did not prepare.</summary>
    public sealed record Described(SortedDictionary<string, string[]> Shapes, List<string> Failures);

    /// <summary>
    /// Every SQL text of <paramref name="assembly"/>: its constants, then the literals of its method bodies that are not
    /// the value of a constant in <paramref name="constantsOf"/> (the compiler copies a used constant into each use).
    /// <paramref name="prefix"/> is cut from type names.
    /// </summary>
    public static List<Statement> Statements(Assembly assembly, string prefix, params Assembly[] constantsOf)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var constants = Constants(assembly, prefix);
        var known = new HashSet<string>(constants.Select(c => c.Sql), StringComparer.Ordinal);
        foreach (var other in constantsOf)
        {
            known.UnionWith(Constants(other, prefix).Select(c => c.Sql));
        }

        var literals = new List<Statement>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var type in assembly.GetTypes().Where(t => Own(t, prefix)).OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance |
                BindingFlags.DeclaredOnly;
            foreach (var method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all))
                         .OrderBy(m => m.MetadataToken))
            {
                foreach (var literal in Literals(method))
                {
                    var sql = FromFormat(literal);
                    if (!IsSql(sql) || known.Contains(literal))
                    {
                        continue;
                    }

                    var owner = Owner(type, method, prefix);
                    var n = seen[owner] = seen.GetValueOrDefault(owner) + 1;
                    literals.Add(new Statement($"{owner}#{n}", sql));
                }
            }
        }

        return [.. constants, .. literals];
    }

    /// <summary>
    /// Describes each statement on one connection, each in a transaction that is rolled back. A statement named in
    /// <paramref name="parameterTypes"/> gets its parameters bound with those Postgres types (by 1-based position; the
    /// others untyped), for parameters Postgres cannot infer from the statement alone.
    /// </summary>
    public static async Task<Described> DescribeAsync(string connectionString, IEnumerable<Statement> statements,
        IReadOnlyDictionary<string, Dictionary<int, string>> parameterTypes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statements);
        ArgumentNullException.ThrowIfNull(parameterTypes);
        var shapes = new SortedDictionary<string, string[]>(StringComparer.Ordinal);
        var failures = new List<string>();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        foreach (var (name, sql) in statements)
        {
            try
            {
                var columns = await DescribeOneAsync(connection, sql, parameterTypes.GetValueOrDefault(name), cancellationToken);
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

        return new Described(shapes, failures);
    }

    /// <summary>
    /// The shapes file's text for <paramref name="shapes"/> and the file's current text (written first when
    /// <see cref="UpdateVariable"/> is <c>1</c>); the test compares the two.
    /// </summary>
    public static async Task<(string Expected, string Actual)> CompareAsync(string shapesFile,
        SortedDictionary<string, string[]> shapes, CancellationToken cancellationToken)
    {
        var json = Render(shapes);
        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            await File.WriteAllTextAsync(shapesFile, json, cancellationToken);
        }

        var current = File.Exists(shapesFile) ? await File.ReadAllTextAsync(shapesFile, cancellationToken) : "";
        return (current.ReplaceLineEndings("\n"), json);
    }

    public static string Render(SortedDictionary<string, string[]> shapes)
    {
        ArgumentNullException.ThrowIfNull(shapes);
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

    /// <summary>
    /// Whether <paramref name="text"/> starts like a statement, in upper case as the code writes SQL (log message templates
    /// such as "… with {Kind}" are compiled to literals too).
    /// </summary>
    public static bool IsSql(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var start = text.TrimStart();
        return new[] { "SELECT ", "SELECT\n", "WITH ", "INSERT ", "UPDATE ", "DELETE ", "MERGE " }
            .Any(k => start.StartsWith(k, StringComparison.Ordinal));
    }

    private static List<Statement> Constants(Assembly assembly, string prefix) =>
        [.. from type in assembly.GetTypes()
            where Own(type, prefix)
            from field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            where field is { IsLiteral: true } && field.FieldType == typeof(string)
            let sql = (string)field.GetRawConstantValue()!
            where IsSql(sql)
            let name = $"{TypeName(type, prefix)}.{field.Name}"
            orderby name
            select new Statement(name, sql)];

    /// <summary>
    /// The project's own types under <paramref name="prefix"/>, without EF migrations (they run once, in order, on a
    /// schema that later migrations change, and no reader takes their rows).
    /// </summary>
    private static bool Own(Type type, string prefix) =>
        type.FullName is { } name && name.StartsWith(prefix, StringComparison.Ordinal) &&
        !name.Contains(".Migrations.", StringComparison.Ordinal);

    private static async Task<string[]> DescribeOneAsync(NpgsqlConnection connection, string sql, Dictionary<int, string>? types,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
#pragma warning disable CA2100 // The services' own SQL texts.
        await using var command = new NpgsqlCommand(sql, connection, transaction);
#pragma warning restore CA2100
        for (var position = 1; types is not null && position <= types.Keys.Max(); position++)
        {
            command.Parameters.Add(types.TryGetValue(position, out var type)
                ? new NpgsqlParameter { DataTypeName = type, Value = DBNull.Value }
                : new NpgsqlParameter { Value = DBNull.Value });
        }

        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SchemaOnly, cancellationToken);
        var columns = (await reader.GetColumnSchemaAsync(cancellationToken)).Select(c => $"{c.ColumnName} {c.DataTypeName}").ToArray();
        await reader.CloseAsync();
        await transaction.RollbackAsync(cancellationToken);
        return columns;
    }

    /// <summary>
    /// The string literals a method body loads (<c>ldstr</c>, opcode 0x72 with a user-string token, 0x70xxxxxx). The scan
    /// is byte-wise; a token that does not resolve is skipped, and what is not SQL is dropped by the caller.
    /// </summary>
    private static IEnumerable<string> Literals(MethodBase method)
    {
        byte[]? il;
        try
        {
            il = method.GetMethodBody()?.GetILAsByteArray();
        }
        catch (InvalidOperationException)
        {
            yield break;
        }

        if (il is null)
        {
            yield break;
        }

        for (var i = 0; i + 4 < il.Length; i++)
        {
            if (il[i] != 0x72 || il[i + 4] != 0x70)
            {
                continue;
            }

            string? text;
            try
            {
                text = method.Module.ResolveString(BitConverter.ToInt32(il, i + 1));
            }
            catch (ArgumentException)
            {
                continue;
            }

            yield return text;
            i += 4;
        }
    }

    /// <summary>A composite format string's <c>{n}</c> holes as <c>$n+1</c> (EF's interpolated <c>SqlQuery</c>).</summary>
    private static string FromFormat(string text)
    {
        if (!Hole().IsMatch(text))
        {
            return text;
        }

        return Hole().Replace(text, m => "$" + (int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) + 1))
            .Replace("{{", "{", StringComparison.Ordinal).Replace("}}", "}", StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>Type.Method</c> for a literal: the method a compiler-generated state machine, closure or local function was
    /// written in (<c>&lt;ReadAsync&gt;d__5.MoveNext</c> is <c>ReadAsync</c>).
    /// </summary>
    private static string Owner(Type type, MethodBase method, string prefix)
    {
        var written = Written(method.Name);
        var declaring = type;
        while (declaring.Name.StartsWith('<') && declaring.DeclaringType is { } outer)
        {
            // A state machine's MoveNext, or a closure class: the name the compiler derived from the written method.
            if (written is null && Written(declaring.Name) is { } fromType)
            {
                written = fromType;
            }

            declaring = outer;
        }

        return $"{TypeName(declaring, prefix)}.{written ?? method.Name}";
    }

    /// <summary>
    /// The written method a compiler-generated name derives from (<c>&lt;&lt;UpdateAsync&gt;b__0&gt;d</c> is
    /// <c>UpdateAsync</c>), or null for an ordinary name, <c>MoveNext</c> or a closure class (<c>&lt;&gt;c__DisplayClass2_0</c>).
    /// </summary>
    private static string? Written(string name) => Generated().Match(name) is { Success: true } m ? m.Groups[1].Value : null;

    [GeneratedRegex("<([^<>]+)>")]
    private static partial Regex Generated();

    private static string TypeName(Type type, string prefix) =>
        type.FullName!.Replace('+', '.').Replace(prefix, "", StringComparison.Ordinal);

    [GeneratedRegex(@"\{(\d+)\}")]
    private static partial Regex Hole();
}
