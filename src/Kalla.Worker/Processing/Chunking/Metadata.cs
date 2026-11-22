using Kalla.Pipelines.Processing;
using Kalla.Worker.Processing.Detect;

namespace Kalla.Worker.Processing.Chunking;

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
/// <c>Metadata</c> (processing design §7, part of <c>chunk</c>): every path rule that matches (<see cref="PathRules"/>,
/// shared with the admin's Topics tab) adds its tags, the first match sets <c>documentType</c> and <c>date</c>; tags are
/// lower case, NFC, <c>[\p{L}\p{N}-]{1,50}</c>, at most 20. The date is the first of: the visible date, a path-rule date,
/// a reliable sitemap <c>lastmod</c>, the document's own date, HTTP <c>Last-Modified</c>.
/// </summary>
public static class Metadata
{
    public static DocumentMetadata Apply(IReadOnlyList<PathRule> rules, MetadataInput input)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(input);
        var subject = new PathRuleSubject(input.MediaType) { Url = input.Url, DisplayName = input.DisplayName, Title = input.Title };
        var tags = new List<string>();
        PathRuleMatch? first = null;
        foreach (var rule in rules)
        {
            if (PathRules.Match(rule, subject) is not { } match)
            {
                continue;
            }

            first ??= match;
            foreach (var tag in match.Tags)
            {
                if (!tags.Contains(tag, StringComparer.Ordinal) && tags.Count < PathRules.MaxTags)
                {
                    tags.Add(tag);
                }
            }
        }

        var when = input.VisibleDate ?? first?.Date
            ?? (input.ReliableLastmod is { } lastmod ? DateOnly.FromDateTime(lastmod.UtcDateTime) : (DateOnly?)null)
            ?? input.DocumentDate
            ?? (input.HttpLastModified is { } modified ? DateOnly.FromDateTime(modified.UtcDateTime) : (DateOnly?)null);
        return new DocumentMetadata(tags, first?.DocumentType ?? DefaultDocumentType(input.MediaType), when);
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
}
