using Microsoft.Extensions.Options;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Worker.Processing.Chunking;
using Ume.Rag.Worker.Processing.Clean;
using Ume.Rag.Worker.Processing.Detect;
using Ume.Rag.Worker.Processing.Html;
using Ume.Rag.Worker.Processing.Parse;
using Ume.Rag.Worker.Processing.Pii;
using Ume.Rag.Worker.Processing.Text;

namespace Ume.Rag.Worker.Processing;

/// <summary>One document's inputs to the processing stages: the raw content and the settings each stage reads.</summary>
public sealed record ProcessingInput(RawContent Raw, PersonalDataContext Pii)
{
    /// <summary>The site profile of the document's host (HTML only); null applies a preset.</summary>
    public HtmlProfileVersion? SiteProfile { get; init; }

    public ParseParams Parse { get; init; } = new();

    public ChunkOptions Chunk { get; init; } = new();

    public IReadOnlyList<PathRule> PathRules { get; init; } = [];

    /// <summary>The parser's <c>request_id</c> prefix (the execution id; ranges add <c>-r{n}</c>).</summary>
    public string RequestId { get; init; } = Guid.CreateVersion7().ToString("N");
}

/// <summary>Every stage value of one document, from <c>detect</c> to <c>personal-data</c>.</summary>
public sealed record ProcessedDocument(DetectResult Detect, ParsedDocument Parsed, ParsedDocument Cleaned, DocumentMetadata Metadata, ChunkSetDraft Chunks, PiiAssessment Pii);

/// <summary>
/// The processing stages of one document in order (engine design §2.1): <c>detect</c>, <c>parse</c> (HTML by site
/// profile or preset, text, CSV in the worker; parser-bound types through <see cref="ParserRoute"/>), <c>clean</c>,
/// <c>chunk</c> (split, metadata, chunks) and <c>personal-data</c>. <c>embed</c> runs in the <c>embed</c> job
/// (<see cref="Embedding.EmbedStage"/>). The engine (item 9) wraps each step as a stage node with its key, cache and
/// segment; this class runs them inline for tests, goldens and previews of the stage values. Empty after extraction ends
/// the document as <c>skipped</c>.
/// </summary>
public sealed class DocumentProcessor(DetectStage detect, HtmlExtractor html, Chunker chunker, PersonalDataStage personalData, IParsePlanner planner,
    IOptions<ParseOptions> parseOptions, IParserConverter? converter = null)
{
    public async Task<ProcessedDocument> RunAsync(ProcessingInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var detected = await detect.RunAsync(input.Raw, cancellationToken);
        var parsed = await ParseAsync(input, detected, cancellationToken);
        var cleaned = Cleaner.Clean(parsed);
        if (!cleaned.Blocks.Any(b => b.Kind != BlockKinds.Heading))
        {
            throw new StageFatalException(RunItemOutcome.Skipped, "parse.empty");
        }

        var metadata = Metadata.Apply(input.PathRules, new MetadataInput(detected.MediaType)
        {
            Url = input.Raw.Url,
            DisplayName = input.Raw.DisplayName,
            Title = cleaned.Title,
            VisibleDate = cleaned.VisibleDate,
            DocumentDate = cleaned.DocumentDate,
            ReliableLastmod = input.Raw.ReliableLastmod,
            HttpLastModified = input.Raw.HttpLastModified,
        });
        var chunks = chunker.Chunk(cleaned, input.Chunk, metadata);
        var (withPii, assessment) = await personalData.RunAsync(chunks, input.Pii, cancellationToken);
        return new ProcessedDocument(detected, parsed, cleaned, metadata, withPii, assessment);
    }

    private async Task<ParsedDocument> ParseAsync(ProcessingInput input, DetectResult detected, CancellationToken cancellationToken)
    {
        var bytes = input.Raw.Bytes;
        return detected.Route switch
        {
            ParseRoute.Html => html.Extract(bytes, input.SiteProfile).Document,
            ParseRoute.Text => PlainTextParser.Parse(bytes.Span, markdown: detected.MediaType == ProcessingMediaTypes.Markdown),
            ParseRoute.Csv => CsvTableParser.Parse(bytes.Span),
            ParseRoute.Parser when converter is not null =>
                await new ParserRoute(converter, planner, parseOptions).ParseAsync(bytes, detected, input.Parse, input.RequestId, cancellationToken),
            ParseRoute.Parser => throw new StageTransientException("parser.not_configured", TimeSpan.FromMinutes(1), countsAttempt: false),
            _ => throw new StageFatalException(RunItemOutcome.Unsupported, "parser.unsupported_format"),
        };
    }
}
