using System.Text.Json;
using Npgsql;
using Kalla.Domain.Entities;
using Kalla.Infrastructure.Jobs;
using Kalla.Pipelines.Definitions;
using Kalla.Pipelines.Jobs;
using Kalla.Pipelines.Json;
using Kalla.Worker.Connectors.Persistence;
using Kalla.Worker.Engine;

namespace Kalla.Worker.Preview;

/// <summary>
/// The collection a preview shows the personal-data state for (its level), embeds with (its profile) and, for a URL without
/// a source, matches department-scoped parse rules with (its department); an <c>embed: true</c> preview's calls run as its
/// gateway key (engine design §7.7; null: the worker's credential key).
/// </summary>
public sealed record PreviewCollectionRow(Guid Id, string Classification, Guid EmbeddingProfileId, Guid DepartmentId, Guid? GatewayKeyId);

/// <summary>
/// The config a preview reads (engine design §11): the pipeline version, which unlike a sync's may be a saved <b>draft</b>
/// (validated with the same rules 1–3 and the stage support check, fail closed), the collection, and the base preview's
/// result. Read-only.
/// </summary>
public sealed class PreviewConfig(JobDatabase database)
{
    private const string VersionSql = """SELECT "Definition"::text FROM config."PipelineVersions" WHERE "Id" = $1""";

    private const string CollectionSql = """
        SELECT "Id", "Classification", "EmbeddingProfileId", "DepartmentId", "GatewayKeyId" FROM config."Collections" WHERE "Id" = $1 AND "DeletedAt" IS NULL
        """;

    private const string BaseSql = $"""
        SELECT "Result"::text FROM content."Jobs" WHERE "Id" = $1 AND "Class" = '{JobClasses.Preview}' AND "Result" IS NOT NULL
        """;

    private static readonly PipelineValidator Validator = new(TemplateCatalog.BuiltIn);

    /// <summary>
    /// The version by id, draft or published, read each time (a draft changes in place); a version that fails validation
    /// throws <see cref="PipelineRefusedException"/>.
    /// </summary>
    public async Task<PipelineRun> VersionAsync(Guid versionId, string? sourceType, CancellationToken cancellationToken)
    {
        string? definition;
        await using (var connection = await database.OpenAsync(cancellationToken))
        await using (var command = new NpgsqlCommand(VersionSql, connection) { Parameters = { Db.P(versionId) } })
        {
            definition = await command.ExecuteScalarAsync(cancellationToken) as string;
        }

        if (definition is null)
        {
            throw new PipelineRefusedException(versionId, "version_unknown");
        }

        using var json = JsonDocument.Parse(definition);
        var result = Validator.Validate(json.RootElement, sourceType);
        if (!result.IsValid || result.Pipeline is not { } pipeline)
        {
            throw new PipelineRefusedException(versionId, (result.Errors.Count > 0 ? result.Errors[0].Code : null) ?? "definition_invalid");
        }

        if (pipeline.Template.Template.Stages.Any(s => !StageManifest.Supports(s.Key, s.Value)))
        {
            throw new PipelineRefusedException(versionId, PipelineValidationCodes.StageVersionUnsupported);
        }

        return new PipelineRun(versionId, pipeline);
    }

    public async Task<PreviewCollectionRow?> CollectionAsync(Guid collectionId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(CollectionSql, connection) { Parameters = { Db.P(collectionId) } };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new PreviewCollectionRow(reader.GetGuid(0), reader.GetString(1), reader.GetGuid(2), reader.GetGuid(3), Db.Nullable<Guid>(reader, 4))
            : null;
    }

    /// <summary>The result of an earlier <c>stage-preview</c>, or null when it is gone or has none.</summary>
    public async Task<StagePreviewResult?> BaseAsync(Guid jobId, CancellationToken cancellationToken)
    {
        string? text;
        await using (var connection = await database.OpenAsync(cancellationToken))
        await using (var command = new NpgsqlCommand(BaseSql, connection) { Parameters = { Db.P(jobId) } })
        {
            text = await command.ExecuteScalarAsync(cancellationToken) as string;
        }

        if (text is null)
        {
            return null;
        }

        try
        {
            using var json = JsonDocument.Parse(text);
            return ContractJson.Deserialize<StagePreviewResult>(json.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
