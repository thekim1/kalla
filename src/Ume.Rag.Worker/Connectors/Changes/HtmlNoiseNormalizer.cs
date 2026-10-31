using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

namespace Ume.Rag.Worker.Connectors.Changes;

/// <summary>
/// HTML noise normalisation, rules v2 (connectors design §12.2): what changes on every request without the page changing
/// is removed before the content hash, so an unchanged page keeps its hash. Conservative on purpose: hiding a real change
/// is worse than one extra re-process; what remains is measured (<c>rag.connector.change.noise</c>). v2 adds Sitevision's
/// per-request values to v1: <c>data-cid</c> is dropped and <c>sv_…</c> element ids (and the references to them) become
/// placeholders numbered by first occurrence.
/// </summary>
public static partial class HtmlNoiseNormalizer
{
    /// <summary><c>Documents.ContentHashVersion</c> for HTML; a bump re-processes HTML documents once (release-notes item).</summary>
    public const int RulesVersion = 2;

    private const string DatePlaceholder = "{date}";

    private const string SitevisionIdPrefix = "sv_";

    /// <summary>The attributes whose <c>sv_…</c> values Sitevision draws per request: the ids and the references to them.</summary>
    private static readonly string[] SitevisionIdAttributes = ["id", "for", "aria-labelledby", "aria-describedby"];

    private static readonly HashSet<string> TokenMetaNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "csrf-token", "csrf-param", "__RequestVerificationToken", "__VIEWSTATE", "__EVENTVALIDATION",
    };

    private static readonly HashSet<string> KeptScriptTypes = new(StringComparer.OrdinalIgnoreCase) { "application/json", "application/ld+json" };

    private static readonly CultureInfo Swedish = CultureInfo.GetCultureInfo("sv-SE");

    /// <summary>Parses <paramref name="html"/> and returns the SHA-256 (lower-case hex) of its normalised form.</summary>
    public static Task<string> HashAsync(Stream html, DateOnly fetchDay, IReadOnlyList<string> noiseSelectors, CancellationToken cancellationToken) =>
        HashAsync(html, fetchDay, noiseSelectors, RulesVersion, cancellationToken);

    /// <summary>As <see cref="HashAsync(Stream, DateOnly, IReadOnlyList{string}, CancellationToken)"/> under the given rules version.</summary>
    internal static async Task<string> HashAsync(Stream html, DateOnly fetchDay, IReadOnlyList<string> noiseSelectors, int rulesVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(html);
        using var document = await new HtmlParser().ParseDocumentAsync(html, cancellationToken);
        return Hash(Normalize(document, fetchDay, noiseSelectors, rulesVersion), rulesVersion);
    }

    public static string Hash(string normalized) => Hash(normalized, RulesVersion);

    /// <summary>
    /// v1 hashed the normalised form alone (stored v1 hashes stay reproducible). From v2 the rules version is hashed with
    /// it, so a hash stored under other rules never equals a new one: a bump is one "changed" per page, never a false
    /// "unchanged", in every store that keeps only the hash (the frontier, <c>Documents.ContentHash</c>).
    /// </summary>
    internal static string Hash(string normalized, int rulesVersion)
    {
        ArgumentNullException.ThrowIfNull(normalized);
        ArgumentOutOfRangeException.ThrowIfLessThan(rulesVersion, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rulesVersion, RulesVersion);
        var input = rulesVersion == 1 ? normalized : $"html-noise-v{rulesVersion.ToString(CultureInfo.InvariantCulture)}\n{normalized}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }

    /// <summary>
    /// Applies the rules to <paramref name="document"/> in place and returns its serialisation with whitespace collapsed.
    /// <paramref name="fetchDay"/> is the fetch day in Europe/Stockholm; <paramref name="noiseSelectors"/> are the source's
    /// CSS selectors (an invalid one is skipped: the admin validates them on save).
    /// </summary>
    public static string Normalize(IHtmlDocument document, DateOnly fetchDay, IReadOnlyList<string> noiseSelectors) =>
        Normalize(document, fetchDay, noiseSelectors, RulesVersion);

    /// <summary>As <see cref="Normalize(IHtmlDocument, DateOnly, IReadOnlyList{string})"/> under the given rules version.</summary>
    internal static string Normalize(IHtmlDocument document, DateOnly fetchDay, IReadOnlyList<string> noiseSelectors, int rulesVersion)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(noiseSelectors);
        ArgumentOutOfRangeException.ThrowIfLessThan(rulesVersion, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rulesVersion, RulesVersion);

        foreach (var comment in document.Descendants<IComment>().ToList())
        {
            comment.Parent?.RemoveChild(comment);
        }

        foreach (var element in document.QuerySelectorAll("script, style, template").ToList())
        {
            if (element.LocalName != "script" || !KeptScriptTypes.Contains(element.GetAttribute("type")?.Trim() ?? string.Empty))
            {
                element.Remove();
            }
        }

        foreach (var selector in noiseSelectors)
        {
            IEnumerable<IElement> matches;
            try
            {
                matches = document.QuerySelectorAll(selector).ToList();
            }
            catch (DomException)
            {
                continue;
            }

            foreach (var element in matches)
            {
                element.Remove();
            }
        }

        var sitevisionIds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var element in document.QuerySelectorAll("*"))
        {
            NormalizeAttributes(element, rulesVersion);
            if (rulesVersion >= 2)
            {
                NormalizeSitevisionIds(element, sitevisionIds);
            }
        }

        ReplaceFetchDay(document, fetchDay);

        var serialized = document.DocumentElement?.OuterHtml ?? string.Empty;
        return Whitespace().Replace(serialized, " ").Trim();
    }

    private static void NormalizeAttributes(IElement element, int rulesVersion)
    {
        var drop = element.Attributes
            .Select(a => a.Name)
            .Where(n => n is "nonce" or "integrity" || n.StartsWith("data-csrf", StringComparison.OrdinalIgnoreCase) || (rulesVersion >= 2 && n == "data-cid"))
            .ToList();
        foreach (var name in drop)
        {
            element.RemoveAttribute(name);
        }

        switch (element.LocalName)
        {
            case "input" when string.Equals(element.GetAttribute("type"), "hidden", StringComparison.OrdinalIgnoreCase) && element.HasAttribute("value"):
                element.SetAttribute("value", string.Empty);
                break;
            case "meta" when element.GetAttribute("name") is { } name && TokenMetaNames.Contains(name) && element.HasAttribute("content"):
                element.SetAttribute("content", string.Empty);
                break;
            case "script" or "img":
                StripQuery(element, "src");
                break;
            case "link":
                StripQuery(element, "href");
                break;
        }
    }

    /// <summary>
    /// Sitevision's per-request ids (v2): each distinct <c>sv_…</c> value of <c>id</c>, <c>for</c>, <c>aria-labelledby</c>
    /// and <c>aria-describedby</c> (the last two are id lists) becomes <c>{sv-id:N}</c>, N by first occurrence in document
    /// order, so an id and the references to it stay paired. Any other value is kept.
    /// </summary>
    private static void NormalizeSitevisionIds(IElement element, Dictionary<string, string> placeholders)
    {
        foreach (var name in SitevisionIdAttributes)
        {
            if (element.GetAttribute(name) is not { } value || !value.Contains(SitevisionIdPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var tokens = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var replaced = false;
            for (var i = 0; i < tokens.Length; i++)
            {
                if (tokens[i].StartsWith(SitevisionIdPrefix, StringComparison.Ordinal))
                {
                    if (!placeholders.TryGetValue(tokens[i], out var placeholder))
                    {
                        placeholder = $"{{sv-id:{(placeholders.Count + 1).ToString(CultureInfo.InvariantCulture)}}}";
                        placeholders.Add(tokens[i], placeholder);
                    }

                    tokens[i] = placeholder;
                    replaced = true;
                }
            }

            if (replaced)
            {
                element.SetAttribute(name, string.Join(' ', tokens));
            }
        }
    }

    /// <summary>Cache busters: <c>app.js?v=123</c> → <c>app.js</c>.</summary>
    private static void StripQuery(IElement element, string attribute)
    {
        if (element.GetAttribute(attribute) is { } value && value.IndexOf('?', StringComparison.Ordinal) is var question and >= 0)
        {
            element.SetAttribute(attribute, value[..question]);
        }
    }

    /// <summary>The fetch day as <c>2026-10-29</c>, <c>29 oktober 2026</c> or <c>idag</c>, outside <c>main</c> and <c>article</c>.</summary>
    private static void ReplaceFetchDay(IHtmlDocument document, DateOnly fetchDay)
    {
        var iso = fetchDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var swedish = fetchDay.ToString("d MMMM yyyy", Swedish);
        foreach (var text in document.Descendants<IText>().ToList())
        {
            if (text.ParentElement?.Closest("main, article") is not null)
            {
                continue;
            }

            var data = text.Data;
            var replaced = data.Replace(iso, DatePlaceholder, StringComparison.Ordinal)
                .Replace(swedish, DatePlaceholder, StringComparison.OrdinalIgnoreCase);
            replaced = Today().Replace(replaced, DatePlaceholder);
            if (!string.Equals(replaced, data, StringComparison.Ordinal))
            {
                text.Data = replaced;
            }
        }
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\bidag\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Today();
}
