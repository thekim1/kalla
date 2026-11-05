using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;
using Pgvector;
using Ume.Rag.Retrieval;
using Ume.Rag.Retrieval.Postgres;
using Ume.Rag.Infrastructure;
using Ume.Rag.Worker.Connectors.Persistence;

namespace Ume.Rag.Worker.Engine;

/// <summary>
/// The bounds of the index-health check (knowledge design §6.4, §12.1), from <c>Settings.Retrieval</c>:
/// <c>hnswRecallFloor</c> and <c>hnswCheckSample</c>, and the scope rule's <c>exactScanMaxChunks</c> and
/// <c>minHnswSelectivity</c>.
/// </summary>
public sealed record HnswHealthSettings(double RecallFloor, int CheckSample, int ExactScanMaxChunks, double MinHnswSelectivity)
{
    public const double DefaultRecallFloor = 0.95;
    public const int DefaultCheckSample = 200;

    public static HnswHealthSettings Read(string? settingsRetrievalJson)
    {
        var platform = RetrievalSettingsResolver.Platform(settingsRetrievalJson);
        var json = string.IsNullOrWhiteSpace(settingsRetrievalJson) ? null : JsonNode.Parse(settingsRetrievalJson) as JsonObject;
        return new HnswHealthSettings(
            Number(json, "hnswRecallFloor") ?? DefaultRecallFloor,
            Number(json, "hnswCheckSample") is { } sample ? (int)sample : DefaultCheckSample,
            platform.ExactScanMaxChunks,
            platform.MinHnswSelectivity);
    }

    private static double? Number(JsonObject? json, string name) =>
        json?[name] is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.GetValue<double>() : null;
}

/// <summary>One recall measurement of a profile's index: the HNSW-searched collections, the query chunks, recall@10 (null when nothing was measured).</summary>
public sealed record HnswRecall(int Scopes, int Queries, double? Recall);

/// <summary>A check: the first measurement and, when it was below the floor, the measurement after the rebuild.</summary>
public sealed record HnswCheckOutcome(string IndexName, HnswRecall Before, HnswRecall? After)
{
    public bool Rebuilt => After is not null;
}

/// <summary>
/// The index-health check of knowledge design §6.4. For each collection of the profile that retrieval searches through
/// HNSW (more than <c>exactScanMaxChunks</c> live child chunks and at least <c>minHnswSelectivity</c> of the profile's),
/// <c>hnswCheckSample</c> random live child chunks are the queries, with their own embeddings: the approximate top 10
/// (<see cref="RetrievalStatements.VectorApproximate"/>, the half-precision HNSW walk with the role's HNSW settings) is
/// compared with the exact top 10 (<see cref="RetrievalStatements.VectorExact"/>), and recall@10 over all queries is
/// recorded as <c>rag.retrieval.hnsw_recall{profile}</c>. Below <c>hnswRecallFloor</c> the index is rebuilt with
/// <c>REINDEX INDEX CONCURRENTLY</c> and measured again.
/// </summary>
public sealed partial class HnswIndexHealth(NpgsqlDataSource dataSource, string rebuildConnectionString, HnswIndexMetrics metrics, ILogger<HnswIndexHealth> logger)
{
    public const int K = 10;

    /// <summary>The connection string of the rebuild, when set (<see cref="ConnectionString"/>).</summary>
    public const string MaintenanceConnectionName = "ragdb-maintenance";

    /// <summary>The rebuild session's first statement (<see cref="RebuildAsync"/>).</summary>
    public const string NoStatementTimeoutSql = "SET statement_timeout = 0";

    /// <summary>
    /// An approximate hit outside the exact top 10 still counts when its distance ties the exact 10th: duplicate texts
    /// have equal vectors, and the half-precision distance differs from the full one by about this much.
    /// </summary>
    public const double TieTolerance = 1e-3;

    private const string SettingsSql = """SELECT "Retrieval"::text FROM config."Settings" ORDER BY "Id" LIMIT 1""";

    private const string DimensionsSql = """SELECT "Dimensions" FROM config."EmbeddingProfiles" WHERE "Id" = $1""";

    private const string IndexExistsSql = "SELECT to_regclass($1) IS NOT NULL";

    private const string ScopesSql = """
        SELECT "CollectionId", count(*)
        FROM content."Chunks"
        WHERE "EmbeddingProfileId" = $1 AND "DeletedAt" IS NULL AND "Kind" = 'child'
        GROUP BY 1
        ORDER BY 1
        """;

    private const string SampleSql = """
        SELECT c."Embedding"
        FROM content."Chunks" c
        WHERE c."Id" IN (SELECT s."Id" FROM content."Chunks" s
                         WHERE s."EmbeddingProfileId" = $1 AND s."CollectionId" = $2 AND s."DeletedAt" IS NULL AND s."Kind" = 'child'
                           AND s."Embedding" IS NOT NULL
                         ORDER BY random()
                         LIMIT $3)
        """;

    /// <summary>The profile's index name (data-model §3.2): <c>IX_Chunks_Embedding_</c> plus the id without hyphens.</summary>
    public static string IndexName(Guid profileId) => "IX_Chunks_Embedding_" + profileId.ToString("N", CultureInfo.InvariantCulture);

    public async Task<HnswHealthSettings> SettingsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(SettingsSql, connection);
        return HnswHealthSettings.Read(await command.ExecuteScalarAsync(cancellationToken) as string);
    }

    /// <summary>The profile's dimensions, or null for an unknown profile.</summary>
    public async Task<int?> DimensionsAsync(Guid profileId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(DimensionsSql, connection) { Parameters = { Db.P(profileId) } };
        return await command.ExecuteScalarAsync(cancellationToken) as int?;
    }

    /// <summary>
    /// Measures the profile's index and rebuilds it below <see cref="HnswHealthSettings.RecallFloor"/>; null when the
    /// profile has no index.
    /// </summary>
    public async Task<HnswCheckOutcome?> CheckAsync(Guid profileId, int dimensions, HnswHealthSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var index = IndexName(profileId);
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
        await using (var exists = new NpgsqlCommand(IndexExistsSql, connection) { Parameters = { Db.P($"content.\"{index}\"") } })
        {
            if (await exists.ExecuteScalarAsync(cancellationToken) is not true)
            {
                return null;
            }
        }

        var before = await MeasureAsync(profileId, dimensions, settings, cancellationToken);
        if (before.Recall is not { } recall)
        {
            return new HnswCheckOutcome(index, before, null);
        }

        metrics.Recall(profileId, recall);
        if (recall >= settings.RecallFloor)
        {
            LogHealthy(logger, profileId, recall, before.Scopes, before.Queries);
            return new HnswCheckOutcome(index, before, null);
        }

        LogRebuilding(logger, profileId, index, recall, settings.RecallFloor);
        await RebuildAsync(index, cancellationToken);
        var after = await MeasureAsync(profileId, dimensions, settings, cancellationToken);
        if (after.Recall is { } again)
        {
            metrics.Recall(profileId, again);
        }

        LogRebuilt(logger, profileId, index, recall, after.Recall);
        return new HnswCheckOutcome(index, before, after);
    }

    /// <summary>recall@<see cref="K"/> of the profile's HNSW-searched collections (knowledge design §6.4).</summary>
    public async Task<HnswRecall> MeasureAsync(Guid profileId, int dimensions, HnswHealthSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var counts = new List<(Guid Collection, long Chunks)>();
        await using (var command = new NpgsqlCommand(ScopesSql, connection) { Parameters = { Db.P(profileId) } })
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                counts.Add((reader.GetGuid(0), reader.GetInt64(1)));
            }
        }

        var total = counts.Sum(c => c.Chunks);
        var scopes = counts.Where(c => c.Chunks > settings.ExactScanMaxChunks && (double)c.Chunks / total >= settings.MinHnswSelectivity)
            .Select(c => c.Collection).ToList();
        var approximateSql = RetrievalStatements.VectorApproximate(profileId, dimensions, K, FilterFragments.None);
        var exactSql = RetrievalStatements.VectorExact(profileId, dimensions, K, FilterFragments.None);
        var queries = 0;
        long hits = 0;
        long expected = 0;
        foreach (var collection in scopes)
        {
            foreach (var query in await SampleAsync(connection, profileId, collection, settings.CheckSample, cancellationToken))
            {
                var exact = await TopAsync(connection, exactSql, query, collection, cancellationToken);
                if (exact.Count == 0)
                {
                    continue;
                }

                var approximate = await TopAsync(connection, approximateSql, query, collection, cancellationToken);
                queries++;
                expected += exact.Count;
                hits += Math.Min(exact.Count, Hits(exact, approximate));
            }
        }

        return new HnswRecall(scopes.Count, queries, expected == 0 ? null : (double)hits / expected);
    }

    /// <summary>The approximate results that are in the exact top <see cref="K"/>, or tie its last distance.</summary>
    public static int Hits(IReadOnlyList<(Guid Id, double Distance)> exact, IReadOnlyList<(Guid Id, double Distance)> approximate)
    {
        ArgumentNullException.ThrowIfNull(exact);
        ArgumentNullException.ThrowIfNull(approximate);
        if (exact.Count == 0)
        {
            return 0;
        }

        var ids = exact.Select(e => e.Id).ToHashSet();
        var last = exact[^1].Distance;
        return approximate.Count(a => ids.Contains(a.Id) || a.Distance <= last + TieTolerance);
    }

    /// <summary>
    /// The connection string the rebuild runs on: <c>ConnectionStrings:ragdb-maintenance</c>, else <c>ragdb</c>
    /// (Decisions "M-2 open design calls"). Like <c>ragdb-listen</c> it never goes through a pooler: the rebuild sets
    /// session state, and a transaction-mode PgBouncer would hand that session to the next client.
    /// </summary>
    public static string ConnectionString(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var value = configuration.GetConnectionString(MaintenanceConnectionName) is { Length: > 0 } maintenance
            ? maintenance
            : configuration.GetConnectionString(InfrastructureExtensions.DatabaseResourceName)
              ?? throw new InvalidOperationException($"ConnectionStrings:{InfrastructureExtensions.DatabaseResourceName} is not configured.");
        // A dedicated session: never returned to a pool another component could reuse.
        return new NpgsqlConnectionStringBuilder(value) { Pooling = false }.ConnectionString;
    }

    /// <summary>
    /// <c>REINDEX INDEX CONCURRENTLY</c> (the worker role holds <c>MAINTAIN</c> on <c>content."Chunks"</c>, data-model §1.2)
    /// on a session of its own from <see cref="ConnectionString"/>, unpooled. The statement runs outside a transaction and
    /// outlasts the role's 60 s default, so the session first runs <c>SET statement_timeout = 0</c>: a <c>SET</c>, not a
    /// startup option, because PgBouncer drops the <c>options</c> startup parameter. Rebuilds run one at a time: each
    /// holds <c>SHARE UPDATE EXCLUSIVE</c> on <c>content."Chunks"</c>, which conflicts with itself.
    /// </summary>
    public async Task RebuildAsync(string indexName, CancellationToken cancellationToken)
    {
        var builder = new NpgsqlConnectionStringBuilder(rebuildConnectionString) { Pooling = false };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using (var timeout = new NpgsqlCommand(NoStatementTimeoutSql, connection))
        {
            await timeout.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var command = new NpgsqlCommand($"REINDEX INDEX CONCURRENTLY content.\"{indexName}\"", connection) { CommandTimeout = 0 };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<List<Vector>> SampleAsync(NpgsqlConnection connection, Guid profileId, Guid collectionId, int sample,
        CancellationToken cancellationToken)
    {
        var vectors = new List<Vector>();
        await using var command = new NpgsqlCommand(SampleSql, connection) { Parameters = { Db.P(profileId), Db.P(collectionId), Db.P(sample) } };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            vectors.Add(reader.GetFieldValue<Vector>(0));
        }

        return vectors;
    }

    private static async Task<List<(Guid Id, double Distance)>> TopAsync(NpgsqlConnection connection, string sql, Vector query, Guid collectionId,
        CancellationToken cancellationToken)
    {
        var top = new List<(Guid, double)>(K);
        await using var command = new NpgsqlCommand(sql, connection)
        {
            Parameters =
            {
                new NpgsqlParameter { Value = query },
                new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid, Value = new[] { collectionId } },
                new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = Domain.Classification.All.ToArray() },
            },
        };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            top.Add((reader.GetGuid(0), reader.GetDouble(9)));
        }

        return top;
    }

    [LoggerMessage(LogLevel.Information, "HNSW index of profile {ProfileId}: recall@10 {Recall:F3} over {Scopes} collections, {Queries} queries")]
    private static partial void LogHealthy(ILogger logger, Guid profileId, double recall, int scopes, int queries);

    [LoggerMessage(LogLevel.Warning, "HNSW index {Index} of profile {ProfileId}: recall@10 {Recall:F3} is below {Floor}; rebuilding it")]
    private static partial void LogRebuilding(ILogger logger, Guid profileId, string index, double recall, double floor);

    [LoggerMessage(LogLevel.Warning, "HNSW index {Index} of profile {ProfileId} rebuilt: recall@10 {Before:F3} before, {After:F3} after")]
    private static partial void LogRebuilt(ILogger logger, Guid profileId, string index, double before, double? after);
}

/// <summary>The index-health metric of knowledge design §6.4 on meter <c>Ume.Rag</c>.</summary>
public sealed class HnswIndexMetrics
{
    private readonly Gauge<double> _recall;

    public HnswIndexMetrics(IMeterFactory meters)
    {
        ArgumentNullException.ThrowIfNull(meters);
        var meter = meters.Create(Microsoft.Extensions.Hosting.Extensions.TelemetryName);
        _recall = meter.CreateGauge<double>("rag.retrieval.hnsw_recall",
            description: "recall@10 of an embedding profile's HNSW index against exact search, measured by the index-health check (knowledge design §6.4).");
    }

    public void Recall(Guid profileId, double recall) =>
        _recall.Record(recall, new KeyValuePair<string, object?>("profile", profileId.ToString()));
}
