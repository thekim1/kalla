using System.Text;
using System.Text.Json;
using Kalla.Pipelines.Jobs;
using Kalla.Pipelines.Json;
using Kalla.Pipelines.Schemas;

namespace Kalla.Pipelines.Tests;

/// <summary>
/// The parse-rule commands (P1b item 8; processing design §3.5): <c>edit-parse-rule</c> carries the rule in the shape the
/// worker's catalog reads from <c>ParseProfiles</c>, <c>disable-parse-rule</c> the rule's id; both are refused when a member
/// is missing, unknown or out of range, and the largest edit stays within 4 KB.
/// </summary>
public sealed class ParseRuleContractTests
{
    private const string G = "0193a4c2-7d1e-7b3f-9c2a-1f2e3d4c5b6a";
    private static readonly Guid Id = Guid.Parse(G);

    private static IReadOnlyList<SchemaError> Check<T>(string json) =>
        ContractSchemas.Validator(typeof(T)).Validate(Repository.Json(json));

    private static EditParseRuleCommand Full => new()
    {
        ProfileId = Id,
        DepartmentId = Id,
        Match = new ParseRuleMatch { Domain = "*.umea.se", PathGlob = "/kommun/protokoll/*", MediaType = "application/pdf", TextPattern = @"\bprotokoll\b" },
        Strategy = new ParseRuleStrategy { Ocr = "force", Tables = "fast", ImageScale = 2.5 },
        CleanupRules = new ParseRuleCleanup { MaxLevel = 3, ParagraphItemsAreSections = true, Furniture = ["^Justerare", "^Utdragsbestyrkande"] },
        DecidedBy = "6f1c2d3e-0000-4000-8000-000000000001",
    };

    [Fact]
    public void Every_parse_rule_record_writes_what_its_schema_accepts()
    {
        object[] samples =
        [
            Full,
            new EditParseRuleCommand { ProfileId = Id, Match = new ParseRuleMatch { MediaType = "image/*" } },
            new DisableParseRuleCommand { ProfileId = Id },
            new DisableParseRuleCommand { ProfileId = Id, DecidedBy = "6f1c2d3e-0000-4000-8000-000000000001" },
            new ParseRuleCommandResult { ProfileId = Id, State = ParseRuleState.Active, Revision = "0123456789abcdef", Changed = true, ReprocessSyncs = 3 },
            new ParseRuleCommandResult { ProfileId = Id, State = ParseRuleState.Disabled, Revision = "0123456789abcdef", Changed = false, ReprocessSyncs = 0 },
        ];

        foreach (var sample in samples)
        {
            var json = JsonSerializer.SerializeToElement(sample, sample.GetType(), ContractJson.Options);
            ContractSchemas.Validator(sample.GetType()).Validate(json).ShouldBeEmpty(json.GetRawText());
            JsonSerializer.Serialize(ContractJson.Deserialize(json, sample.GetType()), sample.GetType(), ContractJson.Options).ShouldBe(json.GetRawText());
        }
    }

    [Fact]
    public void The_payloads_have_the_documented_shape()
    {
        ContractJson.Serialize(new EditParseRuleCommand { ProfileId = Id, Match = new ParseRuleMatch { Domain = "www.umea.se", MediaType = "application/pdf" } })
            .ShouldBe($$$"""{"kind":"edit-parse-rule","profileId":"{{{G}}}","match":{"domain":"www.umea.se","mediaType":"application/pdf"},"strategy":{}}""");
        ContractJson.Serialize(Full.CleanupRules).ShouldBe("""{"maxLevel":3,"paragraphItemsAreSections":true,"furniture":["^Justerare","^Utdragsbestyrkande"]}""");
        ContractJson.Serialize(new DisableParseRuleCommand { ProfileId = Id }).ShouldBe($$$"""{"kind":"disable-parse-rule","profileId":"{{{G}}}"}""");
        JobDedupKeys.ParseRule(Id).ShouldBe($"parse-rule:{G}");
    }

    [Theory]
    [InlineData($$$"""{"profileId":"{{{G}}}","match":{"domain":"www.umea.se"}}""")]
    [InlineData($$$"""{"kind":"disable-parse-rule","profileId":"{{{G}}}","match":{"domain":"www.umea.se"}}""")]
    [InlineData($$$"""{"kind":"edit-parse-rule","match":{"domain":"www.umea.se"}}""")]
    [InlineData($$$"""{"kind":"edit-parse-rule","profileId":"{{{G}}}"}""")]
    [InlineData($$$"""{"kind":"edit-parse-rule","profileId":"{{{G}}}","match":{}}""")]
    [InlineData($$$"""{"kind":"edit-parse-rule","profileId":"{{{G}}}","match":{"host":"www.umea.se"}}""")]
    [InlineData($$$"""{"kind":"edit-parse-rule","profileId":"{{{G}}}","match":{"domain":"WWW.Umea.se"}}""")]
    [InlineData($$$"""{"kind":"edit-parse-rule","profileId":"{{{G}}}","match":{"domain":"https://www.umea.se/"}}""")]
    [InlineData($$$"""{"kind":"edit-parse-rule","profileId":"{{{G}}}","match":{"mediaType":"pdf"}}""")]
    [InlineData($$$"""{"kind":"edit-parse-rule","profileId":"{{{G}}}","match":{"pathGlob":""}}""")]
    [InlineData($$$"""{"kind":"edit-parse-rule","profileId":"{{{G}}}","match":{"textPattern":"("}}""")]
    [InlineData($$$"""{"kind":"edit-parse-rule","profileId":"{{{G}}}","match":{"domain":"www.umea.se"},"strategy":{"ocr":"always"}}""")]
    [InlineData($$$"""{"kind":"edit-parse-rule","profileId":"{{{G}}}","match":{"domain":"www.umea.se"},"strategy":{"imageScale":5}}""")]
    [InlineData($$$"""{"kind":"edit-parse-rule","profileId":"{{{G}}}","match":{"domain":"www.umea.se"},"strategy":{"pages":"1-3"}}""")]
    [InlineData($$$"""{"kind":"edit-parse-rule","profileId":"{{{G}}}","match":{"domain":"www.umea.se"},"cleanupRules":{"maxLevel":7}}""")]
    [InlineData($$$"""{"kind":"edit-parse-rule","profileId":"{{{G}}}","match":{"domain":"www.umea.se"},"cleanupRules":{"furniture":[""]}}""")]
    [InlineData($$$"""{"kind":"edit-parse-rule","profileId":"{{{G}}}","match":{"domain":"www.umea.se"},"decidedBy":""}""")]
    public void An_invalid_edit_is_refused(string json) =>
        Check<EditParseRuleCommand>(json).ShouldNotBeEmpty();

    [Fact]
    public void At_most_ten_furniture_patterns()
    {
        static string Payload(int count) =>
            $$$"""{"kind":"edit-parse-rule","profileId":"{{{G}}}","match":{"domain":"www.umea.se"},"cleanupRules":{"furniture":[{{{string.Join(",", Enumerable.Range(0, count).Select(i => $"\"^rad {i}\""))}}}]}}""";

        Check<EditParseRuleCommand>(Payload(10)).ShouldBeEmpty();
        Check<EditParseRuleCommand>(Payload(11)).ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData($$$"""{"profileId":"{{{G}}}"}""")]
    [InlineData($$$"""{"kind":"edit-parse-rule","profileId":"{{{G}}}"}""")]
    [InlineData("""{"kind":"disable-parse-rule"}""")]
    [InlineData($$$"""{"kind":"disable-parse-rule","profileId":"not-a-guid"}""")]
    [InlineData($$$"""{"kind":"disable-parse-rule","profileId":"{{{G}}}","decidedBy":""}""")]
    public void An_invalid_disable_is_refused(string json) =>
        Check<DisableParseRuleCommand>(json).ShouldNotBeEmpty();

    [Fact]
    public void The_largest_edit_stays_within_4_kb()
    {
        var largest = Full with
        {
            Match = new ParseRuleMatch
            {
                Domain = "*." + string.Join('.', Enumerable.Repeat(new string('a', 62), 4)),
                PathGlob = "/" + new string('p', 299),
                MediaType = "application/" + new string('m', 88),
                TextPattern = new string('t', 500),
            },
            CleanupRules = new ParseRuleCleanup { MaxLevel = 6, ParagraphItemsAreSections = true, Furniture = [.. Enumerable.Range(0, 10).Select(_ => new string('f', 150))] },
            DecidedBy = new string('d', 200),
            MinBuild = new string('b', 100),
            Traceparent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
        };

        var json = ContractJson.Serialize(largest);
        Check<EditParseRuleCommand>(json).ShouldBeEmpty();
        Encoding.UTF8.GetByteCount(json).ShouldBeLessThanOrEqualTo(4096);
    }
}
