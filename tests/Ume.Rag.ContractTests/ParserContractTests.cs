using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ume.Rag.Worker.Parsing;

namespace Ume.Rag.ContractTests;

/// <summary>
/// The parser contract parser/v1 (plans/parser/design.md §7, §15.2; P0 checklist #13): the worker's
/// <see cref="ParserClient"/> converts every fixture of tests/fixtures/parser/cases.json against the parser image;
/// responses are validated against contracts/parser/v1/, deserialised strictly (unknown members fail, so drift in either
/// direction shows) and compared with the goldens. Tests that need the image skip unless UME_RAG_TEST_PARSER_IMAGE is set.
/// </summary>
public sealed class ParserContractTests(ParserContainer parser) : IClassFixture<ParserContainer>
{
    private static readonly string[] SchemaNames = ["convert-request", "convert-result", "inspect-result", "info", "error"];

    public static IEnumerable<TheoryDataRow<string>> Conversions => ContractFiles.CaseNames("conversions");

    public static IEnumerable<TheoryDataRow<string>> Errors => ContractFiles.CaseNames("errors");

    [Fact]
    public void Committed_schemas_use_only_what_the_validator_checks()
    {
        foreach (var name in SchemaNames)
        {
            var schema = JsonSchemaSubset.Load(ContractFiles.Schema(name));
            // An empty object reaches every keyword of the root; a NotSupportedException here names a new keyword.
            schema.Validate(ContractFiles.ParseElement("{}")).ShouldNotBeEmpty(name);
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
        parser.RequireImage();
        var ct = TestContext.Current.CancellationToken;

        var json = await parser.Http.GetStringAsync(new Uri("v1/info", UriKind.Relative), ct);
        JsonSchemaSubset.Load(ContractFiles.Schema("info")).Validate(ContractFiles.ParseElement(json)).ShouldBeEmpty();

        var info = await parser.Client().GetInfoAsync(ct);
        info.BuildHash.ShouldStartWith("sha256:");
        info.Capacity.ConvertSlots.ShouldBeGreaterThanOrEqualTo(1);
        info.Formats.Supported.ShouldContain("pdf");
        info.Tesseract.ShouldNotBeNull().Languages.ShouldContain("swe");
    }

    [Theory]
    [MemberData(nameof(Conversions))]
    public async Task Conversion_matches_the_schema_and_the_golden(string name)
    {
        parser.RequireImage();
        var ct = TestContext.Current.CancellationToken;
        var testCase = ContractFiles.Case("conversions", name);
        var optionsJson = testCase["options"]!.DeepClone().AsObject();
        optionsJson["request_id"] = ContractFiles.RequestId(name);
        // Strict: an option the client's records do not know fails here, not silently at the parser.
        var options = optionsJson.Deserialize<ParserConvertOptions>(ParserJson.Create(strict: true))!;

        await using var file = File.OpenRead(ContractFiles.Fixture(testCase["file"]!.GetValue<string>()));
        using var docling = new MemoryStream();
        var result = await parser.Client(strict: true).ConvertAsync(file, options, docling, ct);

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
        parser.RequireImage();
        var ct = TestContext.Current.CancellationToken;
        var testCase = ContractFiles.Case("errors", name);
        var options = testCase["options"]!.DeepClone().AsObject();
        options["request_id"] = ContractFiles.RequestId(name);

        // Raw multipart, so an option the client cannot express (an unknown property) reaches the parser.
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(await File.ReadAllBytesAsync(ContractFiles.Fixture(testCase["file"]!.GetValue<string>()), ct)), "file", "input");
        content.Add(new StringContent(options.ToJsonString(), Encoding.UTF8, "application/json"), "options");
        using var response = await parser.Http.PostAsync(new Uri("v1/convert", UriKind.Relative), content, ct);

        ((int)response.StatusCode).ShouldBe(testCase["status"]!.GetValue<int>());
        var json = await response.Content.ReadAsStringAsync(ct);
        JsonSchemaSubset.Load(ContractFiles.Schema("error")).Validate(ContractFiles.ParseElement(json)).ShouldBeEmpty(json);
        var error = JsonSerializer.Deserialize<ParserErrorResponse>(json, ParserJson.Create(strict: true))!;
        error.Error.Code.ShouldBe(testCase["code"]!.GetValue<string>());
        error.Error.Retry.ShouldBe(testCase["retry"]!.GetValue<string>());
        // Options that fail the schema have no request id the parser could trust.
        var echoesRequestId = testCase["echoes_request_id"]?.GetValue<bool>() ?? true;
        error.RequestId.ShouldBe(echoesRequestId ? ContractFiles.RequestId(name) : null);
        error.Build.BuildHash.ShouldStartWith("sha256:");
    }

    [Fact]
    public async Task Client_throws_the_contract_error()
    {
        parser.RequireImage();
        var ct = TestContext.Current.CancellationToken;
        await using var file = File.OpenRead(ContractFiles.Fixture("corrupt.pdf"));

        var thrown = await Should.ThrowAsync<ParserException>(() =>
            parser.Client().ConvertAsync(file, new ParserConvertOptions("client-corrupt", ".pdf"), null, ct));

        thrown.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        thrown.Error.ShouldNotBeNull().Error.Code.ShouldBe("corrupt");
    }

    [Fact]
    public async Task Inspect_is_not_implemented_in_p0()
    {
        parser.RequireImage();
        var ct = TestContext.Current.CancellationToken;

        using var response = await parser.Http.PostAsync(new Uri("v1/inspect", UriKind.Relative), new MultipartFormDataContent(), ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotImplemented);
        var error = await response.Content.ReadFromJsonAsync<ParserErrorResponse>(ParserJson.Create(strict: true), ct);
        error.ShouldNotBeNull().Error.Code.ShouldBe("not_implemented");
    }
}
