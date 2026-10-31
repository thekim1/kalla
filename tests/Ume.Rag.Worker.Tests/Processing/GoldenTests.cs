using System.Text.Json;
using System.Text.Json.Nodes;
using Ume.Rag.Pipelines.SiteProfiles;
using Ume.Rag.Worker.Parsing;
using Ume.Rag.Worker.Processing;
using Ume.Rag.Worker.Processing.Chunking;
using Ume.Rag.Worker.Processing.Clean;
using Ume.Rag.Worker.Processing.Detect;
using Ume.Rag.Worker.Processing.Html;
using Ume.Rag.Worker.Processing.Parse;
using Ume.Rag.Worker.Processing.Pii;
using Ume.Rag.Worker.Processing.Text;

namespace Ume.Rag.Worker.Tests.Processing;

/// <summary>
/// The processing goldens of processing design §17 (engine design §19): each case in
/// <c>tests/fixtures/processing/golden/&lt;case&gt;/</c> (an input, <c>case.json</c> with the document's settings, and for
/// parser-bound cases the parser's <c>result.json</c>) runs <c>detect</c> → <c>parse</c> → <c>clean</c> → <c>chunk</c> →
/// <c>personal-data</c> and is compared with <c>expected.json</c>, which also records the stage versions it was made with.
/// A changed output, or a version bump without regenerated goldens, fails.
/// </summary>
public sealed class GoldenTests
{
    public static readonly string Directory = Path.Combine(Fixtures.Processing, "golden");

    public static IReadOnlyList<string> Names => [.. System.IO.Directory.EnumerateDirectories(Directory).Select(d => Path.GetFileName(d)).Order(StringComparer.Ordinal)];

    public static TheoryData<string> Cases => [.. Names];

    /// <summary>The stage versions the goldens are for (a bump makes every golden of that stage stale).</summary>
    public static IReadOnlyDictionary<string, int> Versions { get; } = new SortedDictionary<string, int>(StringComparer.Ordinal)
    {
        ["detect"] = DetectStage.Version,
        ["parse.html"] = HtmlExtractor.Version,
        ["parse.text"] = PlainTextParser.Version,
        ["parse.csv"] = CsvTableParser.Version,
        ["parse.rules"] = ParseRules.Default.Version,
        ["clean"] = Cleaner.Version,
        ["chunk"] = Chunker.Version,
        ["personal-data"] = PersonalDataStage.Version,
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task A_case_gives_its_golden_output(string name)
    {
        var output = await RunAsync(name, new ChunkOptions());
        Golden.Check(Path.Combine(Directory, name, "expected.json"), output);
    }

    [Fact]
    public async Task Negative_proof_a_changed_rule_fails_the_golden()
    {
        // The same case with another chunk budget: the comparison must see the difference.
        var changed = await RunAsync("html-page", new ChunkOptions(ChunkTokens: 60));
        Golden.Difference(Path.Combine(Directory, "html-page", "expected.json"), Golden.Serialize(changed)).ShouldNotBeNull();

        // A planner whose rule 5 no longer picks the text layer: the parser requests differ, so the golden fails.
        var planned = await RunAsync("parser-foreskrift", new ChunkOptions(), new DoclingOnlyPlanner());
        Golden.Difference(Path.Combine(Directory, "parser-foreskrift", "expected.json"), Golden.Serialize(planned)).ShouldNotBeNull();
    }

    [Fact]
    public void Every_golden_was_made_with_the_current_stage_versions()
    {
        foreach (var name in Names)
        {
            var path = Path.Combine(Directory, name, "expected.json");
            if (Fixtures.Update || !File.Exists(path))
            {
                continue;
            }

            var recorded = Golden.Parse(path)["versions"]!.AsObject().ToDictionary(p => p.Key, p => p.Value!.GetValue<int>(), StringComparer.Ordinal);
            recorded.ShouldBe(Versions.ToDictionary(), $"{name}: regenerate the goldens after a stage version bump");
        }
    }

    internal static async Task<JsonObject> RunAsync(string name, ChunkOptions chunk, IParsePlanner? planner = null)
    {
        var dir = Path.Combine(Directory, name);
        var settings = Golden.Parse(Path.Combine(dir, "case.json")).AsObject();
        var input = System.IO.Directory.EnumerateFiles(dir, "input.*").Single();
        FakeConverter? converter = null;
        FakeInspector? inspector = null;
        if (File.Exists(Path.Combine(dir, "result.json")))
        {
            var full = JsonSerializer.Deserialize<ParserConvertResult>(File.ReadAllText(Path.Combine(dir, "result.json")), ParserJson.Create())!;
            converter = new FakeConverter(options => Slice(full, options));
            inspector = new FakeInspector(JsonSerializer.Deserialize<InspectFacts>(settings["inspect"]!.ToJsonString(), ProcessingJson.Options)!);
        }

        var ner = new FakeNer();
        foreach (var entry in settings["ner"]?.AsArray() ?? [])
        {
            ner.Names.Add((entry![0]!.GetValue<string>(), entry[1]!.GetValue<string>(), entry[2]!.GetValue<double>()));
        }

        var harness = new ProcessingHarness(converter, inspector, ner, planner);
        var profile = settings["profile"] is JsonObject p
            ? new HtmlProfileVersion(p["host"]!.GetValue<string>(), p["version"]!.GetValue<int>(),
                JsonSerializer.Deserialize<SiteProfile>(p["profile"]!.ToJsonString(), ProcessingJson.Options)!)
            : null;
        var processing = new ProcessingInput(
            new RawContent(await File.ReadAllBytesAsync(input, Ct), settings["mediaType"]?.GetValue<string>())
            {
                Url = settings["url"] is { } url ? new Uri(url.GetValue<string>()) : null,
                DisplayName = settings["displayName"]?.GetValue<string>(),
                HttpLastModified = new DateTimeOffset(2026, 3, 1, 8, 0, 0, TimeSpan.Zero),
            },
            new PersonalDataContext(settings["collection"]!.GetValue<string>())
            {
                OwnDomains = [.. settings["ownDomains"]?.AsArray().Select(d => d!.GetValue<string>()) ?? ["exempelstad.se"]],
            })
        {
            SiteProfile = profile,
            Chunk = chunk,
            PathRules = Metadata.ReadRules(settings["pathRules"] is { } rules ? JsonSerializer.SerializeToElement(rules) : null),
            RequestId = "golden",
        };

        var result = await harness.Processor.RunAsync(processing, Ct);
        return new JsonObject
        {
            ["versions"] = JsonSerializer.SerializeToNode(Versions),
            ["detect"] = JsonSerializer.SerializeToNode(result.Detect with { Inspect = null }, ProcessingJson.Options),
            ["parserRequests"] = new JsonArray([.. (converter?.Requests ?? []).Select(r => (JsonNode)JsonValue.Create(
                $"{r.Pipeline}:{r.Ocr.Mode}:{r.Tables.Mode}:{r.ImageScale}:{r.Pages?.From}-{r.Pages?.To}"))]),
            ["cleaned"] = JsonSerializer.SerializeToNode(result.Cleaned, ProcessingJson.Options),
            ["metadata"] = JsonSerializer.SerializeToNode(result.Metadata, ProcessingJson.Options),
            ["chunks"] = JsonSerializer.SerializeToNode(result.Chunks.Chunks, ProcessingJson.Options),
            ["chunkFacts"] = JsonSerializer.SerializeToNode(result.Chunks.Facts, ProcessingJson.Options),
            ["pii"] = JsonSerializer.SerializeToNode(new { counts = result.Pii.Counts, typesFired = result.Pii.TypesFired, nerUnits = ner.Calls.Sum(c => c.Count) }, ProcessingJson.Options),
        };
    }

    /// <summary>The pages, blocks and tables of the requested range, as the parser answers a range request.</summary>
    internal static ParserConvertResult Slice(ParserConvertResult full, ParserConvertOptions options)
    {
        var from = options.Pages?.From ?? 1;
        var to = options.Pages?.To ?? int.MaxValue;
        var blocks = full.Blocks.Where(b => b.Page is { } p && p >= from && p <= to).ToList();
        var tables = full.Tables.Where(t => blocks.Any(b => b.Table == t.Id)).ToList();
        return full with
        {
            Pages = [.. full.Pages.Where(p => p.Page >= from && p.Page <= to)],
            Blocks = blocks,
            Tables = tables,
            Input = full.Input with { PagesRequested = new ParserPageSpan(from, Math.Min(to, full.Pages.Count)), PagesCompleted = new ParserPageSpan(from, Math.Min(to, full.Pages.Count)) },
        };
    }

    /// <summary>A changed rule: rule 5 pages go to Docling without OCR instead of the text layer.</summary>
    private sealed class DoclingOnlyPlanner : IParsePlanner
    {
        public PagePlan Plan(InspectFacts inspect, ParseRule rule, ParseParams parameters)
        {
            var plan = new ParsePlanner().Plan(inspect, rule, parameters);
            return plan with { Pages = [.. plan.Pages.Select(p => p.Strategy == ParseStrategies.TextLayer ? p with { Strategy = ParseStrategies.DoclingText } : p)] };
        }
    }
}
