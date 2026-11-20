using System.Text.Json;
using Kalla.Pipelines.Definitions;
using Kalla.Pipelines.Json;
using Kalla.Pipelines.Schemas;

namespace Kalla.Pipelines.Tests;

/// <summary>The P1 templates (engine design §13.1) and the template checks (engine design §3.2, §19 "templates validate").</summary>
public sealed class TemplateTests
{
    [Fact]
    public void The_three_p1_templates_load_and_are_named_after_their_file()
    {
        Repository.TemplateFiles.Select(Path.GetFileName).ShouldBe(["files.v1.json", "url-list.v1.json", "website.v1.json"]);
        foreach (var file in Repository.TemplateFiles)
        {
            var template = ContractJson.Deserialize<PipelineTemplate>(Repository.Json(File.ReadAllText(file)));
            Path.GetFileName(file).ShouldBe($"{template.Name}.v{template.Version}.json");
            ContractSchemas.Validator(typeof(PipelineTemplate)).Validate(Repository.Json(File.ReadAllText(file))).ShouldBeEmpty(file);
            TemplateCatalog.Check(template).ShouldBeEmpty(file);
        }

        Repository.Templates.All.Count.ShouldBe(3);
    }

    [Fact]
    public void The_built_in_catalog_holds_the_template_files()
    {
        TemplateCatalog.BuiltIn.All.Select(t => ContractJson.Serialize(t.Template))
            .ShouldBe(Repository.Templates.All.Select(t => ContractJson.Serialize(t.Template)));
    }

    [Theory]
    [InlineData("website", "crawl", new[] { "fetch", "detect", "parse", "clean", "chunk", "personal-data", "embed", "index" }, new[] { "chunkTokens", "ocr", "overlapPercent", "pathRules", "tables" })]
    [InlineData("url-list", "url-list", new[] { "fetch", "detect", "parse", "clean", "chunk", "personal-data", "embed", "index" }, new[] { "chunkTokens", "ocr", "overlapPercent", "pathRules", "tables" })]
    [InlineData("files", "upload", new[] { "fetch", "scan", "detect", "parse", "clean", "chunk", "personal-data", "embed", "index" }, new[] { "chunkTokens", "ocr", "overlapPercent", "pathRules", "tables" })]
    public void Templates_match_engine_13_1(string name, string sourceType, string[] stages, string[] parameters)
    {
        var template = Repository.Templates.Find(name, 1).ShouldNotBeNull().Template;
        template.SourceType.ShouldBe(sourceType);
        template.Stages.Keys.ShouldBe(stages);
        template.Stages.Values.ShouldAllBe(v => v == 1);
        template.Parameters.Keys.Order(StringComparer.Ordinal).ShouldBe(parameters);

        var defaults = Repository.Templates.Find(name, 1)!.Resolve(new Dictionary<string, JsonElement>());
        defaults["ocr"]!.GetValue<string>().ShouldBe("auto");
        defaults["tables"]!.GetValue<string>().ShouldBe("accurate");
        defaults["chunkTokens"]!.GetValue<int>().ShouldBe(400);
        defaults["overlapPercent"]!.GetValue<int>().ShouldBe(15);
        template.Parameters["ocr"].Stage.ShouldBe("parse");
        template.Parameters["ocr"].AppliesTo.ShouldBe(["parser"]);
        template.Parameters["chunkTokens"].Stage.ShouldBe("chunk");
    }

    [Fact]
    public void Only_website_carries_the_pdf_age_decay_default()
    {
        var recency = Repository.Templates.Find("website", 1)!.Template.CollectionDefaults.ShouldNotBeNull().Recency.ShouldNotBeNull();
        recency.HalfLifeDays.ShouldBe(730);
        recency.Weight.ShouldBe(0.15);
        recency.Kinds.ShouldBe(["pdf"]);
        Repository.Templates.Find("url-list", 1)!.Template.CollectionDefaults.ShouldBeNull();
        Repository.Templates.Find("files", 1)!.Template.CollectionDefaults.ShouldBeNull();
    }

    [Theory]
    [InlineData("website")]
    [InlineData("url-list")]
    [InlineData("files")]
    public void The_path_rules_parameter_is_labelled_topics(string name)
    {
        var labels = Repository.Templates.Find(name, 1)!.ParamsSchema["properties"]!["pathRules"]!["x-labels"]!;
        labels["en"]!.GetValue<string>().ShouldBe("Topics");
        labels["sv"]!.GetValue<string>().ShouldBe("Ämnen");
    }

    [Fact]
    public void Params_schema_describes_every_parameter_for_the_admin_form()
    {
        var schema = Repository.Templates.Find("website", 1)!.ParamsSchema;
        schema["additionalProperties"]!.GetValue<bool>().ShouldBeFalse();
        schema["properties"]!["ocr"]!["x-stage"]!.GetValue<string>().ShouldBe("parse");
        schema["properties"]!["ocr"]!["x-applies-to"]![0]!.GetValue<string>().ShouldBe("parser");
        schema["properties"]!["chunkTokens"]!["default"]!.GetValue<int>().ShouldBe(400);
        schema["$defs"]!["pathRules"].ShouldNotBeNull();

        var validator = SchemaValidator.Compile(schema);
        validator.Validate(Repository.Json("""{"ocr":"force","chunkTokens":600}""")).ShouldBeEmpty();
        validator.Validate(Repository.Json("""{"ocr":"magic"}""")).ShouldNotBeEmpty();
        validator.Validate(Repository.Json("""{"colour":"blue"}""")).ShouldNotBeEmpty();
    }

    [Fact]
    public void Check_names_every_problem_of_a_broken_template()
    {
        var template = Website() with
        {
            Stages = new Dictionary<string, int> { ["fetch"] = 1, ["render"] = 1, ["parse"] = 1, ["detect"] = 1, ["clean"] = 0, ["chunk"] = 1, ["embed"] = 1, ["index"] = 1 },
            Parameters = new Dictionary<string, TemplateParameter>
            {
                ["ocr"] = new() { Stage = "parse", AppliesTo = ["parser"], Schema = Repository.Json("""{"enum":["auto"]}"""), Default = Repository.Json("\"force\"") },
                ["summary"] = new() { Stage = "summarise", Schema = Repository.Json("true"), Default = Repository.Json("true") },
                ["chunkTokens"] = new() { Stage = "chunk", AppliesTo = ["html"], Schema = Repository.Json("""{"type":"integer"}"""), Default = Repository.Json("400") },
            },
            Labels = new Dictionary<string, LocalizedText> { ["colour"] = new() { Sv = "Färg", En = "Colour" } },
        };

        var problems = TemplateCatalog.Check(template);

        problems.ShouldContain(p => p.Contains("unknown stage 'render'", StringComparison.Ordinal));
        problems.ShouldContain(p => p.Contains("missing stage 'personal-data'", StringComparison.Ordinal));
        problems.ShouldContain(p => p.Contains("not in the order", StringComparison.Ordinal));
        problems.ShouldContain(p => p.Contains("stage 'clean' has version 0", StringComparison.Ordinal));
        problems.ShouldContain(p => p.Contains("default of 'ocr' fails its schema", StringComparison.Ordinal));
        problems.ShouldContain(p => p.Contains("'summary' is tagged with stage 'summarise'", StringComparison.Ordinal));
        problems.ShouldContain(p => p.Contains("'chunkTokens' narrows routes", StringComparison.Ordinal));
        problems.ShouldContain(p => p.Contains("label for unknown parameter 'colour'", StringComparison.Ordinal));
        Should.Throw<InvalidOperationException>(() => TemplateCatalog.Create([template]));
    }

    [Fact]
    public void A_template_file_with_an_unknown_member_is_refused() =>
        Should.Throw<InvalidOperationException>(() => TemplateCatalog.Load(
            [File.ReadAllText(Path.Combine(Repository.TemplatesDirectory, "files.v1.json")).Replace("\"version\": 1,", "\"version\": 1, \"graph\": {},", StringComparison.Ordinal)]));

    internal static PipelineTemplate Website() => Repository.Templates.Find("website", 1)!.Template;
}
