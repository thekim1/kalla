using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Kalla.Worker.Processing.Parse;

/// <summary>
/// A parse rule (processing design §3.5): strategy overrides for the plan and structure hints for <c>clean</c> and the
/// split, matched on the title or the first 2,000 characters of the joined output. Built-in rules are
/// <see cref="ParseRules.BuiltIn"/> (<c>ParseProfiles.Origin = builtin</c>); admin-made rules (<c>manual</c>) take the same
/// shape with a <see cref="Revision"/> and a scope (<see cref="AdminParseRule"/>).
/// </summary>
public sealed record ParseRule(string Name, int Version)
{
    /// <summary>Text-level match on the title or the first 2,000 characters; null matches everything (<c>default</c>).</summary>
    public Regex? TextMatch { get; init; }

    /// <summary>Overrides the template's <c>ocr</c> (<c>auto</c>, <c>force</c>, <c>off</c>).</summary>
    public string? Ocr { get; init; }

    /// <summary>Overrides the template's <c>tables</c> (<c>fast</c>, <c>accurate</c>).</summary>
    public string? Tables { get; init; }

    public double? ImageScale { get; init; }

    /// <summary>Headings deeper than this become bold body text (processing design §5.2; default 4).</summary>
    public int MaxLevel { get; init; } = 4;

    /// <summary>Each <c>§ N</c> item is a level-1 section (<c>protokoll</c>).</summary>
    public bool ParagraphItemsAreSections { get; init; }

    /// <summary>Lines dropped as furniture by <c>clean</c> (processing design §4.2), e.g. signature blocks.</summary>
    public IReadOnlyList<Regex> Furniture { get; init; } = [];

    /// <summary>
    /// An admin-made rule's version: <c>ParseProfiles</c> has no version column, so a hash of the rule's content stands for
    /// it and every save that changes the rule changes its id. Null for built-in rules, which are versioned with the stage.
    /// </summary>
    public string? Revision { get; init; }

    /// <summary><c>name@version</c> (admin-made: <c>profileId@revision</c>), the <c>parse.rule</c> fact and part of the strategy string.</summary>
    public string Id => Revision is null ? $"{Name}@{Version}" : $"{Name}@{Revision}";

    /// <summary>The structure hints <c>clean</c> and the split read, as the parse output carries them for an admin-made rule.</summary>
    public ParseRuleHints Hints() => new(MaxLevel, ParagraphItemsAreSections, [.. Furniture.Select(f => f.ToString())]);
}

/// <summary>
/// An admin-made rule's structure hints in the parse output (<see cref="ParsedDocument.RuleHints"/>): <c>clean</c> and the
/// split find built-in rules by id, but an admin-made one may have changed or gone since the parse, and its hints are part
/// of the output hash, so a hint change reaches <c>clean</c>'s key.
/// </summary>
public sealed record ParseRuleHints(int MaxLevel, bool ParagraphItemsAreSections, IReadOnlyList<string> Furniture);

/// <summary>The built-in parse rules of processing design §3.5, matched in order; <c>default</c> last.</summary>
public static partial class ParseRules
{
    public const int TextMatchChars = 2_000;

    public static ParseRule Default { get; } = new("default", 1);

    public static ParseRule Regulations { get; } = new("regulations", 1)
    {
        TextMatch = RegulationsPattern(),
        MaxLevel = 4,
    };

    public static ParseRule Protokoll { get; } = new("protokoll", 1)
    {
        TextMatch = ProtokollPattern(),
        ParagraphItemsAreSections = true,
        Furniture = [SignaturePattern()],
    };

    /// <summary>In match order: <c>regulations</c>, <c>protokoll</c>, <c>default</c>.</summary>
    public static IReadOnlyList<ParseRule> BuiltIn { get; } = [Regulations, Protokoll, Default];

    private static readonly ConcurrentDictionary<string, Regex> AdminPatterns = new(StringComparer.Ordinal);

    /// <summary>The first built-in rule whose text match hits the title or the start of the text.</summary>
    public static ParseRule Match(string? title, string text) => Match(title, text, []);

    /// <summary>
    /// The first of <paramref name="admin"/> (the document's admin-made candidates in precedence order,
    /// <see cref="AdminParseRuleSet.Candidates"/>), then of the built-in rules, whose text match hits the title or the start
    /// of the text; a rule without a text match always hits. Admin-made rules come first (processing design §3.5).
    /// </summary>
    public static ParseRule Match(string? title, string text, IReadOnlyList<ParseRule> admin)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(admin);
        var start = text.Length > TextMatchChars ? text[..TextMatchChars] : text;
        foreach (var rule in admin.Concat(BuiltIn))
        {
            if (rule.TextMatch is null || (title is not null && rule.TextMatch.IsMatch(title)) || rule.TextMatch.IsMatch(start))
            {
                return rule;
            }
        }

        return Default;
    }

    /// <summary>
    /// The strategy-level rule of a document's admin-made candidates: the first without a text match, whose overrides feed
    /// the plan (strategy-level matches run before the plan, text-level ones on the joined output); else <c>default</c>.
    /// </summary>
    public static ParseRule StrategyRule(IReadOnlyList<ParseRule> admin)
    {
        ArgumentNullException.ThrowIfNull(admin);
        return admin.FirstOrDefault(r => r.TextMatch is null) ?? Default;
    }

    public static ParseRule? Find(string id) => BuiltIn.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.Ordinal));

    /// <summary>The rule whose hints <c>clean</c> and the split apply to <paramref name="document"/>: a built-in one by id, an admin-made one from the output's hints.</summary>
    public static ParseRule Of(ParsedDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.ParseRule is not { } id)
        {
            return Default;
        }

        if (Find(id) is { } builtIn)
        {
            return builtIn;
        }

        if (document.RuleHints is not { } hints)
        {
            return Default;
        }

        var at = id.LastIndexOf('@');
        return new ParseRule(at < 0 ? id : id[..at], 0)
        {
            Revision = at < 0 ? string.Empty : id[(at + 1)..],
            MaxLevel = hints.MaxLevel,
            ParagraphItemsAreSections = hints.ParagraphItemsAreSections,
            Furniture = [.. hints.Furniture.Select(AdminPattern)],
        };
    }

    /// <summary>
    /// An admin-made pattern: linear-time (<see cref="RegexOptions.NonBacktracking"/>, so no admin pattern can stall a
    /// stage) and culture-invariant; compiled once per pattern. Throws <see cref="ArgumentException"/> or
    /// <see cref="NotSupportedException"/> for a pattern the engine cannot run (checked when the rule is read).
    /// </summary>
    public static Regex AdminPattern(string pattern) =>
        AdminPatterns.GetOrAdd(pattern, p => new Regex(p, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking));

    [GeneratedRegex(@"(?i)\b(föreskrift(er)?|reglemente|taxa|ordningsföreskrifter|författningssamling)\b", RegexOptions.CultureInvariant)]
    private static partial Regex RegulationsPattern();

    [GeneratedRegex(@"(?i)\b(protokoll|sammanträdesprotokoll|kallelse)\b", RegexOptions.CultureInvariant)]
    private static partial Regex ProtokollPattern();

    [GeneratedRegex(@"^(Justerare|Justerandes signatur|Utdragsbestyrkande)\b", RegexOptions.CultureInvariant)]
    private static partial Regex SignaturePattern();
}
