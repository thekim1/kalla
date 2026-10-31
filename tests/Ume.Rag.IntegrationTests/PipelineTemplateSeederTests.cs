using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ume.Rag.Domain.Entities;
using Ume.Rag.MigrationService;
using Ume.Rag.Pipelines.Definitions;
using Ume.Rag.TestKit;
using static Ume.Rag.TestKit.RagDatabase;

namespace Ume.Rag.IntegrationTests;

/// <summary>
/// The migrator seeds one platform pipeline per built-in template with a published version per template version (engine
/// design §13.1, issue #81), and is safe to run on every deploy.
/// </summary>
[Collection(RagDatabaseGroup.Name)]
public sealed class PipelineTemplateSeederTests(RagDatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IReadOnlyList<PipelineTemplate> BuiltIn => [.. TemplateCatalog.BuiltIn.All.Select(t => t.Template)];

    [Fact]
    public async Task Each_built_in_template_becomes_a_platform_pipeline_with_one_published_version()
    {
        await using var db = TestPostgres.Context(database.Connection(Migrator));
        await PipelineTemplateSeeder.SeedAsync(db, BuiltIn, TimeProvider.System, Ct);
        await PipelineTemplateSeeder.SeedAsync(db, BuiltIn, TimeProvider.System, Ct);

        BuiltIn.Select(t => t.Name).ShouldBe(["files", "url-list", "website"], ignoreOrder: true);
        foreach (var template in BuiltIn)
        {
            var pipeline = await db.Pipelines.AsNoTracking().SingleAsync(p => p.DepartmentId == null && p.Name == template.Name, Ct);
            pipeline.TemplateId.ShouldBe(template.Name);
            var version = await db.PipelineVersions.AsNoTracking().SingleAsync(v => v.PipelineId == pipeline.Id, Ct);
            pipeline.PublishedVersionId.ShouldBe(version.Id);
            version.Number.ShouldBe(1);
            version.State.ShouldBe(PipelineVersionStates.Published);
            using var definition = JsonDocument.Parse(version.Definition);
            definition.RootElement.GetProperty("schema").GetString().ShouldBe("pipeline/v2");
            definition.RootElement.GetProperty("template").GetString().ShouldBe(template.Name);
            definition.RootElement.GetProperty("templateVersion").GetInt32().ShouldBe(template.Version);
            definition.RootElement.GetProperty("params").EnumerateObject().ShouldBeEmpty();
            new PipelineValidator(TemplateCatalog.BuiltIn).Validate(definition.RootElement).Errors.ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task A_new_template_version_becomes_the_next_published_version_and_supersedes_the_last()
    {
        var name = "test-" + Guid.NewGuid().ToString("N")[..12];
        var v1 = BuiltIn.First(t => t.Name == "url-list") with { Name = name };
        var v2 = v1 with { Version = v1.Version + 1 };
        await using var db = TestPostgres.Context(database.Connection(Migrator));
        await PipelineTemplateSeeder.SeedAsync(db, [v1], TimeProvider.System, Ct);
        await PipelineTemplateSeeder.SeedAsync(db, [v1, v2], TimeProvider.System, Ct);
        await PipelineTemplateSeeder.SeedAsync(db, [v1, v2], TimeProvider.System, Ct);

        var pipeline = await db.Pipelines.AsNoTracking().SingleAsync(p => p.DepartmentId == null && p.Name == name, Ct);
        var versions = await db.PipelineVersions.AsNoTracking().Where(v => v.PipelineId == pipeline.Id).OrderBy(v => v.Number).ToListAsync(Ct);
        versions.Select(v => (v.Number, v.State)).ShouldBe([(1, PipelineVersionStates.Superseded), (2, PipelineVersionStates.Published)]);
        pipeline.PublishedVersionId.ShouldBe(versions[1].Id);
        using var definition = JsonDocument.Parse(versions[1].Definition);
        definition.RootElement.GetProperty("templateVersion").GetInt32().ShouldBe(v2.Version);
    }
}
