using System.Collections.Concurrent;
using System.Text.Json;
using Npgsql;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Infrastructure.Jobs;
using Ume.Rag.Pipelines.Definitions;
using Ume.Rag.Worker.Connectors.Persistence;

namespace Ume.Rag.Worker.Engine;

/// <summary>A source as the engine runs it: its collection, department and level, and its pipeline.</summary>
public sealed record SourceContext(
    Guid SourceId,
    string Type,
    string Classification,
    JsonElement Settings,
    bool Active,
    Guid CollectionId,
    string CollectionClassification,
    Guid DepartmentId,
    Guid EmbeddingProfileId,
    Guid PipelineId,
    Guid? PublishedVersionId);

/// <summary>A published pipeline version, validated and resolved (engine design §4: the worker runs rules 1–3, fail closed).</summary>
public sealed record PipelineRun(Guid VersionId, ResolvedPipeline Pipeline);

/// <summary>A published version the worker refuses to run (engine design §4, §17 event 5105).</summary>
public sealed class PipelineRefusedException : Exception
{
    public PipelineRefusedException()
    {
        Reason = "pipeline.refused";
    }

    public PipelineRefusedException(string message)
        : base(message)
    {
        Reason = "pipeline.refused";
    }

    public PipelineRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
        Reason = "pipeline.refused";
    }

    public PipelineRefusedException(Guid pipelineVersionId, string reason)
        : base($"Pipeline version {pipelineVersionId} is refused: {reason}.")
    {
        PipelineVersionId = pipelineVersionId;
        Reason = reason;
    }

    public Guid PipelineVersionId { get; }

    public string Reason { get; }
}

/// <summary>
/// Reads sources, collections and pipeline versions (engine design §15): each execution resolves the source's published
/// version at its start and carries the version id through its continuations, which load that version by id. Versions
/// are immutable once published, so a validated version is cached per id.
/// </summary>
public sealed partial class SourceContexts(JobDatabase database, ILogger<SourceContexts> logger)
{
    private const string SourceSql = """
        SELECT s."Id", s."Type", s."Classification", s."Settings"::text, s."State" = 'active' AND s."DeletedAt" IS NULL AND c."DeletedAt" IS NULL,
               c."Id", c."Classification", c."DepartmentId", c."EmbeddingProfileId", p."Id", p."PublishedVersionId"
        FROM config."Sources" s
        JOIN config."Collections" c ON c."Id" = s."CollectionId"
        JOIN config."Pipelines" p ON p."Id" = s."PipelineId"
        WHERE s."Id" = $1
        """;

    private const string VersionSql = """SELECT "Definition"::text, "State" FROM config."PipelineVersions" WHERE "Id" = $1""";

    private static readonly PipelineValidator Validator = new(TemplateCatalog.BuiltIn);

    private readonly ConcurrentDictionary<Guid, PipelineRun> _versions = new();

    public async Task<SourceContext?> GetAsync(Guid sourceId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(SourceSql, connection) { Parameters = { Db.P(sourceId) } };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        using var settings = JsonDocument.Parse(reader.GetString(3));
        return new SourceContext(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), settings.RootElement.Clone(), reader.GetBoolean(4),
            reader.GetGuid(5), reader.GetString(6), reader.GetGuid(7), reader.GetGuid(8), reader.GetGuid(9), Db.Nullable<Guid>(reader, 10));
    }

    /// <summary>The version by id, validated against the template catalogue; a refused version throws <see cref="PipelineRefusedException"/>.</summary>
    public async Task<PipelineRun> VersionAsync(Guid versionId, string sourceType, CancellationToken cancellationToken)
    {
        if (_versions.TryGetValue(versionId, out var cached))
        {
            return cached;
        }

        string definition;
        await using (var connection = await database.OpenAsync(cancellationToken))
        await using (var command = new NpgsqlCommand(VersionSql, connection) { Parameters = { Db.P(versionId) } })
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw Refuse(versionId, "version_unknown");
            }

            if (reader.GetString(1) == PipelineVersionStates.Draft)
            {
                throw Refuse(versionId, "version_unpublished");
            }

            definition = reader.GetString(0);
        }

        using var json = JsonDocument.Parse(definition);
        var result = Validator.Validate(json.RootElement, sourceType);
        if (!result.IsValid || result.Pipeline is not { } pipeline)
        {
            throw Refuse(versionId, (result.Errors.Count > 0 ? result.Errors[0].Code : null) ?? "definition_invalid");
        }

        var unsupported = pipeline.Template.Template.Stages.FirstOrDefault(s => !StageManifest.Supports(s.Key, s.Value));
        if (unsupported.Key is not null)
        {
            throw Refuse(versionId, PipelineValidationCodes.StageVersionUnsupported);
        }

        return _versions[versionId] = new PipelineRun(versionId, pipeline);
    }

    private PipelineRefusedException Refuse(Guid versionId, string reason)
    {
        // Event 5105 rag.pipeline.refused belongs in SecurityEvents (stream B); until then a warning with the same attributes.
        LogRefused(logger, versionId, reason);
        return new PipelineRefusedException(versionId, reason);
    }

    [LoggerMessage(LogLevel.Warning, "The worker refused to run pipeline version {PipelineVersionId}: {Reason}")]
    private static partial void LogRefused(ILogger logger, Guid pipelineVersionId, string reason);
}
