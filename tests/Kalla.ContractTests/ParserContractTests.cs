using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kalla.Worker.Parsing;

namespace Kalla.ContractTests;

/// <summary>
/// The parser contract parser/v1 (plans/parser/design.md §7, §15.2; P0 checklist #13, P1a item 5): the worker's
/// <see cref="ParserClient"/> converts every fixture of tests/fixtures/parser/cases.json against the parser image,
/// inspects the inspect cases and asks <c>/v1/ner</c> for names; responses are validated against contracts/parser/v1/,
/// deserialised strictly (unknown members fail, so drift in either direction shows) and compared with the goldens. Tests
/// that need the image skip unless UME_RAG_TEST_PARSER_IMAGE is set.
/// </summary>
[Collection(ParserImageTests.Name)]
public sealed class ParserContractTests(ParserContainer parser)
{
    private static readonly string[] SchemaNames =
        ["convert-request", "convert-result", "inspect-request", "inspect-result", "ner-request", "ner-result", "info", "error"];

    public static IEnumerable<TheoryDataRow<string>> Conversions => ContractFiles.CaseNames("conversions");

    public static IEnumerable<TheoryDataRow<string>> Errors => ContractFiles.CaseNames("errors");

    public static IEnumerable<TheoryDataRow<string>> Inspections => ContractFiles.CaseNames("inspections");

    [Fact]
    public void Committed_schemas_use_only_what_the_validator_checks()
    {
        foreach (var name in SchemaNames)
        {
            var schema = JsonSchemaSubset.Load(ContractFiles.Schema(name));
            // An object with one unknown member reaches every keyword of the root and fails each schema (a missing
            // required member or a forbidden extra one); a NotSupportedException here names a new keyword.
            schema.Validate(ContractFiles.ParseElement("""{"_probe": null}""")).ShouldNotBeEmpty(name);
        }
    }

    [Fact]
    public void Client_options_match_the_request_schema()
    {
        var schema = JsonSchemaSubset.Load(ContractFiles.Schema("convert-request"));
        var options = new ParserConvertOptions("job-1-r0", ".pdf")
        {
            MediaType = "application/pdf",
            Pages = new ParserPageRange(41, 60),
            Pipeline = ParserPipeline.TextLayer,
            Ocr = new ParserOcrOptions { Mode = ParserOcrMode.Force },
            Tables = new ParserTableOptions { Mode = ParserTableMode.Fast },
            Outputs = new ParserOutputOptions { DoclingJson = false, Markdown = true },
        };

        foreach (var sent in new[] { options, new ParserConvertOptions("job-1-r1", ".pdf") })
        {
            var json = JsonSerializer.Serialize(sent, ParserJson.Create());
            schema.Validate(ContractFiles.ParseElement(json)).ShouldBeEmpty(json);
        }

        var serialized = JsonNode.Parse(JsonSerializer.Serialize(options, ParserJson.Create()))!;
        serialized["pipeline"]!.GetValue<string>().ShouldBe("text_layer");
        serialized["pages"]!["from"]!.GetValue<int>().ShouldBe(41);
    }

    [Fact]
    public void Client_inspect_and_ner_requests_match_their_schemas()
    {
        var json = ParserJson.Create();
        var inspect = JsonSchemaSubset.Load(ContractFiles.Schema("inspect-request"));
        foreach (var sent in new[] { new ParserInspectOptions(), new ParserInspectOptions { RequestId = "job-1-i", FileExt = ".pdf" } })
        {
            var body = JsonSerializer.Serialize(sent, json);
            inspect.Validate(ContractFiles.ParseElement(body)).ShouldBeEmpty(body);
        }

        var ner = new ParserNerRequest("set-1", [new ParserNerText("c1:0", "Anna Lindqvist")])
        {
            Labels = ["person", "person_ambiguous"],
            MinScore = 0.8,
        };
        var nerJson = JsonSerializer.Serialize(ner, json);
        JsonSchemaSubset.Load(ContractFiles.Schema("ner-request")).Validate(ContractFiles.ParseElement(nerJson)).ShouldBeEmpty(nerJson);
        JsonNode.Parse(nerJson)!["min_score"]!.GetValue<double>().ShouldBe(0.8);
    }

    [Fact]
    public void Code_point_offsets_map_to_utf16()
    {
        // The emoji is one code point and two UTF-16 units; the combining mark is a code point of its own.
        const string text = "😀 Kontakta Ma\u0308rta Berg.";
        var entity = new ParserNerEntity("t", 11, 22, "person", 0.9);
        ParserText.Span(text, entity).ShouldBe("Ma\u0308rta Berg");
        ParserText.Utf16Index(text, 2).ShouldBe(3);
        Should.Throw<ArgumentOutOfRangeException>(() => ParserText.Utf16Index(text, 100));
    }

    [Fact]
    public void Validator_reports_violations()
    {
        var schema = JsonSchemaSubset.Load(ContractFiles.Schema("convert-request"));
        var errors = schema.Validate(ContractFiles.ParseElement(
            """{"request_id": "../x", "file_ext": ".pdf", "colour": "blue", "pipeline": "magic", "deadline_ms": 999999}"""));
        errors.Count.ShouldBe(4, string.Join("; ", errors));
    }

    [Fact]
    public async Task Info_matches_the_schema_and_the_client()
    {
        await parser.RequireReadyAsync();
        var ct = TestContext.Current.CancellationToken;

        var json = await parser.Http.GetStringAsync(new Uri("v1/info", UriKind.Relative), ct);
        JsonSchemaSubset.Load(ContractFiles.Schema("info")).Validate(ContractFiles.ParseElement(json)).ShouldBeEmpty();

        var info = await parser.Client().GetInfoAsync(ct);
        info.BuildHash.ShouldStartWith("sha256:");
        info.Capacity.ConvertSlots.ShouldBeGreaterThanOrEqualTo(1);
        info.Formats.Supported.ShouldBe(["pdf", "docx", "pptx", "xlsx", "odt", "ods", "odp", "jpeg", "png", "tiff"]);
        info.Formats.Deferred.ShouldContain("doc");
        info.Tesseract.ShouldNotBeNull().Languages.ShouldContain("swe");
        info.Capacity.InspectSlots.ShouldBe(1);
        info.Ner.ShouldNotBeNull().Repository.ShouldBe("KBLab/bert-base-swedish-cased-ner");
        info.Ner.Labels.ShouldContain("PER");
    }

    [Theory]
    [MemberData(nameof(Conversions))]
    public async Task Conversion_matches_the_schema_and_the_golden(string name)
    {
        await parser.RequireReadyAsync();
        var ct = TestContext.Current.CancellationToken;
        var testCase = ContractFiles.Case("conversions", name);
        var optionsJson = testCase["options"]!.DeepClone().AsObject();
        optionsJson["request_id"] = ContractFiles.RequestId(name);
        // Strict: an option the client's records do not know fails here, not silently at the parser.
        var options = optionsJson.Deserialize<ParserConvertOptions>(ParserJson.Create(strict: true))!;

        using var docling = new MemoryStream();
        var result = await parser.ConvertWhenReadyAsync(ContractFiles.Fixture(testCase["file"]!.GetValue<string>()), options, docling);

        result.Input.Pipeline.ShouldBe(testCase["pipeline"]!.GetValue<string>());
        (docling.Length > 0).ShouldBe(testCase["docling_part"]!.GetValue<bool>());
        if (docling.Length > 0)
        {
            docling.Position = 0;
            await using var gzip = new System.IO.Compression.GZipStream(docling, System.IO.Compression.CompressionMode.Decompress);
            var document = await JsonNode.ParseAsync(gzip, cancellationToken: ct);
            document!["schema_name"]!.GetValue<string>().ShouldBe("DoclingDocument");
        }

        var resultJson = JsonSerializer.Serialize(result, ParserJson.Create());
        JsonSchemaSubset.Load(ContractFiles.Schema("convert-result"))
            .Validate(ContractFiles.ParseElement(resultJson)).ShouldBeEmpty();

        var golden = JsonNode.Parse(await File.ReadAllTextAsync(ContractFiles.Golden(name), ct))!;
        GoldenComparer.Compare(golden, JsonNode.Parse(resultJson)!).ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(Errors))]
    public async Task Error_case_answers_the_contract_error(string name)
    {
        await parser.RequireReadyAsync();
        var ct = TestContext.Current.CancellationToken;
        var testCase = ContractFiles.Case("errors", name);
        var options = testCase["options"]!.DeepClone().AsObject();
        options["request_id"] = ContractFiles.RequestId(name);

        // Raw multipart, so an option the client cannot express (an unknown property) reaches the parser.
        var bytes = await File.ReadAllBytesAsync(ContractFiles.Fixture(testCase["file"]!.GetValue<string>()), ct);
        using var response = await parser.PostConvertWhenReadyAsync(() => new MultipartFormDataContent
        {
            { new ByteArrayContent(bytes), "file", "input" },
            { new StringContent(options.ToJsonString(), Encoding.UTF8, "application/json"), "options" },
        });

        ((int)response.StatusCode).ShouldBe(testCase["status"]!.GetValue<int>());
        var json = await response.Content.ReadAsStringAsync(ct);
        JsonSchemaSubset.Load(ContractFiles.Schema("error")).Validate(ContractFiles.ParseElement(json)).ShouldBeEmpty(json);
        var error = JsonSerializer.Deserialize<ParserErrorResponse>(json, ParserJson.Create(strict: true))!;
        error.Error.Code.ShouldBe(testCase["code"]!.GetValue<string>());
        error.Error.Retry.ShouldBe(testCase["retry"]!.GetValue<string>());
        error.Error.Reason.ShouldBe(testCase["reason"]?.GetValue<string>());
        error.Error.Limit.ShouldBe(testCase["limit"]?.GetValue<string>());
        // Options that fail the schema have no request id the parser could trust.
        var echoesRequestId = testCase["echoes_request_id"]?.GetValue<bool>() ?? true;
        error.RequestId.ShouldBe(echoesRequestId ? ContractFiles.RequestId(name) : null);
        error.Build.BuildHash.ShouldStartWith("sha256:");
    }

    [Fact]
    public async Task Client_throws_the_contract_error()
    {
        await parser.RequireReadyAsync();
        var ct = TestContext.Current.CancellationToken;
        var thrown = await Should.ThrowAsync<ParserException>(() =>
            parser.ConvertWhenReadyAsync(ContractFiles.Fixture("corrupt.pdf"), new ParserConvertOptions("client-corrupt", ".pdf"), null, strict: false));

        thrown.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        thrown.Error.ShouldNotBeNull().Error.Code.ShouldBe("corrupt");
    }

    [Theory]
    [MemberData(nameof(Inspections))]
    public async Task Inspection_matches_the_schema_and_the_case(string name)
    {
        await parser.RequireReadyAsync();
        var ct = TestContext.Current.CancellationToken;
        var testCase = ContractFiles.Case("inspections", name);

        await using var raw = File.OpenRead(ContractFiles.Fixture(testCase["file"]!.GetValue<string>()));
        using var content = new MultipartFormDataContent { { new StreamContent(raw), "file", "input" } };
        using var response = await parser.Http.PostAsync(new Uri("v1/inspect", UriKind.Relative), content, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync(ct);
        JsonSchemaSubset.Load(ContractFiles.Schema("inspect-result")).Validate(ContractFiles.ParseElement(json)).ShouldBeEmpty(json);

        await using var file = File.OpenRead(ContractFiles.Fixture(testCase["file"]!.GetValue<string>()));
        var result = await parser.Client(strict: true).InspectAsync(
            file, new ParserInspectOptions { RequestId = ContractFiles.RequestId(name) }, ct);
        result.RequestId.ShouldBe(ContractFiles.RequestId(name));
        result.Format.ShouldBe(testCase["format"]!.GetValue<string>());
        result.PageCount.ShouldBe(testCase["page_count"]?.GetValue<int>());
        result.Rangeable.ShouldBe(testCase["rangeable"]!.GetValue<bool>());
        result.Refusal.ShouldBe(testCase["refusal"]?.GetValue<string>());
        result.Pages.Count.ShouldBe(testCase["pages"]!.GetValue<int>());
        if (testCase["has_javascript"] is { } javascript)
        {
            result.HasJavascript.ShouldBe(javascript.GetValue<bool>());
        }
    }

    [Fact]
    public async Task Ner_finds_persons_with_code_point_spans()
    {
        await parser.RequireReadyAsync();
        var ct = TestContext.Current.CancellationToken;
        const string text = "😀 Protokollet justerades av Anna Lindqvist och Per Östberg.";
        var request = new ParserNerRequest("contract-ner", [new ParserNerText("c1:0", text), new ParserNerText("c1:1", "")])
        {
            MinScore = 0.5,
        };

        // ner-0 warms up after the conversion slots; it is not part of readiness (design §7.8).
        ParserNerResult? result = null;
        for (var attempt = 0; result is null; attempt++)
        {
            try
            {
                result = await parser.Client(strict: true).NerAsync(request, ct);
            }
            catch (ParserException e) when (e.Error?.Error.Code == "not_ready" && attempt < 120)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }

        result.Entities.Select(e => ParserText.Span(text, e)).ShouldBe(["Anna Lindqvist", "Per Östberg"]);
        result.Entities.ShouldAllBe(e => e.TextId == "c1:0" && e.Label == "person");
        result.Model.PersonLabels.ShouldBe(["PER", "PRS"]);

        var json = JsonSerializer.Serialize(result, ParserJson.Create());
        JsonSchemaSubset.Load(ContractFiles.Schema("ner-result")).Validate(ContractFiles.ParseElement(json)).ShouldBeEmpty(json);

        var refused = await Should.ThrowAsync<ParserException>(() =>
            parser.Client().NerAsync(new ParserNerRequest("contract-ner-empty", []), ct));
        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        refused.Error.ShouldNotBeNull().Error.Code.ShouldBe("invalid_request");
    }
}
