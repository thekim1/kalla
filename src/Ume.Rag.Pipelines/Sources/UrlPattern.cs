using System.Text;
using System.Text.RegularExpressions;

namespace Ume.Rag.Pipelines.Sources;

/// <summary>
/// An <c>include</c> / <c>exclude</c> / <c>demote</c> pattern (connectors design §5.2), matched against the canonical URL
/// without its scheme (<c>host/path?query</c>). Glob: <c>*</c> is any characters except <c>/</c>, <c>**</c> any
/// characters, everything else literal (also <c>?</c> and <c>.</c>); the host part is case-insensitive, the rest
/// case-sensitive. <c>re:&lt;regex&gt;</c> is a .NET regular expression, non-backtracking. Matching has no timeout: the
/// non-backtracking engine is linear in the input, and a timeout under CPU load used to count as "no match", so an
/// <c>exclude</c> pattern could let an excluded page into the crawl (session 13). Shared by the crawler and retrieval's
/// <c>demote</c> (<c>Sources.DemoteRules</c>), so both match alike (#102). A glob written with a leading <c>http://</c> or
/// <c>https://</c> (an address pasted from the browser) matches as the same glob without it (<see cref="Normalize"/>):
/// the URL is matched without its scheme, so with the scheme kept the glob could match nothing.
/// </summary>
public sealed class UrlPattern
{
    public const string RegexPrefix = "re:";
    /// <summary>No timeout: linear-time matching is the bound (<see cref="RegexOptions.NonBacktracking"/>).</summary>
    internal static readonly TimeSpan MatchTimeout = Regex.InfiniteMatchTimeout;

    private readonly Regex _regex;

    private UrlPattern(string text, Regex regex)
    {
        Text = text;
        _regex = regex;
    }

    public string Text { get; }

    /// <summary>The compiled expression's match timeout (tests).</summary>
    internal TimeSpan Timeout => _regex.MatchTimeout;

    /// <summary>
    /// The glob <paramref name="pattern"/> matches as: trimmed and without a leading <c>http://</c> or <c>https://</c> (any
    /// case). A <c>re:</c> pattern is returned unchanged.
    /// </summary>
    public static string Normalize(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (pattern.StartsWith(RegexPrefix, StringComparison.Ordinal))
        {
            return pattern;
        }

        var glob = pattern.Trim();
        foreach (var scheme in (ReadOnlySpan<string>)["https://", "http://"])
        {
            if (glob.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            {
                return glob[scheme.Length..];
            }
        }

        return glob;
    }

    /// <summary>Parses <paramref name="pattern"/>; a <c>re:</c> pattern that is not a valid expression throws <see cref="ArgumentException"/>.</summary>
    public static UrlPattern Parse(string pattern)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        if (pattern.StartsWith(RegexPrefix, StringComparison.Ordinal))
        {
            return new(pattern, new Regex(pattern[RegexPrefix.Length..], RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, MatchTimeout));
        }

        var glob = Normalize(pattern);
        var slash = glob.IndexOf('/', StringComparison.Ordinal);
        var host = slash < 0 ? glob : glob[..slash];
        var rest = slash < 0 ? string.Empty : glob[slash..];
        var expression = new StringBuilder("^(?i:");
        AppendGlob(expression, host);
        expression.Append(')');
        AppendGlob(expression, rest);
        expression.Append('$');
        return new(pattern, new Regex(expression.ToString(), RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, MatchTimeout));
    }

    /// <summary>True when the canonical URL <paramref name="canonicalUrl"/> matches.</summary>
    public bool Matches(string canonicalUrl)
    {
        ArgumentNullException.ThrowIfNull(canonicalUrl);
        var schemeEnd = canonicalUrl.IndexOf("://", StringComparison.Ordinal);
        var target = schemeEnd < 0 ? canonicalUrl : canonicalUrl[(schemeEnd + 3)..];
        return _regex.IsMatch(target);
    }

    public override string ToString() => Text;

    private static void AppendGlob(StringBuilder expression, string glob)
    {
        for (var i = 0; i < glob.Length; i++)
        {
            if (glob[i] == '*')
            {
                if (i + 1 < glob.Length && glob[i + 1] == '*')
                {
                    expression.Append(".*");
                    i++;
                }
                else
                {
                    expression.Append("[^/]*");
                }
            }
            else
            {
                expression.Append(Regex.Escape(glob[i].ToString()));
            }
        }
    }
}
