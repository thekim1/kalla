using System.Text;
using System.Text.RegularExpressions;
using Kalla.TestKit;

namespace Kalla.IntegrationTests;

/// <summary>
/// Pooler-safe SQL (performance pass §3.1, §8; data-model §1.1): the services' SQL keeps no session state, so a
/// transaction-mode PgBouncer can be put in front of <c>kalla_api</c> and <c>kalla_worker</c> later without code
/// changes. Scans every string literal in <c>src/**/*.cs</c> for the forms PgBouncer's transaction mode does not support.
/// Migrations and the migration service connect directly (never pooled) and are not scanned.
/// </summary>
public sealed partial class PoolerSafeSqlTests
{
    /// <summary>
    /// Files that may LISTEN: the one dedicated listening connection per instance (<c>ConnectionStrings:ragdb-listen</c>,
    /// data-model §4.3).
    /// </summary>
    private static readonly string[] ListenAllowed =
    [
        Path.Combine("Kalla.Infrastructure", "Config", "ConfigVersionMonitor.cs"),
    ];

    /// <summary>
    /// The SQL allowed a session <c>SET</c>: the HNSW rebuild's <c>SET statement_timeout = 0</c>, on its own unpooled
    /// session from <c>ConnectionStrings:ragdb-maintenance</c> (Decisions "M-2 open design calls").
    /// </summary>
    private static readonly (string File, string Sql)[] SessionSetAllowed =
    [
        (Path.Combine("Kalla.Worker", "Engine", "HnswIndexHealth.cs"), "SET statement_timeout = 0"),
    ];

    /// <summary>The repository's <c>src</c>, next to <c>Kalla.slnx</c>.</summary>
    private static string SourceRoot => Path.Combine(Path.GetDirectoryName(TestPostgres.RepositoryFile("Kalla.slnx"))!, "src");

    private static readonly (string Rule, Regex Pattern)[] Forbidden =
    [
        // Session SET (SET LOCAL is fine), also through set_config(…, false); RESET and DISCARD undo session state.
        ("session SET", SessionSet()),
        ("session set_config", SessionSetConfig()),
        ("RESET / DISCARD", ResetOrDiscard()),
        // Only pg_try_advisory_xact_lock / pg_advisory_xact_lock; the session variants outlive the transaction.
        ("session advisory lock", SessionAdvisoryLock()),
        ("WITH HOLD cursor", WithHold()),
        ("SQL-level PREPARE / EXECUTE / DEALLOCATE", SqlPrepare()),
    ];

    public static TheoryData<string> SourceFiles()
    {
        var src = SourceRoot;
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                                 !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                                 !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                                 !f.Contains($"Kalla.MigrationService{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetRelativePath(src, file));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(SourceFiles))]
    public void Sql_in_the_services_keeps_no_session_state(string file)
    {
        var literals = StringLiterals(File.ReadAllText(Path.Combine(SourceRoot, file)));
        var findings = new List<string>();
        foreach (var literal in literals)
        {
            findings.AddRange(Violations(literal)
                .Where(rule => !(rule == "session SET" && SessionSetAllowed.Contains((file, literal))))
                .Select(rule => $"{rule}: {Shorten(literal)}"));
            if (Listen().IsMatch(literal) && !ListenAllowed.Contains(file))
            {
                findings.Add($"LISTEN outside the ragdb-listen connection: {Shorten(literal)}");
            }
        }

        findings.ShouldBeEmpty(file);
    }

    [Theory]
    [InlineData("SET statement_timeout = 0")]
    [InlineData("select 1; set search_path = config")]
    [InlineData("SET SESSION lock_timeout = '1s'")]
    [InlineData("SET search_path TO config, public")]
    [InlineData("set role kalla_admin")]
    [InlineData("SET TIME ZONE 'UTC'")]
    [InlineData("SET \"app.user\" = 'x'")]
    [InlineData("SELECT set_config('app.user', $1, false)")]
    [InlineData("RESET ALL")]
    [InlineData("DISCARD ALL")]
    [InlineData("SELECT pg_advisory_lock(42)")]
    [InlineData("SELECT pg_try_advisory_lock(42)")]
    [InlineData("SELECT pg_advisory_unlock(42)")]
    [InlineData("DECLARE c CURSOR WITH HOLD FOR SELECT 1")]
    [InlineData("PREPARE q AS SELECT 1")]
    [InlineData("EXECUTE q")]
    [InlineData("CREATE TEMP TABLE t (id int)")]
    [InlineData("LISTEN {channel}")]
    [InlineData("EXECUTE {name}")]
    public void The_scan_finds_every_forbidden_form(string sql) => (Violations(sql).Any() || Listen().IsMatch(sql)).ShouldBeTrue(sql);

    [Theory]
    [InlineData("SET LOCAL lock_timeout = '5s'")]
    [InlineData("""UPDATE content."Jobs" SET "State" = 'queued' WHERE "Id" = $1""")]
    [InlineData("SELECT pg_try_advisory_xact_lock(42)")]
    [InlineData("SELECT set_config('app.user', $1, true)")]
    [InlineData("CREATE TEMP TABLE t (id int) ON COMMIT DROP")]
    [InlineData("""SELECT "Version" FROM config."ConfigVersion" WHERE "Id" = 1 FOR UPDATE""")]
    [InlineData("The value is invalid. Reset the form.")]
    [InlineData("Set when the host answered with a redirect.")]
    [InlineData("Set by the worker after the run; null until then.")]
    [InlineData("Set when the source moves to another collection.")]
    public void The_scan_allows_the_transaction_scoped_forms(string sql) => Violations(sql).ShouldBeEmpty(sql);

    [Fact]
    public void The_literal_scanner_reads_every_kind_of_csharp_string()
    {
        const string source = """"""
            // "SET in a comment" is ignored
            var a = "plain \" SET x = 1";
            var b = @"verbatim ""quoted"" text";
            var c = $"interpolated {x} text";
            var d = '"';
            /* "block comment" */
            var e = """""
                raw "with" quotes
                """"";
            """""";
        StringLiterals(source).ShouldBe(["plain \\\" SET x = 1", "verbatim \"\"quoted\"\" text", "interpolated {x} text", "\n    raw \"with\" quotes\n    "]);
    }

    private static IEnumerable<string> Violations(string sql)
    {
        foreach (var (rule, pattern) in Forbidden)
        {
            if (pattern.IsMatch(sql))
            {
                yield return rule;
            }
        }

        if (TempTable().IsMatch(sql) && !OnCommitDrop().IsMatch(sql))
        {
            yield return "temp table without ON COMMIT DROP";
        }
    }

    /// <summary>
    /// The contents of every string literal in C# source: regular, verbatim, interpolated and raw strings; comments and
    /// character literals are skipped. Escapes are kept as written, which is enough to find SQL keywords.
    /// </summary>
    private static List<string> StringLiterals(string source)
    {
        var result = new List<string>();
        var i = 0;
        while (i < source.Length)
        {
            var c = source[i];
            if (c == '/' && At(source, i + 1, '/'))
            {
                i = source.IndexOf('\n', i) is var end and >= 0 ? end : source.Length;
            }
            else if (c == '/' && At(source, i + 1, '*'))
            {
                i = source.IndexOf("*/", i + 2, StringComparison.Ordinal) is var end and >= 0 ? end + 2 : source.Length;
            }
            else if (c == '\'')
            {
                // A character literal: '"', '\'', 'x'.
                i += At(source, i + 1, '\\') ? 4 : 3;
                while (i < source.Length && source[i - 1] != '\'')
                {
                    i++;
                }
            }
            else if (c == '"')
            {
                var start = i;
                while (start > 0 && source[start - 1] is '$' or '@')
                {
                    start--;
                }

                var verbatim = source.AsSpan(start, i - start).Contains('@');
                var quotes = 0;
                while (At(source, i + quotes, '"'))
                {
                    quotes++;
                }

                if (quotes >= 3)
                {
                    // Raw string: ends at the same run of quotes.
                    var close = new string('"', quotes);
                    var end = source.IndexOf(close, i + quotes, StringComparison.Ordinal);
                    end = end < 0 ? source.Length : end;
                    result.Add(source[(i + quotes)..end]);
                    i = end + quotes;
                }
                else if (quotes == 2)
                {
                    result.Add("");
                    i += 2;
                }
                else
                {
                    var text = new StringBuilder();
                    i++;
                    while (i < source.Length)
                    {
                        if (verbatim && source[i] == '"' && At(source, i + 1, '"'))
                        {
                            text.Append("\"\"");
                            i += 2;
                        }
                        else if (!verbatim && source[i] == '\\' && i + 1 < source.Length)
                        {
                            text.Append(source, i, 2);
                            i += 2;
                        }
                        else if (source[i] == '"')
                        {
                            i++;
                            break;
                        }
                        else
                        {
                            text.Append(source[i++]);
                        }
                    }

                    result.Add(text.ToString());
                }
            }
            else
            {
                i++;
            }
        }

        return result;
    }

    private static bool At(string s, int i, char c) => i < s.Length && s[i] == c;

    private static string Shorten(string literal)
    {
        var line = string.Join(' ', literal.Split((char[])['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return line.Length > 120 ? line[..120] + "…" : line;
    }

    // SET at the start of a statement (UPDATE … SET is not), unless SET LOCAL, and only in SQL shape: a parameter followed
    // by = or TO, or ROLE, TIME ZONE, SESSION AUTHORIZATION. Prose such as "Set when the host …" is not SQL (#79).
    [GeneratedRegex(@"(?:^|;)\s*SET\s+(?!LOCAL\b)(?:SESSION\s+)?(?:ROLE\b|TIME\s+ZONE\b|SESSION\s+AUTHORIZATION\b|[A-Za-z_""][\w.""]*\s*(?:=|\bTO\b))", RegexOptions.IgnoreCase)]
    private static partial Regex SessionSet();

    [GeneratedRegex(@"set_config\s*\([^)]*,\s*false\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex SessionSetConfig();

    [GeneratedRegex(@"(?:^|;)\s*(RESET|DISCARD)\s+[\w{""]", RegexOptions.IgnoreCase)]
    private static partial Regex ResetOrDiscard();

    [GeneratedRegex(@"pg_(try_)?advisory_(un)?lock(_shared|_all)?\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex SessionAdvisoryLock();

    [GeneratedRegex(@"\bWITH\s+HOLD\b", RegexOptions.IgnoreCase)]
    private static partial Regex WithHold();

    [GeneratedRegex(@"(?:^|;)\s*(PREPARE|EXECUTE|DEALLOCATE)\s+[\w{""]", RegexOptions.IgnoreCase)]
    private static partial Regex SqlPrepare();

    [GeneratedRegex(@"\bCREATE\s+(GLOBAL\s+|LOCAL\s+)?(TEMP|TEMPORARY)\s+TABLE\b", RegexOptions.IgnoreCase)]
    private static partial Regex TempTable();

    [GeneratedRegex(@"\bON\s+COMMIT\s+DROP\b", RegexOptions.IgnoreCase)]
    private static partial Regex OnCommitDrop();

    [GeneratedRegex(@"(?:^|;)\s*(UN)?LISTEN\s+[\w{""*]", RegexOptions.IgnoreCase)]
    private static partial Regex Listen();
}
