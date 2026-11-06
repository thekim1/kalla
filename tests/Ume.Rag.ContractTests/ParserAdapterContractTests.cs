using System.Text.Json;
using Microsoft.Extensions.Options;
using Ume.Rag.Worker.Parsing;
using Ume.Rag.Worker.Processing.Detect;
using Ume.Rag.Worker.Processing.Parse;
using Ume.Rag.Worker.Processing.Pii;

namespace Ume.Rag.ContractTests;

/// <summary>
/// The processing stages' parser adapters against the parser image (processing design §2, §10, §17): <c>detect</c>'s
/// inspector plans a real PDF, the NER client reports readiness and finds names, and the NER name targets of §17
/// (recall ≥ 0.85, precision ≥ 0.80 at <c>Settings.NerMinScore</c> 0.80) on <c>tests/fixtures/pii/ner-names.jsonl</c>.
/// Skips unless <c>UME_RAG_TEST_PARSER_IMAGE</c> is set.
/// </summary>
[Collection(ParserImageTests.Name)]
public sealed class ParserAdapterContractTests(ParserContainer parser)
{
    public const double MinScore = 0.80;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_inspector_gives_detect_a_page_plan_for_a_real_pdf()
    {
        await parser.RequireReadyAsync();
        var detect = new DetectStage(new ParserInspector(parser.Client(strict: false)));

        var result = await detect.RunAsync(new RawContent(await File.ReadAllBytesAsync(ContractFiles.Fixture("text-layer.pdf"), Ct), "application/pdf"), Ct);

        result.Inspect.ShouldNotBeNull().Pages.ShouldNotBeEmpty();
        result.PageCount.ShouldBe(result.Inspect.Pages.Count);
        var plan = new ParsePlanner().Plan(result.Inspect, ParseRules.Default, new ParseParams());
        plan.Pages.ShouldAllBe(p => p.Strategy == ParseStrategies.TextLayer || p.Strategy == ParseStrategies.DoclingTables || p.Strategy == ParseStrategies.Blank);
        RangePlanner.Ranges(plan, new ParseOptions()).ShouldNotBeEmpty();
    }

    [Fact]
    public async Task The_parser_route_converts_a_real_pdf_through_the_adapters()
    {
        await parser.RequireReadyAsync();
        var client = parser.Client(strict: false);
        var bytes = await File.ReadAllBytesAsync(ContractFiles.Fixture("text-layer.pdf"), Ct);
        var detected = await new DetectStage(new ParserInspector(client)).RunAsync(new RawContent(bytes, "application/pdf"), Ct);

        var parsed = await new ParserRoute(new ParserClientConverter(client), new ParsePlanner(), Options.Create(new ParseOptions()))
            .ParseAsync(bytes, detected, new ParseParams(), "contract-route", Ct);

        parsed.Blocks.ShouldNotBeEmpty();
        parsed.MissingPages.ShouldBeEmpty();
        parsed.Strategy.ShouldNotBeNull().ShouldStartWith("parse=auto");
    }

    [Fact]
    public async Task The_ner_client_meets_the_name_targets_on_the_labelled_fixtures()
    {
        await parser.RequireReadyAsync();
        var ner = new ParserNerClient(parser.Client(strict: false));
        if (!await ner.IsReadyAsync(Ct))
        {
            // NER_IDLE_STOP_S: ner-0 starts on the first /v1/ner, which answers not_ready while it warms.
            await Should.ThrowAsync<ParserException>(() => ner.RecognizeAsync([new NerText("wake", "Möte med Anna.")], 0.5, Ct));
        }

        for (var i = 0; i < 120 && !await ner.IsReadyAsync(Ct); i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), Ct);
        }

        (await ner.IsReadyAsync(Ct)).ShouldBeTrue("ner-0 did not become ready");
        var rows = File.ReadLines(Path.Combine(ContractFiles.RepoRoot, "tests", "fixtures", "pii", "ner-names.jsonl"))
            .Select((line, i) =>
            {
                using var json = JsonDocument.Parse(line);
                return (Id: $"t{i}", Text: json.RootElement.GetProperty("text").GetString()!,
                    Names: json.RootElement.GetProperty("names").EnumerateArray().Select(n => n.GetString()!).ToList());
            })
            .ToList();

        int tp = 0, fp = 0, fn = 0;
        var errors = new List<string>();
        string? model = null;
        foreach (var batch in rows.Chunk(PersonalDataStage.MaxTextsPerRequest))
        {
            var answer = await ner.RecognizeAsync([.. batch.Select(r => new NerText(r.Id, r.Text))], MinScore, Ct);
            model = answer.Model;
            foreach (var row in batch)
            {
                var found = answer.Entities.Where(e => e.TextId == row.Id && e.Label == "person" && e.Score >= MinScore)
                    .Select(e => row.Text[ParserText.Utf16Index(row.Text, e.Start)..ParserText.Utf16Index(row.Text, e.End)]).ToList();
                var hit = found.Count(row.Names.Contains);
                tp += hit;
                fp += found.Count - hit;
                fn += row.Names.Count(n => !found.Contains(n));
                if (found.Count != hit || hit != row.Names.Count)
                {
                    errors.Add($"{row.Text} → [{string.Join(", ", found)}]");
                }
            }
        }

        var precision = tp / (double)Math.Max(1, tp + fp);
        var recall = tp / (double)Math.Max(1, tp + fn);
        var summary = $"{model}: precision {precision:F3}, recall {recall:F3} ({tp} TP, {fp} FP, {fn} FN at min score {MinScore})";
        TestContext.Current.TestOutputHelper?.WriteLine(summary + "\n" + string.Join("\n", errors));
        recall.ShouldBeGreaterThanOrEqualTo(0.85, summary + "\n" + string.Join("\n", errors));
        precision.ShouldBeGreaterThanOrEqualTo(0.80, summary + "\n" + string.Join("\n", errors));
    }
}
