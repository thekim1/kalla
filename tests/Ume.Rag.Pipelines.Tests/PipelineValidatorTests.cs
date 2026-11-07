using System.Text.Json;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Pipelines.Definitions;

namespace Ume.Rag.Pipelines.Tests;

/// <summary>Engine design §4: every rule with a passing and a failing definition (§19).</summary>
public sealed class PipelineValidatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    private static readonly PipelineValidator Validator = new(Repository.Templates);

    private static JsonElement Definition(string parameters = "{}", string template = "website", int version = 1) =>
        Repository.Json($$"""{"schema":"pipeline/v2","template":"{{template}}","templateVersion":{{version}},"params":{{parameters}}}""");

    [Fact]
    public void A_valid_definition_resolves_with_its_defaults()
    {
        var result = Validator.Validate(Definition("""{"ocr":"force","chunkTokens":600}"""), "crawl");

        result.Errors.ShouldBeEmpty();
        result.Warnings.ShouldBeEmpty();
        var resolved = result.Pipeline.ShouldNotBeNull();
        resolved.Params["ocr"]!.GetValue<string>().ShouldBe("force");
        resolved.Params["chunkTokens"]!.GetValue<int>().ShouldBe(600);
        resolved.Params["tables"]!.GetValue<string>().ShouldBe("accurate");
        resolved.Params["pathRules"]!.AsArray().ShouldBeEmpty();
    }

    [Fact]
    public void The_engine_3_1_example_is_valid() =>
        Validator.Validate(Repository.Json("""
            { "schema": "pipeline/v2",
              "template": "website", "templateVersion": 1,
              "params": { "ocr": "auto", "tables": "accurate", "chunkTokens": 400, "overlapPercent": 15, "pathRules": [] } }
            """)).IsValid.ShouldBeTrue();

    [Theory]
    [InlineData("""{"template":"website","templateVersion":1}""")]
    [InlineData("""{"schema":"pipeline/v1","template":"website","templateVersion":1}""")]
    [InlineData("""{"schema":"pipeline/v2","template":"Website!","templateVersion":1}""")]
    [InlineData("""{"schema":"pipeline/v2","template":"website","templateVersion":0}""")]
    [InlineData("""{"schema":"pipeline/v2","template":"website","templateVersion":1,"nodes":[]}""")]
    [InlineData("""{"schema":"pipeline/v2","template":"website","templateVersion":1,"params":[]}""")]
    public void Rule_1_refuses_a_definition_that_fails_the_schema(string json)
    {
        var result = Validator.Validate(Repository.Json(json));
        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldAllBe(e => e.Code == PipelineValidationCodes.DefinitionInvalid);
        result.Pipeline.ShouldBeNull();
    }

    [Fact]
    public void Rule_1_refuses_params_over_16_kb()
    {
        var rules = string.Join(",", Enumerable.Range(0, 100).Select(i => $$"""{"match":"/{{new string('a', 200)}}/{{i}}/**","on":"path","tags":["t"]}"""));
        var result = Validator.Validate(Definition($$"""{"pathRules":[{{rules}}]}"""));

        result.Errors.ShouldHaveSingleItem().Code.ShouldBe(PipelineValidationCodes.ParamsTooLarge);
    }

    [Theory]
    [InlineData("website", 2)]
    [InlineData("sharepoint", 1)]
    public void Rule_2_refuses_an_unknown_template_version(string template, int version) =>
        Validator.Validate(Definition(template: template, version: version)).Errors.ShouldHaveSingleItem().Code.ShouldBe(PipelineValidationCodes.TemplateUnknown);

    [Fact]
    public void Rule_2_warns_on_a_deprecated_template_version()
    {
        var deprecated = TemplateCatalog.Create([TemplateTests.Website() with { Deprecated = true }]);
        var result = new PipelineValidator(deprecated).Validate(Definition());

        result.IsValid.ShouldBeTrue();
        result.Warnings.ShouldHaveSingleItem().Code.ShouldBe(PipelineValidationCodes.TemplateDeprecated);
    }

    [Fact]
    public void The_template_must_serve_the_source_type()
    {
        Validator.Validate(Definition(template: "files"), "upload").IsValid.ShouldBeTrue();
        Validator.Validate(Definition(template: "files"), "crawl").Errors.ShouldHaveSingleItem().Code.ShouldBe(PipelineValidationCodes.TemplateSourceTypeMismatch);
    }

    [Fact]
    public void Rule_3_refuses_a_parameter_the_template_does_not_declare()
    {
        var error = Validator.Validate(Definition("""{"colour":"blue"}""", template: "files")).Errors.ShouldHaveSingleItem();
        error.Code.ShouldBe(PipelineValidationCodes.ParamUnknown);
        error.Param.ShouldBe("colour");
    }

    [Theory]
    [InlineData("ocr", "\"magic\"")]
    [InlineData("tables", "true")]
    [InlineData("chunkTokens", "100")]
    [InlineData("chunkTokens", "400.5")]
    [InlineData("overlapPercent", "31")]
    [InlineData("pathRules", """[{"match":"/a/**"}]""")]
    [InlineData("pathRules", """[{"match":"/a/**","on":"query"}]""")]
    [InlineData("pathRules", """[{"match":"^/protokoll/(?<y>\\d{4}","on":"path-regex"}]""")]
    [InlineData("pathRules", """[{"match":"/a/**","on":"path","tags":["two words"]}]""")]
    public void Rule_3_refuses_a_value_that_fails_its_schema(string param, string value)
    {
        var error = Validator.Validate(Definition($$"""{"{{param}}":{{value}}}""")).Errors.ShouldHaveSingleItem();
        error.Code.ShouldBe(PipelineValidationCodes.ParamInvalid);
        error.Param.ShouldBe(param);
    }

    [Fact]
    public void Rule_3_accepts_the_processing_7_path_rules() =>
        Validator.Validate(Definition("""
            {"pathRules": [ { "match": "/bygga-bo-och-miljo/bygglov/**", "on": "path", "tags": ["bygglov"], "documentType": "page" },
                            { "match": "^/kommun-och-politik/protokoll/(?<y>\\d{4})/", "on": "path-regex", "tags": ["protokoll"], "date": "${y}-01-01" } ]}
            """)).Errors.ShouldBeEmpty();

    [Fact]
    public void Rule_3_checks_cross_parameter_rules_over_the_resolved_parameters()
    {
        // ocr off makes accurate tables pointless here: an if/then rule in the template, so the admin can check it.
        var template = TemplateTests.Website() with
        {
            Name = "ruled",
            Rules = [Repository.Json("""{"if":{"properties":{"ocr":{"const":"off"}}},"then":{"properties":{"tables":{"const":"fast"}}}}""")],
        };
        var validator = new PipelineValidator(TemplateCatalog.Create([template]));

        validator.Validate(Definition("""{"ocr":"off","tables":"fast"}""", template: "ruled")).IsValid.ShouldBeTrue();
        validator.Validate(Definition("""{"ocr":"force"}""", template: "ruled")).IsValid.ShouldBeTrue();
        var error = validator.Validate(Definition("""{"ocr":"off"}""", template: "ruled")).Errors.ShouldHaveSingleItem();
        error.Code.ShouldBe(PipelineValidationCodes.ParamInvalid);
        error.Param.ShouldBe("tables");
    }

    [Fact]
    public void Rule_4_passes_when_live_workers_run_every_stage_version_in_each_class_that_runs_it()
    {
        var result = Validator.ValidateForPublish(Definition(), "crawl", [Worker(JobClasses.Fetch, JobClasses.Parse, JobClasses.Embed)], Now);
        result.Errors.ShouldBeEmpty();

        // The same coverage split over a fetch/embed instance and a parse instance.
        Validator.ValidateForPublish(Definition(), "crawl", [Worker(JobClasses.Fetch, JobClasses.Embed), Worker(JobClasses.Parse)], Now)
            .Errors.ShouldBeEmpty();
    }

    [Fact]
    public void Rule_4_refuses_without_a_live_worker()
    {
        var stale = Worker(JobClasses.Fetch, JobClasses.Parse, JobClasses.Embed) with { LastHeartbeatAt = Now.AddSeconds(-61) };
        var errors = Validator.ValidateForPublish(Definition(), "crawl", [stale], Now).Errors;

        errors.ShouldNotBeEmpty();
        errors.ShouldAllBe(e => e.Code == PipelineValidationCodes.StageVersionUnsupported);
        errors.Select(e => e.Stage).Distinct().Count().ShouldBe(8);
    }

    [Fact]
    public void Rule_4_refuses_when_no_worker_runs_a_class_that_runs_the_stage()
    {
        // No parse instance: a PDF's parse, clean, chunk and personal-data run in parse jobs (engine design §7.4).
        var errors = Validator.ValidateForPublish(Definition(), "crawl", [Worker(JobClasses.Fetch, JobClasses.Embed)], Now).Errors;

        errors.Select(e => e.Stage).ShouldBe(["parse", "clean", "chunk", "personal-data"], ignoreOrder: true);
    }

    [Fact]
    public void Rule_4_refuses_when_no_worker_has_the_stage_version()
    {
        var old = Worker(JobClasses.Fetch, JobClasses.Parse, JobClasses.Embed) with
        {
            Stages = [.. StageNames.Order.Where(s => s != StageNames.Chunk).Select(s => new StageVersion(s, 1)), new StageVersion(StageNames.Chunk, 2)],
        };
        var errors = Validator.ValidateForPublish(Definition(), "crawl", [old], Now).Errors;

        errors.ShouldNotBeEmpty();
        errors.ShouldAllBe(e => e.Code == PipelineValidationCodes.StageVersionUnsupported && e.Stage == StageNames.Chunk);
    }

    [Fact]
    public void Publish_runs_rules_1_to_3_first() =>
        Validator.ValidateForPublish(Definition("""{"ocr":"magic"}"""), "crawl", [Worker(JobClasses.Fetch, JobClasses.Parse, JobClasses.Embed)], Now)
            .Errors.ShouldHaveSingleItem().Code.ShouldBe(PipelineValidationCodes.ParamInvalid);

    private static WorkerSupport Worker(params string[] classes) =>
        new(Guid.CreateVersion7(), Now.AddSeconds(-5), classes, [.. StageNames.Order.Select(s => new StageVersion(s, 1))]);
}
