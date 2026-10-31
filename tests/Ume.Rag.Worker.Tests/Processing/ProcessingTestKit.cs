extern alias fakegateway;

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Ume.Rag.Pipelines.Processing;
using Ume.Rag.Retrieval.Text;
using Ume.Rag.Worker.Parsing;
using Ume.Rag.Worker.Processing;
using Ume.Rag.Worker.Processing.Chunking;
using Ume.Rag.Worker.Processing.Detect;
using Ume.Rag.Worker.Processing.Html;
using Ume.Rag.Worker.Processing.Parse;
using Ume.Rag.Worker.Processing.Persistence;
using Ume.Rag.Worker.Processing.Pii;

namespace Ume.Rag.Worker.Tests.Processing;

/// <summary>Paths of the repository's fixtures.</summary>
public static class Fixtures
{
    public static readonly string Root = FindRoot();

    public static string Processing => Path.Combine(Root, "tests", "fixtures", "processing");

    public static string Pii => Path.Combine(Root, "tests", "fixtures", "pii");

    public static string Umea => Path.Combine(Root, "tests", "fixtures", "umea");

    /// <summary>The goldens are rewritten instead of compared when this variable is 1 (then review the diff).</summary>
    public static bool Update => Environment.GetEnvironmentVariable("UME_RAG_UPDATE_GOLDENS") == "1";

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "UmeRag.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("UmeRag.slnx not found above the test output directory.");
    }
}

/// <summary>The processing stages wired as the worker wires them, with fakes for the parser and a fixed clock.</summary>
public sealed class ProcessingHarness
{
    public static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    public ProcessingHarness(IParserConverter? converter = null, IDocumentInspector? inspector = null, INerClient? ner = null, IParsePlanner? planner = null)
    {
        Clock = new FakeTimeProvider(Now);
        Planner = planner ?? new ParsePlanner();
        Chunker = new Chunker(Bgem3TokenCounter.Shared, SwedishDecompounder.Default);
        PersonalData = new PersonalDataStage(new PiiPatterns(Clock), ner, new EmptyGazetteer(), Clock);
        Processor = new DocumentProcessor(new DetectStage(inspector), new HtmlExtractor(new HtmlPresets()), Chunker, PersonalData, Planner,
            Options.Create(new ParseOptions()), converter);
    }

    public FakeTimeProvider Clock { get; }

    public IParsePlanner Planner { get; }

    public Chunker Chunker { get; }

    public PersonalDataStage PersonalData { get; }

    public DocumentProcessor Processor { get; }
}

/// <summary>A parser that answers from per-range result files or a function, and records every request.</summary>
public sealed class FakeConverter(Func<ParserConvertOptions, ParserConvertResult> answer) : IParserConverter
{
    public ConcurrentQueue<ParserConvertOptions> Requests { get; } = new();

    public Task<ParserConvertResult> ConvertAsync(ReadOnlyMemory<byte> file, ParserConvertOptions options, CancellationToken cancellationToken)
    {
        Requests.Enqueue(options);
        return Task.FromResult(answer(options));
    }

    public static ParserException Error(string code, System.Net.HttpStatusCode status = System.Net.HttpStatusCode.UnprocessableEntity) =>
        new(status, new ParserErrorResponse(new ParserErrorBody(code, "Fixed sentence.", "none", null, null, null), "r", new ParserBuild("1", "2", "h")), null);

    /// <summary>A result for the pages <paramref name="from"/>–<paramref name="to"/> with one paragraph per page (text "Sida N innehåll …").</summary>
    public static ParserConvertResult Pages(int from, int to, string status = "success", int? completedTo = null, Func<int, ParserPageSignals>? signals = null)
    {
        var last = completedTo ?? to;
        var pages = Enumerable.Range(from, last - from + 1)
            .Select(p => new ParserPage(p, null, 595, 842, signals?.Invoke(p) ?? Signals(300)))
            .ToList();
        var blocks = pages.Select(p => new ParserBlock($"b{p.Page}", BlockKinds.Paragraph, null, null, p.Page, p.Page, [50, 300, 500, 400], Layers.Body,
            $"Sida {p.Page} innehåll om avgifter och handläggning.", null, null)).ToList();
        return new ParserConvertResult("parser/v1", "r", status,
            new ParserInput("pdf", "application/pdf", "x", 1, 100, new ParserPageSpan(from, to), new ParserPageSpan(from, last), "docling"),
            pages, blocks, [], [], [], new ParserTimings(1, 0, 1, 0, 0), new ParserBuild("1", "2", "h"), null);
    }

    public static ParserPageSignals Signals(int chars, double garbled = 0, double? image = 0, bool empty = false) =>
        new(chars, 0, garbled, chars, image, "none", null, 0, 0, 0, empty, 1);
}

/// <summary>An inspector that answers fixed facts.</summary>
public sealed class FakeInspector(InspectFacts facts) : IDocumentInspector
{
    public Task<InspectFacts> InspectAsync(ReadOnlyMemory<byte> file, string mediaType, string fileExtension, CancellationToken cancellationToken) =>
        Task.FromResult(facts);
}

/// <summary>A NER that marks every capitalised word pair found in <see cref="Names"/> as a person, and records calls.</summary>
public sealed class FakeNer : INerClient
{
    public bool Ready { get; set; } = true;

    public string? Fail { get; set; }

    public List<IReadOnlyList<NerText>> Calls { get; } = [];

    /// <summary>Names to find (exact substrings), with the label and score answered.</summary>
    public List<(string Name, string Label, double Score)> Names { get; } = [];

    public Task<bool> IsReadyAsync(CancellationToken cancellationToken) => Task.FromResult(Ready);

    public Task<NerAnswer> RecognizeAsync(IReadOnlyList<NerText> texts, double minScore, CancellationToken cancellationToken)
    {
        if (Fail is { } code)
        {
            throw FakeConverter.Error(code, System.Net.HttpStatusCode.ServiceUnavailable);
        }

        Calls.Add(texts);
        var entities = new List<NerEntity>();
        foreach (var text in texts)
        {
            foreach (var (name, label, score) in Names)
            {
                var at = text.Text.IndexOf(name, StringComparison.Ordinal);
                while (at >= 0)
                {
                    // Code-point offsets, as the parser sends them.
                    var start = text.Text[..at].EnumerateRunes().Count();
                    entities.Add(new NerEntity(text.Id, start, start + name.EnumerateRunes().Count(), label, score));
                    at = text.Text.IndexOf(name, at + name.Length, StringComparison.Ordinal);
                }
            }
        }

        return Task.FromResult(new NerAnswer(entities, "kb-bert-ner@f3c83af"));
    }
}

/// <summary>In-memory <see cref="IChunkSetStore"/> with the swap semantics of engine design §14 rule 3.</summary>
public sealed class InMemoryChunkSetStore : IChunkSetStore
{
    private readonly Dictionary<Guid, ChunkSetRecord> _sets = [];
    private readonly Dictionary<Guid, List<ChunkRecord>> _chunks = [];
    private readonly Dictionary<Guid, Guid> _live = [];
    private readonly Dictionary<Guid, PiiClearance> _clearances = [];
    private readonly Lock _lock = new();

    public int ChunkInserts { get; private set; }

    /// <summary>The chunks a search would see: those with <c>DeletedAt</c> null.</summary>
    public IReadOnlyList<ChunkRecord> Searchable()
    {
        lock (_lock)
        {
            return [.. _chunks.Values.SelectMany(c => c).Where(c => c.DeletedAt is null)];
        }
    }

    public Task InsertWritingAsync(ChunkSetRecord set, IReadOnlyList<ChunkRecord> chunks, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _sets[set.Id] = set with { State = ChunkSetState.Writing, ChunkCount = chunks.Count };
            _chunks[set.Id] = [.. chunks.Select(c => c with { DeletedAt = set.CreatedAt })];
            ChunkInserts += chunks.Count;
        }

        return Task.CompletedTask;
    }

    public Task<ChunkSetRecord?> GetAsync(Guid chunkSetId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_sets.GetValueOrDefault(chunkSetId));
        }
    }

    public Task<ChunkSetRecord?> GetCurrentAsync(Guid documentId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_live.TryGetValue(documentId, out var id) ? _sets[id] : null);
        }
    }

    public Task SwapAsync(Guid chunkSetId, string state, DateTimeOffset now, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var set = _sets[chunkSetId];
            if (_live.TryGetValue(set.Key.DocumentId, out var previous) && previous != chunkSetId)
            {
                _sets[previous] = _sets[previous] with { State = ChunkSetState.Superseded, SupersededAt = now };
                Visible(previous, false, now);
            }

            _live[set.Key.DocumentId] = chunkSetId;
            Apply(chunkSetId, state, now);
        }

        return Task.CompletedTask;
    }

    public Task SetStateAsync(Guid chunkSetId, string state, DateTimeOffset now, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            Apply(chunkSetId, state, now);
        }

        return Task.CompletedTask;
    }

    public Task UpdatePiiAsync(Guid chunkSetId, PiiCounts counts, bool flagged, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _sets[chunkSetId] = _sets[chunkSetId] with { PiiCounts = counts, PiiFlagged = flagged };
        }

        return Task.CompletedTask;
    }

    public Task<PiiClearance?> GetClearanceAsync(Guid documentId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_clearances.GetValueOrDefault(documentId));
        }
    }

    public Task SetClearanceAsync(Guid documentId, PiiClearance clearance, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _clearances[documentId] = clearance;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ChunkSetRecord>> PendingNerAsync(int limit, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            IReadOnlyList<ChunkSetRecord> pending = [.. _live.Values.Select(id => _sets[id]).Where(s => s.PiiCounts?.Ner == NerStatus.Pending).Take(limit)];
            return Task.FromResult(pending);
        }
    }

    public Task<IReadOnlyList<ChunkRecord>> ChunksAsync(Guid chunkSetId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            IReadOnlyList<ChunkRecord> chunks = [.. _chunks[chunkSetId].OrderBy(c => c.Ordinal)];
            return Task.FromResult(chunks);
        }
    }

    private void Apply(Guid chunkSetId, string state, DateTimeOffset now)
    {
        _sets[chunkSetId] = _sets[chunkSetId] with { State = state, LiveAt = state == ChunkSetState.Live ? now : _sets[chunkSetId].LiveAt };
        Visible(chunkSetId, state == ChunkSetState.Live, now);
    }

    private void Visible(Guid chunkSetId, bool visible, DateTimeOffset now) =>
        _chunks[chunkSetId] = [.. _chunks[chunkSetId].Select(c => c with { DeletedAt = visible ? null : now })];
}

/// <summary>In-memory <see cref="IEmbeddingCache"/>, keyed as the table's primary key.</summary>
public sealed class InMemoryEmbeddingCache : IEmbeddingCache
{
    public ConcurrentDictionary<(string, Guid, string), float[]> Rows { get; } = new();

    public Task<IReadOnlyDictionary<string, float[]>> GetAsync(string classification, Guid profileId, IReadOnlyCollection<string> textHashes, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, float[]> hits = textHashes
            .Where(h => Rows.ContainsKey((classification, profileId, h)))
            .ToDictionary(h => h, h => Rows[(classification, profileId, h)], StringComparer.Ordinal);
        return Task.FromResult(hits);
    }

    public Task PutAsync(string classification, Guid profileId, IReadOnlyDictionary<string, float[]> vectors, DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var (hash, vector) in vectors)
        {
            Rows.TryAdd((classification, profileId, hash), vector);
        }

        return Task.CompletedTask;
    }

    public Task TouchAsync(string classification, Guid profileId, IReadOnlyCollection<string> textHashes, DateTimeOffset now, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

/// <summary>In-memory <see cref="IEmbeddingProfileStore"/>.</summary>
public sealed class InMemoryProfiles(params EmbeddingProfileRecord[] profiles) : IEmbeddingProfileStore
{
    public List<EmbeddingProfileRecord> Profiles { get; } = [.. profiles];

    public Task<EmbeddingProfileRecord?> GetAsync(Guid profileId, CancellationToken cancellationToken) =>
        Task.FromResult(Profiles.FirstOrDefault(p => p.Id == profileId));

    public Task<IReadOnlyList<EmbeddingProfileRecord>> ActiveAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<EmbeddingProfileRecord>>([.. Profiles.Where(p => p.State == "active")]);
}

/// <summary>The FakeGateway in process with the per-level dev keys (as <c>Ume.Rag.Api.Tests</c>' factory).</summary>
public sealed class FakeGatewayFactory : WebApplicationFactory<fakegateway::Program>
{
    public const string OnPremKey = "fake-onprem-key-0123456789";
    public const string EuKey = "fake-eu-key-0123456789";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FakeGateway:Keys:OnPrem"] = OnPremKey,
            ["FakeGateway:Keys:Eu"] = EuKey,
            ["FakeGateway:Keys:External"] = "fake-external-key-0123456789",
        }));
    }
}

/// <summary>Golden files (processing design §17): an output compared with its stored form; rewritten with <see cref="Fixtures.Update"/>.</summary>
public static class Golden
{
    private static readonly JsonSerializerOptions Pretty = new(ProcessingJson.Options) { WriteIndented = true };

    public static string Serialize(object value) => JsonSerializer.Serialize(value, Pretty).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";

    /// <summary>The first differing line between the stored golden and <paramref name="actual"/>, or null when equal.</summary>
    public static string? Difference(string path, string actual)
    {
        if (!File.Exists(path))
        {
            return $"{path} does not exist";
        }

        var expected = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        if (expected == actual)
        {
            return null;
        }

        var a = expected.Split('\n');
        var b = actual.Split('\n');
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : "<end>";
            var y = i < b.Length ? b[i] : "<end>";
            if (x != y)
            {
                return $"{Path.GetFileName(path)} line {i + 1}:\n  golden: {x}\n  actual: {y}";
            }
        }

        return "differs";
    }

    /// <summary>Compares, or writes when updating. A difference means a stage's output changed: bump its version and regenerate.</summary>
    public static void Check(string path, object value)
    {
        var actual = Serialize(value);
        if (Fixtures.Update)
        {
            File.WriteAllText(path, actual);
            return;
        }

        if (Difference(path, actual) is { } difference)
        {
            Assert.Fail("A stage output changed without a version bump (engine design §19). Bump the stage version, regenerate with "
                + "UME_RAG_UPDATE_GOLDENS=1 and review the diff.\n" + difference);
        }
    }

    public static JsonNode Parse(string path) => JsonNode.Parse(File.ReadAllText(path))!;
}
