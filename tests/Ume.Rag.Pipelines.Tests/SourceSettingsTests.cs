using System.Text.Json;
using Ume.Rag.Pipelines.Schemas;
using Ume.Rag.Pipelines.Sources;

namespace Ume.Rag.Pipelines.Tests;

/// <summary>Connectors design §12.3: settings schemas with <c>x-effect</c> and the per-tag hashes stored in <c>Runs.SettingsHashes</c>.</summary>
public sealed class SourceSettingsTests
{
    private const string Umea = """
        { "startUrls": ["https://www.umea.se/"], "sitemaps": [], "extraHosts": [],
          "include": ["www.umea.se/**"], "exclude": ["**/sok?*", "**?*print=*"], "demote": [], "maxDepth": 10, "maxPages": 20000,
          "ignoreQueryParams": [], "directoryIndexNames": [], "noiseSelectors": [],
          "conditionalGet": true, "revalidateEvery": 4, "crawlWindow": "22:00-06:00", "incrementalSchedule": null }
        """;

    [Fact]
    public void The_connectors_12_3_example_validates_and_binds()
    {
        ContractSchemas.Validator(typeof(CrawlSettings)).Validate(Repository.Json(Umea)).ShouldBeEmpty();
        var settings = (CrawlSettings)SourceSettings.Bind(SourceTypes.Crawl, Repository.Json(Umea));
        settings.MaxPages.ShouldBe(20000);
        settings.TimeoutSeconds.ShouldBe(30);
    }

    [Theory]
    [InlineData(SourceTypes.Crawl, """{"startUrls":[]}""")]
    [InlineData(SourceTypes.Crawl, """{"startUrls":["ftp://www.umea.se/"]}""")]
    [InlineData(SourceTypes.Crawl, """{"startUrls":["https://www.umea.se/"],"demote":[{"pattern":"**/protokoll/**","factor":1}]}""")]
    [InlineData(SourceTypes.Crawl, """{"startUrls":["https://www.umea.se/"],"crawlWindow":"22-06"}""")]
    [InlineData(SourceTypes.Crawl, """{"startUrls":["https://www.umea.se/"],"render":true}""")]
    [InlineData(SourceTypes.Crawl, """{"startUrls":["https://www.umea.se/"],"extraHosts":["Umea.SE"]}""")]
    [InlineData(SourceTypes.Crawl, """{"startUrls":["https://www.umea.se/"],"incrementalSchedule":"daily"}""")]
    [InlineData(SourceTypes.Crawl, """{"startUrls":["https://www.umea.se/"],"incrementalSchedule":"0 3 * *"}""")]
    [InlineData(SourceTypes.UrlList, """{"urls":["https://www.umea.se/"],"incrementalSchedule":"0 3 * * *"}""")]
    [InlineData(SourceTypes.UrlList, """{"urls":["https://www.umea.se/"],"revalidateEvery":0}""")]
    [InlineData(SourceTypes.Upload, """{"maxFileSizeMb":51}""")]
    [InlineData(SourceTypes.Crawl, """{"startUrls":["https://www.umea.se/"],"piiMode":"block"}""")]
    [InlineData(SourceTypes.UrlList, """{"urls":["https://www.umea.se/"],"piiMode":"Report"}""")]
    public void Invalid_settings_fail_the_schema(string sourceType, string json) =>
        ContractSchemas.Validator(SourceSettings.SettingsType(sourceType)).Validate(Repository.Json(json)).ShouldNotBeEmpty();

    [Fact]
    public void An_unknown_member_does_not_bind_either() =>
        Should.Throw<JsonException>(() => SourceSettings.Bind(SourceTypes.Upload, Repository.Json("""{"maxFileSizeMb":10,"virusScan":false}""")));

    [Theory]
    [InlineData(SourceTypes.UrlList)]
    [InlineData(SourceTypes.Crawl)]
    [InlineData(SourceTypes.Upload)]
    public void Every_member_has_an_effect_tag_in_the_schema(string sourceType)
    {
        var effects = SourceSettings.Effects(sourceType);
        var schema = ContractSchemas.For(SourceSettings.SettingsType(sourceType)).Export();
        foreach (var (name, property) in schema["properties"]!.AsObject())
        {
            property!["x-effect"]!.GetValue<string>().ShouldBe(effects[name].ToString().ToLowerInvariant(), name);
        }
    }

    [Theory]
    [InlineData("exclude", """["**/sok?*"]""", SettingEffect.Scope)]
    [InlineData("include", """["www.umea.se/kommun/**"]""", SettingEffect.Scope)]
    [InlineData("maxPages", "25000", SettingEffect.Scope)]
    [InlineData("ignoreQueryParams", """["sv.state"]""", SettingEffect.Identity)]
    [InlineData("directoryIndexNames", """["index.html"]""", SettingEffect.Identity)]
    [InlineData("noiseSelectors", """["#cookie-banner"]""", SettingEffect.Behaviour)]
    [InlineData("timeoutSeconds", "60", SettingEffect.Behaviour)]
    [InlineData("demote", """[{"pattern":"**/protokoll/2019/**"}]""", SettingEffect.Cosmetic)]
    [InlineData("crawlWindow", "\"23:00-05:00\"", SettingEffect.Cosmetic)]
    [InlineData("incrementalSchedule", "\"0 3 * * *\"", SettingEffect.Cosmetic)]
    [InlineData("revalidateEvery", "8", SettingEffect.Behaviour)]
    [InlineData("piiMode", "\"report\"", SettingEffect.Cosmetic)]
    [InlineData("fetchMode", "\"browser\"", SettingEffect.Behaviour)]
    [InlineData("readySelector", "\"#app article\"", SettingEffect.Behaviour)]
    [InlineData("scrollToLoad", "true", SettingEffect.Behaviour)]
    public void A_change_moves_exactly_its_tag(string member, string value, SettingEffect effect)
    {
        var before = Repository.Json(Umea);
        var node = System.Text.Json.Nodes.JsonNode.Parse(Umea)!.AsObject();
        node[member] = System.Text.Json.Nodes.JsonNode.Parse(value);
        var after = JsonSerializer.SerializeToElement(node);

        SourceSettings.Changed(SourceTypes.Crawl, before, after).ShouldBe([effect]);
        var old = SourceSettings.Hashes(SourceTypes.Crawl, before);
        var current = SourceSettings.Hashes(SourceTypes.Crawl, after);
        (old.Identity != current.Identity).ShouldBe(effect == SettingEffect.Identity);
        (old.Scope != current.Scope).ShouldBe(effect == SettingEffect.Scope);
        (old.Behaviour != current.Behaviour).ShouldBe(effect == SettingEffect.Behaviour);
    }

    [Theory]
    [InlineData(SourceTypes.Crawl, "fetchMode", "\"browser\"", true)]
    [InlineData(SourceTypes.Crawl, "readySelector", "\"#app article\"", true)]
    [InlineData(SourceTypes.Crawl, "scrollToLoad", "true", true)]
    [InlineData(SourceTypes.UrlList, "fetchMode", "\"browser\"", true)]
    [InlineData(SourceTypes.Crawl, "noiseSelectors", "[\"#cookie-banner\"]", false)]
    [InlineData(SourceTypes.UrlList, "conditionalGet", "false", false)]
    public void Only_the_fetch_settings_move_the_fetch_hash(string sourceType, string member, string value, bool moves)
    {
        var json = sourceType == SourceTypes.Crawl ? """{"startUrls":["https://www.umea.se/"]}""" : """{"urls":["https://www.umea.se/"]}""";
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        node[member] = System.Text.Json.Nodes.JsonNode.Parse(value);

        var after = JsonSerializer.SerializeToElement(node);
        ContractSchemas.Validator(SourceSettings.SettingsType(sourceType)).Validate(after).ShouldBeEmpty();
        (SourceSettings.Hashes(sourceType, Repository.Json(json)).Fetch != SourceSettings.Hashes(sourceType, after).Fetch).ShouldBe(moves);
    }

    [Theory]
    [InlineData("""{"startUrls":["https://www.umea.se/"],"fetchMode":"chrome"}""")]
    [InlineData("""{"startUrls":["https://www.umea.se/"],"readySelector":""}""")]
    public void Invalid_fetch_settings_fail_the_schema(string json) =>
        ContractSchemas.Validator(typeof(CrawlSettings)).Validate(Repository.Json(json)).ShouldNotBeEmpty();

    [Fact]
    public void Settings_without_the_p1b_members_still_validate_and_hash_alike()
    {
        const string p1a = """{"startUrls":["https://www.umea.se/"],"demote":[{"pattern":"**/protokoll/**","factor":0.3}],"revalidateEvery":4}""";
        ContractSchemas.Validator(typeof(CrawlSettings)).Validate(Repository.Json(p1a)).ShouldBeEmpty();
        var withSchedule = p1a[..^1] + ""","incrementalSchedule":"0 3 * * *"}""";
        ((CrawlSettings)SourceSettings.Bind(SourceTypes.Crawl, Repository.Json(withSchedule))).IncrementalSchedule.ShouldBe("0 3 * * *");
        SourceSettings.Hashes(SourceTypes.Crawl, Repository.Json(withSchedule)).ShouldBe(SourceSettings.Hashes(SourceTypes.Crawl, Repository.Json(p1a)));
    }

    [Fact]
    public void An_omitted_member_hashes_as_its_default()
    {
        SourceSettings.Hashes(SourceTypes.Crawl, Repository.Json("""{"startUrls":["https://www.umea.se/"]}"""))
            .ShouldBe(SourceSettings.Hashes(SourceTypes.Crawl, Repository.Json("""
                {"startUrls":["https://www.umea.se/"],"maxDepth":10,"maxPages":10000,"conditionalGet":true,"revalidateEvery":4,
                 "exclude":["**/sok?*", "**/search?*", "**?*sort=*", "**?*print=*", "**/login*", "**/logout*"]}
                """)));
    }

    [Theory]
    [InlineData(SourceTypes.UrlList, """{"urls":["https://www.umea.se/"]}""", PiiMode.Hold)]
    [InlineData(SourceTypes.UrlList, """{"urls":["https://www.umea.se/"],"piiMode":"report"}""", PiiMode.Report)]
    [InlineData(SourceTypes.Crawl, """{"startUrls":["https://www.umea.se/"],"piiMode":"report"}""", PiiMode.Report)]
    [InlineData(SourceTypes.Crawl, """{"startUrls":["https://www.umea.se/"],"piiMode":"hold"}""", PiiMode.Hold)]
    [InlineData(SourceTypes.Upload, """{"piiMode":"report"}""", PiiMode.Report)]
    [InlineData(SourceTypes.Upload, "{}", PiiMode.Hold)]
    public void Pii_mode_defaults_to_hold_and_validates(string sourceType, string json, PiiMode mode)
    {
        ContractSchemas.Validator(SourceSettings.SettingsType(sourceType)).Validate(Repository.Json(json)).ShouldBeEmpty();
        SourceSettings.PiiModeOf(sourceType, Repository.Json(json)).ShouldBe(mode);
    }

    [Theory]
    [InlineData(SourceTypes.UrlList, """{"piiMode":"report"}""")]
    [InlineData(SourceTypes.Crawl, """{"startUrls":["https://www.umea.se/"],"piiMode":"block"}""")]
    [InlineData(SourceTypes.Upload, """{"piiMode":"report","virusScan":false}""")]
    [InlineData("sharepoint", """{"piiMode":"report"}""")]
    public void Settings_that_do_not_bind_hold(string sourceType, string json) =>
        SourceSettings.PiiModeOf(sourceType, Repository.Json(json)).ShouldBe(PiiMode.Hold);

    [Fact]
    public void Hashes_are_lower_case_sha256_hex() =>
        SourceSettings.Hashes(SourceTypes.Upload, Repository.Json("{}")).Behaviour.ShouldMatch("^[0-9a-f]{64}$");
}
