using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Ume.Rag.Domain.Admin;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Infrastructure.Blobs;

namespace Ume.Rag.AdminApi;

/// <summary>
/// Jobs and previews (admin design §4.5 "Jobs and previews", §4.7, P1b item 9): a job's state and result for the UI's
/// polling, the preflight of an unsaved source (<c>preview</c> <c>preflight</c>), a document's PII highlights
/// (<c>preview</c> <c>stage-preview</c> up to the personal-data stage, audited <c>pii.view</c>), and a preview stage's
/// output blob. A job is in scope when its department is one of the caller's.
/// </summary>
public static class PreviewEndpoints
{
    /// <summary>A URL list's preflight samples its first URLs only (admin design §4.7).</summary>
    public const int PreflightUrls = 20;

    /// <summary>The largest preview output returned (the worker cuts at 2 MB with a marker).</summary>
    public const int MaxOutputBytes = 2 * 1024 * 1024;

    public static RouteGroupBuilder MapPreviews(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        api.MapGet("/jobs/{id:guid}", async (Guid id, AdminContext admin, CancellationToken ct) => JobDto.Of(await ScopedJobAsync(admin, id, ct)))
            .RequireAuthorization(AdminPolicies.Read).WithTags("Jobs").WithName("GetJob").WithSummary("A job's state, result and error code (polling)");

        api.MapPost("/sources/preflight", async (PreflightRequest request, AdminContext admin, CancellationToken ct) =>
                await admin.WriteAsync(async attempt =>
                {
                    var collection = await CollectionEndpoints.Scoped(admin).AsNoTracking().SingleOrDefaultAsync(c => c.Id == request.CollectionId, attempt)
                        ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
                    if (request.Type is not (SourceTypes.UrlList or SourceTypes.Crawl))
                    {
                        throw AdminFaults.Field("type", AdminErrorCodes.InvalidValue);
                    }

                    var settings = SampleUrls(SourceRules.Settings(request.Type, request.Settings));
                    var job = PreviewJobs.PreflightJob(collection.Id, request.Type, settings, collection.DepartmentId, collection.Classification);
                    return new AdminChange<CommandStarted>(new CommandStarted(job.Id), "preflight", AuditEntityTypes.Collection, collection.Id, null,
                        new { jobId = job.Id, sourceType = request.Type })
                    {
                        DepartmentId = collection.DepartmentId, Classification = collection.Classification, Jobs = [job],
                    };
                }, ct))
            .RequireAuthorization(AdminPolicies.Edit).WithTags("Sources").WithName("PreflightSource")
            .WithSummary("Check unsaved source settings: robots, reachability, CMS, estimated pages, raw vs rendered words (preview preflight)");

        api.MapPost("/documents/{id:guid}/pii-preview", async (Guid id, AdminContext admin, CancellationToken ct) =>
                await admin.WriteAsync(async attempt =>
                {
                    var sources = SourceEndpoints.Scoped(admin).Select(s => s.Id);
                    var document = await admin.Db.Documents.AsNoTracking()
                        .SingleOrDefaultAsync(d => d.Id == id && d.DeletedAt == null && sources.Contains(d.SourceId), attempt)
                        ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
                    // The newest set: a held flagged set is the one an admin decides on; else the live one.
                    var set = await admin.Db.ChunkSets.AsNoTracking()
                        .Where(cs => cs.DocumentId == id && (cs.State == ChunkSetStates.Held || cs.Id == document.LiveChunkSetId))
                        .OrderByDescending(cs => cs.CreatedAt).FirstOrDefaultAsync(attempt)
                        ?? throw AdminFaults.Of(AdminErrorCodes.PiiNotFlagged);
                    var (department, _) = await DocumentEndpoints.DepartmentAsync(admin, document.SourceId, attempt);
                    var job = PreviewJobs.PiiPreviewJob(document.Id, document.SourceId, set.PipelineVersionId, set.CollectionId, department, document.Classification);
                    return new AdminChange<CommandStarted>(new CommandStarted(job.Id), "pii.view", AuditEntityTypes.Document, document.Id, null,
                        new { jobId = job.Id })
                    {
                        DepartmentId = department, Classification = document.Classification, Jobs = [job],
                    };
                }, ct))
            .RequireAuthorization(AdminPolicies.Manage).WithTags("Documents").WithName("PreviewPii")
            .WithSummary("Personal-data highlights of a document (preview up to the personal-data stage; audited pii.view)");

        // {document}: the document's id, or its index in the result (a URL or upload preview has no document id);
        // {stage}: a stage name, or before/after for a crawl page's extraction without and with the site profile (§6.4).
        api.MapGet("/previews/{jobId:guid}/documents/{document}/stages/{stage}/output",
                async (Guid jobId, string document, string stage, AdminContext admin, IBlobStore blobs, CancellationToken ct) =>
                {
                    var job = await ScopedJobAsync(admin, jobId, ct);
                    if (job.Class != JobClasses.Preview || job.State != JobStates.Succeeded || job.Result is null)
                    {
                        throw AdminFaults.Of(AdminErrorCodes.NotFound);
                    }

                    var output = OutputPath(job, document, stage) ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
                    await using var stream = await blobs.ForLevel(job.Classification).OpenReadAsync(new BlobKey(output), null, ct)
                        ?? throw AdminFaults.Of(AdminErrorCodes.NotFound); // expired (24 h)
                    var bytes = await ReadAtMostAsync(stream, MaxOutputBytes, ct);
                    return Results.Bytes(bytes, output.EndsWith(".json", StringComparison.Ordinal) ? "application/json" : "text/markdown; charset=utf-8");
                })
            .RequireAuthorization(AdminPolicies.EditOrEditPipelines).WithTags("Jobs").WithName("GetPreviewOutput")
            .WithSummary("A preview stage's output (decrypted, ≤ 2 MB)");

        return api;
    }

    private static async Task<Job> ScopedJobAsync(AdminContext admin, Guid id, CancellationToken ct)
    {
        var departments = admin.Departments().Select(d => d.Id);
        return await admin.Db.Jobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == id && departments.Contains(j.DepartmentId), ct)
            ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
    }

    /// <summary>A URL list's settings with its first <see cref="PreflightUrls"/> URLs; other types unchanged.</summary>
    internal static JsonObject SampleUrls(string settingsJson)
    {
        var settings = JsonNode.Parse(settingsJson)!.AsObject();
        if (settings["urls"] is JsonArray urls && urls.Count > PreflightUrls)
        {
            settings["urls"] = new JsonArray([.. urls.Take(PreflightUrls).Select(u => u?.DeepClone())]);
        }

        return settings;
    }

    /// <summary>
    /// The output path from the job's result (<c>preview.stage-preview.result</c>) of document <paramref name="document"/> (its id,
    /// or its index in <c>documents</c>) and <paramref name="stage"/> (a stage, or <c>before</c>/<c>after</c> of its
    /// <c>siteProfile</c>), only when it lies under the job's own <c>previews/{jobId}/</c>: the path comes from a result, so it
    /// never reaches another job's blobs.
    /// </summary>
    internal static string? OutputPath(Job job, string document, string stage)
    {
        using var result = JsonDocument.Parse(job.Result!);
        if (!result.RootElement.TryGetProperty("documents", out var documents) || documents.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var byId = Guid.TryParse(document, out var documentId);
        var byIndex = !byId && int.TryParse(document, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index)
            ? index : -1;
        var prefix = $"previews/{job.Id:D}/";
        var i = -1;
        foreach (var item in documents.EnumerateArray())
        {
            i++;
            var match = byId
                ? item.TryGetProperty("documentId", out var idElement) && idElement.ValueKind == JsonValueKind.String
                    && Guid.TryParse(idElement.GetString(), out var id) && id == documentId
                : i == byIndex;
            if (!match)
            {
                continue;
            }

            var path = stage is "before" or "after"
                ? item.TryGetProperty("siteProfile", out var profile) && profile.ValueKind == JsonValueKind.Object
                    && profile.TryGetProperty(stage, out var side) && side.ValueKind == JsonValueKind.String ? side.GetString() : null
                : StageOutput(item, stage);
            if (path is not null && path.StartsWith(prefix, StringComparison.Ordinal) && !path.Contains("..", StringComparison.Ordinal))
            {
                return path;
            }
        }

        return null;
    }

    private static string? StageOutput(JsonElement document, string stage)
    {
        if (!document.TryGetProperty("stages", out var stages) || stages.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in stages.EnumerateArray())
        {
            if (item.TryGetProperty("stage", out var name) && name.GetString() == stage
                && item.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.String)
            {
                return output.GetString();
            }
        }

        return null;
    }

    private static async Task<byte[]> ReadAtMostAsync(Stream stream, int max, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while (buffer.Length < max && (read = await stream.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, max - buffer.Length)), ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}

/// <summary>The admin's <c>preview</c> jobs (admin design §4.7; <c>contracts/jobs/v1/preview.*.payload.schema.json</c>).</summary>
public static class PreviewJobs
{
    public static JobRequest PreflightJob(Guid collectionId, string sourceType, JsonObject settings, Guid departmentId, string classification) =>
        new(JobClasses.Preview, departmentId, classification,
            new JsonObject { ["kind"] = "preflight", ["collectionId"] = collectionId, ["sourceType"] = sourceType, ["settings"] = settings }.ToJsonString());

    /// <summary>A document's PII highlights: <c>stage-preview</c> up to <c>personal-data</c> with spans; dedup <c>pii-preview:{documentId}</c>.</summary>
    public static JobRequest PiiPreviewJob(Guid documentId, Guid sourceId, Guid pipelineVersionId, Guid collectionId, Guid departmentId, string classification) =>
        new(JobClasses.Preview, departmentId, classification, JsonSerializer.Serialize(new
        {
            kind = "stage-preview", pipelineVersionId, collectionId, sourceId, documentIds = new[] { documentId }, untilStage = "personal-data", piiSpans = true,
        }))
        {
            DedupKey = $"pii-preview:{documentId}",
        };
}

/// <summary>A job as the UI polls it: state, the result as stored (<c>contracts/jobs/v1/*.result</c>) and the error code.</summary>
public sealed record JobDto(Guid Id, string Class, string? Kind, string State, DateTimeOffset EnqueuedAt, DateTimeOffset? CompletedAt, Guid? RunId,
    JsonElement? Result, string? LastError)
{
    public static JobDto Of(Job job)
    {
        ArgumentNullException.ThrowIfNull(job);
        string? kind = null;
        using (var payload = JsonDocument.Parse(job.Payload))
        {
            if (payload.RootElement.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String)
            {
                kind = k.GetString();
            }
        }

        JsonElement? result = null;
        if (!string.IsNullOrEmpty(job.Result))
        {
            using var json = JsonDocument.Parse(job.Result);
            result = json.RootElement.Clone();
        }

        return new(job.Id, job.Class, kind, job.State, job.EnqueuedAt, job.CompletedAt, job.RunId, result, job.LastError);
    }
}

/// <summary>An unsaved source to check (V25 first: the settings must match its type's schema).</summary>
public sealed record PreflightRequest : AdminRequest
{
    public Guid CollectionId { get; init; }

    [System.ComponentModel.DataAnnotations.Required]
    public string Type { get; init; } = "";

    public JsonElement Settings { get; init; }
}
