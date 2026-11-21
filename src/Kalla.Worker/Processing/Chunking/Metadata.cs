using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kalla.Worker.Processing.Detect;

namespace Kalla.Worker.Processing.Chunking;

/// <summary>One path rule of the template parameter <c>pathRules</c> (processing design §7; <c>website.v1.json</c> <c>#/defs/pathRule</c>).</summary>
public sealed record PathRule
{
    public required string Match { get; init; }

    /// <summary><c>path</c>, <c>path-regex</c>, <c>host</c>, <c>title-regex</c>, <c>mediaType</c>.</summary>
    public required string On { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    public string? DocumentType { get; init; }

    /// <summary>A date, with <c>${group}</c> for named groups of a regex match (<c>${y}-01-01</c>).</summary>
    public string? Date { get; init; }
}

/// <summary>What <c>Metadata</c> knows about the document (processing design §7).</summary>
public sealed record MetadataInput(string MediaType)
{
    public Uri? Url { get; init; }

    public string? DisplayName { get; init; }

    public string? Title { get; init; }

    public DateOnly? VisibleDate { get; init; }

    public DateOnly? DocumentDate { get; init; }

    public DateTimeOffset? ReliableLastmod { get; init; }

    public DateTimeOffset? HttpLastModified { get; init; }
}

/// <summary>The metadata every chunk of the set carries.</summary>
public sealed record DocumentMetadata(IReadOnlyList<string> Tags, string DocumentType, DateOnly? SourceModifiedAt);

/// <summary>
/// <c>Metadata</c> (processing design §7, part of <c>chunk</c>): every matching path rule adds its tags, the first match
/// sets <c>documentType</c> and <c>date</c>; tags are lower case, NFC, <c>[\p{L}\p{N}-]{1,50}</c>, at most 20. The date is
/// the first of: the visible date, a path-rule date, a reliable sitemap <c>lastmod</c>, the document's own date, HTTP
/// <c>Last-Modified</c>.
/// </summary>
public static partial class Metadata
{
    public const int MaxTags = 20;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    public static IReadOnlyList<PathRule> ReadRules(JsonElement? pathRules) =>
        pathRules is { ValueKind: JsonValueKind.Array } rules
            ? rules.Deserialize<List<PathRule>>(ProcessingJson.Options) ?? []
            : [];

    public static DocumentMetadata Apply(IReadOnlyList<PathRule> rules, MetadataInput input)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(input);
        var tags = new List<string>();
        string? documentType = null;
        DateOnly? ruleDate = null;
        var matchedFirst = false;
        foreach (var rule in rules)
        {
            if (Match(rule, input) is not { } groups)
            {
                continue;
            }

            foreach (var tag in rule.Tags.Select(NormaliseTag).OfType<string>())
            {
                if (!tags.Contains(tag, StringComparer.Ordinal) && tags.Count < MaxTags)
                {
                    tags.Add(tag);
                }
            }

            if (!matchedFirst)
            {
                matchedFirst = true;
                documentType = rule.DocumentType;
                if (rule.Date is { } date)
                {
                    var expanded = Group().Replace(date, m => groups.GetValueOrDefault(m.Groups[1].Value) ?? string.Empty);
                    ruleDate = DateOnly.TryParseExact(expanded, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : (DateOnly?)null;
                }
            }
        }

        var when = input.VisibleDate ?? ruleDate
            ?? (input.ReliableLastmod is { } lastmod ? DateOnly.FromDateTime(lastmod.UtcDateTime) : (DateOnly?)null)
            ?? input.DocumentDate
            ?? (input.HttpLastModified is { } modified ? DateOnly.FromDateTime(modified.UtcDateTime) : (DateOnly?)null);
        return new DocumentMetadata(tags, documentType ?? DefaultDocumentType(input.MediaType), when);
    }

    /// <summary>The default <c>DocumentType</c> from the media type (processing design §6.5).</summary>
    public static string DefaultDocumentType(string mediaType) => mediaType switch
    {
        ProcessingMediaTypes.Html or ProcessingMediaTypes.Xhtml => "page",
        ProcessingMediaTypes.Pdf => "pdf",
        ProcessingMediaTypes.Docx or ProcessingMediaTypes.Odt or ProcessingMediaTypes.Doc or ProcessingMediaTypes.Rtf => "word",
        ProcessingMediaTypes.Pptx or ProcessingMediaTypes.Odp or ProcessingMediaTypes.Ppt => "presentation",
        ProcessingMediaTypes.Xlsx or ProcessingMediaTypes.Ods or ProcessingMediaTypes.Xls or ProcessingMediaTypes.Csv => "spreadsheet",
        ProcessingMediaTypes.Png or ProcessingMediaTypes.Jpeg or ProcessingMediaTypes.Tiff => "image",
        _ => "text",
    };

    private static Dictionary<string, string>? Match(PathRule rule, MetadataInput input)
    {
        var path = input.Url is { } url ? Uri.UnescapeDataString(url.AbsolutePath) : input.DisplayName;
        try
        {
            switch (rule.On)
            {
                case "path":
                    return path is not null && PathMatches(rule.Match, input.Url, path) ? [] : null;
                case "path-regex":
                    return path is null ? null : Groups(new Regex(rule.Match, RegexOptions.CultureInvariant, RegexTimeout).Match(path));
                case "title-regex":
                    return input.Title is null ? null : Groups(new Regex(rule.Match, RegexOptions.CultureInvariant, RegexTimeout).Match(input.Title));
                case "host":
                    return input.Url is { } u && string.Equals(u.IdnHost, rule.Match, StringComparison.OrdinalIgnoreCase) ? [] : null;
                case "mediaType":
                    return string.Equals(input.MediaType, rule.Match, StringComparison.OrdinalIgnoreCase) ? [] : null;
                default:
                    return null;
            }
        }
        catch (Exception e) when (e is ArgumentException or RegexMatchTimeoutException)
        {
            return null; // validated at save (format: regex); a timeout is no match
        }
    }

    private static Dictionary<string, string>? Groups(Match match)
    {
        if (!match.Success)
        {
            return null;
        }

        var groups = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Group group in match.Groups)
        {
            if (group.Success && !int.TryParse(group.Name, CultureInfo.InvariantCulture, out _))
            {
                groups[group.Name] = group.Value;
            }
        }

        return groups;
    }

    /// <summary>
    /// A <c>path</c> rule (#929). On a website document the glob may be written as a URL pasted from the browser
    /// (<c>https://www.umea.se/bygga/</c>) or without its scheme (<c>www.umea.se/bygga/**</c>): the host then has to be the
    /// page's (case-insensitive, IDN as its ASCII form) and the rest is the path glob, with the URL's query and fragment
    /// dropped. A glob that ends in <c>/</c> also matches everything under it, on websites and uploads alike.
    /// </summary>
    private static bool PathMatches(string match, Uri? url, string path)
    {
        var (host, glob) = PathGlob(match, website: url is not null);
        return (host is null || (url is not null && string.Equals(url.IdnHost, host, StringComparison.OrdinalIgnoreCase)))
            && GlobMatches(glob, path, upload: url is null);
    }

    /// <summary>The host (when <paramref name="match"/> names one) and the path glob of a <c>path</c> rule (see <see cref="PathMatches"/>).</summary>
    internal static (string? Host, string Glob) PathGlob(string match, bool website)
    {
        var glob = match.Trim();
        string? host = null;
        if (website)
        {
            var pasted = false;
            foreach (var scheme in (ReadOnlySpan<string>)["https://", "http://"])
            {
                if (glob.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
                {
                    glob = glob[scheme.Length..];
                    pasted = true;
                    break;
                }
            }

            var slash = glob.IndexOf('/', StringComparison.Ordinal);
            var first = slash < 0 ? glob : glob[..slash];
            if (pasted || (slash != 0 && HostName().IsMatch(first)))
            {
                host = HostOf(first);
                glob = slash < 0 ? "/" : glob[slash..];
                if (pasted)
                {
                    // A pasted address: its query and fragment are not part of the path the rule is matched against.
                    var end = glob.IndexOfAny(['?', '#']);
                    glob = end < 0 ? glob : glob[..end];
                }
            }
        }

        return (host, glob.EndsWith('/') ? glob + "**" : glob);
    }

    /// <summary>A host as <see cref="Uri.IdnHost"/> gives it: an IDN in its ASCII form, without a port or a trailing dot.</summary>
    private static string HostOf(string authority)
    {
        var name = authority.Split(':')[0].TrimEnd('.');
        try
        {
            return new IdnMapping().GetAscii(name);
        }
        catch (ArgumentException)
        {
            return name;
        }
    }

    /// <summary>
    /// An upload's path is relative (its display name, <c>HR/Policys/semester.pdf</c>), so a rule written like a website path
    /// (<c>/HR/**</c>) matches it as well as <c>HR/**</c>; a URL path is matched as it is.
    /// </summary>
    private static bool GlobMatches(string glob, string path, bool upload)
    {
        var regex = Glob(glob);
        return regex.IsMatch(path) || (upload && regex.IsMatch("/" + path));
    }

    /// <summary>A glob over the path: <c>**</c> any characters, <c>*</c> within one segment, <c>?</c> one character.</summary>
    internal static Regex Glob(string glob)
    {
        var pattern = new StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                pattern.Append(".*");
                i++;
            }
            else if (c == '*')
            {
                pattern.Append("[^/]*");
            }
            else if (c == '?')
            {
                pattern.Append("[^/]");
            }
            else
            {
                pattern.Append(Regex.Escape(c.ToString()));
            }
        }

        return new Regex(pattern.Append('$').ToString(), RegexOptions.CultureInvariant, RegexTimeout);
    }

    private static string? NormaliseTag(string tag)
    {
        var normal = tag.Trim().ToLower(CultureInfo.InvariantCulture).Normalize(NormalizationForm.FormC);
        return TagPattern().IsMatch(normal) ? normal : null;
    }

    [GeneratedRegex(@"\$\{(\w+)\}")]
    private static partial Regex Group();

    [GeneratedRegex(@"^[\p{L}\p{N}-]{1,50}$")]
    private static partial Regex TagPattern();

    /// <summary>A host name with at least one dot and a top-level part of letters (<c>www.umea.se</c>, <c>umea.se:443</c>), not a folder such as <c>HR</c>.</summary>
    [GeneratedRegex(@"^(?:[\p{L}\p{N}-]+\.)+\p{L}{2,24}\.?(?::\d{1,5})?$", RegexOptions.IgnoreCase)]
    private static partial Regex HostName();
}
