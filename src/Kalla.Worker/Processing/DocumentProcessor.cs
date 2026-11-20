using Microsoft.Extensions.Options;
using Kalla.Pipelines.Jobs;
using Kalla.Worker.Processing.Chunking;
using Kalla.Worker.Processing.Clean;
using Kalla.Worker.Processing.Detect;
using Kalla.Worker.Processing.Html;
using Kalla.Worker.Processing.Parse;
using Kalla.Worker.Processing.Pii;

namespace Kalla.Worker.Processing;

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
/// (<see cref="Embedding.EmbedStage"/>). This class runs them inline, without stage keys or the output cache, for the
/// tests, goldens and benchmarks only; the engine runs them as <c>Engine.ProcessingSegment</c> and a preview as
/// <c>Preview.PreviewSegment</c>, all three with the same inputs (<see cref="StageInputs"/>) and parse dispatch
/// (<see cref="StageParser"/>). Empty after extraction ends the document as <c>skipped</c>.
/// </summary>
public sealed class DocumentProcessor(DetectStage detect, HtmlExtractor html, Chunker chunker, PersonalDataStage personalData, IParsePlanner planner,
    IOptions<ParseOptions> parseOptions, IParserConverter? converter = null)
{
    private readonly StageParser _parser = new(html, planner, parseOptions, converter);

    public async Task<ProcessedDocument> RunAsync(ProcessingInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var detected = await detect.RunAsync(input.Raw, cancellationToken);
        var parsed = await _parser.ParseAsync(input.Raw.Bytes, detected, input.SiteProfile, input.Parse,
            input.RequestId, cancellationToken);
        var cleaned = Cleaner.Clean(parsed);
        if (!cleaned.Blocks.Any(b => b.Kind != BlockKinds.Heading))
        {
            throw new StageFatalException(RunItemOutcome.Skipped, "parse.empty");
        }

        var metadataInput = StageInputs.Metadata(detected.MediaType, cleaned, input.Raw.Url, input.Raw.DisplayName,
            input.Raw.ReliableLastmod, input.Raw.HttpLastModified);
        var metadata = Metadata.Apply(input.PathRules, metadataInput);
        var chunks = chunker.Chunk(cleaned, input.Chunk, metadata);
        var (withPii, assessment) = await personalData.RunAsync(chunks, input.Pii, cancellationToken);
        return new ProcessedDocument(detected, parsed, cleaned, metadata, withPii, assessment);
    }
}
