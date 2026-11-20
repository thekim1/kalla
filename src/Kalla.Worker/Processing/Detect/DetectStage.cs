using System.Text;
using System.Text.RegularExpressions;
using Kalla.Pipelines.Jobs;
using Kalla.Worker.Parsing;
using Kalla.Worker.Processing.Parse;

namespace Kalla.Worker.Processing.Detect;

/// <summary>
/// <c>detect</c> (processing design §2): media type by magic bytes then the declared type, the mismatch fact, language
/// (HTML <c>lang</c>; text by the stop-word detector; parser-bound types later by <c>clean</c>), and for PDF, TIFF,
/// Office and ODF the parser's <c>/v1/inspect</c>. A refusal (inspect's, or a deferred or unsupported type) ends the
/// document at once without a parse slot. Without an inspector (until item 5's client call is merged) parser-bound
/// documents are sent whole.
/// </summary>
public sealed partial class DetectStage(IDocumentInspector? inspector = null)
{
    public const int Version = 1;

    public async Task<DetectResult> RunAsync(RawContent raw, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var declared = ProcessingMediaTypes.Essence(raw.DeclaredMediaType);
        var media = MediaTypeSniffer.Sniff(raw.Bytes.Span, raw.DeclaredMediaType);
        var route = ProcessingMediaTypes.Route(media);
        switch (route)
        {
            case ParseRoute.Deferred:
                throw new StageFatalException(RunItemOutcome.Unsupported, "parser.format_deferred");
            case ParseRoute.Unsupported:
                throw new StageFatalException(RunItemOutcome.Unsupported, "parser.unsupported_format");
        }

        var result = new DetectResult(media, route)
        {
            MediaTypeDeclared = declared,
            MediaTypeMismatch = declared is not null and not ProcessingMediaTypes.OctetStream && !string.Equals(declared, media, StringComparison.Ordinal),
            Bytes = raw.Bytes.Length,
            FileExtension = ProcessingMediaTypes.FileExtension(media),
        };
        if (route == ParseRoute.Html)
        {
            var head = Encoding.UTF8.GetString(raw.Bytes.Span[..Math.Min(raw.Bytes.Length, 5_000)]);
            return result with { Language = LanguageDetector.FromLangAttribute(LangAttribute().Match(head) is { Success: true } m ? m.Groups[1].Value : null) };
        }

        if (route is ParseRoute.Text or ParseRoute.Csv)
        {
            return result with { Language = route == ParseRoute.Text ? LanguageDetector.Detect(TextDecoding.Decode(raw.Bytes.Span)) : null };
        }

        if (inspector is null || !ProcessingMediaTypes.IsInspected(media))
        {
            return result;
        }

        InspectFacts inspect;
        try
        {
            inspect = await inspector.InspectAsync(raw.Bytes, media, result.FileExtension!, cancellationToken);
        }
        catch (ParserException e)
        {
            throw ParserRoute.Map(e, splittable: false);
        }

        if (inspect.Refusal is { } refusal || inspect.Encrypted == "user_password")
        {
            throw (inspect.Refusal ?? "encrypted") switch
            {
                "encrypted" => new StageFatalException(RunItemOutcome.Encrypted, "parser.encrypted"),
                "too_many_pages" => new StageFatalException(RunItemOutcome.TooLarge, "parser.too_many_pages"),
                "too_large" => new StageFatalException(RunItemOutcome.TooLarge, "parser.too_large"),
                "format_deferred" => new StageFatalException(RunItemOutcome.Unsupported, "parser.format_deferred"),
                "unsupported_format" => new StageFatalException(RunItemOutcome.Unsupported, "parser.unsupported_format"),
                "busy" or "not_ready" => new StageTransientException("parser." + inspect.Refusal, TimeSpan.FromSeconds(5), countsAttempt: false),
                { } other => new StageFatalException(RunItemOutcome.Failed, "parser." + other),
            };
        }

        var notClean = inspect.Pages.Count == 0 ? 0 : inspect.Pages.Count(p => !ParsePlanner.IsClean(p)) / (double)inspect.Pages.Count;
        return result with { Inspect = inspect, PageCount = inspect.PageCount, Scanned = notClean >= 0.5 };
    }

    [GeneratedRegex(@"<html[^>]*\blang\s*=\s*[""']?([A-Za-z]{2,3}(?:[-_][A-Za-z0-9]{1,8})?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LangAttribute();
}
