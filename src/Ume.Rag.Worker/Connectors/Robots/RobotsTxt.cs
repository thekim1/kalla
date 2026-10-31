using System.Globalization;
using System.Text;
using Ume.Rag.Worker.Connectors.Urls;

namespace Ume.Rag.Worker.Connectors.Robots;

/// <summary>
/// A parsed robots.txt (RFC 9309; connectors design §5.1). The groups naming <see cref="ProductToken"/> apply, else the
/// <c>*</c> groups; the longest matching rule wins and <c>Allow</c> beats <c>Disallow</c> on a tie; <c>*</c> and
/// <c>$</c> are supported; only the first 500 KiB are parsed. <c>Crawl-delay</c> and <c>Sitemap:</c> lines are read as well.
/// </summary>
public sealed class RobotsTxt
{
    /// <summary>The group name the crawler answers to, case-insensitive (§5.1).</summary>
    public const string ProductToken = "UmeaKommunBot";

    /// <summary>RFC 9309 §2.5: a crawler must parse at least 500 KiB.</summary>
    public const int MaxBytes = 500 * 1024;

    private readonly IReadOnlyList<Rule> _rules;
    private readonly bool _disallowAll;

    private RobotsTxt(IReadOnlyList<Rule> rules, bool disallowAll, double? crawlDelaySeconds, IReadOnlyList<string> sitemaps)
    {
        _rules = rules;
        _disallowAll = disallowAll;
        CrawlDelaySeconds = crawlDelaySeconds;
        Sitemaps = sitemaps;
    }

    /// <summary>4xx: no robots.txt, everything allowed (RFC 9309 §2.3.1.3 "unavailable").</summary>
    public static RobotsTxt AllowAll { get; } = new([], false, null, []);

    /// <summary>5xx or unreachable: everything disallowed for the run (§2.3.1.4 "unreachable").</summary>
    public static RobotsTxt DisallowAll { get; } = new([], true, null, []);

    /// <summary>The <c>Crawl-delay</c> of the applying group, if any (honoured only when it lowers the rate, §5.1).</summary>
    public double? CrawlDelaySeconds { get; }

    /// <summary>Every <c>Sitemap:</c> line, whatever its group.</summary>
    public IReadOnlyList<string> Sitemaps { get; }

    public static RobotsTxt Parse(ReadOnlySpan<byte> content)
    {
        if (content.Length > MaxBytes)
        {
            content = content[..MaxBytes];
        }

        var text = Encoding.UTF8.GetString(content);
        if (text.Length > 0 && text[0] == '﻿')
        {
            text = text[1..];
        }

        var groups = new List<Group>();
        var sitemaps = new List<string>();
        Group? current = null;
        var lastWasAgent = false;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine;
            var hash = line.IndexOf('#', StringComparison.Ordinal);
            if (hash >= 0)
            {
                line = line[..hash];
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
            {
                continue;
            }

            var key = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();
            switch (key)
            {
                case "user-agent":
                    // Consecutive user-agent lines share one group; one after a rule starts a new group.
                    if (current is null || !lastWasAgent)
                    {
                        current = new Group();
                        groups.Add(current);
                    }

                    current.Agents.Add(value);
                    lastWasAgent = true;
                    continue;
                case "allow" or "disallow" when current is not null:
                    if (value.Length > 0)
                    {
                        current.Rules.Add(new Rule(key == "allow", NormalizePattern(value)));
                    }

                    break;
                case "crawl-delay" when current is not null:
                    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var delay) && delay >= 0)
                    {
                        current.CrawlDelay = delay;
                    }

                    break;
                case "sitemap":
                    if (value.Length > 0)
                    {
                        sitemaps.Add(value);
                    }

                    break;
            }

            lastWasAgent = false;
        }

        // RFC 9309 §2.2.1: every group naming the crawler applies, combined; otherwise every "*" group.
        var applying = groups.Where(g => g.Agents.Any(NamesUs)).ToList();
        if (applying.Count == 0)
        {
            applying = [.. groups.Where(g => g.Agents.Contains("*", StringComparer.Ordinal))];
        }

        return new RobotsTxt(
            [.. applying.SelectMany(g => g.Rules)],
            false,
            applying.Select(g => g.CrawlDelay).FirstOrDefault(d => d is not null),
            sitemaps);
    }

    /// <summary>Whether <paramref name="url"/>'s path and query may be fetched; <c>/robots.txt</c> always may.</summary>
    public bool IsAllowed(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        var target = UrlCanonicalizer.NormalizePercentEncoding(url.PathAndQuery, isQuery: false);
        if (target == "/robots.txt")
        {
            return true;
        }

        if (_disallowAll)
        {
            return false;
        }

        Rule? best = null;
        foreach (var rule in _rules)
        {
            if (rule.Matches(target)
                && (best is null || rule.Length > best.Length || (rule.Length == best.Length && rule.Allow && !best.Allow)))
            {
                best = rule;
            }
        }

        return best?.Allow ?? true;
    }

    private static bool NamesUs(string agent)
    {
        // "UmeaKommunBot/1.0" and "UmeaKommunBot" both name us; the token is letters only, so compare up to '/' or space.
        var end = agent.IndexOfAny(['/', ' ']);
        var token = end < 0 ? agent : agent[..end];
        return string.Equals(token, ProductToken, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Patterns are compared in the canonicaliser's percent-encoding, so <c>/ö</c> and <c>/%c3%b6</c> are one path.</summary>
    private static string NormalizePattern(string pattern)
    {
        var anchored = pattern.EndsWith('$');
        var body = anchored ? pattern[..^1] : pattern;
        var parts = body.Split('*').Select(p => UrlCanonicalizer.NormalizePercentEncoding(p, isQuery: false));
        return string.Join('*', parts) + (anchored ? "$" : string.Empty);
    }

    private sealed class Group
    {
        public List<string> Agents { get; } = [];

        public List<Rule> Rules { get; } = [];

        public double? CrawlDelay { get; set; }
    }

    private sealed class Rule(bool allow, string pattern)
    {
        public bool Allow { get; } = allow;

        /// <summary>The pattern's length in octets, for "longest match wins".</summary>
        public int Length { get; } = pattern.Length;

        /// <summary>
        /// <c>*</c> is any run of characters, a final <c>$</c> anchors the end, everything else is literal; a pattern without
        /// <c>$</c> matches a prefix. Greedy with one backtrack point, so no pattern is exponential.
        /// </summary>
        public bool Matches(string path)
        {
            var anchored = pattern.EndsWith('$');
            var glob = anchored ? pattern[..^1] : pattern + "*";
            int p = 0, i = 0, star = -1, mark = 0;
            while (i < path.Length)
            {
                if (p < glob.Length && glob[p] != '*' && glob[p] == path[i])
                {
                    p++;
                    i++;
                }
                else if (p < glob.Length && glob[p] == '*')
                {
                    star = p++;
                    mark = i;
                }
                else if (star >= 0)
                {
                    p = star + 1;
                    i = ++mark;
                }
                else
                {
                    return false;
                }
            }

            while (p < glob.Length && glob[p] == '*')
            {
                p++;
            }

            return p == glob.Length;
        }
    }
}
