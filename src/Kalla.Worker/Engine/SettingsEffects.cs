using System.Text.Json;
using Npgsql;
using Kalla.Domain.Entities;
using Kalla.Domain.Ports;
using Kalla.Infrastructure.Jobs;
using Kalla.Pipelines.Jobs;
using Kalla.Pipelines.Json;
using Kalla.Pipelines.Sources;
using Kalla.Worker.Connectors.Persistence;

namespace Kalla.Worker.Engine;

/// <summary>Which effect tags of a source's settings differ from the settings it last acted on (connectors design §12.3).</summary>
/// <param name="Identity">An <c>identity</c> member changed: a full re-sync, old identities not re-found removed <c>identity-changed</c>.</param>
/// <param name="Scope">A <c>scope</c> member changed: a traversal now; narrowing removals <c>scope-narrowed</c>.</param>
/// <param name="Fetch">
/// <c>fetchMode</c>, <c>readySelector</c> or <c>scrollToLoad</c> changed (§12.3): a sync now that fetches every page again in the
/// new mode, without validators and <c>lastmod</c> skips.
/// </param>
public sealed record SettingsChange(bool Identity, bool Scope, bool Fetch = false)
{
    public static SettingsChange None { get; } = new(false, false);

    /// <summary>
    /// The change needs a run now (§12.4); other <c>behaviour</c> changes and <c>cosmetic</c> ones wait for the next sync.
    /// </summary>
    public bool NeedsRun => Identity || Scope || Fetch;
}

/// <summary>
/// Settings-change effects (connectors design §12.3, §12.4). Every run stores the per-tag hashes of the settings it opened
/// with (<c>Runs.SettingsHashes</c>, <see cref="SourceSettings.Hashes"/>: the tags are the schemas' <c>x-effect</c>). The
/// scheduler compares an idle source's current hashes with its newest <c>sync</c> run's and enqueues a <c>config-change</c>
/// sync when the scope, identity or fetch hash differs (<see cref="ChangedAsync"/>); a run compares its own hashes with
/// the last <c>sync</c> run that applied its settings (finished, and for a crawl a complete traversal;
/// <see cref="SinceAppliedAsync"/>), so a lost or cancelled <c>config-change</c> run leaves the effect to the next sync.
/// </summary>
public static partial class SettingsEffects
{
    // The settings the source last applied: the newest finished sync run before this one (a crawl's only with a complete traversal).
    private const string SinceAppliedSql = """
        SELECT (SELECT "SettingsHashes"::text FROM content."Runs" WHERE "Id" = $2),
               (SELECT r."SettingsHashes"::text FROM content."Runs" r
                 WHERE r."SourceId" = $1 AND r."Kind" = 'sync' AND r."Id" <> $2 AND r."SettingsHashes" IS NOT NULL
                   AND r."State" IN ('succeeded', 'degraded', 'awaiting-deletion-confirmation')
                   AND coalesce((r."Report" -> 'completeness' ->> 'complete')::boolean, true)
                   AND r."StartedAt" < (SELECT "StartedAt" FROM content."Runs" WHERE "Id" = $2)
                 ORDER BY r."StartedAt" DESC
                 LIMIT 1)
        """;

    // Idle sources with a published pipeline, their settings and the hashes of their newest sync run. A config-change sync
    // that failed before opening a run since then (a refused version) is not enqueued again every pass: the next sync applies it.
    private const string ChangedSql = """
        SELECT s."Id", s."Type", s."Classification", c."DepartmentId", s."Settings"::text, r."SettingsHashes"::text
        FROM config."Sources" s
        JOIN config."Collections" c ON c."Id" = s."CollectionId"
        JOIN config."Pipelines" p ON p."Id" = s."PipelineId"
        CROSS JOIN LATERAL (
            SELECT x."SettingsHashes", x."StartedAt" FROM content."Runs" x
            WHERE x."SourceId" = s."Id" AND x."Kind" = 'sync' AND x."SettingsHashes" IS NOT NULL
            ORDER BY x."StartedAt" DESC
            LIMIT 1) r
        WHERE s."State" = 'active' AND s."DeletedAt" IS NULL AND c."DeletedAt" IS NULL AND p."PublishedVersionId" IS NOT NULL
          AND NOT EXISTS (SELECT 1 FROM content."Runs" o WHERE o."SourceId" = s."Id" AND o."State" = 'running')
          AND NOT EXISTS (SELECT 1 FROM content."Jobs" j
                          WHERE j."Class" = 'sync' AND j."RunId" IS NULL AND j."State" IN ('failed', 'dead') AND j."CompletedAt" > r."StartedAt"
                            AND j."Payload" ->> 'sourceId' = s."Id"::text AND j."Payload" ->> 'trigger' = 'config-change')
        ORDER BY s."Id"
        """;

    /// <summary>The settings' per-tag hashes as <c>Runs.SettingsHashes</c> stores them, or null when they do not bind to the type's record.</summary>
    public static string? HashesJson(string sourceType, JsonElement settings) =>
        Hashes(sourceType, settings) is { } hashes ? ContractJson.Serialize(hashes) : null;

    /// <summary>The tags that differ; nothing when either side is unknown (a run before hashes were stored, settings that do not bind).</summary>
    public static SettingsChange Compare(SettingsHashes? applied, SettingsHashes? current) =>
        applied is null || current is null
            ? SettingsChange.None
            : new SettingsChange(!string.Equals(applied.Identity, current.Identity, StringComparison.Ordinal),
                !string.Equals(applied.Scope, current.Scope, StringComparison.Ordinal),
                !string.Equals(applied.Fetch, current.Fetch, StringComparison.Ordinal));

    /// <summary>What changed between the settings the source last applied and the ones <paramref name="runId"/> opened with.</summary>
    public static async Task<SettingsChange> SinceAppliedAsync(JobDatabase database, Guid sourceId, Guid runId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(SinceAppliedSql, connection) { Parameters = { Db.P(sourceId), Db.P(runId) } };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Compare(Parse(Db.String(reader, 1)), Parse(Db.String(reader, 0))) : SettingsChange.None;
    }

    /// <summary>
    /// The scheduler's part (§12.4): one <c>config-change</c> <c>sync</c> (dedup <c>sync:{sourceId}</c>) for each idle source
    /// whose scope, identity or fetch hash differs from its newest <c>sync</c> run's, except <paramref name="skip"/> (a scheduled
    /// sync is enqueued already). Other behaviour changes and cosmetic ones enqueue nothing. Returns the requests, for the caller's enqueue.
    /// </summary>
    public static async Task<IReadOnlyList<JobRequest>> ChangedAsync(NpgsqlTransaction transaction, IReadOnlySet<Guid> skip, ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(skip);
        var jobs = new List<JobRequest>();
        await using var command = new NpgsqlCommand(ChangedSql, transaction.Connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var sourceId = reader.GetGuid(0);
            if (skip.Contains(sourceId))
            {
                continue;
            }

            using var settings = JsonDocument.Parse(reader.GetString(4));
            var change = Compare(Parse(Db.String(reader, 5)), Hashes(reader.GetString(1), settings.RootElement));
            if (!change.NeedsRun)
            {
                continue;
            }

            var payload = new SyncPayload { SourceId = sourceId, Kind = SyncKind.Sync, Trigger = RunTrigger.ConfigChange };
            jobs.Add(new JobRequest(JobClasses.Sync, reader.GetGuid(3), reader.GetString(2), ContractJson.Serialize(payload))
            {
                DedupKey = JobDedupKeys.Sync(sourceId),
            });
            LogChanged(logger, sourceId, change.Scope, change.Identity, change.Fetch);
        }

        return jobs;
    }

    private static SettingsHashes? Hashes(string sourceType, JsonElement settings)
    {
        try
        {
            return SourceSettings.Hashes(sourceType, settings);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static SettingsHashes? Parse(string? json)
    {
        if (json is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        return ContractJson.Deserialize<SettingsHashes>(document.RootElement);
    }

    [LoggerMessage(LogLevel.Information, "Source {SourceId}: settings changed (scope {Scope}, identity {Identity}, fetch {Fetch}); config-change sync enqueued")]
    private static partial void LogChanged(ILogger logger, Guid sourceId, bool scope, bool identity, bool fetch);
}
