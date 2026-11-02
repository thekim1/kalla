using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Options;
using Ume.Rag.Retrieval.Text;
using Ume.Rag.Worker.Processing;
using Ume.Rag.Worker.Processing.Chunking;
using Ume.Rag.Worker.Processing.Detect;
using Ume.Rag.Worker.Processing.Html;
using Ume.Rag.Worker.Processing.Parse;
using Ume.Rag.Worker.Processing.Pii;

namespace Ume.Rag.Benchmarks;

/// <summary>
/// The worker's CPU work per crawled HTML page (processing design §4–§12), from bytes to chunk drafts: detect, extract
/// (with the umea.se Sitevision preset), clean, metadata, chunk (BGE-M3 token counts, the decompounder's
/// <c>SearchText</c>), personal data. Embedding and storing are out (gateway and database). Pages from
/// <c>tests/fixtures/umea/pages</c>: a short service page, a long regulation page and a page with many PDF links.
/// The stage benchmarks run on the long page's cleaned text.
/// </summary>
[MemoryDiagnoser]
public class IngestionBenchmarks
{
    private DocumentProcessor _processor = null!;
    private ProcessingInput _input = null!;
    private Chunker _chunker = null!;
    private PiiPatterns _pii = null!;
    private ParsedDocument _cleaned = null!;
    private DocumentMetadata _metadata = null!;
    private string _text = null!;
    private string _chunkText = null!;

    [Params("hours-savar-simhall.html", "regulation-avfall.html", "pdf-links-kommunstyrelsen.html")]
    public string Page { get; set; } = "";

    [GlobalSetup]
    public void Setup()
    {
        var clock = TimeProvider.System;
        _chunker = new Chunker(Bgem3TokenCounter.Shared, SwedishDecompounder.Default);
        _pii = new PiiPatterns(clock);
        _processor = new DocumentProcessor(new DetectStage(null), new HtmlExtractor(new HtmlPresets()), _chunker,
            new PersonalDataStage(_pii, null, new EmptyGazetteer(), clock), new ParsePlanner(), Options.Create(new ParseOptions()), null);
        var bytes = File.ReadAllBytes(BenchData.Fixture("umea", "pages", Page));
        _input = new ProcessingInput(new RawContent(bytes, "text/html;charset=UTF-8") { Url = new Uri("https://www.umea.se/" + Page) },
            new PersonalDataContext("public") { OwnDomains = ["umea.se"] });
        var result = _processor.RunAsync(_input, CancellationToken.None).GetAwaiter().GetResult();
        _cleaned = result.Cleaned;
        _metadata = result.Metadata;
        _text = string.Join("\n", result.Cleaned.Blocks.Select(b => b.Text));
        _chunkText = result.Chunks.Chunks.Count > 0 ? result.Chunks.Chunks[0].Text : _text;
    }

    [Benchmark(Description = "Whole page: detect → … → personal data")]
    public Task<ProcessedDocument> ProcessPage() => _processor.RunAsync(_input, CancellationToken.None);

    [Benchmark(Description = "Chunk the cleaned page")]
    public ChunkSetDraft Chunk() => _chunker.Chunk(_cleaned, new ChunkOptions(), _metadata);

    [Benchmark(Description = "Personal-data patterns over the page text")]
    public IReadOnlyList<PiiMatch> PersonalDataPatterns() => _pii.Scan(_text);

    [Benchmark(Description = "BGE-M3 token count of the page text")]
    public int TokenCount() => Bgem3TokenCounter.Shared.Count(_text);

    [Benchmark(Description = "Decompounder parts of one chunk (SearchText)")]
    public string ChunkSearchText() => SwedishDecompounder.Default.WithParts(_chunkText);

    [Benchmark(Description = "Language detection")]
    public string Language() => LanguageDetector.Detect(_text);
}
