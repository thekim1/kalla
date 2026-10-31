using System.Text.Json;
using Npgsql;
using Ume.Rag.Infrastructure.Jobs;
using Ume.Rag.Pipelines.Definitions;
using Ume.Rag.Worker.Connectors.Persistence;

namespace Ume.Rag.Worker.Engine;

/// <summary>One stage type and code version this build contains, with its engine facts (engine design §3.2, §5.2, §6).</summary>
public sealed record StageManifestEntry(string Stage, int Version, string CacheClass, IReadOnlyList<string> HostClasses)
{
    /// <summary><c>NodeManifest.Metadata</c>: job classes and cache class (the parameter schemas live in the templates).</summary>
    public string Metadata => JsonSerializer.Serialize(new { hostClasses = HostClasses, cacheClass = CacheClass });
}

/// <summary>The stages of this build: every P1 stage at version 1 (engine design §6).</summary>
public static class StageManifest
{
    public const int P1Version = 1;

    public static IReadOnlyList<StageManifestEntry> Entries { get; } =
        [.. Stages.All.Select(s => new StageManifestEntry(s.Name, P1Version, s.CacheClass, s.HostClasses))];

    public static bool Supports(string stage, int version) => Entries.Any(e => e.Stage == stage && e.Version == version);
}

/// <summary>A stage's descriptor differs from the one registered first (engine design §5.2): the worker must not start.</summary>
public sealed class StageManifestConflictException : Exception
{
    public StageManifestConflictException()
    {
    }

    public StageManifestConflictException(string message)
        : base(message)
    {
    }

    public StageManifestConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Registration step 2 and 3 of engine design §5.2, run once the instance row exists and before the dispatcher dequeues:
/// each stage of the build is inserted into <c>content."NodeManifest"</c> if absent (a present pair with another descriptor
/// stops the worker with <see cref="StageManifestConflictException"/>), the instance's <c>WorkerNodeSupport</c> rows are
/// written (they cascade with the instance row), and every published pipeline version whose template uses a stage version
/// this build lacks is logged.
/// </summary>
public sealed partial class StageManifestRegistrar(JobDatabase database, ILogger<StageManifestRegistrar> logger)
{
    private const string InsertManifestSql = """
        INSERT INTO content."NodeManifest" ("NodeType", "TypeVersion", "ParamsSchema", "Metadata")
        SELECT * FROM unnest($1::varchar[], $2::int[], $3::jsonb[], $4::jsonb[])
        ON CONFLICT ("NodeType", "TypeVersion") DO NOTHING
        """;

    private const string ReadManifestSql = """
        SELECT "NodeType", "TypeVersion", "Metadata"::text FROM content."NodeManifest" WHERE "NodeType" = ANY($1)
        """;

    private const string InsertSupportSql = """
        INSERT INTO content."WorkerNodeSupport" ("InstanceId", "NodeType", "TypeVersion")
        SELECT $1, * FROM unnest($2::varchar[], $3::int[])
        ON CONFLICT DO NOTHING
        """;

    private const string PublishedSql = """
        SELECT v."Id", v."Definition"::text FROM config."PipelineVersions" v WHERE v."State" = 'published'
        """;

    public async Task RegisterAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        var entries = StageManifest.Entries;
        var stages = entries.Select(e => e.Stage).ToArray();
        var versions = entries.Select(e => e.Version).ToArray();
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var insert = new NpgsqlCommand(InsertManifestSql, connection, transaction)
        {
            Parameters = { Db.P(stages), Db.P(versions), Db.P(entries.Select(_ => "{}").ToArray()), Db.P(entries.Select(e => e.Metadata).ToArray()) },
        })
        {
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var read = new NpgsqlCommand(ReadManifestSql, connection, transaction) { Parameters = { Db.P(stages) } })
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var entry = entries.FirstOrDefault(e => e.Stage == reader.GetString(0) && e.Version == reader.GetInt32(1));
                if (entry is null)
                {
                    continue;
                }

                using var stored = JsonDocument.Parse(reader.GetString(2));
                using var own = JsonDocument.Parse(entry.Metadata);
                if (!JsonElement.DeepEquals(stored.RootElement, own.RootElement))
                {
                    throw new StageManifestConflictException(
                        $"Stage {entry.Stage} v{entry.Version} is registered with another descriptor; bump the stage version instead of changing it.");
                }
            }
        }

        await using (var support = new NpgsqlCommand(InsertSupportSql, connection, transaction)
        {
            Parameters = { Db.P(instanceId), Db.P(stages), Db.P(versions) },
        })
        {
            await support.ExecuteNonQueryAsync(cancellationToken);
        }

        var unsupported = new List<(Guid Version, string Stage)>();
        await using (var published = new NpgsqlCommand(PublishedSql, connection, transaction))
        await using (var reader = await published.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                using var parsed = JsonDocument.Parse(reader.GetString(1));
                var definition = parsed.RootElement;
                if (!definition.TryGetProperty("template", out var name) || !definition.TryGetProperty("templateVersion", out var version) ||
                    TemplateCatalog.BuiltIn.Find(name.GetString() ?? string.Empty, version.GetInt32()) is not { } template)
                {
                    continue;
                }

                unsupported.AddRange(template.Template.Stages.Where(s => !StageManifest.Supports(s.Key, s.Value)).Select(s => (reader.GetGuid(0), $"{s.Key} v{s.Value}")));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        foreach (var (version, stage) in unsupported)
        {
            LogUnsupported(logger, version, stage);
        }
    }

    [LoggerMessage(LogLevel.Warning, "Published pipeline version {PipelineVersionId} uses {Stage}, which this worker build does not contain")]
    private static partial void LogUnsupported(ILogger logger, Guid pipelineVersionId, string stage);
}
