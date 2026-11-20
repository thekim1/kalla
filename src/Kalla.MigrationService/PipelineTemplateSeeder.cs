using Microsoft.EntityFrameworkCore;
using Kalla.Domain.Entities;
using Kalla.Infrastructure.Persistence;
using Kalla.Pipelines.Definitions;

namespace Kalla.MigrationService;

/// <summary>
/// Seeds the platform pipelines from the built-in templates (engine design §13.1, issue #81), in every environment: for
/// each template a platform <c>config."Pipelines"</c> row (no department, name and template id = the template name) and,
/// for each template version not yet seeded, a published <c>PipelineVersions</c> row with the template's default
/// parameters. A changed template is a new version file, so a new template version becomes the next published version and
/// the previous one is superseded. Insert-if-absent, so it is safe on every deploy.
/// </summary>
public static class PipelineTemplateSeeder
{
    public const string Actor = "system:seed";

    public static async Task SeedAsync(RagDbContext db, IEnumerable<PipelineTemplate> templates, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(clock);
        var strategy = db.Database.CreateExecutionStrategy();
        foreach (var byName in templates.GroupBy(t => t.Name, StringComparer.Ordinal))
        {
            await strategy.ExecuteAsync(ct => SeedTemplateAsync(db, byName.Key, [.. byName.OrderBy(t => t.Version)], clock, ct),
                cancellationToken);
        }
    }

    private static async Task SeedTemplateAsync(RagDbContext db, string name, IReadOnlyList<PipelineTemplate> templates,
        TimeProvider clock, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var now = clock.GetUtcNow();
            var pipeline = await db.Pipelines.SingleOrDefaultAsync(p => p.DepartmentId == null && p.Name == name, cancellationToken);
            if (pipeline is null)
            {
                pipeline = new Pipeline { Name = name, TemplateId = name, UpdatedAt = now, UpdatedBy = Actor };
                db.Pipelines.Add(pipeline);
                await db.SaveChangesAsync(cancellationToken);
            }

            var versions = await db.PipelineVersions.Where(v => v.PipelineId == pipeline.Id).ToListAsync(cancellationToken);
            foreach (var template in templates)
            {
                var definition = Definition(template);
                if (versions.Any(v => SameTemplateVersion(v.Definition, template)))
                {
                    continue;
                }

                foreach (var published in versions.Where(v => v.State == PipelineVersionStates.Published))
                {
                    published.State = PipelineVersionStates.Superseded;
                }

                var version = new PipelineVersion
                {
                    PipelineId = pipeline.Id,
                    Number = versions.Count == 0 ? 1 : versions.Max(v => v.Number) + 1,
                    State = PipelineVersionStates.Published,
                    Definition = definition,
                    CreatedBy = Actor,
                    PublishedAt = now,
                };
                db.PipelineVersions.Add(version);
                versions.Add(version);
                await db.SaveChangesAsync(cancellationToken);
                pipeline.PublishedVersionId = version.Id;
                pipeline.UpdatedAt = now;
                pipeline.UpdatedBy = Actor;
                await db.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
    }

    /// <summary>The published definition of a template version with its default parameters (<c>pipeline/v2</c>).</summary>
    public static string Definition(PipelineTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            schema = "pipeline/v2",
            template = template.Name,
            templateVersion = template.Version,
            @params = new { },
        });
    }

    private static bool SameTemplateVersion(string definition, PipelineTemplate template)
    {
        using var document = System.Text.Json.JsonDocument.Parse(definition);
        var root = document.RootElement;
        return root.TryGetProperty("template", out var name) && name.GetString() == template.Name
            && root.TryGetProperty("templateVersion", out var version) && version.TryGetInt32(out var number)
            && number == template.Version;
    }
}
