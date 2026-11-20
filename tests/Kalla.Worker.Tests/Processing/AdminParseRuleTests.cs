using Microsoft.Extensions.Options;
using Kalla.Domain;
using Kalla.Worker.Parsing;
using Kalla.Worker.Processing;
using Kalla.Worker.Processing.Chunking;
using Kalla.Worker.Processing.Clean;
using Kalla.Worker.Processing.Detect;
using Kalla.Worker.Processing.Parse;

namespace Kalla.Worker.Tests.Processing;

/// <summary>
/// Admin-made parse rules (processing design §3.5; P1b item 8): the <c>ParseProfiles</c> shape and its refusals, the
/// revision that stands for the version, precedence (admin before built-in, more specific first), level and department
/// scope, the strategy-level overrides reaching the parser and the text-level hints reaching <c>clean</c> and the split.
/// </summary>
public sealed class AdminParseRuleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AdminParseRule Rule(string match, string strategy = "{}", string? cleanup = null, string level = Classification.Internal,
        Guid? department = null, Guid? id = null) =>
        AdminParseRuleReader.Read(id ?? Guid.NewGuid(), level, department, match, strategy, cleanup, out var error)
            ?? throw new InvalidOperationException(error);

    private static DetectResult Pdf(int pages) => new(ProcessingMediaTypes.Pdf, ParseRoute.Parser)
    {
        FileExtension = ".pdf",
        Inspect = new InspectFacts("pdf", pages, true, [.. Enumerable.Range(1, pages).Select(p => new InspectPageSignals(p) { TextChars = 1000, ImageCoverage = 0 })]),
    };

    private static ParserRoute Route(FakeConverter converter) => new(converter, new ParsePlanner(), Options.Create(new ParseOptions()));

    [Theory]
    [InlineData("{}", "{}", null, "parse_rule.match_empty")]
    [InlineData("""{"host":"a.test"}""", "{}", null, "parse_rule.match_invalid")]
    [InlineData("""{"pathGlob":""}""", "{}", null, "parse_rule.match_invalid")]
    [InlineData("""{"pathGlob":"/a/*"}""", """{"ocr":"always"}""", null, "parse_rule.strategy_invalid")]
    [InlineData("""{"pathGlob":"/a/*"}""", """{"imageScale":5}""", null, "parse_rule.strategy_invalid")]
    [InlineData("""{"pathGlob":"/a/*"}""", "{}", """{"maxLevel":9}""", "parse_rule.cleanup_invalid")]
    [InlineData("""{"textPattern":"(?<=a)b"}""", "{}", null, "parse_rule.pattern_invalid")]
    [InlineData("""{"textPattern":"("}""", "{}", null, "parse_rule.pattern_invalid")]
    [InlineData("""{"pathGlob":"/a/*"}""", "[", null, "parse_rule.json_invalid")]
    public void A_row_that_does_not_read_is_refused_with_its_code(string match, string strategy, string? cleanup, string code)
    {
        AdminParseRuleReader.Read(Guid.NewGuid(), Classification.Internal, null, match, strategy, cleanup, out var error).ShouldBeNull();
        error.ShouldBe(code);
    }

    [Fact]
    public void The_revision_follows_the_content_so_a_changed_rule_has_another_id()
    {
        var id = Guid.NewGuid();
        var first = Rule("""{"pathGlob":"/kallelser/*"}""", """{"ocr":"force"}""", id: id).Rule;
        var same = Rule("""{"pathGlob":"/kallelser/*"}""", """{"ocr":"force"}""", id: id).Rule;
        var changed = Rule("""{"pathGlob":"/kallelser/*"}""", """{"ocr":"off"}""", id: id).Rule;

        same.Id.ShouldBe(first.Id);
        changed.Id.ShouldNotBe(first.Id);
        first.Id.ShouldStartWith(id.ToString("D") + "@");
    }

    [Fact]
    public void More_specific_rules_come_first_and_the_candidates_stop_at_the_first_rule_without_a_text_match()
    {
        // Equally specific rules are ordered by profile id: the domain rule before the media-type rule.
        var domain = Rule("""{"domain":"*.umea.test"}""", id: Guid.Parse("00000000-0000-7000-8000-000000000001"));
        var path = Rule("""{"domain":"www.umea.test","pathGlob":"/kallelser/*"}""");
        var text = Rule("""{"domain":"www.umea.test","pathGlob":"/kallelser/*","textPattern":"justering"}""");
        var media = Rule("""{"mediaType":"application/pdf"}""", id: Guid.Parse("00000000-0000-7000-8000-000000000002"));
        var set = new AdminParseRuleSet([domain, media, path, text]);
        var url = new Uri("https://www.umea.test/kallelser/2026/KF%20mars.pdf");

        set.Candidates(Classification.Internal, null, url, ProcessingMediaTypes.Pdf).ShouldBe([text.Rule, path.Rule]);
        set.Candidates(Classification.Internal, null, new Uri("https://www.umea.test/annat/a.pdf"), ProcessingMediaTypes.Pdf).ShouldBe([domain.Rule]);
        set.Candidates(Classification.Internal, null, new Uri("https://other.test/a.pdf"), ProcessingMediaTypes.Pdf).ShouldBe([media.Rule]);
        set.Candidates(Classification.Internal, null, null, ProcessingMediaTypes.Docx).ShouldBeEmpty("an upload matches no domain or path");
        set.Candidates(Classification.Public, null, url, ProcessingMediaTypes.Pdf).ShouldBeEmpty("rules apply only within their level");
        AdminParseRuleSet.Key(set.Candidates(Classification.Internal, null, url, ProcessingMediaTypes.Pdf)).ShouldBe($"{text.Rule.Id},{path.Rule.Id}");
        AdminParseRuleSet.Key([]).ShouldBeNull();
    }

    [Fact]
    public void A_department_rule_applies_only_to_that_departments_sources()
    {
        var department = Guid.NewGuid();
        var rule = Rule("""{"mediaType":"application/*"}""", department: department);
        var set = new AdminParseRuleSet([rule]);

        set.NeedsDepartment.ShouldBeTrue();
        set.Candidates(Classification.Internal, department, null, ProcessingMediaTypes.Pdf).ShouldBe([rule.Rule]);
        set.Candidates(Classification.Internal, Guid.NewGuid(), null, ProcessingMediaTypes.Pdf).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_admin_rule_beats_the_built_in_rule_its_text_also_matches()
    {
        var marker = Guid.NewGuid().ToString("N");
        var converter = new FakeConverter(o =>
        {
            var result = FakeConverter.Pages(o.Pages!.From, o.Pages.To);
            return result with { Blocks = [.. result.Blocks.Select(b => b with { Md = $"Sammanträdesprotokoll {marker} Justerare" })] };
        });
        var admin = Rule($$"""{"pathGlob":"/protokoll/{{marker}}/*"}""", cleanup: """{"maxLevel":2,"furniture":["^Sammanträdesprotokoll"]}""");

        var builtIn = await Route(converter).ParseAsync(new byte[1], Pdf(1), new ParseParams(), "doc", Ct);
        var ruled = await Route(converter).ParseAsync(new byte[1], Pdf(1), new ParseParams(), "doc", Ct, rules: [admin.Rule]);

        builtIn.ParseRule.ShouldBe(ParseRules.Protokoll.Id);
        builtIn.RuleHints.ShouldBeNull();
        ruled.ParseRule.ShouldBe(admin.Rule.Id);
        ruled.Strategy.ShouldBe($"parse=auto(rule={admin.Rule.Id})[text-layer:1]");
        var hints = ruled.RuleHints.ShouldNotBeNull();
        (hints.MaxLevel, hints.ParagraphItemsAreSections).ShouldBe((2, false));
        hints.Furniture.ShouldBe(["^Sammanträdesprotokoll"]);
        Cleaner.Clean(builtIn).Blocks.ShouldHaveSingleItem("the built-in rule keeps the line");
        Cleaner.Clean(ruled).Blocks.ShouldBeEmpty("the admin rule's furniture pattern drops it");
    }

    [Fact]
    public async Task A_strategy_level_rule_overrides_the_plan_and_a_text_level_rule_only_when_its_text_matches()
    {
        var converter = new FakeConverter(o => FakeConverter.Pages(o.Pages!.From, o.Pages.To));
        var force = Rule("""{"mediaType":"application/pdf"}""", """{"ocr":"force","tables":"fast"}""");
        var unmatched = Rule($$"""{"mediaType":"application/pdf","textPattern":"{{Guid.NewGuid():N}}"}""", """{"ocr":"off"}""");

        var parsed = await Route(converter).ParseAsync(new byte[1], Pdf(2), new ParseParams(), "doc", Ct, rules: [unmatched.Rule, force.Rule]);

        converter.Requests.Select(r => $"{r.Ocr.Mode}:{r.Tables.Mode}").Distinct().ShouldBe([$"{ParserOcrMode.Force}:{ParserTableMode.Fast}"]);
        parsed.ParseRule.ShouldBe(force.Rule.Id, "the text-level rule's pattern does not hit, so the strategy-level rule is the match");
        parsed.Strategy.ShouldBe($"parse=auto(rule={force.Rule.Id})[docling-ocr-force:1-2]");
    }

    [Fact]
    public void The_split_reads_an_admin_rules_hints_from_the_parse_output()
    {
        var hints = new ParseRuleHints(1, true, []);
        var document = new ParsedDocument
        {
            Title = "Titel",
            ParseRule = Guid.NewGuid().ToString("D") + "@0123456789abcdef",
            RuleHints = hints,
            Blocks =
            [
                new DocBlock(BlockKinds.Heading, "1 Inledning") { Level = 1, LevelSource = "layout" },
                new DocBlock(BlockKinds.Heading, "1.1 Bakgrund") { Level = 1, LevelSource = "layout" },
                new DocBlock(BlockKinds.Paragraph, "Text."),
            ],
        };

        var rule = ParseRules.Of(document);
        rule.MaxLevel.ShouldBe(1);
        rule.ParagraphItemsAreSections.ShouldBeTrue();
        SectionSplitter.Split(document).Select(s => s.Heading).ShouldBe(["1 Inledning"], "level 2 is deeper than maxLevel 1: bold text, not a section");
        ParseRules.Of(document with { RuleHints = null }).ShouldBe(ParseRules.Default, "an unknown id without hints is the default rule");
    }
}
