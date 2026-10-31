using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ume.Rag.Worker.Processing.Parse;

/// <summary>
/// Where an admin-made rule applies (processing design §3.5: domain, path glob, media type). <c>Domain</c> is a host
/// (<c>www.umea.se</c>) or a suffix wildcard (<c>*.umea.se</c>, subdomains only); <c>PathGlob</c> matches the whole
/// unescaped URL path, case-insensitively, with <c>*</c> for any run of characters (<c>/</c> included) and <c>?</c> for
/// one, as the observations' path patterns read (<c>/kommun/protokoll/0/*</c>); <c>MediaType</c> is a media type or
/// <c>type/*</c>. An unset field matches everything; documents without a URL (uploads) match no domain or path.
/// </summary>
public sealed record ParseRuleScope(string? Domain, string? PathGlob, string? MediaType)
{
    private readonly Regex? _path = PathGlob is null ? null : Glob(PathGlob);

    /// <summary>The number of fields set: more specific rules come first.</summary>
    public int Fields => (Domain is null ? 0 : 1) + (PathGlob is null ? 0 : 1) + (MediaType is null ? 0 : 1);

    public bool Matches(Uri? url, string mediaType)
    {
        ArgumentNullException.ThrowIfNull(mediaType);
        if (MediaType is not null && !MediaTypeMatches(mediaType))
        {
            return false;
        }

        if (Domain is null && PathGlob is null)
        {
            return true;
        }

        if (url is null)
        {
            return false;
        }

        var host = url.IdnHost;
        if (Domain is not null && !(Domain.StartsWith("*.", StringComparison.Ordinal)
                ? host.EndsWith(Domain[1..], StringComparison.OrdinalIgnoreCase)
                : string.Equals(host, Domain, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return _path is null || _path.IsMatch(Uri.UnescapeDataString(url.AbsolutePath));
    }

    private bool MediaTypeMatches(string mediaType) => MediaType!.EndsWith("/*", StringComparison.Ordinal)
        ? mediaType.StartsWith(MediaType[..^1], StringComparison.OrdinalIgnoreCase)
        : string.Equals(mediaType, MediaType, StringComparison.OrdinalIgnoreCase);

    private static Regex Glob(string glob) =>
        new("^" + Regex.Escape(glob).Replace(@"\*", ".*", StringComparison.Ordinal).Replace(@"\?", ".", StringComparison.Ordinal) + "$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking);
}

/// <summary>
/// An admin-made parse rule (<c>content."ParseProfiles"</c>, <c>Origin = manual</c>, <c>State = active</c>): its level
/// (rules apply only within their level, data-model §12 row 17), its department scope (null = every department), where it
/// applies and the <see cref="ParseRule"/> it contributes (overrides, hints, optional text match).
/// </summary>
public sealed record AdminParseRule(Guid ProfileId, string Classification, Guid? DepartmentId, ParseRuleScope Scope, ParseRule Rule);

/// <summary>
/// The active admin-made rules of one config snapshot in precedence order (processing design §3.5): admin-made rules
/// before the built-in ones, and among them the more specific first: more match fields (a text match counts as one), then
/// the longer path glob, then the profile id so the order is stable.
/// </summary>
public sealed class AdminParseRuleSet
{
    public static AdminParseRuleSet Empty { get; } = new([]);

    private readonly IReadOnlyList<AdminParseRule> _rules;

    public AdminParseRuleSet(IEnumerable<AdminParseRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _rules = [.. rules
            .OrderByDescending(r => r.Scope.Fields + (r.Rule.TextMatch is null ? 0 : 1))
            .ThenByDescending(r => r.Scope.PathGlob?.Length ?? 0)
            .ThenBy(r => r.ProfileId)];
    }

    public int Count => _rules.Count;

    /// <summary>Whether any rule is scoped to a department, so matching needs the source's department.</summary>
    public bool NeedsDepartment => _rules.Any(r => r.DepartmentId is not null);

    /// <summary>
    /// The admin-made rules that can apply to a document, in precedence order: those of its level and department whose
    /// scope matches, cut after the first without a text match (it always hits, so the rest can never be chosen). Their
    /// ids are the parse stage's <c>parseRule</c> input (<see cref="Key"/>), so a rule change re-keys only the documents in
    /// its scope; whether a text match hits is decided on the joined output (<see cref="ParseRules.Match(string?, string, IReadOnlyList{ParseRule})"/>).
    /// </summary>
    public IReadOnlyList<ParseRule> Candidates(string classification, Guid? departmentId, Uri? url, string mediaType)
    {
        ArgumentNullException.ThrowIfNull(classification);
        ArgumentNullException.ThrowIfNull(mediaType);
        var candidates = new List<ParseRule>();
        foreach (var rule in _rules)
        {
            if (!string.Equals(rule.Classification, classification, StringComparison.Ordinal) ||
                (rule.DepartmentId is { } department && department != departmentId) ||
                !rule.Scope.Matches(url, mediaType))
            {
                continue;
            }

            candidates.Add(rule.Rule);
            if (rule.Rule.TextMatch is null)
            {
                break;
            }
        }

        return candidates;
    }

    /// <summary>The <c>parseRule</c> input of the parse key: the candidates' ids in order, or null when none applies (the built-in key).</summary>
    public static string? Key(IReadOnlyList<ParseRule> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return candidates.Count == 0 ? null : string.Join(',', candidates.Select(c => c.Id));
    }
}

/// <summary>
/// Reads a <c>ParseProfiles</c> row into an <see cref="AdminParseRule"/>. The shape (camelCase, unknown members refused):
/// <c>Match</c> <c>{domain, pathGlob, mediaType, textPattern}</c> (at least one); <c>Strategy</c> <c>{ocr: auto|force|off,
/// tables: fast|accurate, imageScale: 1.0–4.0}</c>; <c>CleanupRules</c> <c>{maxLevel: 1–6, paragraphItemsAreSections,
/// furniture: [pattern, …]}</c>. Patterns run linear-time (<see cref="ParseRules.AdminPattern"/>). A row that does not
/// read is refused with a <c>parse_rule.*</c> code and never matched.
/// </summary>
public static class AdminParseRuleReader
{
    public const int RevisionChars = 16;
    public const int MaxFurniture = 20;
    public const int MaxPatternChars = 500;

    public static AdminParseRule? Read(Guid profileId, string classification, Guid? departmentId, string match, string strategy, string? cleanupRules,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(match);
        ArgumentNullException.ThrowIfNull(strategy);
        try
        {
            using var matchJson = JsonDocument.Parse(match);
            using var strategyJson = JsonDocument.Parse(strategy);
            using var cleanupJson = cleanupRules is null ? null : JsonDocument.Parse(cleanupRules);
            string? domain = null, pathGlob = null, mediaType = null, ocr = null, tables = null;
            Regex? text = null;
            double? imageScale = null;
            int? maxLevel = null;
            var paragraphItems = false;
            var furniture = new List<Regex>();

            foreach (var p in Members(matchJson.RootElement, "parse_rule.match_invalid"))
            {
                switch (p.Name)
                {
                    case "domain": domain = Text(p.Value, "parse_rule.match_invalid").ToLowerInvariant(); break;
                    case "pathGlob": pathGlob = Text(p.Value, "parse_rule.match_invalid"); break;
                    case "mediaType": mediaType = Text(p.Value, "parse_rule.match_invalid"); break;
                    case "textPattern": text = Pattern(p.Value); break;
                    default: throw new RuleException("parse_rule.match_invalid");
                }
            }

            if (domain is null && pathGlob is null && mediaType is null && text is null)
            {
                throw new RuleException("parse_rule.match_empty");
            }

            foreach (var p in Members(strategyJson.RootElement, "parse_rule.strategy_invalid"))
            {
                switch (p.Name)
                {
                    case "ocr": ocr = OneOf(p.Value, "parse_rule.strategy_invalid", "auto", "force", "off"); break;
                    case "tables": tables = OneOf(p.Value, "parse_rule.strategy_invalid", "fast", "accurate"); break;
                    case "imageScale" when p.Value.ValueKind == JsonValueKind.Number && p.Value.GetDouble() is >= 1.0 and <= 4.0:
                        imageScale = p.Value.GetDouble();
                        break;
                    default: throw new RuleException("parse_rule.strategy_invalid");
                }
            }

            if (cleanupJson is not null && cleanupJson.RootElement.ValueKind != JsonValueKind.Null)
            {
                foreach (var p in Members(cleanupJson.RootElement, "parse_rule.cleanup_invalid"))
                {
                    switch (p.Name)
                    {
                        case "maxLevel" when p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var level) && level is >= 1 and <= 6:
                            maxLevel = level;
                            break;
                        case "paragraphItemsAreSections" when p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                            paragraphItems = p.Value.GetBoolean();
                            break;
                        case "furniture" when p.Value.ValueKind == JsonValueKind.Array && p.Value.GetArrayLength() <= MaxFurniture:
                            furniture.AddRange(p.Value.EnumerateArray().Select(Pattern));
                            break;
                        default: throw new RuleException("parse_rule.cleanup_invalid");
                    }
                }
            }

            error = null;
            var rule = new ParseRule(profileId.ToString("D"), 0)
            {
                Revision = Revision(match, strategy, cleanupRules),
                TextMatch = text,
                Ocr = ocr,
                Tables = tables,
                ImageScale = imageScale,
                MaxLevel = maxLevel ?? ParseRules.Default.MaxLevel,
                ParagraphItemsAreSections = paragraphItems,
                Furniture = furniture,
            };
            return new AdminParseRule(profileId, classification, departmentId, new ParseRuleScope(domain, pathGlob, mediaType), rule);
        }
        catch (RuleException e)
        {
            error = e.Message;
            return null;
        }
        catch (JsonException)
        {
            error = "parse_rule.json_invalid";
            return null;
        }
    }

    /// <summary>The first <see cref="RevisionChars"/> hex characters of SHA-256 over the three <c>jsonb</c> texts (Postgres prints <c>jsonb</c> in one normal form).</summary>
    public static string Revision(string match, string strategy, string? cleanupRules) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(match + "\n" + strategy + "\n" + (cleanupRules ?? string.Empty))))[..RevisionChars];

    private static JsonElement.ObjectEnumerator Members(JsonElement element, string code) =>
        element.ValueKind == JsonValueKind.Object ? element.EnumerateObject() : throw new RuleException(code);

    private static string Text(JsonElement value, string code) =>
        value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 and <= MaxPatternChars } text ? text : throw new RuleException(code);

    private static string OneOf(JsonElement value, string code, params string[] allowed) =>
        Text(value, code) is var text && allowed.Contains(text, StringComparer.Ordinal) ? text : throw new RuleException(code);

    private static Regex Pattern(JsonElement value)
    {
        var pattern = Text(value, "parse_rule.pattern_invalid");
        try
        {
            return ParseRules.AdminPattern(pattern);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException)
        {
            throw new RuleException("parse_rule.pattern_invalid");
        }
    }

    private sealed class RuleException(string code) : Exception(code);
}
