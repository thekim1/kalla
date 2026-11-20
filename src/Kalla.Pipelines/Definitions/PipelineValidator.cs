using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kalla.Pipelines.Json;
using Kalla.Pipelines.Schemas;

namespace Kalla.Pipelines.Definitions;

/// <summary>The error and warning codes of <see cref="PipelineValidator"/> (engine design §4).</summary>
public static class PipelineValidationCodes
{
    /// <summary>Rule 1: the definition does not match <c>definition.schema.json</c>.</summary>
    public const string DefinitionInvalid = "definition_invalid";

    /// <summary>Rule 1: <c>params</c> is larger than 16 KB.</summary>
    public const string ParamsTooLarge = "params_too_large";

    /// <summary>Rule 2: no such (template, template version).</summary>
    public const string TemplateUnknown = "template_unknown";

    /// <summary>Rule 2, a warning: the template version is deprecated.</summary>
    public const string TemplateDeprecated = "template_deprecated";

    /// <summary>The template serves another source type (admin V24).</summary>
    public const string TemplateSourceTypeMismatch = "template_source_type_mismatch";

    /// <summary>Rule 3: the template declares no such parameter.</summary>
    public const string ParamUnknown = "param_unknown";

    /// <summary>Rule 3: the value fails the parameter's schema or a cross-parameter rule.</summary>
    public const string ParamInvalid = "param_invalid";

    /// <summary>Rule 4: no live worker runs this stage version in a job class that runs the stage.</summary>
    public const string StageVersionUnsupported = "stage_version_unsupported";
}

/// <summary>
/// One finding. <see cref="Param"/> names the parameter and <see cref="Stage"/> the stage when there is one, so the form
/// can point at the fault; <see cref="Message"/> is English for logs and Scalar, not UI text.
/// </summary>
public sealed record PipelineValidationIssue(string Code, string Message, string? Param = null, string? Stage = null);

/// <summary>A valid definition with its template and parameters, defaults applied.</summary>
public sealed record ResolvedPipeline(PipelineDefinition Definition, CompiledTemplate Template, JsonObject Params);

public sealed record PipelineValidationResult(
    IReadOnlyList<PipelineValidationIssue> Errors,
    IReadOnlyList<PipelineValidationIssue> Warnings,
    ResolvedPipeline? Pipeline)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>A stage and its code version (<c>content."NodeManifest"</c>, <c>"WorkerNodeSupport"</c>).</summary>
public readonly record struct StageVersion(string Stage, int Version);

/// <summary>
/// One live worker instance as rule 4 needs it: <c>WorkerInstances</c> (heartbeat, <c>Classes</c>) and its
/// <c>WorkerNodeSupport</c> rows.
/// </summary>
public sealed record WorkerSupport(Guid InstanceId, DateTimeOffset LastHeartbeatAt, IReadOnlyList<string> Classes, IReadOnlyList<StageVersion> Stages);

/// <summary>
/// <c>PipelineValidator</c> (engine design §4): run by the admin on save (<see cref="Validate"/>, rules 1–3) and publish
/// (<see cref="ValidateForPublish"/>, rules 1–4), and again by the worker when it loads a published version (rules 1–3;
/// fail closed, event 5105). Rule 5 (LLM-calling stages) waits for the P2 stages.
/// </summary>
public sealed class PipelineValidator(ITemplateCatalog templates)
{
    public const int MaxParamsBytes = 16 * 1024;

    /// <summary>Rule 4: a support row counts while its instance's heartbeat is younger than this.</summary>
    public static readonly TimeSpan SupportHeartbeatWindow = TimeSpan.FromSeconds(60);

    private static readonly Lazy<SchemaValidator> DefinitionSchema = new(() => ContractSchemas.Validator(typeof(PipelineDefinition)));

    /// <summary>Rules 1–3. <paramref name="sourceType"/>, when given, must be the template's (admin V24).</summary>
    public PipelineValidationResult Validate(JsonElement definition, string? sourceType = null)
    {
        var errors = new List<PipelineValidationIssue>();
        var warnings = new List<PipelineValidationIssue>();
        var resolved = Check(definition, sourceType, errors, warnings);
        return new PipelineValidationResult(errors, warnings, errors.Count == 0 ? resolved : null);
    }

    /// <summary>Rules 1–4 against the live instances in <paramref name="workers"/> at <paramref name="now"/>.</summary>
    public PipelineValidationResult ValidateForPublish(JsonElement definition, string? sourceType, IReadOnlyCollection<WorkerSupport> workers, DateTimeOffset now)
    {
        var errors = new List<PipelineValidationIssue>();
        var warnings = new List<PipelineValidationIssue>();
        var resolved = Check(definition, sourceType, errors, warnings);
        if (resolved is not null)
        {
            var live = workers.Where(w => now - w.LastHeartbeatAt < SupportHeartbeatWindow).ToList();
            foreach (var (stage, version) in resolved.Template.Template.Stages)
            {
                foreach (var jobClass in Stages.Find(stage)!.HostClasses)
                {
                    if (!live.Any(w => w.Classes.Contains(jobClass, StringComparer.Ordinal) && w.Stages.Contains(new StageVersion(stage, version))))
                    {
                        errors.Add(new PipelineValidationIssue(
                            PipelineValidationCodes.StageVersionUnsupported,
                            $"No live worker runs {stage} v{version} in {jobClass} jobs.",
                            Stage: stage));
                    }
                }
            }
        }

        return new PipelineValidationResult(errors, warnings, errors.Count == 0 ? resolved : null);
    }

    private ResolvedPipeline? Check(JsonElement json, string? sourceType, List<PipelineValidationIssue> errors, List<PipelineValidationIssue> warnings)
    {
        // Rule 1: the schema, then the size of params.
        var schemaErrors = DefinitionSchema.Value.Validate(json);
        if (schemaErrors.Count > 0)
        {
            errors.AddRange(schemaErrors.Take(10).Select(e => new PipelineValidationIssue(
                PipelineValidationCodes.DefinitionInvalid, $"{Location(e.InstanceLocation)}: {e.Message}")));
            return null;
        }

        if (json.TryGetProperty("params", out var rawParams) && Encoding.UTF8.GetByteCount(rawParams.GetRawText()) > MaxParamsBytes)
        {
            errors.Add(new PipelineValidationIssue(PipelineValidationCodes.ParamsTooLarge, $"params is larger than {MaxParamsBytes} bytes."));
            return null;
        }

        var definition = ContractJson.Deserialize<PipelineDefinition>(json);

        // Rule 2: the template version exists; deprecated warns.
        var template = templates.Find(definition.Template, definition.TemplateVersion);
        if (template is null)
        {
            errors.Add(new PipelineValidationIssue(
                PipelineValidationCodes.TemplateUnknown, $"Template {definition.Template} v{definition.TemplateVersion} does not exist."));
            return null;
        }

        if (template.Template.Deprecated)
        {
            warnings.Add(new PipelineValidationIssue(
                PipelineValidationCodes.TemplateDeprecated, $"Template {definition.Template} v{definition.TemplateVersion} is deprecated."));
        }

        if (sourceType is not null && !string.Equals(sourceType, template.Template.SourceType, StringComparison.Ordinal))
        {
            errors.Add(new PipelineValidationIssue(
                PipelineValidationCodes.TemplateSourceTypeMismatch,
                $"Template {definition.Template} serves {template.Template.SourceType} sources, not {sourceType}."));
        }

        // Rule 3: every parameter is declared and valid; then the cross-parameter rules over the resolved set.
        foreach (var (name, value) in definition.Params.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var parameterErrors = template.ValidateParameter(name, value);
            if (parameterErrors is null)
            {
                errors.Add(new PipelineValidationIssue(PipelineValidationCodes.ParamUnknown, $"Template {definition.Template} has no parameter {name}.", name));
            }
            else if (parameterErrors.Count > 0)
            {
                errors.Add(new PipelineValidationIssue(PipelineValidationCodes.ParamInvalid, $"{name}{Location(parameterErrors[0].InstanceLocation, string.Empty)}: {parameterErrors[0].Message}", name));
            }
        }

        if (errors.Count > 0)
        {
            return null;
        }

        var resolved = template.Resolve(definition.Params);
        foreach (var error in template.ValidateRules(resolved).Take(10))
        {
            var param = error.InstanceLocation.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            errors.Add(new PipelineValidationIssue(PipelineValidationCodes.ParamInvalid, $"{Location(error.InstanceLocation)}: {error.Message}", param));
        }

        return errors.Count == 0 ? new ResolvedPipeline(definition, template, resolved) : null;
    }

    private static string Location(string pointer, string root = "$") => string.IsNullOrEmpty(pointer) ? root : pointer;
}
