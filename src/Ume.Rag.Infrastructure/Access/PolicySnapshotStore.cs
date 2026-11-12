using Microsoft.Extensions.Logging;
using Npgsql;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Access;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Domain.Services;
using Ume.Rag.Infrastructure.Config;

namespace Ume.Rag.Infrastructure.Access;

/// <summary>The snapshot decisions use and whether it is confirmed (identity §4.3 step 0).</summary>
public interface IPolicySnapshotSource
{
    PolicySnapshot Current { get; }

    bool IsConfirmed { get; }
}

/// <summary>
/// The api's <see cref="PolicySnapshot"/> (identity design §4.2, knowledge §12.1, data-model §5): loaded with the api
/// role's grants only (assistants through <c>config."PublishedAssistantVersions"</c>, the granted columns of
/// <c>Sources</c>), rebuilt whole when the config monitor reports a change to any table it holds, and swapped in one
/// reference, so a request always decides on one version. Rows that do not parse are left out and logged (fail closed,
/// identity §4.5 rule 3). The monitor confirms the new version only after this handler succeeded.
/// </summary>
public sealed partial class PolicySnapshotStore(NpgsqlDataSource dataSource, ConfigVersionState state, ILogger<PolicySnapshotStore> logger)
    : IConfigChangeHandler, IPolicySnapshotSource
{
    /// <summary>The outbox entity types (table names) the snapshot is built from.</summary>
    public static IReadOnlySet<string> EntityTypes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "Clients", "Assistants", "AssistantVersions", "AssistantAudiences", "Collections", "Sources", "EmbeddingProfiles",
        "DataProtectionPolicies", "Departments", "Settings",
    };

    private PolicySnapshot _current = PolicySnapshot.Empty;

    /// <summary>The snapshot of the latest applied version (<see cref="PolicySnapshot.Empty"/> before the first load).</summary>
    public PolicySnapshot Current => Volatile.Read(ref _current);

    /// <summary>Whether decisions may use <see cref="Current"/> (identity §4.3 step 0).</summary>
    public bool IsConfirmed => state.IsConfirmed && Current.Version >= 0;

    public async Task ApplyAsync(ConfigChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (Current.Version >= 0 && !EntityTypes.Any(change.Affects))
        {
            Volatile.Write(ref _current, Current with { Version = change.ToVersion });
            return;
        }

        Volatile.Write(ref _current, await LoadAsync(change.ToVersion, cancellationToken));
    }

    /// <summary>Reads the whole snapshot in one repeatable-read transaction, labelled <paramref name="version"/>.</summary>
    public async Task<PolicySnapshot> LoadAsync(long version, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);

        var clients = new Dictionary<Guid, ClientEntry>();
        await ReadAsync(connection, transaction, """
            SELECT "Id", "Name", "Kind", "ClassificationCeiling", "StoresHistory", "IsEnabled" FROM config."Clients"
            """, r =>
        {
            var entry = new ClientEntry(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetBoolean(4), r.GetBoolean(5));
            if (Levels.Rank(entry.Ceiling) is null)
            {
                LogUnparsable(logger, "Clients", entry.Id);
                entry = entry with { IsEnabled = false };
            }

            clients[entry.Id] = entry;
        }, cancellationToken);

        var audiences = new Dictionary<Guid, List<AudienceEntry>>();
        await ReadAsync(connection, transaction, """
            SELECT "AssistantId", "Kind", "SubjectId", "GroupValue" FROM config."AssistantAudiences"
            """, r =>
        {
            var id = r.GetGuid(0);
            (audiences.TryGetValue(id, out var list) ? list : audiences[id] = []).Add(
                new AudienceEntry(r.GetString(1), r.IsDBNull(2) ? null : r.GetGuid(2), r.IsDBNull(3) ? null : r.GetString(3)));
        }, cancellationToken);

        var published = new Dictionary<Guid, PublishedVersionEntry>();
        await ReadAsync(connection, transaction, """
            SELECT "AssistantId", "Id", "Number", "GatewayModel", "CollectionLinks"::text, "SystemPrompt", "QuerySteps"::text, "NotFound"::text, "PublishedAt"
            FROM config."PublishedAssistantVersions"
            """, r => published[r.GetGuid(0)] = new PublishedVersionEntry(r.GetGuid(1), r.GetInt32(2), r.GetString(3), CollectionLink.ParseList(r.GetString(4)),
                r.GetString(5), r.GetString(6), r.GetString(7), r.IsDBNull(8) ? null : r.GetFieldValue<DateTimeOffset>(8)),
            cancellationToken);

        var assistants = new Dictionary<Guid, AssistantEntry>();
        await ReadAsync(connection, transaction, """
            SELECT "Id", "Slug", "Name", "Classification", "HistoryMode", "State", "DepartmentId", "OwnerTeamId", "Description", "ContactName", "ContactEmail",
                   "RetentionDays"
            FROM config."Assistants"
            """, r =>
        {
            var id = r.GetGuid(0);
            var entry = new AssistantEntry(id, r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetGuid(6),
                r.IsDBNull(7) ? null : r.GetGuid(7), published.GetValueOrDefault(id), audiences.TryGetValue(id, out var list) ? list : [],
                r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10), r.GetInt32(11));
            if (Levels.Rank(entry.Classification) is null)
            {
                LogUnparsable(logger, "Assistants", id);
                return;
            }

            assistants[id] = entry;
        }, cancellationToken);

        var collections = new Dictionary<Guid, CollectionEntry>();
        await ReadAsync(connection, transaction, """
            SELECT "Id", "Slug", "Name", "DepartmentId", "Classification", "EmbeddingProfileId", "RetrievalSettings"::text, "PublishedAt",
                   "DeletedAt" IS NOT NULL
            FROM config."Collections"
            """, r =>
        {
            var entry = new CollectionEntry(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetGuid(3), r.GetString(4), r.GetGuid(5), r.GetString(6),
                r.IsDBNull(7) ? null : r.GetFieldValue<DateTimeOffset>(7), r.GetBoolean(8));
            if (Levels.Rank(entry.Classification) is null)
            {
                LogUnparsable(logger, "Collections", entry.Id);
                return;
            }

            collections[entry.Id] = entry;
        }, cancellationToken);

        var sources = new Dictionary<Guid, List<Guid>>();
        var demote = new Dictionary<Guid, string>();
        await ReadAsync(connection, transaction, """
            SELECT "CollectionId", "Id", "DemoteRules"::text FROM config."Sources" WHERE "DeletedAt" IS NULL
            """, r =>
        {
            var collection = r.GetGuid(0);
            (sources.TryGetValue(collection, out var list) ? list : sources[collection] = []).Add(r.GetGuid(1));
            if (!r.IsDBNull(2) && r.GetString(2) is { Length: > 0 } rules && rules != "null")
            {
                demote[r.GetGuid(1)] = rules;
            }
        }, cancellationToken);

        var profiles = new Dictionary<Guid, EmbeddingProfileEntry>();
        await ReadAsync(connection, transaction, """
            SELECT "Id", "Name", "GatewayModel", "Dimensions", "MaxResidency", "State", "CanaryText", "RequestDimensions" FROM config."EmbeddingProfiles"
            """, r =>
        {
            if (!ResidencyCeiling.TryParse(r.GetString(4), out var residency))
            {
                LogUnparsable(logger, "EmbeddingProfiles", r.GetGuid(0));
                return;
            }

            profiles[r.GetGuid(0)] = new EmbeddingProfileEntry(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetInt32(3), residency, r.GetString(5),
                r.IsDBNull(6) ? null : r.GetString(6), r.GetBoolean(7));
        }, cancellationToken);

        var levels = new Dictionary<string, LevelPolicyEntry>(StringComparer.Ordinal);
        await ReadAsync(connection, transaction, """
            SELECT "Classification", "AllowedHistoryModes", "ClientsWithoutHistoryOnly" FROM config."DataProtectionPolicies"
            """, r => levels[r.GetString(0)] = new LevelPolicyEntry(r.GetString(0), r.GetFieldValue<string[]>(1), r.GetBoolean(2)), cancellationToken);
        WithDefaults(levels);

        var departments = new Dictionary<Guid, string>();
        await ReadAsync(connection, transaction, """SELECT "Id", "Name" FROM config."Departments" """,
            r => departments[r.GetGuid(0)] = r.GetString(1), cancellationToken);

        // The answer models' limits (chat design §6.3) and every model's prices for the cost estimate (§8.3); a GatewayModels edit
        // or sync bumps the config version like any config row.
        var limits = new Dictionary<string, ModelLimitsEntry>(StringComparer.Ordinal);
        var prices = new Dictionary<string, ModelPricesEntry>(StringComparer.Ordinal);
        await ReadAsync(connection, transaction, """
            SELECT "Name", "ContextWindow", "MaxOutputTokens", "InputPerMillionUsd", "CachedInputPerMillionUsd", "OutputPerMillionUsd" FROM config."GatewayModels"
            """, r =>
        {
            if (!r.IsDBNull(1) || !r.IsDBNull(2))
            {
                limits[r.GetString(0)] = new ModelLimitsEntry(r.IsDBNull(1) ? null : r.GetInt32(1), r.IsDBNull(2) ? null : r.GetInt32(2));
            }

            if (!r.IsDBNull(3) || !r.IsDBNull(4) || !r.IsDBNull(5))
            {
                prices[r.GetString(0)] = new ModelPricesEntry(r.IsDBNull(3) ? null : r.GetDecimal(3), r.IsDBNull(4) ? null : r.GetDecimal(4),
                    r.IsDBNull(5) ? null : r.GetDecimal(5));
            }
        }, cancellationToken);

        string? retrieval = null;
        string? chat = null;
        string[] trustedProxies = [];
        await ReadAsync(connection, transaction, """SELECT "Retrieval"::text, "Chat"::text, "TrustedProxies" FROM config."Settings" """,
            r => (retrieval, chat, trustedProxies) = (r.GetString(0), r.GetString(1), r.GetFieldValue<string[]>(2)), cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new PolicySnapshot(version, clients, assistants, collections,
            sources.ToDictionary(s => s.Key, s => (IReadOnlyList<Guid>)s.Value), profiles, levels, departments, retrieval, chat)
        {
            DemoteRules = demote,
            TrustedProxies = [.. NetworkRanges.ParseValid(trustedProxies)],
            ModelLimits = limits,
            ModelPrices = prices,
        };
    }

    private static async Task ReadAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, Action<NpgsqlDataReader> row,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            row(reader);
        }
    }

    [LoggerMessage(LogLevel.Error, "A {Table} row ({Id}) does not parse and is left out of the access snapshot (treated as disabled)")]
    private static partial void LogUnparsable(ILogger logger, string table, Guid id);

    /// <summary>
    /// A level without a saved row gets <see cref="DataProtectionPolicy.DefaultFor"/>, as the admin shows it and the blob key
    /// policy applies it (data-model §2.1 seeds no rows), so the policy's step 9 decides on the same values everywhere.
    /// </summary>
    public static void WithDefaults(IDictionary<string, LevelPolicyEntry> levels)
    {
        ArgumentNullException.ThrowIfNull(levels);
        foreach (var level in Classification.All.Where(l => !levels.ContainsKey(l)))
        {
            var policy = DataProtectionPolicy.DefaultFor(level);
            levels[level] = new LevelPolicyEntry(level, policy.AllowedHistoryModes, policy.ClientsWithoutHistoryOnly);
        }
    }
}
