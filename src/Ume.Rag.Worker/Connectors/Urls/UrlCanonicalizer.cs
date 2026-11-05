using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Ume.Rag.Worker.Connectors.Urls;

/// <summary>A site profile's identity rule (connectors design §3.1 rule 10, §5.6), applied only when verified for the host.</summary>
public enum IdentityRule
{
    None,

    /// <summary>Sitevision: <c>/{path}.4.88db09b1724ee319ca45d.html</c> → <c>/4.88db09b1724ee319ca45d.html</c> (the node id).</summary>
    SitevisionNodeId,
}

/// <summary>The per-source and per-host inputs of the rules (§3.1 rules 7, 9, 10).</summary>
public sealed record CanonicalizerOptions
{
    public static CanonicalizerOptions Default { get; } = new();

    /// <summary>The source's and the site profile's <c>ignoreQueryParams</c> (rule 7), compared case-insensitively; a trailing * is a prefix.</summary>
    public IReadOnlyCollection<string> IgnoreQueryParams { get; init; } = [];

    /// <summary>The source setting <c>directoryIndexNames</c> (rule 9): a last segment with one of these names is dropped.</summary>
    public IReadOnlyCollection<string> DirectoryIndexNames { get; init; } = [];

    /// <summary>The host's verified identity rule (rule 10); <see cref="IdentityRule.None"/> unless the profile verified it.</summary>
    public IdentityRule IdentityRule { get; init; }

    /// <summary>
    /// The site profiles' rules by host (lower-case, without a port, as <c>SiteProfiles.Host</c>; connectors design §5.6):
    /// a URL on one of these hosts also drops the host's <c>ignoreQueryParams</c> and, when set, maps its path by the host's
    /// verified identity rule instead of <see cref="IdentityRule"/>.
    /// </summary>
    public IReadOnlyDictionary<string, HostCanonicalRules> Hosts { get; init; } = new Dictionary<string, HostCanonicalRules>(StringComparer.Ordinal);
}

/// <summary>A host's canonicalisation inputs from its applied site profile (§3.1 rules 7 and 10, §5.6).</summary>
/// <param name="IgnoreQueryParams">The profile's <c>ignoreQueryParams</c> (case-insensitive; a trailing * is a prefix).</param>
/// <param name="Identity">The profile's identity rule, only when verified; null keeps the path.</param>
public sealed record HostCanonicalRules(IReadOnlyCollection<string> IgnoreQueryParams, PathIdentityRule? Identity);

/// <summary>
/// A site profile's identity rule (<c>identityRule</c>): a .NET regular expression over the URL path and its replacement
/// with <c>${name}</c> groups. A path it does not match, or one whose match times out (50 ms), is kept.
/// </summary>
public sealed class PathIdentityRule
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(50);

    private readonly Regex _pattern;
    private readonly string _replacement;

    private PathIdentityRule(Regex pattern, string replacement)
    {
        _pattern = pattern;
        _replacement = replacement;
    }

    /// <summary>The rule, or null when <paramref name="pattern"/> is not a valid expression.</summary>
    public static PathIdentityRule? Create(string pattern, string replacement)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(replacement);
        try
        {
            return new PathIdentityRule(new Regex(pattern, RegexOptions.CultureInvariant, Timeout), replacement);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public string Apply(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        try
        {
            var match = _pattern.Match(path);
            return match.Success ? match.Result(_replacement) : path;
        }
        catch (RegexMatchTimeoutException)
        {
            return path;
        }
    }

    /// <summary>Whether the rule maps <paramref name="path"/>; false when the match times out, as <see cref="Apply"/> keeps it then.</summary>
    public bool Matches(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        try
        {
            return _pattern.IsMatch(path);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}

/// <summary>A canonical URL, or why the input cannot be one (<c>url.*</c>).</summary>
public sealed record CanonicalUrl(string? Value, string? Refusal)
{
    public bool IsValid => Value is not null;

    /// <summary>The canonical form as a <see cref="System.Uri"/>; only for valid results.</summary>
    public Uri ToUri() => new(Value ?? throw new InvalidOperationException($"Not a canonical URL: {Refusal}."), UriKind.Absolute);
}

/// <summary>
/// URL normalisation rules v1 (connectors design §3.1). Applied to every URL before it becomes an identity or a comparison
/// key. The input string is split by the RFC 3986 appendix B expression and every rule is our own, so
/// <see cref="Uri"/>'s escaping never changes an identity.
/// </summary>
public static partial class UrlCanonicalizer
{
    /// <summary>The rules' version (<c>Settings.Crawl.IdentityRulesVersion</c>); a change is an identity re-sync.</summary>
    public const int RulesVersion = 1;

    public const string RefusedScheme = "url.scheme";
    public const string RefusedUserInfo = "url.userinfo";
    public const string RefusedHost = "url.host";
    public const string RefusedPort = "url.port";
    public const string RefusedSyntax = "url.syntax";

    /// <summary>
    /// Tracking and session parameters dropped from every query (§3.1, case-insensitive; a trailing * is a prefix). Sitevision's
    /// portlet state and routing (<c>sv.target</c>, <c>sv.12.&lt;portlet&gt;.route</c>) and menu toggle (<c>open.12.&lt;portlet&gt;</c>)
    /// are dropped on every host, with or without a site profile (#363).
    /// </summary>
    public static IReadOnlyList<string> DroppedParameters { get; } =
    [
        "utm_*", "gclid", "gbraid", "wbraid", "dclid", "fbclid", "msclkid", "yclid", "twclid", "igshid", "mc_cid", "mc_eid",
        "_ga", "_gl", "_hsenc", "_hsmi", "hsCtaTracking", "mkt_tok", "jsessionid", "phpsessid", "aspsessionid*", "sessionid",
        "cfid", "cftoken", "sv.*", "open.*",
    ];

    private static readonly IdnMapping Idn = new() { UseStd3AsciiRules = true, AllowUnassigned = false };

    public static CanonicalUrl Canonicalize(string url, CanonicalizerOptions? options = null) => Canonicalize(url, options, identityRule: true);

    /// <summary>
    /// The path form of <paramref name="url"/>: every rule but the identity rule (rule 10), so the URL keeps the path an
    /// editor sees. The source's <c>include</c>, <c>exclude</c> and <c>demote</c> patterns match this form (§5.2).
    /// </summary>
    public static CanonicalUrl PathForm(string url, CanonicalizerOptions? options = null) => Canonicalize(url, options, identityRule: false);

    private static CanonicalUrl Canonicalize(string url, CanonicalizerOptions? options, bool identityRule)
    {
        ArgumentNullException.ThrowIfNull(url);
        options ??= CanonicalizerOptions.Default;
        var match = Rfc3986().Match(url.Trim());
        if (!match.Success || !match.Groups["scheme"].Success)
        {
            return new(null, RefusedSyntax);
        }

        // Rule 1: http and https only, lower-case.
        var scheme = match.Groups["scheme"].Value.ToLowerInvariant();
        if (scheme is not ("http" or "https"))
        {
            return new(null, RefusedScheme);
        }

        if (!match.Groups["authority"].Success)
        {
            return new(null, RefusedSyntax);
        }

        // Rules 2 and 3: host, port, no userinfo.
        var authority = match.Groups["authority"].Value;
        if (authority.Contains('@', StringComparison.Ordinal))
        {
            return new(null, RefusedUserInfo);
        }

        if (!SplitAuthority(authority, out var rawHost, out var rawPort))
        {
            return new(null, RefusedHost);
        }

        var host = CanonicalHost(rawHost);
        if (host is null)
        {
            return new(null, RefusedHost);
        }

        string port;
        if (rawPort.Length == 0)
        {
            port = string.Empty;
        }
        else if (rawPort.Length > 5 || !rawPort.All(char.IsAsciiDigit) || int.Parse(rawPort, CultureInfo.InvariantCulture) is < 1 or > 65535)
        {
            return new(null, RefusedPort);
        }
        else
        {
            var number = int.Parse(rawPort, CultureInfo.InvariantCulture);
            port = (scheme, number) is ("http", 80) or ("https", 443) ? string.Empty : ":" + number.ToString(CultureInfo.InvariantCulture);
        }

        // Rules 4, 5, 8, 9, 10: the path.
        var hostRules = options.Hosts.Count == 0 ? null : options.Hosts.GetValueOrDefault(host);
        var path = NormalizePercentEncoding(match.Groups["path"].Value, isQuery: false);
        path = RemoveSessionSegments(path);
        path = RemoveDotSegments(path.Length == 0 ? "/" : path);
        path = DropDirectoryIndex(path, options.DirectoryIndexNames);
        if (identityRule)
        {
            path = hostRules?.Identity is { } identity ? identity.Apply(path) : ApplyIdentityRule(path, options.IdentityRule);
        }

        // Rule 7: the query; rule 6: the fragment is dropped.
        IReadOnlyCollection<string> ignored = hostRules is null ? options.IgnoreQueryParams : [.. options.IgnoreQueryParams, .. hostRules.IgnoreQueryParams];
        var query = match.Groups["query"].Success ? NormalizeQuery(match.Groups["query"].Value, ignored) : string.Empty;

        var builder = new StringBuilder(scheme.Length + host.Length + path.Length + query.Length + 8);
        builder.Append(scheme).Append("://").Append(host).Append(port).Append(path);
        if (query.Length > 0)
        {
            builder.Append('?').Append(query);
        }

        return new(builder.ToString(), null);
    }

    /// <summary>
    /// Resolves <paramref name="reference"/> (a link or a <c>Location</c> header) against <paramref name="baseUrl"/> and
    /// canonicalises the result.
    /// </summary>
    public static CanonicalUrl Resolve(Uri baseUrl, string reference, CanonicalizerOptions? options = null) =>
        Resolve(baseUrl, reference, options, identityRule: true);

    /// <summary>As <see cref="Resolve(Uri, string, CanonicalizerOptions?)"/>, to the <see cref="PathForm"/>.</summary>
    public static CanonicalUrl ResolvePathForm(Uri baseUrl, string reference, CanonicalizerOptions? options = null) =>
        Resolve(baseUrl, reference, options, identityRule: false);

    /// <summary>
    /// Whether <paramref name="canonicalUrl"/>'s host has a verified identity rule (rule 10) that maps its path: such an
    /// identity no longer tells the path it was found under.
    /// </summary>
    public static bool IsIdentityRuleForm(string canonicalUrl, CanonicalizerOptions options)
    {
        ArgumentNullException.ThrowIfNull(canonicalUrl);
        ArgumentNullException.ThrowIfNull(options);
        var match = Rfc3986().Match(canonicalUrl);
        if (!match.Success || !match.Groups["authority"].Success || !SplitAuthority(match.Groups["authority"].Value, out var host, out _))
        {
            return false;
        }

        var path = match.Groups["path"].Value;
        if (options.Hosts.Count > 0 && options.Hosts.GetValueOrDefault(host)?.Identity is { } identity)
        {
            return identity.Matches(path);
        }

        return options.IdentityRule == IdentityRule.SitevisionNodeId && SitevisionNodeId().IsMatch(path[(path.LastIndexOf('/') + 1)..]);
    }

    private static CanonicalUrl Resolve(Uri baseUrl, string reference, CanonicalizerOptions? options, bool identityRule)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(reference);
        var trimmed = reference.Trim();
        if (Rfc3986().Match(trimmed) is { Success: true } match && match.Groups["scheme"].Success)
        {
            // Absolute: our rules alone, so Uri's escaping never touches it.
            return Canonicalize(trimmed, options, identityRule);
        }

        return Uri.TryCreate(baseUrl, trimmed, out var absolute) ? Canonicalize(absolute.AbsoluteUri, options, identityRule) : new(null, RefusedSyntax);
    }

    /// <summary><c>IdentityHash</c> / <c>UrlHash</c>: SHA-256 of the canonical identity (data-model §2.2).</summary>
    public static byte[] IdentityHash(string canonicalIdentity)
    {
        ArgumentNullException.ThrowIfNull(canonicalIdentity);
        return SHA256.HashData(Encoding.UTF8.GetBytes(canonicalIdentity));
    }

    /// <summary>Rule 2 on a host alone: lower-case A-label (UTS 46, STD3), no trailing dot, IPv4 in dotted-quad form; null if invalid.</summary>
    public static string? CanonicalHost(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (host.StartsWith('['))
        {
            return host.EndsWith(']') && IPAddress.TryParse(host.AsSpan(1, host.Length - 2), out var v6) && v6.AddressFamily == AddressFamily.InterNetworkV6
                ? "[" + v6.ToString().ToLowerInvariant() + "]"
                : null;
        }

        if (host.Contains('%', StringComparison.Ordinal))
        {
            return null;
        }

        var trimmed = host.EndsWith('.') ? host[..^1] : host;
        if (trimmed.Length == 0)
        {
            return null;
        }

        // Numeric hosts (decimal, octal, hex, short forms) are IPv4 literals, as browsers read them: 2130706433 is 127.0.0.1.
        if (NumericHost().IsMatch(trimmed) && IPAddress.TryParse(trimmed, out var v4) && v4.AddressFamily == AddressFamily.InterNetwork)
        {
            return v4.ToString();
        }

        try
        {
            return Idn.GetAscii(trimmed.ToLowerInvariant()).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool SplitAuthority(string authority, out string host, out string port)
    {
        host = authority;
        port = string.Empty;
        if (authority.StartsWith('['))
        {
            var close = authority.IndexOf(']', StringComparison.Ordinal);
            if (close < 0)
            {
                return false;
            }

            host = authority[..(close + 1)];
            var rest = authority[(close + 1)..];
            if (rest.Length > 0)
            {
                if (rest[0] != ':')
                {
                    return false;
                }

                port = rest[1..];
            }

            return true;
        }

        var colon = authority.LastIndexOf(':');
        if (colon >= 0)
        {
            host = authority[..colon];
            port = authority[(colon + 1)..];
        }

        return host.Length > 0;
    }

    /// <summary>
    /// Rule 5: unreserved escapes decoded, other escapes upper-cased, non-ASCII as UTF-8 escapes, characters not allowed
    /// in the component escaped; reserved escapes such as <c>%2F</c> and <c>%3F</c> are kept.
    /// </summary>
    internal static string NormalizePercentEncoding(string value, bool isQuery)
    {
        var builder = new StringBuilder(value.Length);
        Span<byte> utf8 = stackalloc byte[4];
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '%')
            {
                if (i + 2 < value.Length && char.IsAsciiHexDigit(value[i + 1]) && char.IsAsciiHexDigit(value[i + 2]))
                {
                    var b = (byte)((HexValue(value[i + 1]) << 4) | HexValue(value[i + 2]));
                    if (IsUnreserved((char)b))
                    {
                        builder.Append((char)b);
                    }
                    else
                    {
                        AppendEscaped(builder, b);
                    }

                    i += 2;
                }
                else
                {
                    builder.Append("%25");
                }
            }
            else if (c > 0x7F)
            {
                int length;
                if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                {
                    length = new Rune(c, value[i + 1]).EncodeToUtf8(utf8);
                    i++;
                }
                else
                {
                    length = (Rune.TryCreate(c, out var rune) ? rune : Rune.ReplacementChar).EncodeToUtf8(utf8);
                }

                foreach (var b in utf8[..length])
                {
                    AppendEscaped(builder, b);
                }
            }
            else if (IsAllowed(c, isQuery))
            {
                builder.Append(c);
            }
            else
            {
                AppendEscaped(builder, (byte)c);
            }
        }

        return builder.ToString();
    }

    /// <summary>RFC 3986 §5.2.4 remove_dot_segments; duplicate slashes are kept.</summary>
    internal static string RemoveDotSegments(string path)
    {
        var input = path;
        var output = new StringBuilder(path.Length);
        while (input.Length > 0)
        {
            if (input.StartsWith("../", StringComparison.Ordinal))
            {
                input = input[3..];
            }
            else if (input.StartsWith("./", StringComparison.Ordinal))
            {
                input = input[2..];
            }
            else if (input.StartsWith("/./", StringComparison.Ordinal))
            {
                input = input[2..];
            }
            else if (input == "/.")
            {
                input = "/";
            }
            else if (input.StartsWith("/../", StringComparison.Ordinal) || input == "/..")
            {
                input = input == "/.." ? "/" : input[3..];
                RemoveLastSegment(output);
            }
            else if (input is "." or "..")
            {
                input = string.Empty;
            }
            else
            {
                var start = input.StartsWith('/') ? 1 : 0;
                var next = input.IndexOf('/', start);
                var segment = next < 0 ? input : input[..next];
                output.Append(segment);
                input = next < 0 ? string.Empty : input[next..];
            }
        }

        return output.Length == 0 ? "/" : output.ToString();
    }

    private static void RemoveLastSegment(StringBuilder output)
    {
        var text = output.ToString();
        var slash = text.LastIndexOf('/');
        output.Length = slash < 0 ? 0 : slash;
    }

    /// <summary>Rule 8: <c>;jsessionid=…</c> and ASP.NET cookieless segments such as <c>/(S(…))/</c>.</summary>
    private static string RemoveSessionSegments(string path)
    {
        path = JSessionId().Replace(path, string.Empty);
        if (!path.Contains("))", StringComparison.Ordinal))
        {
            return path;
        }

        var segments = path.Split('/');
        return string.Join('/', segments.Where((s, i) => i == 0 || !AspNetCookieless().IsMatch(s)));
    }

    private static string DropDirectoryIndex(string path, IReadOnlyCollection<string> names)
    {
        if (names.Count == 0)
        {
            return path;
        }

        var slash = path.LastIndexOf('/');
        var last = path[(slash + 1)..];
        return names.Contains(last, StringComparer.OrdinalIgnoreCase) ? path[..(slash + 1)] : path;
    }

    private static string ApplyIdentityRule(string path, IdentityRule rule)
    {
        if (rule != IdentityRule.SitevisionNodeId)
        {
            return path;
        }

        var slash = path.LastIndexOf('/');
        var match = SitevisionNodeId().Match(path[(slash + 1)..]);
        return match.Success ? "/" + match.Groups["id"].Value + ".html" : path;
    }

    private static string NormalizeQuery(string query, IReadOnlyCollection<string> ignored)
    {
        var kept = new List<(string Name, string Pair)>();
        foreach (var raw in query.Split('&'))
        {
            if (raw.Length == 0)
            {
                continue;
            }

            var pair = NormalizePercentEncoding(raw, isQuery: true);
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            var name = equals < 0 ? pair : pair[..equals];
            if (!IsDropped(name, ignored))
            {
                kept.Add((name, pair));
            }
        }

        // OrderBy is stable, so repeated names keep their order.
        return string.Join('&', kept.OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => p.Pair));
    }

    private static bool IsDropped(string name, IReadOnlyCollection<string> ignored) =>
        DroppedParameters.Any(dropped => Matches(name, dropped)) || ignored.Any(dropped => Matches(name, dropped));

    /// <summary>A parameter name against a list entry: case-insensitive, a trailing <c>*</c> is a prefix (<c>sv.*</c>).</summary>
    private static bool Matches(string name, string entry) =>
        entry.EndsWith('*')
            ? name.StartsWith(entry[..^1], StringComparison.OrdinalIgnoreCase)
            : string.Equals(name, entry, StringComparison.OrdinalIgnoreCase);

    private static bool IsUnreserved(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~';

    // pchar = unreserved / pct-encoded / sub-delims / ":" / "@"; path adds "/", query adds "/" and "?".
    private static bool IsAllowed(char c, bool isQuery) =>
        IsUnreserved(c) || c is '!' or '$' or '&' or '\'' or '(' or ')' or '*' or '+' or ',' or ';' or '=' or ':' or '@' or '/'
        || (isQuery && c == '?');

    private static void AppendEscaped(StringBuilder builder, byte b) =>
        builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));

    private static int HexValue(char c) => c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10;

    [GeneratedRegex(@"^(?:(?<scheme>[A-Za-z][A-Za-z0-9+.\-]*):)?(?://(?<authority>[^/?#]*))?(?<path>[^?#]*)(?:\?(?<query>[^#]*))?(?:#.*)?$", RegexOptions.Singleline)]
    private static partial Regex Rfc3986();

    [GeneratedRegex(@"^(?:0x[0-9a-f]+|[0-9]+)(?:\.(?:0x[0-9a-f]+|[0-9]+)){0,3}$", RegexOptions.IgnoreCase)]
    private static partial Regex NumericHost();

    [GeneratedRegex(@";jsessionid=[^/]*", RegexOptions.IgnoreCase)]
    private static partial Regex JSessionId();

    [GeneratedRegex(@"^\((?:[A-Z]\([^()/]*\))+\)$")]
    private static partial Regex AspNetCookieless();

    [GeneratedRegex(@"^(?:.+\.)?(?<id>[0-9]+\.[0-9a-f]{10,})\.html$")]
    private static partial Regex SitevisionNodeId();
}
