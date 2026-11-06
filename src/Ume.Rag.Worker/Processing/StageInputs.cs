using System.Text.Json;
using Microsoft.Extensions.Options;
using Ume.Rag.Pipelines.Definitions;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Pipelines.Json;
using Ume.Rag.Pipelines.SiteProfiles;
using Ume.Rag.Worker.Processing.Chunking;
using Ume.Rag.Worker.Processing.Detect;
using Ume.Rag.Worker.Processing.Html;
using Ume.Rag.Worker.Processing.Parse;
using Ume.Rag.Worker.Processing.Text;

namespace Ume.Rag.Worker.Processing;

/// <summary>
/// The stage inputs every runner of the processing stages builds alike: the engine's segment, the preview (keyed as
/// the engine keys them, engine design §11) and <see cref="DocumentProcessor"/>. One builder keeps their chunk dates,
/// options and cache keys equal.
/// </summary>
public static class StageInputs
{
    /// <summary>
    /// The sitemap <c>lastmod</c> as a date candidate, only when the host's applied site profile marks it reliable
    /// (processing design §7; <c>lastmodReliable</c>, set from the <c>lastmod</c> sample, connectors design §5.4).
    /// </summary>
    public static DateTimeOffset? ReliableLastmod(DateTimeOffset? sitemapLastMod, SiteProfile? hostProfile) =>
        hostProfile is { LastmodReliable: true } ? sitemapLastMod : null;

    /// <summary>
    /// What <see cref="Chunking.Metadata"/> reads (processing design §7): the URL (or the upload's name), the cleaned
    /// document's title and dates, and the dates from the fetch, a reliable sitemap <c>lastmod</c> and HTTP
    /// <c>Last-Modified</c>. Its hash is the <c>chunk</c> stage's <c>metadata</c> input.
    /// </summary>
    public static MetadataInput Metadata(string mediaType, ParsedDocument cleaned, Uri? url, string? displayName,
        DateTimeOffset? reliableLastmod, DateTimeOffset? httpLastModified) =>
        new(mediaType)
        {
            Url = url,
            DisplayName = displayName,
            Title = cleaned.Title,
            VisibleDate = cleaned.VisibleDate,
            DocumentDate = cleaned.DocumentDate,
            ReliableLastmod = reliableLastmod,
            HttpLastModified = httpLastModified,
        };

    /// <summary>The template parameter <c>pathRules</c> (processing design §7); none when unset.</summary>
    public static IReadOnlyList<PathRule> PathRules(ResolvedPipeline pipeline) =>
        Param<IReadOnlyList<PathRule>>(pipeline, "pathRules") ?? [];

    /// <summary>The template parameters <c>chunkTokens</c> and <c>overlapPercent</c> (processing design §6).</summary>
    public static ChunkOptions Chunk(ResolvedPipeline pipeline) =>
        new(Param<int?>(pipeline, "chunkTokens") ?? 400, Param<int?>(pipeline, "overlapPercent") ?? 15);

    /// <summary>The parser route's template parameters <c>ocr</c> and <c>tables</c> (processing design §3).</summary>
    public static ParseParams Parse(ResolvedPipeline pipeline) =>
        new(Param<string>(pipeline, "ocr") ?? "auto", Param<string>(pipeline, "tables") ?? "accurate");

    private static T? Param<T>(ResolvedPipeline pipeline, string name) =>
        pipeline.Params[name] is { } node ? node.Deserialize<T>(ContractJson.Options) : default;
}

/// <summary>
/// The <c>parse</c> stage's dispatch by route (processing design §3.2): HTML by the host's site profile or a preset,
/// text and CSV in the worker, parser-bound types through <see cref="ParserRoute"/>. A parser-bound document without a
/// parser is transient (<c>parser.not_configured</c>), any other route <c>unsupported</c>.
/// </summary>
public sealed class StageParser(HtmlExtractor html, IParsePlanner planner, IOptions<ParseOptions> parseOptions,
    IParserConverter? converter)
{
    /// <param name="bytes">The raw content.</param>
    /// <param name="detected">The <c>detect</c> output; its route chooses the parser.</param>
    /// <param name="profile">The host's site profile (HTML); null applies a preset.</param>
    /// <param name="parameters">The parser route's parameters (<see cref="StageInputs.Parse"/>).</param>
    /// <param name="requestId">The parser's <c>request_id</c> prefix.</param>
    /// <param name="cancellationToken">Cancels the parse.</param>
    /// <param name="progress">A <c>parse</c> job's ranges and slice (engine design §9.5); null outside one.</param>
    /// <param name="rules">The document's admin-made parse-rule candidates; null: the built-in rules only.</param>
    public async Task<ParsedDocument> ParseAsync(ReadOnlyMemory<byte> bytes, DetectResult detected,
        HtmlProfileVersion? profile, ParseParams parameters, string requestId, CancellationToken cancellationToken,
        ParseProgress? progress = null, IReadOnlyList<ParseRule>? rules = null) =>
        detected.Route switch
        {
            ParseRoute.Html => html.Extract(bytes, profile).Document,
            ParseRoute.Text => PlainTextParser.Parse(bytes.Span,
                markdown: detected.MediaType == ProcessingMediaTypes.Markdown),
            ParseRoute.Csv => CsvTableParser.Parse(bytes.Span),
            ParseRoute.Parser when converter is not null => await new ParserRoute(converter, planner, parseOptions)
                .ParseAsync(bytes, detected, parameters, requestId, cancellationToken, progress, rules),
            ParseRoute.Parser => throw new StageTransientException("parser.not_configured", TimeSpan.FromMinutes(1),
                countsAttempt: false),
            _ => throw new StageFatalException(RunItemOutcome.Unsupported, "parser.unsupported_format"),
        };
}
