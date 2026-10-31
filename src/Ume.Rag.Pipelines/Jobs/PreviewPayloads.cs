using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ume.Rag.Pipelines.Schemas;

namespace Ume.Rag.Pipelines.Jobs;

/// <summary>
/// The P1 <c>preview</c> kinds (engine design §9.1, §11, §12; admin design §4.7; P1b README §7 O9: the preview kind is
/// <c>stage-preview</c>, not admin §4.7's <c>node-preview</c>). The <c>site-profile</c> kind is P2.
/// </summary>
public static class PreviewKinds
{
    public const string StagePreview = "stage-preview";
    public const string Preflight = "preflight";
    public const string PublishPlan = "publish-plan";

    public static IReadOnlyList<string> All { get; } = [StagePreview, Preflight, PublishPlan];
}

/// <summary>
/// <c>stage-preview</c> (engine design §11): up to five documents through a pipeline version, up to <c>untilStage</c>,
/// in <c>ExecutionMode.Preview</c>. Existing documents are read from their raw blobs and cached outputs; URLs and upload
/// slots of a source without documents are fetched live. No dedup, except <see cref="JobDedupKeys.PiiPreview"/> for the
/// PII highlights of one document.
/// </summary>
[Description("Payload of a preview job of kind stage-preview (engine design §11): up to five documents (documentIds, urls or uploadIds) through the pipeline version up to untilStage, writing only previews/{jobId}/. No dedup, except pii-preview:{documentId} for the PII highlights of one document.")]
[SchemaKeyword("oneOf", """[{ "required": ["documentIds"] }, { "required": ["urls"] }, { "required": ["uploadIds"] }]""")]
[SchemaKeyword("allOf", """
    [
      { "if": { "required": ["uploadIds"] }, "then": { "required": ["sourceId"] } }
    ]
    """)]
public sealed record StagePreviewPayload : JobPayload
{
    [JsonRequired]
    [SchemaKeyword("const", "\"stage-preview\"")]
    public string Kind { get; init; } = PreviewKinds.StagePreview;

    [Description("The saved draft version (template parameters) or the version a new source's form would use.")]
    public required Guid PipelineVersionId { get; init; }

    [Description("The collection the source is in or would be in: its publicity decides the personal-data state shown (processing design §9.5).")]
    public required Guid CollectionId { get; init; }

    [Description("Absent for a new source's form; required with uploadIds.")]
    public Guid? SourceId { get; init; }

    [Description("Existing documents of the source.")]
    [MinLength(1)]
    [MaxLength(5)]
    [SchemaKeyword("uniqueItems", "true")]
    public IReadOnlyList<Guid>? DocumentIds { get; init; }

    [Description("URLs fetched live through the egress guard and host politeness, for a source without documents.")]
    [MinLength(1)]
    [MaxLength(5)]
    [SchemaItems(MaxLength = 2048, Pattern = "^https?://")]
    [SchemaKeyword("uniqueItems", "true")]
    public IReadOnlyList<string>? Urls { get; init; }

    [Description("Upload slots (content.Uploads.Id) of the source.")]
    [MinLength(1)]
    [MaxLength(5)]
    [SchemaKeyword("uniqueItems", "true")]
    public IReadOnlyList<Guid>? UploadIds { get; init; }

    [Description("The last stage to run; index never runs.")]
    [SchemaKeyword("enum", ContractPatterns.StageEnum)]
    public string UntilStage { get; init; } = "personal-data";

    [Description("A preview of the same documents whose outputs (previews/{basePreviewJobId}/) are reused up to the first stage whose settings changed.")]
    public Guid? BasePreviewJobId { get; init; }

    [Description("Embed through the gateway; without it the embed stage reports chunk count and tokens only.")]
    public bool Embed { get; init; }

    [Description("Write the personal-data spans, for the PII highlights (admin design §4.7).")]
    public bool PiiSpans { get; init; }
}

/// <summary>
/// <c>preflight</c> (connectors design §2.2, §19; admin design §4.7, §5.4 step 4): checks of an unsaved URL-list or crawl
/// configuration. A URL-list preflight samples the first 20 URLs. No dedup.
/// </summary>
[Description("Payload of a preview job of kind preflight (connectors design §2.2; admin design §4.7, §5.4 step 4): reachability, robots, sitemaps, estimated pages against maxPages, egress and CMS of an unsaved URL-list or crawl configuration. No dedup.")]
[SchemaKeyword("allOf", """
    [
      { "if": { "properties": { "sourceType": { "const": "url-list" } }, "required": ["sourceType"] },
        "then": { "properties": { "settings": { "properties": { "urls": { "maxItems": 20 } } } } } }
    ]
    """)]
public sealed record PreflightPayload : JobPayload
{
    [JsonRequired]
    [SchemaKeyword("const", "\"preflight\"")]
    public string Kind { get; init; } = PreviewKinds.Preflight;

    [Description("The collection the source would be in.")]
    public required Guid CollectionId { get; init; }

    [SchemaKeyword("enum", "[\"url-list\", \"crawl\"]")]
    public required string SourceType { get; init; }

    [Description("The unsaved Sources.Settings, valid against contracts/sources/v1/{sourceType}.schema.json (the admin checks it, V25); a URL list sends its first 20 URLs only.")]
    [SchemaKeyword("type", "\"object\"")]
    public required JsonElement Settings { get; init; }
}

/// <summary>
/// <c>publish-plan</c> (engine design §12.1 step 1, §12.2): the re-processing plan and forecast of a pipeline version
/// before publish or rollback. Dedup <see cref="JobDedupKeys.Plan"/>.
/// </summary>
[Description("Payload of a preview job of kind publish-plan (engine design §12.1, §12.2): the re-processing plan and forecast of publishing (or rolling back to) the pipeline version. P1: every live document of the affected sources. Dedup plan:{pipelineId}.")]
public sealed record PublishPlanPayload : JobPayload
{
    [JsonRequired]
    [SchemaKeyword("const", "\"publish-plan\"")]
    public string Kind { get; init; } = PreviewKinds.PublishPlan;

    public required Guid PipelineId { get; init; }

    [Description("The draft to publish, or the superseded version a rollback re-publishes.")]
    public required Guid PipelineVersionId { get; init; }
}
