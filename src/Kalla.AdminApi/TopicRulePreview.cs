using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Kalla.Domain.Admin;
using Kalla.Pipelines.Definitions;
using Kalla.Pipelines.Processing;

namespace Kalla.AdminApi;

/// <summary>
/// What a topic rule matches before it is saved (admin design §4.5, §5.5.2; Decisions "Topics editor"): one rule of the
/// processing §7 shape against one source's live documents, through the worker's matcher (<see cref="PathRules"/>), as
/// "Matches N of M documents" with the first ten. The rule is checked against the template's schema as V24 checks
/// <c>pathRules</c> at publish, so an invalid regular expression is a 400 here as it would be at Save. Owning department and <c>EditPipelines</c> only; nothing is saved or logged.
/// </summary>
public static class TopicRulePreview
{
    /// <summary>At most this many matched documents are listed.</summary>
    public const int SampleSize = 10;

    public static RouteGroupBuilder MapTopicRulePreview(this RouteGroupBuilder collections)
    {
        ArgumentNullException.ThrowIfNull(collections);
        collections.MapPost("/{id:guid}/topic-rules/preview", async (Guid id, TopicRulePreviewRequest request, AdminContext admin, CancellationToken ct) =>
            {
                var collection = await CollectionEndpoints.Scoped(admin).AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct)
                    ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
                var source = await (from s in admin.Db.Sources.AsNoTracking()
                                    join p in admin.Db.Pipelines.AsNoTracking() on s.PipelineId equals p.Id
                                    join v in admin.Db.PipelineVersions.AsNoTracking() on p.PublishedVersionId equals v.Id into published
                                    from v in published.DefaultIfEmpty()
                                    where s.Id == request.SourceId && s.CollectionId == collection.Id && s.DeletedAt == null
                                    select new { s.Id, s.Type, p.TemplateId, Definition = v == null ? null : v.Definition }).SingleOrDefaultAsync(ct)
                    ?? throw AdminFaults.Field("sourceId", AdminErrorCodes.InvalidValue);

                var rule = Check(request.Rule, source.TemplateId, source.Definition, source.Type);
                var documents = admin.Db.Documents.AsNoTracking()
                    .Where(d => d.SourceId == source.Id && d.DeletedAt == null && d.LiveChunkSetId != null)
                    .OrderBy(d => d.Id)
                    .Select(d => new { d.Id, d.Title, d.Url, d.DisplayName, d.MediaType })
                    .AsAsyncEnumerable();
                var matched = 0;
                var total = 0;
                var sample = new List<TopicRuleSampleDto>();
                await foreach (var d in documents.WithCancellation(ct))
                {
                    total++;
                    var subject = new PathRuleSubject(d.MediaType ?? "")
                    {
                        Url = d.Url is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : null, DisplayName = d.DisplayName, Title = d.Title,
                    };
                    if (PathRules.Match(rule, subject) is null)
                    {
                        continue;
                    }

                    matched++;
                    if (sample.Count < SampleSize)
                    {
                        sample.Add(new TopicRuleSampleDto(d.Id, d.Title, d.Url, d.DisplayName));
                    }
                }

                return new TopicRulePreviewDto(matched, total, sample);
            })
            .RequireAuthorization(AdminPolicies.EditPipelines).WithName("PreviewTopicRule")
            .WithSummary("What one topic rule matches among a source's live documents, before it is saved (nothing is saved)");
        return collections;
    }

    /// <summary>
    /// The rule as V24 checks it: a definition of the source's template (at its published version) whose only parameter is
    /// <c>pathRules</c> with this one rule must validate; a 400 on <c>rule</c> otherwise.
    /// </summary>
    private static PathRule Check(JsonElement rule, string templateId, string? published, string sourceType)
    {
        if (rule.ValueKind != JsonValueKind.Object)
        {
            throw AdminFaults.Field("rule", AdminErrorCodes.InvalidValue);
        }

        // Only the rule is checked: the template and version of the published processing, no other parameter.
        var templateVersion = published is not null && JsonNode.Parse(published)?["templateVersion"] is JsonValue stored && stored.TryGetValue<int>(out var v)
            ? v
            : TemplateCatalog.BuiltIn.All.Where(t => t.Template.Name == templateId).Max(t => (int?)t.Template.Version) ?? 1;
        var parameters = new JsonObject();
        var definition = new JsonObject
        {
            ["schema"] = PipelineDefinition.CurrentSchema, ["template"] = templateId, ["templateVersion"] = templateVersion, ["params"] = parameters,
        };
        parameters["pathRules"] = new JsonArray(JsonNode.Parse(rule.GetRawText()));
        var validation = new PipelineValidator(TemplateCatalog.BuiltIn).Validate(JsonSerializer.SerializeToElement(definition), sourceType);
        if (!validation.IsValid)
        {
            var first = validation.Errors[0];
            throw AdminFaults.Field("rule", AdminErrorCodes.PipelineParamsInvalid,
                new Dictionary<string, string> { ["param"] = first.Param ?? "pathRules", ["message"] = first.Message });
        }

        return PathRules.Read(JsonSerializer.SerializeToElement(new[] { rule })).Single();
    }
}

/// <summary><see cref="Rule"/>: one rule of processing §7 (<c>match</c>, <c>on</c>, <c>tags</c>, <c>documentType</c>, <c>date</c>).</summary>
public sealed record TopicRulePreviewRequest(Guid SourceId, JsonElement Rule);

/// <summary>The live documents the rule matches, of <see cref="Total"/>, with the first <see cref="TopicRulePreview.SampleSize"/>.</summary>
public sealed record TopicRulePreviewDto(int Matched, int Total, IReadOnlyList<TopicRuleSampleDto> Sample);

/// <summary>A matched document: its URL for a website, its display name for an upload.</summary>
public sealed record TopicRuleSampleDto(Guid DocumentId, string? Title, string? Url, string? DisplayName);
