using Microsoft.Extensions.Options;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Worker.Parsing;
using Ume.Rag.Worker.Processing;
using Ume.Rag.Worker.Processing.Detect;
using Ume.Rag.Worker.Processing.Parse;

namespace Ume.Rag.Worker.Tests.Processing;

/// <summary>
/// The parse planner and the parser route (processing design §3.1–§3.4, §17 "Planner"): every §3.1 rule at and around
/// its thresholds, fixed range sizes, halving and missing-page outcomes, <c>partial</c>, the post-conversion checks,
/// and the strategy string's grammar and truncation.
/// </summary>
public sealed class ParsePlanningTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static InspectPageSignals Page(int page, int chars, double image = 0, int paths = 0, double garbled = 0, int bad = 0) =>
        new(page) { TextChars = chars, ImageCoverage = image, PathObjects = paths, GarbledScore = garbled, BadChars = bad };

    private static (string Strategy, int Rule) Plan(InspectPageSignals page, string ocr = "auto")
    {
        var plan = new ParsePlanner().Plan(new InspectFacts("pdf", 1, true, [page]), ParseRules.Default, new ParseParams(ocr)).Pages.Single();
        return (plan.Strategy, plan.Rule);
    }

    [Theory]
    [InlineData(49, 0.09, 199, 0, 0, "blank", 1)]
    [InlineData(50, 0.09, 0, 0, 0, "text-layer", 5)]
    [InlineData(49, 0.10, 0, 0, 0, "docling-ocr", 6)]
    [InlineData(49, 0.0, 200, 0, 0, "docling-ocr-force", 2)]
    [InlineData(1000, 0.0, 0, 0.4, 0, "docling-ocr-force", 3)]
    [InlineData(1000, 0.0, 0, 0.39, 0, "docling-ocr", 6)]
    [InlineData(1000, 0.0, 0, 0.0, 100, "docling-ocr-force", 3)]
    [InlineData(1000, 0.0, 0, 0.0, 99, "text-layer", 5)]
    [InlineData(200, 0.9, 40, 0.19, 0, "docling-tables", 4)]
    [InlineData(200, 0.9, 39, 0.19, 0, "text-layer", 5)]
    [InlineData(199, 0.3, 0, 0, 0, "docling-ocr", 6)]
    [InlineData(199, 0.29, 0, 0, 0, "text-layer", 5)]
    [InlineData(1000, 0.0, 0, 0.2, 0, "docling-ocr", 6)]
    public void Each_rule_holds_at_and_around_its_thresholds(int chars, double image, int paths, double garbled, int bad, string strategy, int rule) =>
        Plan(Page(1, chars, image, paths, garbled, bad)).ShouldBe((strategy, rule));

    [Fact]
    public void Template_ocr_overrides_the_rules()
    {
        Plan(Page(1, 1000), ocr: "force").ShouldBe((ParseStrategies.DoclingOcrForce, 0));
        Plan(Page(1, 1000, garbled: 0.5), ocr: "off").ShouldBe((ParseStrategies.DoclingText, 0));
        Plan(Page(1, 10, image: 0.8), ocr: "off").ShouldBe((ParseStrategies.DoclingText, 0));
        Plan(Page(1, 1000), ocr: "off").ShouldBe((ParseStrategies.TextLayer, 5));
    }

    [Fact]
    public void Tiff_frames_have_docling_ocr_as_the_floor()
    {
        var plan = new ParsePlanner().Plan(new InspectFacts("tiff", 2, true, [Page(1, 0), Page(2, 1000)]), ParseRules.Default, new ParseParams());
        plan.Pages.Select(p => p.Strategy).ShouldBe([ParseStrategies.DoclingOcr, ParseStrategies.DoclingOcr]);
    }

    [Fact]
    public void Ranges_cut_runs_at_the_fixed_size_per_strategy_and_skip_blank_pages()
    {
        var pages = Enumerable.Range(1, 45).Select(p => p switch
        {
            <= 25 => Page(p, 1000),
            <= 40 => Page(p, 10, image: 0.8),
            45 => Page(p, 0),
            _ => Page(p, 1000, paths: 100),
        }).ToList();
        var plan = new ParsePlanner().Plan(new InspectFacts("pdf", 45, true, pages), ParseRules.Default, new ParseParams());

        RangePlanner.Ranges(plan, new ParseOptions()).Select(r => $"{r.Strategy}:{r.From}-{r.To}").ShouldBe(
        [
            "text-layer:1-20", "text-layer:21-25", "docling-ocr:26-33", "docling-ocr:34-40", "docling-tables:41-44",
        ]);
        RangePlanner.Ranges(plan, new ParseOptions { RangePages = { ["text-layer"] = 10 } })[0].To.ShouldBe(10);
    }

    [Fact]
    public void Range_options_map_each_strategy_to_the_parser_request()
    {
        var plan = new PagePlan([], ParserTableMode.Fast, 2.0);
        RangePlanner.Options(new ParseRange(ParseStrategies.TextLayer, 1, 2), plan, "r", ".pdf", null, 1000).Pipeline.ShouldBe(ParserPipeline.TextLayer);
        RangePlanner.Options(new ParseRange(ParseStrategies.DoclingTables, 1, 2), plan, "r", ".pdf", null, 1000).Tables.Mode.ShouldBe(ParserTableMode.Accurate);
        RangePlanner.Options(new ParseRange(ParseStrategies.DoclingOcr, 1, 2), plan, "r", ".pdf", null, 1000).Tables.Mode.ShouldBe(ParserTableMode.Fast);
        var rerender = RangePlanner.Options(new ParseRange(ParseStrategies.DoclingOcrForce3, 3, 3), plan, "r", ".pdf", null, 1000);
        (rerender.Ocr.Mode, rerender.ImageScale, rerender.Pages).ShouldBe((ParserOcrMode.Force, 3.0, new ParserPageRange(3, 3)));
    }

    private static DetectResult Pdf(int pages, Func<int, InspectPageSignals>? page = null) =>
        new(ProcessingMediaTypes.Pdf, ParseRoute.Parser)
        {
            FileExtension = ".pdf",
            Inspect = new InspectFacts("pdf", pages, true, [.. Enumerable.Range(1, pages).Select(p => page?.Invoke(p) ?? Page(p, 1000))]),
        };

    private static ParserRoute Route(FakeConverter converter) => new(converter, new ParsePlanner(), Options.Create(new ParseOptions()));

    [Fact]
    public async Task A_timeout_halves_the_range_and_a_single_failing_page_is_missing_and_degraded()
    {
        var converter = new FakeConverter(o => o.Pages!.From <= 7 && o.Pages.To >= 7 ? throw FakeConverter.Error("timeout") : FakeConverter.Pages(o.Pages.From, o.Pages.To));
        var parsed = await Route(converter).ParseAsync(new byte[1], Pdf(20), new ParseParams(), "doc", Ct);

        parsed.MissingPages.ShouldBe([7]);
        parsed.Strategy.ShouldBe("parse=auto[text-layer:1-20;missing:7]");
        converter.Requests.Select(r => $"{r.Pages!.From}-{r.Pages.To}").ShouldBe(["1-20", "1-10", "1-5", "6-10", "6-7", "6-6", "7-7", "8-10", "11-20"]);
        parsed.Pages.Select(p => p.Page).ShouldNotContain(7);
    }

    [Fact]
    public async Task More_than_ten_percent_missing_pages_fail_the_document()
    {
        var converter = new FakeConverter(o => o.Pages!.From <= 3 ? throw FakeConverter.Error("worker_crashed", System.Net.HttpStatusCode.InternalServerError) : FakeConverter.Pages(o.Pages.From, o.Pages.To));
        var error = await Should.ThrowAsync<StageFatalException>(() => Route(converter).ParseAsync(new byte[1], Pdf(10, p => Page(p, 10, image: 0.8)), new ParseParams(), "doc", Ct));

        (error.Outcome, error.Reason).ShouldBe((RunItemOutcome.Failed, "parser.worker_crashed"));
    }

    [Fact]
    public async Task A_partial_answer_keeps_the_completed_pages_and_requests_the_rest()
    {
        var first = true;
        var converter = new FakeConverter(o =>
        {
            if (first)
            {
                first = false;
                return FakeConverter.Pages(o.Pages!.From, o.Pages.To, "partial", completedTo: 12);
            }

            return FakeConverter.Pages(o.Pages!.From, o.Pages.To);
        });
        var parsed = await Route(converter).ParseAsync(new byte[1], Pdf(20), new ParseParams(), "doc", Ct);

        converter.Requests.Select(r => $"{r.Pages!.From}-{r.Pages.To}").ShouldBe(["1-20", "13-20"]);
        parsed.Pages.Count.ShouldBe(20);
        parsed.MissingPages.ShouldBeEmpty();
    }

    [Fact]
    public async Task Ocr_failed_is_retried_once_then_halved()
    {
        var calls = 0;
        var converter = new FakeConverter(o => o.Pages!.From == 1 && o.Pages.To == 8 && calls++ < 2 ? throw FakeConverter.Error("ocr_failed") : FakeConverter.Pages(o.Pages.From, o.Pages.To));
        await Route(converter).ParseAsync(new byte[1], Pdf(8, p => Page(p, 10, image: 0.8)), new ParseParams(), "doc", Ct);

        converter.Requests.Select(r => $"{r.Pages!.From}-{r.Pages.To}").ShouldBe(["1-8", "1-8", "1-4", "5-8"]);
    }

    [Fact]
    public async Task Garbled_pages_rerun_with_forced_ocr_and_empty_scanned_pages_rerender_once_at_scale_three()
    {
        var converter = new FakeConverter(o => FakeConverter.Pages(o.Pages!.From, o.Pages.To, signals: p =>
            o.Ocr.Mode == ParserOcrMode.Force ? FakeConverter.Signals(500)
            : p == 2 ? FakeConverter.Signals(800, garbled: 0.5)
            : p == 4 ? FakeConverter.Signals(5, image: 0.9)
            : FakeConverter.Signals(800)));
        var parsed = await Route(converter).ParseAsync(new byte[1], Pdf(5), new ParseParams(), "doc", Ct);

        converter.Requests.Skip(1).Select(r => $"{r.Ocr.Mode}@{r.ImageScale}:{r.Pages!.From}-{r.Pages.To}").ShouldBe(["Force@2:2-2", "Force@3:4-4"]);
        parsed.Strategy.ShouldBe("parse=auto[text-layer:1,docling-ocr-force:2,text-layer:3-5;rerender:4]");
    }

    [Theory]
    [InlineData("busy", System.Net.HttpStatusCode.ServiceUnavailable, false)]
    [InlineData("not_ready", System.Net.HttpStatusCode.ServiceUnavailable, false)]
    [InlineData("duplicate_in_flight", System.Net.HttpStatusCode.Conflict, false)]
    public async Task Busy_answers_requeue_without_an_attempt(string code, System.Net.HttpStatusCode status, bool counts)
    {
        var converter = new FakeConverter(_ => throw FakeConverter.Error(code, status));
        var error = await Should.ThrowAsync<StageTransientException>(() => Route(converter).ParseAsync(new byte[1], Pdf(2), new ParseParams(), "doc", Ct));
        error.CountsAttempt.ShouldBe(counts);
    }

    [Theory]
    [InlineData("encrypted", RunItemOutcome.Encrypted)]
    [InlineData("too_large", RunItemOutcome.TooLarge)]
    [InlineData("format_deferred", RunItemOutcome.Unsupported)]
    [InlineData("unsupported_format", RunItemOutcome.Unsupported)]
    [InlineData("corrupt", RunItemOutcome.Failed)]
    public async Task Refusals_end_the_document_with_their_outcome(string code, RunItemOutcome outcome)
    {
        var converter = new FakeConverter(_ => throw FakeConverter.Error(code));
        (await Should.ThrowAsync<StageFatalException>(() => Route(converter).ParseAsync(new byte[1], Pdf(2), new ParseParams(), "doc", Ct))).Outcome.ShouldBe(outcome);
    }

    [Fact]
    public async Task Limit_exceeded_is_fatal_with_event_5104()
    {
        var converter = new FakeConverter(_ => throw FakeConverter.Error("limit_exceeded"));
        (await Should.ThrowAsync<StageFatalException>(() => Route(converter).ParseAsync(new byte[1], Pdf(2), new ParseParams(), "doc", Ct))).SecurityEvent.ShouldBeTrue();
    }

    [Fact]
    public async Task A_non_rangeable_document_is_one_request_and_its_timeout_fails_it()
    {
        var docx = new DetectResult(ProcessingMediaTypes.Docx, ParseRoute.Parser) { FileExtension = ".docx" };
        var converter = new FakeConverter(_ => throw FakeConverter.Error("timeout"));
        (await Should.ThrowAsync<StageFatalException>(() => Route(converter).ParseAsync(new byte[1], docx, new ParseParams(), "doc", Ct))).Reason.ShouldBe("parser.timeout");
        converter.Requests.Single().Pages.ShouldBeNull();
    }

    [Fact]
    public void The_strategy_string_follows_the_grammar_and_is_cut_at_500_characters()
    {
        var pages = Enumerable.Range(1, 45).Select(p => new PageStrategy(p, p <= 38 ? ParseStrategies.TextLayer : p <= 40 ? ParseStrategies.DoclingTables : p <= 44 ? ParseStrategies.DoclingOcr : ParseStrategies.Blank, 0)).ToList();
        StrategyString.Paged(ParseRules.Regulations, pages, [43], [44])
            .ShouldBe("parse=auto(rule=regulations@1)[text-layer:1-38,docling-tables:39-40,docling-ocr:41-44,blank:45;rerender:43;missing:44]");
        StrategyString.Html("www.umea.se", 3, "generic", 1).ShouldBe("parse=html(profile=www.umea.se@3)");
        StrategyString.Whole(ParseRules.Default, "docx").ShouldBe("parse=auto[docling-docx]");

        var alternating = Enumerable.Range(1, 400).Select(p => new PageStrategy(p, p % 2 == 0 ? ParseStrategies.TextLayer : ParseStrategies.DoclingOcr, 0)).ToList();
        var cut = StrategyString.Paged(ParseRules.Default, alternating, [], []);
        cut.Length.ShouldBe(StrategyString.MaxLength);
        cut.ShouldEndWith("…");
        StrategyString.Pages([5, 1, 2, 3, 9, 10]).ShouldBe("1-3,5,9-10");
    }
}
