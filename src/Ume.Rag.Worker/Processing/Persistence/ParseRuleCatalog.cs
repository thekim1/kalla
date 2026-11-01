using System.Collections.Concurrent;
using Npgsql;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Infrastructure.Config;
using Ume.Rag.Infrastructure.Jobs;
using Ume.Rag.Worker.Connectors.Persistence;
using Ume.Rag.Worker.Processing.Parse;

namespace Ume.Rag.Worker.Processing.Persistence;

/// <summary>
/// The active admin-made parse rules (<c>content."ParseProfiles"</c>, <c>Origin = manual</c>, <c>State = active</c>;
/// processing design §3.5), cached per config version: read again when the applied config version moves, which every
/// <c>ParseProfiles</c> write does through its outbox trigger (<c>TR_ParseProfiles_ConfigChanged</c>, data-model §5; the
/// parse-rule commands also <see cref="Invalidate"/> it in their own process at once). The sources' departments (their
/// collections', for department-scoped rules) are cached in the same snapshot. A row that does not read is skipped with a
/// warning; a failed read throws, so the stage retries rather than keying documents without their rules.
/// </summary>
public sealed partial class ParseRuleCatalog(JobDatabase database, ConfigVersionState config, ILogger<ParseRuleCatalog> logger)
{
    private const string RulesSql = """
        SELECT "Id", "Classification", "DepartmentId", "Match"::text, "Strategy"::text, "CleanupRules"::text
        FROM content."ParseProfiles" WHERE "Origin" = $1 AND "State" = $2
        """;

    private const string DepartmentSql = """
        SELECT c."DepartmentId" FROM config."Sources" s JOIN config."Collections" c ON c."Id" = s."CollectionId" WHERE s."Id" = $1
        """;

    private volatile Snapshot? _snapshot;

    /// <summary>
    /// Drops the snapshot, so the next parse in this process reads the rules again: called by the parse-rule commands after
    /// their commit, before this process has applied the config version the change moved. Other instances see it at that version.
    /// </summary>
    public void Invalidate() => _snapshot = null;

    /// <summary>The rules of the current snapshot; two callers that find it stale at once may both read, which is harmless.</summary>
    public async Task<AdminParseRuleSet> GetAsync(CancellationToken cancellationToken) => (await SnapshotAsync(cancellationToken)).Rules;

    /// <summary>
    /// The admin-made candidates of a document of source <paramref name="sourceId"/> at level <paramref name="classification"/>
    /// (<see cref="AdminParseRuleSet.Candidates"/>); empty when no rule is in its scope, which leaves the built-in parse key.
    /// </summary>
    public async Task<IReadOnlyList<ParseRule>> CandidatesAsync(string classification, Guid sourceId, string? url, string mediaType,
        CancellationToken cancellationToken)
    {
        var snapshot = await SnapshotAsync(cancellationToken);
        if (snapshot.Rules.Count == 0)
        {
            return [];
        }

        var department = snapshot.Rules.NeedsDepartment ? await DepartmentAsync(snapshot, sourceId, cancellationToken) : null;
        return Candidates(snapshot, classification, department, url, mediaType);
    }

    /// <summary>
    /// The candidates of a document that belongs to no source yet (a preview of a URL against a collection), with
    /// <paramref name="departmentId"/> for department-scoped rules; otherwise as the overload for a source's document.
    /// </summary>
    public async Task<IReadOnlyList<ParseRule>> CandidatesForDepartmentAsync(string classification, Guid departmentId, string? url, string mediaType,
        CancellationToken cancellationToken)
    {
        var snapshot = await SnapshotAsync(cancellationToken);
        return snapshot.Rules.Count == 0 ? [] : Candidates(snapshot, classification, departmentId, url, mediaType);
    }

    private static IReadOnlyList<ParseRule> Candidates(Snapshot snapshot, string classification, Guid? departmentId, string? url, string mediaType)
    {
        var uri = url is not null && Uri.TryCreate(url, UriKind.Absolute, out var parsed) ? parsed : null;
        return snapshot.Rules.Candidates(classification, departmentId, uri, mediaType);
    }

    private async Task<Snapshot> SnapshotAsync(CancellationToken cancellationToken)
    {
        var version = config.Version;
        if (_snapshot is { } current && current.ConfigVersion == version)
        {
            return current;
        }

        var rules = new List<AdminParseRule>();
        await using (var connection = await database.OpenAsync(cancellationToken))
        await using (var command = new NpgsqlCommand(RulesSql, connection) { Parameters = { Db.P(ParseProfileOrigins.Manual), Db.P(ParseProfileStates.Active) } })
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetGuid(0);
                if (AdminParseRuleReader.Read(id, reader.GetString(1), Db.Nullable<Guid>(reader, 2), reader.GetString(3), reader.GetString(4),
                        Db.String(reader, 5), out var error) is { } rule)
                {
                    rules.Add(rule);
                }
                else
                {
                    LogRuleRefused(logger, id, error!);
                }
            }
        }

        var snapshot = new Snapshot(version, new AdminParseRuleSet(rules), new ConcurrentDictionary<Guid, Guid?>());
        _snapshot = snapshot;
        return snapshot;
    }

    private async Task<Guid?> DepartmentAsync(Snapshot snapshot, Guid sourceId, CancellationToken cancellationToken)
    {
        if (snapshot.Departments.TryGetValue(sourceId, out var known))
        {
            return known;
        }

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(DepartmentSql, connection) { Parameters = { Db.P(sourceId) } };
        var department = await command.ExecuteScalarAsync(cancellationToken) is Guid value ? value : (Guid?)null;
        snapshot.Departments[sourceId] = department;
        return department;
    }

    [LoggerMessage(LogLevel.Warning, "Parse rule {ProfileId} is not applied: {Reason}.")]
    private static partial void LogRuleRefused(ILogger logger, Guid profileId, string reason);

    private sealed record Snapshot(long ConfigVersion, AdminParseRuleSet Rules, ConcurrentDictionary<Guid, Guid?> Departments);
}
