using System.Net;
using System.Text;
using System.Text.Json;
using Ume.Rag.Worker.Parsing;
using Ume.Rag.Worker.Processing;
using Ume.Rag.Worker.Processing.Detect;
using Ume.Rag.Worker.Processing.Parse;
using Ume.Rag.Worker.Processing.Pii;

namespace Ume.Rag.Worker.Tests.Processing;

/// <summary>The inspect and NER adapters over <see cref="ParserClient"/> against a fake parser (contract JSON shapes).</summary>
public sealed class ParserAdaptersTests
{
    private static readonly ParserBuild Build = new("1.0.0", "2.145.0", "sha256:x");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ParserClient Client(Func<HttpRequestMessage, HttpResponseMessage> answer, List<(string Path, string Body)> seen) =>
        new(new HttpClient(new Stub(answer, seen)) { BaseAddress = new Uri("http://parser/") });

    private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(value, ParserJson.Create()), Encoding.UTF8, "application/json") };

    private static ParserInfo Info(bool nerReady) => new(
        new ParserVersion("1.0.0", "abc"), "3.12", new Dictionary<string, string>(), new ParserPdfium(7000, "pypi", []), null, [], null,
        new ParserLimits(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 64, 4000, 100_000, 15_000), new ParserFormats(["pdf"], []), "sha256:x",
        new ParserCapacity(1, 1, 1, nerReady));

    [Fact]
    public async Task The_inspector_maps_the_page_signals_encryption_and_refusal()
    {
        var seen = new List<(string Path, string Body)>();
        var result = new ParserInspectResult("parser/v1", null, "pdf", "application/pdf", 2, true, "owner_only", false, false, false,
            [new ParserInspectPage(1, 595, 842, 0, 1500, 3, 0.02, 0.1, 12, null), new ParserInspectPage(2, 595, 842, 0, null, null, null, 0.9, null, null)],
            null, [], new ParserInspectTimings(5, 0, 5), Build);
        var inspector = new ParserInspector(Client(_ => Json(result), seen));

        var facts = await inspector.InspectAsync("%PDF-1.7"u8.ToArray(), "application/pdf", ".pdf", Ct);

        (facts.Format, facts.PageCount, facts.Rangeable, facts.Encrypted, facts.Refusal).ShouldBe(("pdf", 2, true, "owner_only", (string?)null));
        facts.Pages[0].ShouldBe(new InspectPageSignals(1) { TextChars = 1500, BadChars = 3, GarbledScore = 0.02, ImageCoverage = 0.1, PathObjects = 12 });
        facts.Pages[1].TextChars.ShouldBeNull();
        seen.Single().Path.ShouldBe("/v1/inspect");
        seen.Single().Body.ShouldContain("\"file_ext\":\".pdf\"");
    }

    [Fact]
    public async Task An_inspect_error_reaches_detect_as_the_contract_error()
    {
        var error = new ParserErrorResponse(new ParserErrorBody("busy", "No slot.", "later", null, null, null), null, Build);
        var inspector = new ParserInspector(Client(_ => Json(error, HttpStatusCode.ServiceUnavailable), []));

        var thrown = await Should.ThrowAsync<ParserException>(() => inspector.InspectAsync(new byte[1], "application/pdf", ".pdf", Ct));
        thrown.Error!.Error.Code.ShouldBe("busy");
    }

    [Fact]
    public async Task The_ner_client_sends_the_texts_and_maps_entities_and_the_model()
    {
        var seen = new List<(string Path, string Body)>();
        var answer = new ParserNerResult("parser/v1", "r", [new ParserNerEntity("c0.0", 2, 16, "person", 0.97)],
            new ParserNerModel("KBLab/bert-base-swedish-cased-ner", "f3c83af8196b812028fc08e1971a2d289d0a3f1d", "x", ["PER"], ["PER", "PRS"], false),
            new ParserNerTimings(10, 0, 9), Build);
        var ner = new ParserNerClient(Client(_ => Json(answer), seen));

        var result = await ner.RecognizeAsync([new NerText("c0.0", "😀 Anna Lindqvist ringer.")], 0.8, Ct);

        result.Model.ShouldBe("bert-base-swedish-cased-ner@f3c83af");
        result.Entities.Single().ShouldBe(new NerEntity("c0.0", 2, 16, "person", 0.97));
        seen.Single().Path.ShouldBe("/v1/ner");
        using var body = JsonDocument.Parse(seen.Single().Body);
        body.RootElement.GetProperty("min_score").GetDouble().ShouldBe(0.8);
        body.RootElement.GetProperty("texts")[0].GetProperty("id").GetString().ShouldBe("c0.0");
        body.RootElement.GetProperty("labels").EnumerateArray().Select(l => l.GetString()).ShouldBe(["person"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Readiness_comes_from_info(bool ready) =>
        (await new ParserNerClient(Client(_ => Json(Info(ready)), [])).IsReadyAsync(Ct)).ShouldBe(ready);

    [Fact]
    public async Task An_unreachable_parser_is_not_ready() =>
        (await new ParserNerClient(Client(_ => throw new HttpRequestException("down"), [])).IsReadyAsync(Ct)).ShouldBeFalse();

    [Fact]
    public async Task A_busy_ner_requeues_the_personal_data_stage_without_an_attempt()
    {
        var error = new ParserErrorResponse(new ParserErrorBody("busy", "No slot.", "later", null, null, null), "r", Build);
        var parser = Client(r => r.RequestUri!.AbsolutePath == "/v1/info" ? Json(Info(true)) : Json(error, HttpStatusCode.ServiceUnavailable), []);
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(ProcessingHarness.Now);
        var stage = new PersonalDataStage(new PiiPatterns(clock), new ParserNerClient(parser), new EmptyGazetteer(), clock);
        var text = string.Join('\n', Enumerable.Range(1, 5).Select(i => $"Anna Person{i}, 070-31{i} 22 33"));
        var draft = new ChunkSetDraft
        {
            Chunks = [new ChunkDraft { Ordinal = 0, Prefix = "P", HeadingPath = "P", Text = text, SearchText = text, OverlapChars = 0, TextHash = "h", TokenCount = 10, DocumentType = "pdf" }],
        };

        var thrown = await Should.ThrowAsync<StageTransientException>(() => stage.RunAsync(draft, new PersonalDataContext(Domain.Classification.Public), Ct));
        (thrown.Code, thrown.CountsAttempt).ShouldBe(("parser.ner_busy", false));
    }

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> answer, List<(string Path, string Body)> seen) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            seen.Add((request.RequestUri!.AbsolutePath, request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            return answer(request);
        }
    }
}
