using System.IO.Compression;
using System.Text;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Pipelines.SiteProfiles;
using Ume.Rag.Retrieval.Text;
using Ume.Rag.Worker.Processing;
using Ume.Rag.Worker.Processing.Chunking;
using Ume.Rag.Worker.Processing.Clean;
using Ume.Rag.Worker.Processing.Detect;
using Ume.Rag.Worker.Processing.Html;
using Ume.Rag.Worker.Processing.Parse;
using Ume.Rag.Worker.Processing.Persistence;
using ChunkContentKinds = Ume.Rag.Domain.Entities.ChunkContentKinds;

namespace Ume.Rag.Worker.Tests.Processing;

/// <summary>Rule-level tests of <c>detect</c>, HTML extraction, <c>clean</c>, the split, the chunker and metadata (processing design §2–§8).</summary>
public sealed class StageRuleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Chunker Chunker = new(Bgem3TokenCounter.Shared, SwedishDecompounder.Default);

    [Theory]
    [InlineData(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31 }, null, ProcessingMediaTypes.Pdf)]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, "application/octet-stream", ProcessingMediaTypes.Png)]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, null, ProcessingMediaTypes.Jpeg)]
    [InlineData(new byte[] { 0x49, 0x49, 0x2A, 0x00 }, null, ProcessingMediaTypes.Tiff)]
    [InlineData(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }, "application/msword", "application/msword")]
    [InlineData(new byte[] { 0x7B, 0x5C, 0x72, 0x74, 0x66 }, null, ProcessingMediaTypes.Rtf)]
    public void Magic_bytes_win_over_the_declared_type(byte[] bytes, string? declared, string expected) =>
        MediaTypeSniffer.Sniff(bytes, declared).ShouldBe(expected);

    [Theory]
    [InlineData("word/document.xml", ProcessingMediaTypes.Docx)]
    [InlineData("xl/workbook.xml", ProcessingMediaTypes.Xlsx)]
    [InlineData("ppt/presentation.xml", ProcessingMediaTypes.Pptx)]
    public void Ooxml_is_recognised_by_its_part_folders(string part, string expected)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            zip.CreateEntry("[Content_Types].xml");
            zip.CreateEntry(part);
        }

        MediaTypeSniffer.Sniff(buffer.ToArray(), "application/octet-stream").ShouldBe(expected);
    }

    [Fact]
    public async Task Detect_refuses_deferred_and_unsupported_types_before_parsing()
    {
        var detect = new DetectStage();
        (await Should.ThrowAsync<StageFatalException>(() => detect.RunAsync(new RawContent(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }, "application/msword"), Ct)))
            .Reason.ShouldBe("parser.format_deferred");
        (await Should.ThrowAsync<StageFatalException>(() => detect.RunAsync(new RawContent("MZ\x90\0"u8.ToArray(), "application/x-msdownload"), Ct)))
            .Outcome.ShouldBe(RunItemOutcome.Unsupported);
    }

    [Fact]
    public async Task Detect_records_a_declared_type_mismatch_and_the_html_lang()
    {
        var result = await new DetectStage().RunAsync(new RawContent("<!DOCTYPE html><html lang=\"sv-SE\"><body>x</body></html>"u8.ToArray(), "text/plain"), Ct);
        (result.MediaType, result.MediaTypeMismatch).ShouldBe((ProcessingMediaTypes.PlainText, false)); // a declared text type is trusted
        var sniffed = await new DetectStage().RunAsync(new RawContent("<!DOCTYPE html><html lang=\"sv-SE\"><body>x</body></html>"u8.ToArray(), null), Ct);
        (sniffed.MediaType, sniffed.Language).ShouldBe((ProcessingMediaTypes.Html, "sv"));
    }

    [Fact]
    public async Task Detect_ends_an_encrypted_or_too_long_pdf_from_inspect()
    {
        var encrypted = new DetectStage(new FakeInspector(new InspectFacts("pdf", 3, true, []) { Encrypted = "user_password" }));
        (await Should.ThrowAsync<StageFatalException>(() => encrypted.RunAsync(new RawContent("%PDF-1.7"u8.ToArray(), null), Ct))).Outcome.ShouldBe(RunItemOutcome.Encrypted);
        var long_ = new DetectStage(new FakeInspector(new InspectFacts("pdf", 3000, true, []) { Refusal = "too_many_pages" }));
        (await Should.ThrowAsync<StageFatalException>(() => long_.RunAsync(new RawContent("%PDF-1.7"u8.ToArray(), null), Ct))).Outcome.ShouldBe(RunItemOutcome.TooLarge);
    }

    [Theory]
    [InlineData("Kommunen ansvarar för att det finns förskola och skola för alla barn som bor i kommunen, och det är viktigt.", "sv")]
    [InlineData("The municipality is responsible for the schools and it is important that all of the children have a place.", "en")]
    [InlineData("Bygglov Rivningslov Marklov Strandskydd", "und")]
    public void The_language_detector_tells_swedish_from_english(string text, string language) =>
        LanguageDetector.Detect(text).ShouldBe(language);

    [Fact]
    public void Text_decoding_falls_back_to_windows_1252()
    {
        TextDecoding.Decode([0x52, 0xE4, 0x6B, 0x6E, 0x61]).ShouldBe("Räkna");
        TextDecoding.Decode(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("Umeå")).ToArray()).ShouldBe("Umeå");
    }

    private static ParsedDocument Html(string body, SiteProfile? profile = null, string head = "<title>Sida - Kommun</title>") =>
        new HtmlExtractor(new HtmlPresets()).Extract(Encoding.UTF8.GetBytes($"<html lang=\"sv\"><head>{head}</head><body>{body}</body></html>"),
            profile is null ? null : new HtmlProfileVersion("www.exempel.se", 1, profile)).Document;

    [Fact]
    public void Html_breadcrumb_drops_screen_reader_text_and_the_current_page()
    {
        var parsed = Html("""<nav aria-label="breadcrumb"><span class="sr-only">Du är här:</span><ul><li><a>Hem</a></li><li><a>Kommun och politik</a></li><li>Sida</li></ul></nav><main><p>Text på sidan som är tillräckligt lång.</p></main>""");
        parsed.Breadcrumb.ShouldBe(["Hem", "Kommun och politik"]);
        parsed.Title.ShouldBe("Sida");
    }

    [Fact]
    public void Html_breadcrumb_falls_back_to_json_ld()
    {
        var parsed = Html("<main><p>Text.</p></main>", head: """<title>Sida</title><script type="application/ld+json">{"@type":"BreadcrumbList","itemListElement":[{"position":2,"name":"Bo"},{"position":1,"name":"Start"}]}</script>""");
        parsed.Breadcrumb.ShouldBe(["Start", "Bo"]);
    }

    [Fact]
    public void Html_profile_root_boilerplate_link_blocks_and_date_apply()
    {
        var profile = new SiteProfile
        {
            MainRoot = "#content",
            Boilerplate = new SiteProfileBoilerplate { Selectors = [".share"], Phrases = ["Dela sidan"] },
            LinkBlocks = [".links"],
            DatePattern = @"Uppdaterad (?<d>\d{4}-\d{2}-\d{2})",
        };
        var parsed = Html("""<main><p>Utanför roten.</p></main><div id="content"><p>Innehåll i roten. Dela sidan</p><div class="share">Facebook</div><ul class="links"><li>Länk</li></ul><p>Uppdaterad 2026-01-15</p></div>""", profile);

        parsed.Blocks.Select(b => b.Text).ShouldBe(["Innehåll i roten.", "Uppdaterad 2026-01-15"]);
        parsed.VisibleDate.ShouldBe(new DateOnly(2026, 1, 15));
        parsed.Strategy.ShouldBe("parse=html(profile=www.exempel.se@1)");
    }

    [Fact]
    public void Html_a_root_that_matches_nothing_falls_back_and_is_reported()
    {
        var extraction = new HtmlExtractor(new HtmlPresets()).Extract("<html><body><article><p>Text i artikeln.</p></article></body></html>"u8.ToArray(),
            new HtmlProfileVersion("h", 1, new SiteProfile { MainRoot = "#saknas" }));
        extraction.Outcome.ShouldBe(HtmlExtraction.EmptyRoot);
        extraction.Document.Facts["quality.emptyRoot"].ShouldBe(1);
        extraction.Document.Blocks.Single().Text.ShouldBe("Text i artikeln.");
    }

    [Fact]
    public void Html_root_alternatives_are_tried_in_order_not_in_document_order()
    {
        Html("<article><p>Puff för en nyhet.</p></article><main><p>Sidans text.</p></main>").Blocks.Single().Text.ShouldBe("Sidans text.");
        Html("""<div class="top"><h1>Rubrik</h1></div><div class="top"><p>Lista.</p></div>""", new SiteProfile { MainRoot = "main, .top" })
            .Blocks.Single().Text.ShouldBe("Rubrik");
    }

    [Theory]
    [InlineData("main, .top", new[] { "main", ".top" })]
    [InlineData("a[title='x, y'], li:not(:first-child, .z) , b", new[] { "a[title='x, y']", "li:not(:first-child, .z)", "b" })]
    [InlineData("main", new[] { "main" })]
    public void Html_selector_lists_split_at_top_level_commas(string selector, string[] alternatives) =>
        HtmlExtractor.Alternatives(selector).ShouldBe(alternatives);

    [Fact]
    public void Html_a_breadcrumb_selector_matching_several_crumbs_gives_one_crumb_each()
    {
        var profile = new SiteProfile { MainRoot = "main", Breadcrumb = "nav li:not(:first-child)" };
        var parsed = Html("""<nav><ol><li><a>Startsida</a></li><li><a>Bo <span class="sr-only">nivå</span></a></li><li><a>Sida</a></li></ol></nav><main><p>Text.</p></main>""", profile);
        parsed.Breadcrumb.ShouldBe(["Bo"]);
    }

    [Fact]
    public void Html_a_list_item_of_paragraphs_keeps_its_lines()
    {
        var parsed = Html("<main><ul><li><span>Kontakt</span><p>Enhet</p><address><p>Gatan 1<br>Huset</p></address></li></ul></main>");
        parsed.Blocks.Single().Text.ShouldBe("Kontakt\nEnhet\nGatan 1\nHuset");
    }

    [Fact]
    public void Html_tables_keep_header_rows_and_spans()
    {
        var parsed = Html("<main><table><tr><th>A</th><th>B</th></tr><tr><td rowspan=\"2\">x</td><td>1</td></tr><tr><td>2</td></tr></table></main>");
        var table = parsed.Tables.Single();
        (table.HeaderRows, table.Cols).ShouldBe((1, 2));
        table.Cells.Single(c => c.R == 2).C.ShouldBe(1);
    }

    private static ParsedDocument Doc(params DocBlock[] blocks) => new() { Title = "Titel", Blocks = blocks };

    [Fact]
    public void Clean_dehyphenates_except_suspended_compounds_and_capitals()
    {
        var cleaned = Cleaner.Clean(Doc(new DocBlock(BlockKinds.Paragraph, "Ansökan om pro-\njektstöd, kommun-\noch regionförbund och EU-\nmedel.")));
        cleaned.Blocks.Single().Text.ShouldBe("Ansökan om projektstöd, kommun- och regionförbund och EU-medel.");
    }

    [Fact]
    public void Clean_normalises_unicode_and_swedish_paragraph_signs()
    {
        var cleaned = Cleaner.Clean(Doc(new DocBlock(BlockKinds.Paragraph, "Enligt §4 gäller ﬁnans och år ord­et​.")));
        cleaned.Blocks.Single().Text.ShouldBe("Enligt § 4 gäller finans och år ordet.");
    }

    [Fact]
    public void Clean_drops_page_numbers_tables_of_contents_and_tiny_blocks()
    {
        var cleaned = Cleaner.Clean(Doc(
            new DocBlock(BlockKinds.Paragraph, "Sida 3 av 12"),
            new DocBlock(BlockKinds.Paragraph, "1 Inledning ...... 1"), new DocBlock(BlockKinds.Paragraph, "2 Avgifter ...... 3"),
            new DocBlock(BlockKinds.Paragraph, "3 Kontakt ...... 5"), new DocBlock(BlockKinds.Paragraph, "4 Bilagor ...... 7"),
            new DocBlock(BlockKinds.Paragraph, "x"),
            new DocBlock(BlockKinds.Paragraph, "Brödtexten finns kvar.")));

        cleaned.Blocks.Select(b => b.Text).ShouldBe(["Brödtexten finns kvar."]);
        (cleaned.Facts["clean.pageNumbersDropped"], cleaned.Facts["clean.tocDropped"], cleaned.Facts["clean.minimumDropped"]).ShouldBe((1, 4, 1));
    }

    [Fact]
    public void Clean_drops_recurring_edge_lines_on_text_layer_pages()
    {
        var blocks = Enumerable.Range(1, 4).SelectMany(p => new[]
        {
            new DocBlock(BlockKinds.Paragraph, $"Författningssamling 2026:{p}") { Page = p, PageEdge = true },
            new DocBlock(BlockKinds.Paragraph, $"Innehåll på sida {p} som är längre än så.") { Page = p },
        }).ToArray();
        var pages = Enumerable.Range(1, 4).Select(p => new DocPage(p) { Strategy = ParseStrategies.TextLayer }).ToList();

        var cleaned = Cleaner.Clean(new ParsedDocument { Blocks = blocks, Pages = pages });

        cleaned.Blocks.ShouldAllBe(b => !b.Text.StartsWith("Författningssamling", StringComparison.Ordinal));
        cleaned.Facts["clean.furnitureDropped"].ShouldBe(4);
    }

    [Fact]
    public void Split_relevels_layout_headings_by_swedish_numbering()
    {
        DocBlock H(string text) => new(BlockKinds.Heading, text) { Level = 1, LevelSource = LevelSources.Layout };
        var sections = SectionSplitter.Split(new ParsedDocument
        {
            Title = "Taxa",
            Blocks =
            [
                H("Kapitel 1"), H("1.2 Avgifter"), H("1.2.3 Undantag"), new DocBlock(BlockKinds.Paragraph, "Text."), H("§ 4 Betalning"), new DocBlock(BlockKinds.Paragraph, "Text."),
                H("Bilaga 2"), new DocBlock(BlockKinds.Paragraph, "Text."),
                H("1.2.3.4.5 För djupt"), new DocBlock(BlockKinds.Paragraph, "Text."),
                H("Detta är en mening som läsaren tog för en rubrik men som har mer än tolv ord och slutar med punkt."),
            ],
        });

        sections.Where(s => s.Heading is not null).Select(s => $"{s.Level}:{string.Join(" › ", s.HeadingPath)}").ShouldBe(
        [
            "1:Taxa › Kapitel 1", "2:Taxa › Kapitel 1 › 1.2 Avgifter", "3:Taxa › Kapitel 1 › 1.2 Avgifter › 1.2.3 Undantag",
            "4:Taxa › Kapitel 1 › 1.2 Avgifter › 1.2.3 Undantag › § 4 Betalning", "1:Taxa › Bilaga 2",
        ]);
        sections[^1].Blocks.Select(b => b.Text).ShouldContain("**1.2.3.4.5 För djupt**");
        sections[^1].Blocks.ShouldContain(b => b.Kind == BlockKinds.Paragraph && b.Text.StartsWith("Detta är en mening", StringComparison.Ordinal));
    }

    [Fact]
    public void Split_keeps_trusted_levels_and_a_heading_that_repeats_the_title()
    {
        var sections = SectionSplitter.Split(new ParsedDocument
        {
            Title = "Bygglov",
            Blocks = [new DocBlock(BlockKinds.Heading, "Bygglov") { Level = 1, LevelSource = LevelSources.Html }, new DocBlock(BlockKinds.Paragraph, "Intro."),
                new DocBlock(BlockKinds.Heading, "3.1 Avgifter") { Level = 2, LevelSource = LevelSources.Html }, new DocBlock(BlockKinds.Paragraph, "Text.")],
        });
        sections.Select(s => (s.Level, string.Join(" › ", s.HeadingPath))).ShouldBe([(0, "Bygglov"), (2, "Bygglov › 3.1 Avgifter")]);
    }

    [Theory]
    [InlineData("Se t.ex. bilagan. Den gäller bl.a. bygglov. Ring A. Andersson. Enligt § 4. Det gäller.", 4)]
    [InlineData("Avgiften är 300 kr: Den betalas i förskott! Frågor? Ring oss.", 4)]
    public void The_sentence_splitter_respects_swedish_abbreviations(string text, int sentences) =>
        SentenceSplitter.Split(text).Count.ShouldBe(sentences);

    private static ChunkSetDraft Chunk(ParsedDocument document, ChunkOptions? options = null) =>
        Chunker.Chunk(document, options ?? new ChunkOptions(), new DocumentMetadata(["bygglov"], "page", new DateOnly(2026, 1, 15)));

    private static string Sentences(int count, string topic = "bygglovsansökan") =>
        string.Join(' ', Enumerable.Range(1, count).Select(i => $"Mening nummer {i} handlar om {topic} och om hur handläggningen av ärendet går till."));

    [Fact]
    public void Chunks_stay_within_the_budget_and_overlap_whole_sentences_within_a_section()
    {
        var chunks = Chunk(new ParsedDocument { Title = "Bygglov", Breadcrumb = ["Start", "Bo"], Blocks = [new DocBlock(BlockKinds.Paragraph, Sentences(60))] }).Chunks;

        chunks.Count.ShouldBeGreaterThan(3);
        chunks.Take(chunks.Count - 1).ShouldAllBe(c => c.TokenCount <= 400);
        chunks[^1].TokenCount.ShouldBeLessThanOrEqualTo(460); // a merged short tail may reach 115 %
        chunks[0].OverlapChars.ShouldBe(0);
        foreach (var (previous, next) in chunks.Zip(chunks.Skip(1)))
        {
            next.OverlapChars.ShouldBeGreaterThan(0);
            previous.Text.ShouldEndWith(next.Text[..next.OverlapChars]);
            Bgem3TokenCounter.Shared.Count(next.Text.AsSpan(0, next.OverlapChars)).ShouldBeLessThanOrEqualTo(60);
        }
    }

    [Theory]
    [InlineData("Start › Bygga, bo och miljö", "Bygglovsansökan och bygglovsavgift. BYGGLOVSANSÖKAN igen, bygglovsansökan.")]
    [InlineData("", "Förskoleklass/förskolebarn 12skolskjuts\u200Bbarnomsorg e-tjänsten ﬁskevatten 𝐛𝐲𝐠𝐠𝐥𝐨𝐯 Ansökan")]
    [InlineData("Avfall", "Avfallstaxa")]
    public void Compound_parts_memoised_for_a_run_equal_the_decompounder_parts(string prefix, string text)
    {
        var compounds = new CompoundParts(SwedishDecompounder.Default);

        foreach (var _ in Enumerable.Range(0, 2))
        {
            compounds.Of(prefix, text).ShouldBe(SwedishDecompounder.Default.PartsOf(prefix + "\n" + text));
        }
    }

    [Theory]
    [InlineData(400, 15)]
    [InlineData(120, 25)]
    [InlineData(40, 50)]
    public void Reused_token_counts_equal_counting_each_chunk_again(int chunkTokens, int overlapPercent)
    {
        var counter = Bgem3TokenCounter.Shared;
        var document = new ParsedDocument
        {
            Title = "Bygglov",
            Breadcrumb = ["Start", "Bo"],
            Blocks =
            [
                new DocBlock(BlockKinds.Paragraph, Sentences(20)),
                new DocBlock(BlockKinds.Paragraph, "Gör så här."),
                .. Enumerable.Range(1, 12).Select(i => new DocBlock(BlockKinds.ListItem, $"Steg {i}: fyll i uppgifterna om fastigheten och skicka in blanketten.")),
                new DocBlock(BlockKinds.Paragraph, Sentences(7, "ansökan").Normalize(NormalizationForm.FormD)),
                new DocBlock(BlockKinds.ListItem, "Kort."),
            ],
        };

        var result = Chunk(document, new ChunkOptions(chunkTokens, overlapPercent));

        foreach (var chunk in result.Chunks)
        {
            var parts = SwedishDecompounder.Default.PartsOf(chunk.Prefix + "\n" + chunk.Text);
            chunk.TokenCount.ShouldBe(counter.Count(chunk.EmbeddedText));
            chunk.SearchText.ShouldBe(parts.Count == 0 ? chunk.Text : chunk.Text + "\n" + string.Join(' ', parts));
        }

        result.Facts["quality.emptyChunkRatio"].ShouldBe(Math.Round(result.Chunks.Count(c => counter.Count(c.Text) < 20) / (double)result.Chunks.Count, 4));
    }

    [Fact]
    public void Chunk_rows_carry_the_prefix_hash_tokens_search_text_and_metadata()
    {
        var chunk = Chunk(new ParsedDocument { Title = "Bygglov", Breadcrumb = ["Start", "Bygga, bo och miljö", "Bygglov"], Blocks = [new DocBlock(BlockKinds.Paragraph, "Om bygglovsansökan.")] }).Chunks.Single();

        chunk.Prefix.ShouldBe("Start › Bygga, bo och miljö › Bygglov");
        chunk.HeadingPath.ShouldBe("Bygglov");
        chunk.EmbeddedText.ShouldBe("Start › Bygga, bo och miljö › Bygglov\n\nOm bygglovsansökan.");
        chunk.TextHash.ShouldBe(ProcessingJson.Sha256Hex(chunk.EmbeddedText));
        chunk.TokenCount.ShouldBe(Bgem3TokenCounter.Shared.Count(chunk.EmbeddedText));
        chunk.SearchText.ShouldStartWith("Om bygglovsansökan.\n");
        chunk.SearchText.ShouldContain("ansökan");
        (chunk.DocumentType, chunk.SourceModifiedAt, chunk.Tags.Single()).ShouldBe(("page", new DateOnly(2026, 1, 15), "bygglov"));
    }

    [Fact]
    public void A_long_prefix_keeps_the_first_element_and_the_last_two_heading_levels()
    {
        var path = new[] { "Titel", "Första kapitlet om mycket långa rubriker i den kommunala förvaltningen", "Andra nivån med ännu fler ord i rubriken än den första", "Tredje nivån", "Fjärde nivån som också är lång nog" };
        var prefix = Chunker.Prefix(["Kommun och politik", "Kommunens organisation", "Förvaltningar och verksamheter", "Teknik och fastighet"], path);
        prefix.ShouldBe("Kommun och politik › … › Tredje nivån › Fjärde nivån som också är lång nog");
        Bgem3TokenCounter.Shared.Count(prefix).ShouldBeLessThanOrEqualTo(60);
    }

    [Fact]
    public void A_short_tail_is_merged_into_the_previous_chunk()
    {
        // Enough for one full chunk and a few tokens more: the tail is merged (≤ 115 % of the budget).
        var text = Sentences(14);
        var chunks = Chunk(new ParsedDocument { Title = "T", Blocks = [new DocBlock(BlockKinds.Paragraph, text)] }).Chunks;
        chunks.Count.ShouldBe(1);
        chunks[0].TokenCount.ShouldBeLessThanOrEqualTo(460);
    }

    [Fact]
    public void A_chunk_exactly_at_the_budget_stays_whole_when_its_unit_counts_sum_to_more()
    {
        // A list item ending in a zero-width space counts one token more alone than inside the chunk.
        var items = Enumerable.Range(1, 3).Select(i => new DocBlock(BlockKinds.ListItem, Sentences(4, "punkt " + i) + "\u200B")).ToList();
        var document = new ParsedDocument { Title = "Lista", Blocks = items };
        var whole = Chunk(document, new ChunkOptions(ChunkTokens: 2_000)).Chunks.Single();
        var unitSum = Bgem3TokenCounter.Shared.Count(whole.Prefix) + items.Sum(b => Bgem3TokenCounter.Shared.Count("- " + b.Text));
        unitSum.ShouldBeGreaterThan(whole.TokenCount);

        var chunks = Chunk(document, new ChunkOptions(ChunkTokens: whole.TokenCount)).Chunks;

        chunks.Single().Text.ShouldBe(whole.Text);
        Chunk(document, new ChunkOptions(ChunkTokens: whole.TokenCount - 1)).Chunks.Count.ShouldBe(2);
    }

    [Fact]
    public void A_unit_larger_than_the_budget_is_cut_at_whitespace()
    {
        var huge = string.Join(' ', Enumerable.Repeat("ordningsföreskrifterna", 400));
        var chunks = Chunk(new ParsedDocument { Title = "T", Blocks = [new DocBlock(BlockKinds.Paragraph, huge)] }).Chunks;
        chunks.Count.ShouldBeGreaterThan(1);
        chunks.Take(chunks.Count - 1).ShouldAllBe(c => c.TokenCount <= 400);
        chunks.ShouldAllBe(c => c.TokenCount <= 460 && !c.Text.StartsWith(' ') && !c.Text.EndsWith(' '));
    }

    [Fact]
    public void Tables_become_row_groups_of_at_most_20_rows_without_overlap()
    {
        var cells = new List<DocCell> { new(0, 0, 1, 1, true, "Avgift"), new(0, 1, 1, 1, true, "Belopp") };
        for (var r = 1; r <= 45; r++)
        {
            cells.Add(new DocCell(r, 0, 1, 1, false, $"Avgift {r}"));
            cells.Add(new DocCell(r, 1, 1, 1, false, $"{r * 100} kr"));
        }

        var document = new ParsedDocument
        {
            Title = "Taxa",
            Blocks = [new DocBlock(BlockKinds.Paragraph, "Avgifterna framgår av tabellen."), new DocBlock(BlockKinds.Table, "t") { Table = 0 }],
            Tables = [new DocTable(46, 2, 1, cells) { Caption = "Taxa 2026", Sheet = "Blad1" }],
        };
        var chunks = Chunk(document).Chunks;
        var tables = chunks.Where(c => c.IsTable).ToList();

        tables.Count.ShouldBe(3);
        tables.ShouldAllBe(c => c.OverlapChars == 0 && c.Text.StartsWith("Tabell: Taxa 2026\n", StringComparison.Ordinal) && c.HeadingPath == "Taxa › Blad1");
        tables[0].Text.Split('\n')[1].ShouldBe("Avgift: Avgift 1; Belopp: 100 kr");
        tables.Sum(c => c.Text.Split('\n').Length - 1).ShouldBe(45);
        chunks[0].IsTable.ShouldBeFalse(); // the lead-in prose comes first
    }

    [Fact]
    public void Chunks_carry_their_content_kind_and_only_later_row_groups_of_a_large_table_are_marked()
    {
        static DocTable Table(int dataRows)
        {
            var cells = new List<DocCell> { new(0, 0, 1, 1, true, "Tjänst"), new(0, 1, 1, 1, true, "Belopp") };
            for (var r = 1; r <= dataRows; r++)
            {
                cells.Add(new DocCell(r, 0, 1, 1, false, $"Tjänst {r}"));
                cells.Add(new DocCell(r, 1, 1, 1, false, $"{r * 10} kr"));
            }

            return new DocTable(dataRows + 1, 2, 1, cells);
        }

        DocBlock H(string text) => new(BlockKinds.Heading, text) { Level = 2, LevelSource = LevelSources.Html };
        DocBlock Item(string text) => new(BlockKinds.ListItem, text);
        var document = new ParsedDocument
        {
            Title = "Taxa",
            Blocks =
            [
                H("Om taxan"), new DocBlock(BlockKinds.Paragraph, Sentences(6, "taxan")),
                H("Så ansöker du"), new DocBlock(BlockKinds.Paragraph, "Gör så här."),
                .. Enumerable.Range(1, 6).Select(i => Item($"Steg {i}: fyll i uppgifterna om fastigheten och skicka in blanketten till nämnden.")),
                H("Mer om ansökan"), new DocBlock(BlockKinds.Paragraph, Sentences(5, "ansökan")), Item("Se blanketten."),
                new DocBlock(BlockKinds.Paragraph, Sentences(3, "avgiften")),
                H("Stor taxa"), new DocBlock(BlockKinds.Table, "t") { Table = 0 },
                H("Liten taxa"), new DocBlock(BlockKinds.Table, "t") { Table = 1 },
            ],
            Tables = [Table(ChunkOptions.LargeTableRows), Table(45)],
        };
        var chunks = Chunk(document).Chunks;

        var large = chunks.Where(c => c.HeadingPath.EndsWith("Stor taxa", StringComparison.Ordinal)).ToList();
        var small = chunks.Where(c => c.HeadingPath.EndsWith("Liten taxa", StringComparison.Ordinal)).ToList();
        large.Count.ShouldBeGreaterThan(2);
        large.ShouldAllBe(c => c.ContentKind == ChunkContentKinds.Table && c.IsTable);
        large[0].LargeTable.ShouldBeFalse("the first row group of a large table stays eligible");
        large.Skip(1).ShouldAllBe(c => c.LargeTable, "a table over 2,000 rows: every later row group");
        small.Count.ShouldBe(3);
        small.ShouldAllBe(c => c.ContentKind == ChunkContentKinds.Table && !c.LargeTable);
        chunks.Single(c => c.HeadingPath.EndsWith("Så ansöker du", StringComparison.Ordinal)).ContentKind.ShouldBe(ChunkContentKinds.List);
        chunks.Where(c => c.HeadingPath.EndsWith("Mer om ansökan", StringComparison.Ordinal) || c.HeadingPath.EndsWith("Om taxan", StringComparison.Ordinal))
            .ShouldAllBe(c => c.ContentKind == ChunkContentKinds.Text && !c.LargeTable && !c.IsTable, "one short list item in prose is text");
        chunks.Where(c => !c.IsTable).ShouldAllBe(c => !c.LargeTable);
    }

    [Fact]
    public void A_two_column_table_without_header_reads_as_key_value()
    {
        var document = new ParsedDocument
        {
            Title = "Kontakt",
            Blocks = [new DocBlock(BlockKinds.Table, "t") { Table = 0 }],
            Tables = [new DocTable(2, 2, 0, [new(0, 0, 1, 1, false, "Telefon"), new(0, 1, 1, 1, false, "090-16 10 00"), new(1, 0, 1, 1, false, "Öppet"), new(1, 1, 1, 1, false, "8–16")])],
        };
        Chunk(document).Chunks.Single().Text.ShouldBe("Telefon: 090-16 10 00\nÖppet: 8–16");
    }

    [Fact]
    public void Metadata_path_rules_tags_type_and_date_in_order()
    {
        var rules = Metadata.ReadRules(System.Text.Json.JsonDocument.Parse("""
            [ { "match": "/bygga/**", "on": "path", "tags": ["Bygglov", "ogiltig tagg"], "documentType": "page" },
              { "match": "^/protokoll/(?<y>\\d{4})/", "on": "path-regex", "tags": ["protokoll"], "date": "${y}-01-01" },
              { "match": "application/pdf", "on": "mediaType", "tags": ["pdf"] } ]
            """).RootElement);
        var protokoll = Metadata.Apply(rules, new MetadataInput(ProcessingMediaTypes.Pdf) { Url = new Uri("https://x.se/protokoll/2024/ks.pdf") });
        protokoll.ShouldBe(protokoll with { DocumentType = "pdf", SourceModifiedAt = new DateOnly(2024, 1, 1) });
        protokoll.Tags.ShouldBe(["protokoll", "pdf"]);

        var page = Metadata.Apply(rules, new MetadataInput(ProcessingMediaTypes.Html)
        {
            Url = new Uri("https://x.se/bygga/lov/a.html"), VisibleDate = new DateOnly(2026, 1, 15), HttpLastModified = DateTimeOffset.Parse("2026-10-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
        });
        (page.DocumentType, page.SourceModifiedAt, page.Tags.Single()).ShouldBe(("page", new DateOnly(2026, 1, 15), "bygglov"));
        Metadata.Apply([], new MetadataInput(ProcessingMediaTypes.Html) { HttpLastModified = DateTimeOffset.Parse("2026-10-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture) })
            .SourceModifiedAt.ShouldBe(new DateOnly(2026, 10, 1));
    }

    [Fact]
    public void Quality_facts_and_the_observation_score()
    {
        var parsed = QualityFacts.ForParsed(new ParsedDocument
        {
            Blocks = [new DocBlock(BlockKinds.Paragraph, new string('a', 1500)) { Page = 1 }],
            Pages = [new DocPage(1) { Chars = 1500, GarbledScore = 0.1, OcrP10 = 0.8 }, new DocPage(2) { Chars = 0 }],
        });
        (parsed.Facts["quality.chars"], parsed.Facts["quality.emptyPageRatio"], parsed.Facts["quality.ocrConfidence"]).ShouldBe((1500, 0.5, 0.8));
        QualityFacts.Score(parsed).ShouldBe(0.5 * (1 - 0.05) * 0.8 * 0.5, 0.0001);

        var observation = ParseObservations.Create(Guid.CreateVersion7(), "public", new Uri("https://www.umea.se/kommun/protokoll/2024/kf-12.pdf"), null, "pdf", parsed, ProcessingHarness.Now);
        (observation.Domain, observation.PathPattern).ShouldBe(("www.umea.se", "/kommun/protokoll/0/*"));
    }

    [Fact]
    public void A_parsed_document_hash_covers_content_only()
    {
        var a = new ParsedDocument { Title = "T", Blocks = [new DocBlock(BlockKinds.Paragraph, "Text.")] };
        ProcessingJson.OutputHash(a).ShouldBe(ProcessingJson.OutputHash(a with { Blocks = [new DocBlock(BlockKinds.Paragraph, "Text.")] }));
        ProcessingJson.OutputHash(a).ShouldNotBe(ProcessingJson.OutputHash(a with { Title = "U" }));
    }
}
