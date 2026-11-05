using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;
using Pgvector;
using Ume.Rag.Domain.Entities;
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

/// <summary>
/// One recall measurement of a profile's index: the HNSW-searched scopes, the query chunks over all of them, and the
/// worst scope's recall@10 (null when nothing was measured); <see cref="ByScope"/> has each measured scope's recall.
/// </summary>
public sealed record HnswRecall(int Scopes, int Queries, double? Recall)
{
    /// <summary>The recall of each HNSW-searched scope that had queries, worst first.</summary>
    public IReadOnlyList<HnswScopeRecall> ByScope { get; init; } = [];

    /// <summary>The scope of <see cref="Recall"/>, or null when nothing was measured.</summary>
    public HnswScopeRecall? Worst => ByScope.Count == 0 ? null : ByScope[0];
}

/// <summary>
/// A scope retrieval can search with HNSW (knowledge design §6.4): its collections and, for an assistant's links with
/// topics, the (collection, tag) pairs (§4.2; a collection without a pair is searched whole). <see cref="Assistants"/>
/// are the active published assistants whose links make exactly this scope; empty for a single collection searched by
/// a direct caller.
/// </summary>
public sealed record HnswScope(IReadOnlyList<Guid> CollectionIds, IReadOnlyList<(Guid CollectionId, string Tag)> TopicLinks)
{
    public IReadOnlyList<Guid> Assistants { get; init; } = [];

    /// <summary>The scope's identity: collections and topic pairs, sorted (the same scope from two assistants is one).</summary>
    public string Key { get; } = string.Join(',', CollectionIds.Order().Select(id => id.ToString("D"))) + "|" +
        string.Join(',', TopicLinks.Select(l => l.CollectionId.ToString("D") + ":" + l.Tag).Order(StringComparer.Ordinal));

    public override string ToString()
    {
        var text = "collections " + string.Join(", ", CollectionIds.Order().Select(id => id.ToString("D")));
        if (TopicLinks.Count > 0)
        {
            text += "; topics " + string.Join(", ", TopicLinks.Select(l => l.CollectionId.ToString("D") + ":" + l.Tag).Order(StringComparer.Ordinal));
        }

        return Assistants.Count == 0 ? text : text + "; assistants " + string.Join(", ", Assistants.Select(id => id.ToString("D")));
    }
}

/// <summary>The recall@10 of one scope over its <paramref name="Queries"/> sample chunks.</summary>
public sealed record HnswScopeRecall(HnswScope Scope, int Queries, double Recall);

/// <summary>A check: the first measurement and, when it was below the floor, the measurement after the rebuild.</summary>
public sealed record HnswCheckOutcome(string IndexName, HnswRecall Before, HnswRecall? After)
{
    public bool Rebuilt => After is not null;
}

/// <summary>
/// The index-health check of knowledge design §6.4. The scopes are those retrieval can search: each live collection on
/// the profile alone (a direct caller, or a request narrowed to one collection), and the live linked collections of each
/// active assistant's published version, with the links' topics (Decisions "M-2 open design calls": several small
/// collections can together be HNSW-searched). A scope is HNSW-searched by retrieval's own rule
/// (<see cref="ScopeSizeCache.Choose"/>: more than <c>exactScanMaxChunks</c> live child chunks and at least
/// <c>minHnswSelectivity</c> of the profile's). For each such scope <c>hnswCheckSample</c> random live child chunks of
/// the scope are the queries, with their own embeddings: the approximate top 10
/// (<see cref="RetrievalStatements.VectorApproximate"/>, the half-precision HNSW walk with the role's HNSW settings) is
/// compared with the exact top 10 (<see cref="RetrievalStatements.VectorExact"/>), both with the scope's topic filter.
/// The worst scope's recall@10 is recorded as <c>rag.retrieval.hnsw_recall{profile}</c>, and each scope below
/// <c>hnswRecallFloor</c> is logged by name. Below the floor the index is rebuilt with <c>REINDEX INDEX CONCURRENTLY</c>
/// and measured again.
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

    /// <summary>The live collections and their configured profile: retrieval searches no other (knowledge design §4.4).</summary>
    private const string LiveCollectionsSql = """
        SELECT "Id", "EmbeddingProfileId" FROM config."Collections" WHERE "PublishedAt" IS NOT NULL AND "DeletedAt" IS NULL
        """;

    /// <summary>
    /// The links of every active assistant's published version (the worker's column grants on the view and on
    /// <c>Assistants</c>, data-model §1.2); an archived assistant is not searched (AccessPolicy).
    /// </summary>
    private const string AssistantLinksSql = """
        SELECT v."AssistantId", v."CollectionLinks"::text
        FROM config."PublishedAssistantVersions" v JOIN config."Assistants" a ON a."Id" = v."AssistantId"
        WHERE a."State" = 'active'
        ORDER BY 1
        """;

    /// <summary>
    /// Random live child chunks of the scope: collections <c>$2</c>, limited by the topic pairs <c>$4</c>/<c>$5</c> as
    /// statement V's topic fragment is (a collection without a pair is taken whole).
    /// </summary>
    private const string SampleSql = """
        SELECT c."Embedding"
        FROM content."Chunks" c
        WHERE c."Id" IN (SELECT s."Id" FROM content."Chunks" s
                         WHERE s."EmbeddingProfileId" = $1 AND s."CollectionId" = ANY ($2) AND s."DeletedAt" IS NULL AND s."Kind" = 'child'
                           AND s."Embedding" IS NOT NULL
                           AND (s."CollectionId" <> ALL ($4::uuid[])
                                OR EXISTS (SELECT 1 FROM unnest($4::uuid[], $5::text[]) AS l(cid, tag) WHERE l.cid = s."CollectionId" AND l.tag = ANY (s."Tags")))
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
        LogFailingScopes(profileId, before, settings.RecallFloor);
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
            LogFailingScopes(profileId, after, settings.RecallFloor);
        }

        LogRebuilt(logger, profileId, index, recall, after.Recall);
        return new HnswCheckOutcome(index, before, after);
    }

    /// <summary>
    /// recall@<see cref="K"/> of each of the profile's HNSW-searched scopes (<see cref="ScopesAsync"/>); the worst one is
    /// <see cref="HnswRecall.Recall"/> (knowledge design §6.4; Decisions "M-2 open design calls").
    /// </summary>
    public async Task<HnswRecall> MeasureAsync(Guid profileId, int dimensions, HnswHealthSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var scopes = await ReadScopesAsync(connection, profileId, settings, cancellationToken);
        var queries = 0;
        var byScope = new List<HnswScopeRecall>();
        foreach (var scope in scopes)
        {
            var fragments = scope.TopicLinks.Count > 0 ? FilterFragments.Topics : FilterFragments.None;
            var approximateSql = RetrievalStatements.VectorApproximate(profileId, dimensions, K, fragments);
            var exactSql = RetrievalStatements.VectorExact(profileId, dimensions, K, fragments);
            var scopeQueries = 0;
            long hits = 0;
            long expected = 0;
            foreach (var query in await SampleAsync(connection, profileId, scope, settings.CheckSample, cancellationToken))
            {
                var exact = await TopAsync(connection, exactSql, query, scope, cancellationToken);
                if (exact.Count == 0)
                {
                    continue;
                }

                var approximate = await TopAsync(connection, approximateSql, query, scope, cancellationToken);
                scopeQueries++;
                expected += exact.Count;
                hits += Math.Min(exact.Count, Hits(exact, approximate));
            }

            queries += scopeQueries;
            if (expected > 0)
            {
                byScope.Add(new HnswScopeRecall(scope, scopeQueries, (double)hits / expected));
            }
        }

        byScope = [.. byScope.OrderBy(r => r.Recall)];
        return new HnswRecall(scopes.Count, queries, byScope.Count == 0 ? null : byScope[0].Recall) { ByScope = byScope };
    }

    /// <summary>
    /// The profile's scopes that retrieval searches with HNSW: each live collection on the profile alone, then
    /// each distinct scope of an active assistant's published links (its live linked collections on the profile of the
    /// first live one, as <c>KnowledgeService</c> resolves them, with the links' topics). Sized as
    /// <see cref="ScopeSizeCache.SizeAsync"/> sizes them (the collections' live child chunks against the profile's) and
    /// kept when <see cref="ScopeSizeCache.Choose"/> gives HNSW.
    /// </summary>
    public async Task<IReadOnlyList<HnswScope>> ScopesAsync(Guid profileId, HnswHealthSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await ReadScopesAsync(connection, profileId, settings, cancellationToken);
    }

    private static async Task<List<HnswScope>> ReadScopesAsync(NpgsqlConnection connection, Guid profileId, HnswHealthSettings settings,
        CancellationToken cancellationToken)
    {
        var counts = new Dictionary<Guid, long>();
        await using (var command = new NpgsqlCommand(ScopesSql, connection) { Parameters = { Db.P(profileId) } })
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                counts[reader.GetGuid(0)] = reader.GetInt64(1);
            }
        }

        var live = new Dictionary<Guid, Guid>();
        await using (var command = new NpgsqlCommand(LiveCollectionsSql, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                live[reader.GetGuid(0)] = reader.GetGuid(1);
            }
        }

        var assistantLinks = new List<(Guid Assistant, IReadOnlyList<CollectionLink> Links)>();
        await using (var command = new NpgsqlCommand(AssistantLinksSql, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                assistantLinks.Add((reader.GetGuid(0), CollectionLink.ParseList(reader.GetString(1))));
            }
        }

        var candidates = new List<HnswScope>();
        foreach (var collection in counts.Keys.Where(id => live.TryGetValue(id, out var configured) && configured == profileId).Order())
        {
            candidates.Add(new HnswScope([collection], []));
        }

        var byKey = new Dictionary<string, HnswScope>(StringComparer.Ordinal);
        foreach (var (assistant, links) in assistantLinks)
        {
            // KnowledgeService: live links only, on the first live link's profile; topics per collection from its first link.
            var liveLinks = links.Where(l => live.ContainsKey(l.CollectionId)).DistinctBy(l => l.CollectionId).ToList();
            if (liveLinks.Count == 0 || live[liveLinks[0].CollectionId] != profileId)
            {
                continue;
            }

            liveLinks = [.. liveLinks.Where(l => live[l.CollectionId] == profileId)];
            var scope = new HnswScope([.. liveLinks.Select(l => l.CollectionId)],
                [.. liveLinks.SelectMany(l => l.Tags.Distinct(StringComparer.Ordinal).Select(tag => (l.CollectionId, tag)))]);
            byKey[scope.Key] = byKey.TryGetValue(scope.Key, out var known)
                ? known with { Assistants = [.. known.Assistants, assistant] }
                : scope with { Assistants = [assistant] };
        }

        var singles = candidates.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        candidates.AddRange(byKey.Values.Where(s => !singles.Contains(s.Key)).OrderBy(s => s.Key, StringComparer.Ordinal));

        var platform = new RetrievalPlatformSettings { ExactScanMaxChunks = settings.ExactScanMaxChunks, MinHnswSelectivity = settings.MinHnswSelectivity };
        var total = counts.Values.Sum();
        return [.. candidates.Where(s => ScopeSizeCache.Choose(new ScopeSize(s.CollectionIds.Sum(id => counts.GetValueOrDefault(id)), total), platform).Search ==
                                         VectorSearch.Approximate)];
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

    private void LogFailingScopes(Guid profileId, HnswRecall recall, double floor)
    {
        foreach (var scope in recall.ByScope.Where(s => s.Recall < floor))
        {
            LogFailingScope(logger, profileId, scope.Recall, floor, scope.Queries, scope.Scope.ToString());
        }
    }

    private static NpgsqlParameter Uuids(IEnumerable<Guid> ids) => new() { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid, Value = ids.ToArray() };

    private static NpgsqlParameter Texts(IEnumerable<string> values) => new() { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = values.ToArray() };

    private static async Task<List<Vector>> SampleAsync(NpgsqlConnection connection, Guid profileId, HnswScope scope, int sample,
        CancellationToken cancellationToken)
    {
        var vectors = new List<Vector>();
        await using var command = new NpgsqlCommand(SampleSql, connection)
        {
            Parameters =
            {
                Db.P(profileId), Uuids(scope.CollectionIds), Db.P(sample),
                Uuids(scope.TopicLinks.Select(l => l.CollectionId)), Texts(scope.TopicLinks.Select(l => l.Tag)),
            },
        };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            vectors.Add(reader.GetFieldValue<Vector>(0));
        }

        return vectors;
    }

    /// <summary>Statement V's parameters (PostgresSearchIndex): vector, collections, every level, then the topic pairs when present.</summary>
    private static async Task<List<(Guid Id, double Distance)>> TopAsync(NpgsqlConnection connection, string sql, Vector query, HnswScope scope,
        CancellationToken cancellationToken)
    {
        var top = new List<(Guid, double)>(K);
        await using var command = new NpgsqlCommand(sql, connection)
        {
            Parameters = { new NpgsqlParameter { Value = query }, Uuids(scope.CollectionIds), Texts(Domain.Classification.All) },
        };
        if (scope.TopicLinks.Count > 0)
        {
            command.Parameters.Add(Uuids(scope.TopicLinks.Select(l => l.CollectionId)));
            command.Parameters.Add(Texts(scope.TopicLinks.Select(l => l.Tag)));
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            top.Add((reader.GetGuid(0), reader.GetDouble(9)));
        }

        return top;
    }

    [LoggerMessage(LogLevel.Warning, "HNSW index of profile {ProfileId}: recall@10 {Recall:F3} is below {Floor} over {Queries} queries of the scope {Scope}")]
    private static partial void LogFailingScope(ILogger logger, Guid profileId, double recall, double floor, int queries, string scope);

    [LoggerMessage(LogLevel.Information, "HNSW index of profile {ProfileId}: worst recall@10 {Recall:F3} over {Scopes} scopes, {Queries} queries")]
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
