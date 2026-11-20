using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Kalla.Domain;
using Kalla.Domain.Entities;
using Kalla.Pipelines.Definitions;
using Kalla.TestKit;

namespace Kalla.AdminApi.Tests;

/// <summary>
/// #129 request 2: a pipeline version keeps its publish report past job retention. The linker copies the newest succeeded
/// <c>publish-report</c> job's <c>evaluationRunId</c> into <c>PipelineVersions.EvaluationRunId</c>, once, and never overwrites.
/// </summary>
[Collection(AdminDatabaseGroup1.Name)]
public sealed class PublishReportLinkerTests(AdminDatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_version_takes_the_newest_succeeded_publish_report_once()
    {
        var code = "L" + Random.Shared.Next(100_000, 999_999);
        await using var db = TestPostgres.Context(database.Connection(null));
        var template = TemplateCatalog.BuiltIn.All.Where(t => t.Template.SourceType == SourceTypes.UrlList).MaxBy(t => t.Template.Version)!.Template;
        var department = new Department { Id = Guid.CreateVersion7(), Name = "Lnk " + code, CostCenterCode = code, SyncedAt = DateTimeOffset.UtcNow };
        var pipeline = new Pipeline { Name = template.Name + "-" + code.ToLowerInvariant(), TemplateId = template.Name, DepartmentId = department.Id, UpdatedAt = DateTimeOffset.UtcNow };
        db.AddRange(department, pipeline);
        await db.SaveChangesAsync(Ct);
        var definition = JsonSerializer.Serialize(new { schema = PipelineDefinition.CurrentSchema, template = template.Name, templateVersion = template.Version, @params = new { } });
        var published = new PipelineVersion { PipelineId = pipeline.Id, Number = 1, State = PipelineVersionStates.Published, PublishedAt = DateTimeOffset.UtcNow, Definition = definition };
        var other = new PipelineVersion { PipelineId = pipeline.Id, Number = 2, State = PipelineVersionStates.Draft, Definition = definition };
        db.AddRange(published, other);
        await db.SaveChangesAsync(Ct);

        var (older, newer, failed) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var now = DateTimeOffset.UtcNow;
        Job Report(Guid run, string state, DateTimeOffset completed) => new()
        {
            Id = Guid.CreateVersion7(), Class = JobClasses.Eval, DepartmentId = department.Id, Classification = Classification.Public,
            Payload = JsonSerializer.Serialize(new { kind = "publish-report", collectionId = Guid.NewGuid(), runId = Guid.NewGuid(), pipelineVersionId = published.Id }),
            State = state, Result = state == JobStates.Succeeded ? JsonSerializer.Serialize(new { evaluationRunId = run, questionCount = 10 }) : null,
            EnqueuedAt = completed, AvailableAt = completed, CompletedAt = completed,
        };
        db.Jobs.AddRange(Report(older, JobStates.Succeeded, now.AddMinutes(-30)), Report(newer, JobStates.Succeeded, now.AddMinutes(-5)), Report(failed, JobStates.Failed, now));
        await db.SaveChangesAsync(Ct);

        await using var factory = database.HeaderFactory();
        var linker = new PublishReportLinker(factory.Services.GetRequiredService<IServiceScopeFactory>(), Options.Create(new AdminOptions()), TimeProvider.System,
            NullLogger<PublishReportLinker>.Instance);

        (await linker.LinkAsync(Ct)).ShouldBeGreaterThanOrEqualTo(1);
        await using var read = TestPostgres.Context(database.Connection(null));
        (await read.PipelineVersions.AsNoTracking().SingleAsync(v => v.Id == published.Id, Ct)).EvaluationRunId.ShouldBe(newer);
        (await read.PipelineVersions.AsNoTracking().SingleAsync(v => v.Id == other.Id, Ct)).EvaluationRunId.ShouldBeNull();

        // A later report does not overwrite the version's report.
        db.Jobs.Add(Report(Guid.CreateVersion7(), JobStates.Succeeded, now.AddMinutes(1)));
        await db.SaveChangesAsync(Ct);
        await linker.LinkAsync(Ct);
        await using var again = TestPostgres.Context(database.Connection(null));
        (await again.PipelineVersions.AsNoTracking().SingleAsync(v => v.Id == published.Id, Ct)).EvaluationRunId.ShouldBe(newer);
    }
}
