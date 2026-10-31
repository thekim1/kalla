using System.Text.RegularExpressions;

namespace Ume.Rag.Worker.Processing.Parse;

/// <summary>
/// A parse rule (processing design §3.5): strategy overrides for the plan and structure hints for <c>clean</c> and the
/// split, matched on the title or the first 2,000 characters of the joined output. P1a has the built-in rules only
/// (<see cref="ParseRules.BuiltIn"/>, <c>ParseProfiles.Origin = builtin</c>); admin-made rules take the same shape later.
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

    /// <summary><c>name@version</c>, the <c>parse.rule</c> fact and part of the strategy string.</summary>
    public string Id => $"{Name}@{Version}";
}

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

    /// <summary>The first built-in rule whose text match hits the title or the start of the text.</summary>
    public static ParseRule Match(string? title, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var start = text.Length > TextMatchChars ? text[..TextMatchChars] : text;
        foreach (var rule in BuiltIn)
        {
            if (rule.TextMatch is null || (title is not null && rule.TextMatch.IsMatch(title)) || rule.TextMatch.IsMatch(start))
            {
                return rule;
            }
        }

        return Default;
    }

    public static ParseRule? Find(string id) => BuiltIn.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.Ordinal));

    [GeneratedRegex(@"(?i)\b(föreskrift(er)?|reglemente|taxa|ordningsföreskrifter|författningssamling)\b", RegexOptions.CultureInvariant)]
    private static partial Regex RegulationsPattern();

    [GeneratedRegex(@"(?i)\b(protokoll|sammanträdesprotokoll|kallelse)\b", RegexOptions.CultureInvariant)]
    private static partial Regex ProtokollPattern();

    [GeneratedRegex(@"^(Justerare|Justerandes signatur|Utdragsbestyrkande)\b", RegexOptions.CultureInvariant)]
    private static partial Regex SignaturePattern();
}
