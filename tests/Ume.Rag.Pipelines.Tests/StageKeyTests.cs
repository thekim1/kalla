using System.Security.Cryptography;
using System.Text;
using Ume.Rag.Pipelines.Definitions;

namespace Ume.Rag.Pipelines.Tests;

/// <summary>Engine design §3.3 and §19: a changed parameter changes exactly its stage's key and the later ones.</summary>
public sealed class StageKeyTests
{
    private static readonly PipelineValidator Validator = new(Repository.Templates);

    private static readonly byte[] ContentHash = SHA256.HashData("<html>Sidan uppdaterades 2026-01-15</html>"u8.ToArray());

    private static ResolvedPipeline Pipeline(string parameters = "{}", string template = "website") =>
        Validator.Validate(Repository.Json($$"""{"schema":"pipeline/v2","template":"{{template}}","templateVersion":1,"params":{{parameters}}}""")).Pipeline.ShouldNotBeNull();

    /// <summary>
    /// The keys of a document's chain from detect to index, as the engine computes them: each stage's input is the
    /// previous stage's OutputHash. A stage here is deterministic (its output is a function of its key), as a real stage
    /// is a pure function of input, settings and version (engine design §5.1).
    /// </summary>
    private static Dictionary<string, string> Chain(ResolvedPipeline pipeline, string route, IReadOnlyDictionary<string, string>? parseInputs = null)
    {
        var keys = new Dictionary<string, string>();
        var input = ContentHash;
        foreach (var stage in pipeline.Template.Template.Stages.Keys.Where(s => s is not (StageNames.Fetch or StageNames.Scan)))
        {
            var key = StageKeys.Key(pipeline, stage, stage == StageNames.Parse ? ContentHash : input, route,
                stage == StageNames.Parse ? parseInputs : null);
            keys[stage] = StageKeys.ToHex(key);
            input = SHA256.HashData(key);
        }

        return keys;
    }

    private static string[] Changed(Dictionary<string, string> before, Dictionary<string, string> after) =>
        [.. before.Keys.Where(stage => before[stage] != after[stage])];

    [Fact]
    public void Defaults_written_out_give_the_same_keys_as_defaults_left_out() =>
        Chain(Pipeline("""{"ocr":"auto","tables":"accurate","chunkTokens":400,"overlapPercent":15,"pathRules":[]}"""), ParseRoutes.Parser)
            .ShouldBe(Chain(Pipeline(), ParseRoutes.Parser));

    [Theory]
    [InlineData("""{"chunkTokens":600}""", new[] { "chunk", "personal-data", "embed", "index" })]
    [InlineData("""{"overlapPercent":20}""", new[] { "chunk", "personal-data", "embed", "index" })]
    [InlineData("""{"pathRules":[{"match":"/a/**","on":"path","tags":["a"]}]}""", new[] { "chunk", "personal-data", "embed", "index" })]
    [InlineData("""{"ocr":"force"}""", new[] { "parse", "clean", "chunk", "personal-data", "embed", "index" })]
    [InlineData("""{"tables":"fast"}""", new[] { "parse", "clean", "chunk", "personal-data", "embed", "index" })]
    public void A_changed_parameter_changes_its_stage_and_the_later_ones(string parameters, string[] changed) =>
        Changed(Chain(Pipeline(), ParseRoutes.Parser), Chain(Pipeline(parameters), ParseRoutes.Parser)).ShouldBe(changed);

    [Theory]
    [InlineData("""{"ocr":"force"}""")]
    [InlineData("""{"tables":"fast"}""")]
    public void A_parser_route_parameter_does_not_touch_html_pages(string parameters) =>
        Changed(Chain(Pipeline(), ParseRoutes.Html), Chain(Pipeline(parameters), ParseRoutes.Html)).ShouldBeEmpty();

    [Fact]
    public void A_new_site_profile_version_changes_the_html_parse_key_and_the_later_ones()
    {
        var v3 = new Dictionary<string, string> { ["siteProfileVersion"] = "0193a4c2-7d1e-7b3f-9c2a-000000000003" };
        var v4 = new Dictionary<string, string> { ["siteProfileVersion"] = "0193a4c2-7d1e-7b3f-9c2a-000000000004" };

        Changed(Chain(Pipeline(), ParseRoutes.Html, v3), Chain(Pipeline(), ParseRoutes.Html, v4))
            .ShouldBe(["parse", "clean", "chunk", "personal-data", "embed", "index"]);
    }

    [Fact]
    public void Settings_hold_only_the_stage_parameters_of_the_route()
    {
        var pipeline = Pipeline("""{"ocr":"force"}""");

        Encoding.UTF8.GetString(StageKeys.Settings(pipeline, StageNames.Parse, ParseRoutes.Parser, new Dictionary<string, string> { ["parseRule"] = "builtin-pdf@1" }))
            .ShouldBe("""{"inputs":{"parseRule":"builtin-pdf@1"},"params":{"ocr":"force","tables":"accurate"}}""");
        Encoding.UTF8.GetString(StageKeys.Settings(pipeline, StageNames.Parse, ParseRoutes.Html))
            .ShouldBe("""{"inputs":{},"params":{}}""");
        Encoding.UTF8.GetString(StageKeys.Settings(pipeline, StageNames.Chunk, ParseRoutes.Html))
            .ShouldBe("""{"inputs":{},"params":{"chunkTokens":400,"overlapPercent":15,"pathRules":[]}}""");
        Encoding.UTF8.GetString(StageKeys.Settings(pipeline, StageNames.Embed, ParseRoutes.Html))
            .ShouldBe("""{"inputs":{},"params":{}}""");
    }

    [Fact]
    public void A_route_narrowed_stage_needs_a_route() =>
        Should.Throw<ArgumentException>(() => StageKeys.Settings(Pipeline(), StageNames.Parse, route: null));

    [Fact]
    public void The_key_changes_with_input_stage_version_and_settings()
    {
        var input = ContentHash;
        var settings = StageKeys.SettingsHash(Pipeline(), StageNames.Chunk, ParseRoutes.Html);
        var key = StageKeys.Key(input, StageNames.Chunk, 1, settings);

        StageKeys.Key(input, StageNames.Chunk, 1, settings).ShouldBe(key);
        StageKeys.Key(SHA256.HashData("other"u8.ToArray()), StageNames.Chunk, 1, settings).ShouldNotBe(key);
        StageKeys.Key(input, StageNames.Chunk, 2, settings).ShouldNotBe(key);
        StageKeys.Key(input, StageNames.Clean, 1, settings).ShouldNotBe(key);
        StageKeys.Key(input, StageNames.Chunk, 1, StageKeys.SettingsHash(Pipeline("""{"chunkTokens":500}"""), StageNames.Chunk, ParseRoutes.Html)).ShouldNotBe(key);
        Should.Throw<ArgumentException>(() => StageKeys.Key(input.AsSpan(0, 31), StageNames.Chunk, 1, settings));
    }

    [Fact]
    public void Templates_with_the_same_stage_settings_share_keys()
    {
        // Sharing is by content: url-list and website documents with the same bytes and settings hit the same cache rows.
        Chain(Pipeline(template: "url-list"), ParseRoutes.Parser).ShouldBe(Chain(Pipeline(), ParseRoutes.Parser));
    }

    [Fact]
    public void The_parser_key_has_the_content_the_build_and_the_parser_settings()
    {
        var settings = StageKeys.SettingsHash(Pipeline(), StageNames.Parse, ParseRoutes.Parser);
        var key = StageKeys.ParserKey(ContentHash, "sha256:aaaa", settings);

        StageKeys.ParserKey(ContentHash, "sha256:aaaa", settings).ShouldBe(key);
        StageKeys.ParserKey(ContentHash, "sha256:bbbb", settings).ShouldNotBe(key);
        StageKeys.ParserKey(SHA256.HashData("other"u8.ToArray()), "sha256:aaaa", settings).ShouldNotBe(key);
        StageKeys.ParserKey(ContentHash, "sha256:aaaa", StageKeys.SettingsHash(Pipeline("""{"ocr":"off"}"""), StageNames.Parse, ParseRoutes.Parser)).ShouldNotBe(key);
        // A parser upgrade changes the parse key only; chunking keys depend on the parse output, not on the build.
        StageKeys.ParserKey(ContentHash, "sha256:aaaa", settings).ShouldNotBe(StageKeys.Key(ContentHash, StageNames.Parse, 1, settings));
    }

    [Fact]
    public void Keys_are_stable_across_releases()
    {
        // Pinned: a change to the key format or the canonical settings would orphan every cached output.
        var settings = StageKeys.SettingsHash(Pipeline(), StageNames.Chunk, ParseRoutes.Html);
        StageKeys.ToHex(settings).ShouldBe(StageKeys.ToHex(SHA256.HashData("""{"inputs":{},"params":{"chunkTokens":400,"overlapPercent":15,"pathRules":[]}}"""u8.ToArray())));
        var expected = new List<byte>(ContentHash);
        expected.AddRange("chunk\0"u8.ToArray());
        expected.AddRange(new byte[] { 0, 0, 0, 1 });
        expected.AddRange(settings);
        StageKeys.Key(ContentHash, StageNames.Chunk, 1, settings).ShouldBe(SHA256.HashData(expected.ToArray()));
    }
}
