using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using Kalla.Pipelines.Json;
using Kalla.Pipelines.Schemas;
using Kalla.Pipelines.SiteProfiles;
using Kalla.Worker.Connectors.Urls;
using Kalla.Worker.Processing;
using Kalla.Worker.Processing.Chunking;
using Kalla.Worker.Processing.Detect;
using Kalla.Worker.Processing.Html;
using Kalla.Worker.Processing.Pii;

namespace Kalla.Worker.Tests.Processing;

/// <summary>
/// The umea.se regression corpus (connectors design §5.6, §18; processing design §17; P1 README §3 item 8): ≈ 20 pages
/// saved once from www.umea.se (<c>tests/fixtures/umea/MANIFEST.csv</c>: URL, fetch time, SHA-256, sitemap <c>lastmod</c>)
/// extracted with the hand-checked umea.se profile (<c>contracts/site-profiles/v1/www.umea.se.profile.json</c>). Each page's
/// extraction is a golden in <c>tests/fixtures/umea/golden/</c> (<c>UME_RAG_UPDATE_GOLDENS=1</c> rewrites them; read the
/// diff by hand). The acceptance page (simplification pass §13.2) is checked against its hand-written main text.
/// </summary>
public sealed class UmeaCorpusTests
{
    public const string Host = "www.umea.se";

    public static readonly string Pages = Path.Combine(Fixtures.Umea, "pages");

    public static readonly string Goldens = Path.Combine(Fixtures.Umea, "golden");

    public static readonly string ProfilePath = Path.Combine(Fixtures.Root, "contracts", "site-profiles", "v1", "www.umea.se.profile.json");

    public const string AcceptancePage = "acceptance-digital-omstallning-och-it.html";

    /// <summary>The acceptance page's main text, block by block, read off the live page on 2026-10-31.</summary>
    public static readonly string[] AcceptanceText =
    [
        "Digital omställning och IT",
        "Digital omställning och IT har i uppdrag att stärka Umeå kommuns förmåga till en digital omställning som är hållbar, säker och skapar nytta för medborgare och verksamheter. Genom att tillhandahålla, driva och samordna strategier, IT-tjänster och digitala lösningar möjliggör vi utveckling och effektivitet i hela kommunen.\nVerksamheten arbetar nära kommunens olika delar för att förstå behov, stödja förändring och omsätta teknik till praktisk nytta. Vi stöttar verksamheterna i att tillsammans utföra och utveckla kärnuppdragen, och lyckas då vi arbetar med verksamheters mål på ett sätt som skapar nytta för hela kommunen.",
        "Digital omställning och IT är en strategisk möjliggörare för innovation, samverkan och långsiktig hållbarhet i Umeå kommun.",
        "Du kan läsa mer om Umeå kommuns arbete med digital omställning på umea.se/digitalomstallning.",
        "Kontakt\nDigital omställning och IT\n090-16 11 34 (support)\nBesöksadress\nSkolgatan 31\nStadshuset, norra flygeln\nPostadress\nUmeå kommun Digital omställning och IT\n901 84 Umeå\nTelefonsupport är öppet vardagar klockan 8–17, bokade besök 8.00–16.30, lunchstängt klockan 12–13.",
        "Kontakt\nMattias Wallmark\nverksamhetschef, Digital omställning och IT\n090-16 11 52\nmattias.wallmark@umea.se",
    ];

    /// <summary>The breadcrumb on the live page: §13.2's third crumb reads "Förvaltningar, verksamheter" there.</summary>
    public static readonly string[] AcceptanceBreadcrumb = ["Kommun och politik", "Kommunens organisation", "Förvaltningar, verksamheter", "Teknik och fastighet"];

    public static IReadOnlyList<CorpusPage> Manifest { get; } = ReadManifest();

    public static TheoryData<string> HtmlPages => [.. Manifest.Where(p => p.MediaType == "text/html").Select(p => p.File)];

    /// <summary>Full URL and node-id URL of one page each (connectors §3.1 rule 10 verification).</summary>
    public static TheoryData<string, string> NodeIdPairs => new()
    {
        { AcceptancePage, "node-id-digital-omstallning-och-it.html" },
        { "service-hamtning.html", "node-id-hamtning.html" },
        { "news-budget-2027.html", "node-id-news-budget-2027.html" },
    };

    public static SiteProfile UmeaProfile { get; } = ReadProfile();

    private static readonly HtmlExtractor Extractor = new(new HtmlPresets());

    [Fact]
    public void The_corpus_files_match_their_manifest()
    {
        System.IO.Directory.EnumerateFiles(Pages).Select(Path.GetFileName).Order(StringComparer.Ordinal)
            .ShouldBe(Manifest.Select(p => p.File).Order(StringComparer.Ordinal));
        foreach (var page in Manifest)
        {
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(Pages, page.File)))).ShouldBe(page.Sha256, page.File);
        }
    }

    [Fact]
    public void The_acceptance_page_extracts_to_exactly_its_main_text_breadcrumb_and_date()
    {
        AcceptanceDifferences(UmeaProfile).ShouldBeEmpty();
        AcceptanceDifferences(null).ShouldBeEmpty("the Sitevision preset alone, as for a host without a profile");
    }

    [Fact]
    public void Negative_proof_a_changed_preset_selector_fails_the_acceptance_check()
    {
        var preset = SitevisionPreset.Profile;

        // Without the toolbar selector, the date line, the share widget's labels and the short URL come back as text.
        AcceptanceDifferences(preset with { Boilerplate = new SiteProfileBoilerplate() }).ShouldContain(d => d.StartsWith("main text", StringComparison.Ordinal));

        // Without the menu portlet as a link block, the child-link list's heading "Undersidor" comes back.
        AcceptanceDifferences(preset with { LinkBlocks = [] }).ShouldContain(d => d.Contains("Undersidor", StringComparison.Ordinal));

        // The whole breadcrumb instead of the crumbs after the first: "Startsida" comes back.
        AcceptanceDifferences(preset with { Breadcrumb = "nav[aria-label=\"Brödsmulor\"]" }).ShouldContain(d => d.Contains("Startsida", StringComparison.Ordinal));

        // Another date wording finds no date.
        AcceptanceDifferences(preset with { DatePattern = @"Senast uppdaterad (?<d>\d{4}-\d{2}-\d{2})" }).ShouldBe(["date: "]);
    }

    [Fact]
    public async Task The_acceptance_page_chunks_carry_its_breadcrumb_and_date()
    {
        var input = new ProcessingInput(
            new RawContent(await File.ReadAllBytesAsync(Path.Combine(Pages, AcceptancePage), TestContext.Current.CancellationToken), "text/html;charset=UTF-8")
            {
                Url = new Uri(Url(AcceptancePage)),
            },
            new PersonalDataContext("public") { OwnDomains = ["umea.se"] })
        {
            SiteProfile = new HtmlProfileVersion(Host, 1, UmeaProfile),
            RequestId = "acceptance",
        };

        var result = await new ProcessingHarness().Processor.RunAsync(input, TestContext.Current.CancellationToken);

        result.Metadata.SourceModifiedAt.ShouldBe(new DateOnly(2026, 1, 15));
        result.Chunks.Chunks.ShouldNotBeEmpty();
        result.Chunks.Chunks.ShouldAllBe(c => c.Prefix.StartsWith(string.Join(ChunkText.PathSeparator, AcceptanceBreadcrumb), StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(HtmlPages))]
    public void A_corpus_page_gives_its_golden_extraction(string file)
    {
        var extraction = Extract(file, UmeaProfile);
        extraction.Outcome.ShouldBe(HtmlExtraction.ByProfile);
        Golden.Check(Path.Combine(Goldens, Path.ChangeExtension(file, ".json")), new JsonObject
        {
            ["versions"] = new JsonObject { ["parse.html"] = HtmlExtractor.Version, ["preset.sitevision"] = SitevisionPreset.Version },
            ["url"] = Manifest.Single(p => p.File == file).Url,
            ["outcome"] = extraction.Outcome,
            ["document"] = JsonSerializer.SerializeToNode(extraction.Document, ProcessingJson.Options),
        });
    }

    [Theory]
    [MemberData(nameof(HtmlPages))]
    public void The_sitevision_preset_and_the_umea_profile_extract_the_same_content(string file)
    {
        var byPreset = Extract(file, null);
        var byProfile = Extract(file, UmeaProfile);

        byPreset.Outcome.ShouldBe(HtmlExtraction.ByPreset);
        byPreset.Document.Strategy.ShouldBe($"parse=html(preset={SitevisionPreset.Name}@{SitevisionPreset.Version})");
        Content(byPreset.Document).ShouldBe(Content(byProfile.Document));
    }

    [Fact]
    public void The_umea_profile_differs_from_the_preset_only_in_hand_checked_host_values()
    {
        // Extraction fields equal the preset's; the host adds the verified identity rule and the robots.txt parameters.
        var host = UmeaProfile with { IdentityRule = UmeaProfile.IdentityRule! with { Verified = false }, IgnoreQueryParams = SitevisionPreset.Profile.IgnoreQueryParams };
        ContractJson.Serialize(host).ShouldBe(ContractJson.Serialize(SitevisionPreset.Profile));
        UmeaProfile.IdentityRule.Verified.ShouldBeTrue();
        SitevisionPreset.Profile.IgnoreQueryParams.Except(UmeaProfile.IgnoreQueryParams).ShouldBeEmpty();
        UmeaProfile.LastmodReliable.ShouldBeFalse("set only from the recorded 500-URL lastmod sample (to-verify #170)");
    }

    [Fact]
    public void The_sitevision_preset_is_a_valid_site_profile() =>
        ContractSchemas.Validator(typeof(SiteProfile)).Validate(ContractJson.ToElement(SitevisionPreset.Profile)).ShouldBeEmpty();

    [Theory]
    [MemberData(nameof(HtmlPages))]
    public void Every_corpus_page_is_recognised_as_sitevision(string file)
    {
        using var document = new HtmlParser().ParseDocument(File.ReadAllText(Path.Combine(Pages, file)));
        SitevisionPreset.Recognises(document).ShouldBeTrue();
    }

    [Fact]
    public void A_page_without_sitevision_markers_gets_the_generic_preset()
    {
        var html = File.ReadAllBytes(Path.Combine(Fixtures.Processing, "golden", "html-page", "input.html"));
        Extractor.Extract(html, null).Document.Strategy.ShouldBe("parse=html(preset=generic@1)");
    }

    [Theory]
    [MemberData(nameof(NodeIdPairs))]
    public void A_node_id_url_serves_the_same_document_as_the_full_url(string full, string nodeId)
    {
        var a = Extract(full, UmeaProfile).Document;
        var b = Extract(nodeId, UmeaProfile).Document;
        ProcessingJson.OutputHash(b).ShouldBe(ProcessingJson.OutputHash(a));

        var options = new CanonicalizerOptions { IdentityRule = IdentityRule.SitevisionNodeId };
        UrlCanonicalizer.Canonicalize(Url(full), options).Value.ShouldBe(UrlCanonicalizer.Canonicalize(Url(nodeId), options).Value);
        Url(nodeId).ShouldBe($"https://{Host}{Rewrite(new Uri(Url(full)).AbsolutePath)}");
    }

    [Fact]
    public void The_profiles_identity_rule_maps_every_corpus_url_as_the_canonicaliser_does()
    {
        var options = new CanonicalizerOptions { IdentityRule = IdentityRule.SitevisionNodeId };
        foreach (var page in Manifest)
        {
            var path = new Uri(page.Url).AbsolutePath;
            new Uri(UrlCanonicalizer.Canonicalize(page.Url, options).Value!).AbsolutePath.ShouldBe(Rewrite(path), page.File);
        }
    }

    [Theory]
    [MemberData(nameof(HtmlPages))]
    public void The_visible_date_equals_the_sitemap_lastmod_day(string file)
    {
        var page = Manifest.Single(p => p.File == file);
        var date = Extract(file, UmeaProfile).Document.VisibleDate;
        if (page.SitemapLastmod is not { } lastmod || date is null)
        {
            // Node-id URLs are not in the sitemap; landing and archive pages show no date.
            return;
        }

        date.ShouldBe(DateOnly.FromDateTime(lastmod.DateTime));
    }

    private static List<string> AcceptanceDifferences(SiteProfile? profile)
    {
        var document = Extract(AcceptancePage, profile).Document;
        var differences = new List<string>();
        var text = document.Blocks.Select(b => b.Text).ToList();
        if (!text.SequenceEqual(AcceptanceText))
        {
            differences.Add("main text: " + string.Join(" ¶ ", text));
        }

        if (!document.Breadcrumb.SequenceEqual(AcceptanceBreadcrumb))
        {
            differences.Add("breadcrumb: " + string.Join(" › ", document.Breadcrumb));
        }

        if (document.VisibleDate != new DateOnly(2026, 1, 15))
        {
            differences.Add($"date: {document.VisibleDate}");
        }

        if (document.Title != "Digital omställning och IT")
        {
            differences.Add($"title: {document.Title}");
        }

        return differences;
    }

    private static HtmlExtraction Extract(string file, SiteProfile? profile) =>
        Extractor.Extract(File.ReadAllBytes(Path.Combine(Pages, file)), profile is null ? null : new HtmlProfileVersion(Host, 1, profile));

    private static string Content(ParsedDocument document) =>
        JsonSerializer.Serialize(document with { Strategy = null }, ProcessingJson.Options);

    private static string Url(string file) => Manifest.Single(p => p.File == file).Url;

    private static string Rewrite(string path)
    {
        var rule = UmeaProfile.IdentityRule!;
        return Regex.Replace(path, rule.Pattern, rule.Replacement, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    private static SiteProfile ReadProfile()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(ProfilePath));
        return ContractJson.Deserialize<SiteProfile>(json.RootElement);
    }

    private static List<CorpusPage> ReadManifest()
    {
        var lines = File.ReadAllLines(Path.Combine(Fixtures.Umea, "MANIFEST.csv"), Encoding.UTF8);
        var header = Csv(lines[0]);
        return
        [
            .. lines.Skip(1).Where(l => l.Length > 0).Select(Csv).Select(f => f.Zip(header).ToDictionary(p => p.Second, p => p.First, StringComparer.Ordinal))
                .Select(f => new CorpusPage(
                    f["file"], f["url"], DateTimeOffset.Parse(f["fetched_at"], CultureInfo.InvariantCulture), f["sha256"], f["media_type"],
                    f["sitemap_lastmod"] is { Length: > 0 } lastmod ? DateTimeOffset.Parse(lastmod, CultureInfo.InvariantCulture) : null, f["kind"])),
        ];
    }

    /// <summary>One CSV line (RFC 4180 quoting, no line breaks inside fields).</summary>
    private static List<string> Csv(string line)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else
            {
                field.Append(c);
            }
        }

        fields.Add(field.ToString());
        return fields;
    }

    public sealed record CorpusPage(string File, string Url, DateTimeOffset FetchedAt, string Sha256, string MediaType, DateTimeOffset? SitemapLastmod, string Kind);
}
