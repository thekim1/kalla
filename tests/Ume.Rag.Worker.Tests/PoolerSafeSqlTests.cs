using System.Text;
using System.Text.RegularExpressions;
using Ume.Rag.TestKit;

namespace Ume.Rag.Worker.Tests;

/// <summary>
/// Pooler-safe SQL (performance pass §3.1, §8; data-model §1.1): no statement in the source may leave session state on a
/// pooled connection, so a transaction-mode PgBouncer can be put in front later without a code change. Scans the string
/// literals of every C# file under <c>src/</c> and the SQL the migration service applies.
/// </summary>
public sealed partial class PoolerSafeSqlTests
{
    /// <summary>Files allowed a rule, with the reason. Keep it short.</summary>
    private static readonly Dictionary<string, (string Rule, string Reason)> Allowed = new(StringComparer.Ordinal)
    {
        ["src/Ume.Rag.Infrastructure/Config/ConfigVersionMonitor.cs"] =
            ("LISTEN", "LISTEN rag_config on its own unpooled connection from ConnectionStrings:ragdb-listen"),
    };

    private static readonly (string Rule, Regex Pattern)[] Rules =
    [
        ("session SET", SessionSet()),
        ("session set_config", SessionSetConfig()),
        ("session advisory lock", SessionAdvisoryLock()),
        ("WITH HOLD cursor", WithHold()),
        ("temp table without ON COMMIT DROP", TempTable()),
        ("SQL PREPARE", SqlPrepare()),
        ("LISTEN", Listen()),
    ];

    [Fact]
    public void No_session_state_sql_in_the_source()
    {
        var root = Path.GetDirectoryName(TestPostgres.RepositoryFile("UmeRag.slnx"))!;
        var files = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                        !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Append(Path.Combine(root, "deploy", "postgres", "app-roles.sql"))
            .ToList();
        files.Count.ShouldBeGreaterThan(50);

        var findings = new List<string>();
        var literals = 0;
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            var text = File.ReadAllText(file);
            var sqlTexts = file.EndsWith(".sql", StringComparison.Ordinal) ? [text] : StringLiterals(text);
            literals += sqlTexts.Count;
            findings.AddRange(from sql in sqlTexts
                              from finding in Findings(sql)
                              where !(Allowed.TryGetValue(relative, out var allowed) && allowed.Rule == finding)
                              select $"{relative}: {finding}");
        }

        literals.ShouldBeGreaterThan(100);
        findings.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("SET statement_timeout = '5s'", "session SET")]
    [InlineData("SET SESSION search_path TO content", "session SET")]
    [InlineData("BEGIN; set hnsw.ef_search = 200", "session SET")]
    [InlineData("\n    SET ROLE ume_rag_admin", "session SET")]
    [InlineData("SELECT set_config('app.user', $1, false)", "session set_config")]
    [InlineData("SELECT pg_advisory_lock(42)", "session advisory lock")]
    [InlineData("SELECT pg_try_advisory_lock_shared(1, 2)", "session advisory lock")]
    [InlineData("SELECT pg_advisory_unlock(42)", "session advisory lock")]
    [InlineData("DECLARE c CURSOR WITH HOLD FOR SELECT 1", "WITH HOLD cursor")]
    [InlineData("CREATE TEMP TABLE t (id int)", "temp table without ON COMMIT DROP")]
    [InlineData("PREPARE q (uuid) AS SELECT 1", "SQL PREPARE")]
    [InlineData("LISTEN rag_jobs", "LISTEN")]
    public void Forbidden_forms_are_found(string sql, string rule) => Findings(sql).ShouldContain(rule);

    [Theory]
    [InlineData("SET LOCAL statement_timeout = '5s'")]
    [InlineData("SELECT set_config('app.user', $1, true)")]
    [InlineData("SELECT pg_try_advisory_xact_lock($1)")]
    [InlineData("UPDATE content.\"Jobs\" j\n    SET \"State\" = 'running'")]
    [InlineData("UPDATE content.\"Jobs\" SET \"State\" = 'queued'")]
    [InlineData("LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$")]
    [InlineData("ALTER ROLE ume_rag_worker IN DATABASE ragdb SET statement_timeout = '60s'")]
    [InlineData("CREATE TEMP TABLE t (id int) ON COMMIT DROP")]
    [InlineData("SELECT pg_notify('rag_jobs', $1)")]
    public void Allowed_forms_pass(string sql) => Findings(sql).ShouldBeEmpty();

    [Fact]
    public void Literals_are_read_from_code_and_comments_are_skipped()
    {
        const string code = """"
            // LISTEN in a comment
            /* SET x = 1 */
            var a = "SELECT 1";
            var b = @"SELECT ""q""";
            var c = $"SELECT {x}";
            var d = """
                SELECT "Id"
                """;
            var e = '"';
            """";

        StringLiterals(code).ShouldBe(["SELECT 1", "SELECT \"\"q\"\"", "SELECT {x}", "\n    SELECT \"Id\"\n    "]);
    }

    private static List<string> Findings(string sql) => [.. Rules.Where(r => r.Pattern.IsMatch(sql)).Select(r => r.Rule)];

    /// <summary>
    /// The contents of the string literals of a C# file (regular, verbatim, interpolated and raw), skipping comments and
    /// character literals. Escapes are kept as written; the rules do not depend on them.
    /// </summary>
    private static List<string> StringLiterals(string code)
    {
        var literals = new List<string>();
        var i = 0;
        while (i < code.Length)
        {
            if (code[i] == '/' && i + 1 < code.Length && code[i + 1] == '/')
            {
                i = code.IndexOf('\n', i) is var end and >= 0 ? end : code.Length;
            }
            else if (code[i] == '/' && i + 1 < code.Length && code[i + 1] == '*')
            {
                i = code.IndexOf("*/", i + 2, StringComparison.Ordinal) is var end and >= 0 ? end + 2 : code.Length;
            }
            else if (code[i] == '\'')
            {
                i++;
                while (i < code.Length && code[i] != '\'')
                {
                    i += code[i] == '\\' ? 2 : 1;
                }

                i++;
            }
            else if (code[i] == '"')
            {
                var verbatim = i > 0 && (code[i - 1] == '@' || (code[i - 1] == '$' && i > 1 && code[i - 2] == '@'));
                var quotes = 0;
                while (i + quotes < code.Length && code[i + quotes] == '"')
                {
                    quotes++;
                }

                if (quotes >= 3)
                {
                    var delimiter = new string('"', quotes);
                    var start = i + quotes;
                    var end = code.IndexOf(delimiter, start, StringComparison.Ordinal);
                    end = end < 0 ? code.Length : end;
                    literals.Add(code[start..end]);
                    i = end + quotes;
                }
                else if (quotes == 2)
                {
                    literals.Add(string.Empty);
                    i += 2;
                }
                else
                {
                    var literal = new StringBuilder();
                    i++;
                    while (i < code.Length)
                    {
                        if (verbatim && code[i] == '"' && i + 1 < code.Length && code[i + 1] == '"')
                        {
                            literal.Append("\"\"");
                            i += 2;
                        }
                        else if (code[i] == '"' || (!verbatim && code[i] == '\n'))
                        {
                            break;
                        }
                        else if (!verbatim && code[i] == '\\' && i + 1 < code.Length)
                        {
                            literal.Append(code, i, 2);
                            i += 2;
                        }
                        else
                        {
                            literal.Append(code[i++]);
                        }
                    }

                    literals.Add(literal.ToString());
                    i++;
                }
            }
            else
            {
                i++;
            }
        }

        return literals;
    }

    // A SET statement: at the start of a line or literal, or after a semicolon, naming an unquoted parameter (UPDATE … SET
    // lists quoted columns). SET LOCAL is transaction-scoped and allowed.
    [GeneratedRegex("""(?:^\s*|;\s*|")SET\s+(?!LOCAL\b)(?:SESSION\s+)?(?:ROLE\b|AUTHORIZATION\b|TIME\s+ZONE\b|SCHEMA\b|NAMES\b|[A-Za-z_][\w.]*\s*(?:=|\bTO\b))""",
        RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex SessionSet();

    [GeneratedRegex("""set_config\s*\([^)]*,\s*false\s*\)""", RegexOptions.IgnoreCase)]
    private static partial Regex SessionSetConfig();

    [GeneratedRegex("""pg_(?:try_)?advisory_(?:lock|unlock)(?:_shared|_all)?\s*\(""", RegexOptions.IgnoreCase)]
    private static partial Regex SessionAdvisoryLock();

    [GeneratedRegex("""\bWITH\s+HOLD\b""", RegexOptions.IgnoreCase)]
    private static partial Regex WithHold();

    [GeneratedRegex("""\bCREATE\s+(?:(?:LOCAL|GLOBAL)\s+)?TEMP(?:ORARY)?\s+TABLE\b(?![^;]*\bON\s+COMMIT\s+DROP\b)""", RegexOptions.IgnoreCase)]
    private static partial Regex TempTable();

    [GeneratedRegex("""\b(?:PREPARE\s+(?!TRANSACTION\b)[A-Za-z_]\w*\s*(?:\(|\bAS\b)|DEALLOCATE\b)""", RegexOptions.IgnoreCase)]
    private static partial Regex SqlPrepare();

    [GeneratedRegex("""\b(?:UN)?LISTEN\s+[A-Za-z_*"]""", RegexOptions.IgnoreCase)]
    private static partial Regex Listen();
}
