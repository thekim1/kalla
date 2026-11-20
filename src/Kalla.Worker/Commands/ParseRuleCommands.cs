using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using Kalla.Domain.Entities;
using Kalla.Domain.Ports;
using Kalla.Infrastructure.Config;
using Kalla.Infrastructure.Jobs;
using Kalla.Pipelines.Jobs;
using Kalla.Pipelines.Json;
using Kalla.Worker.Connectors.Deletion;
using Kalla.Worker.Connectors.Persistence;
using Kalla.Worker.Engine;
using Kalla.Worker.Jobs;
using Kalla.Worker.Processing.Detect;
using Kalla.Worker.Processing.Parse;
using Kalla.Worker.Processing.Persistence;

namespace Kalla.Worker.Commands;

/// <summary>A <c>ParseProfiles</c> row under its row lock, its <c>jsonb</c> columns as Postgres prints them.</summary>
internal sealed record ParseRuleRow(Guid Id, string Classification, Guid? DepartmentId, string Origin, string State, string Match, string Strategy,
    string? CleanupRules)
{
    /// <summary>The rule's version, as <see cref="ParseRuleCatalog"/> computes it from the same texts.</summary>
    public string Revision => AdminParseRuleReader.Revision(Match, Strategy, CleanupRules);

    /// <summary>The rule as the catalog applies it; null when it is disabled or does not read (then it is never matched).</summary>
    public AdminParseRule? Applied => State == ParseProfileStates.Active
        ? AdminParseRuleReader.Read(Id, Classification, DepartmentId, Match, Strategy, CleanupRules, out _)
        : null;

    /// <summary>The row as the audit entry's <c>before</c> or <c>after</c>.</summary>
    public JsonObject Snapshot() => new()
    {
        ["state"] = State,
        ["revision"] = Revision,
        ["departmentId"] = DepartmentId?.ToString("D"),
        ["match"] = JsonNode.Parse(Match),
        ["strategy"] = JsonNode.Parse(Strategy),
        ["cleanupRules"] = CleanupRules is null ? null : JsonNode.Parse(CleanupRules),
    };
}

/// <summary>
/// What both parse-rule commands lock, write and audit (data-model §2.2 <c>ParseProfiles</c>; processing design §3.5).
/// <c>ParseProfiles</c> keeps only the current content of a rule, so the audit entry each change writes
/// (<c>audit."AuditLog"</c>, entity <c>parserule</c>, <c>before</c>/<c>after</c> with the revisions) is the rule's version
/// history; the actor is the payload's <c>decidedBy</c>, absent = <c>system:worker</c>.
/// </summary>
internal static class ParseRuleRows
{
    public const string UnknownCode = "parse_rule.unknown";
    public const string NotManualCode = "parse_rule.not_manual";
    public const string LevelMismatchCode = "parse_rule.level_mismatch";
    public const string AuditEntityType = "parserule";
    public const string AuditService = "rag-worker";

    public const string Columns = """
        "Id", "Classification", "DepartmentId", "Origin", "State", "Match"::text, "Strategy"::text, "CleanupRules"::text
        """;

    private const string LockSql = $"""SELECT {Columns} FROM content."ParseProfiles" WHERE "Id" = $1 FOR UPDATE""";

    private const string AuditSql = """
        INSERT INTO audit."AuditLog" ("Timestamp", "Actor", "Action", "EntityType", "EntityId", "Details", "Service", "Classification", "DepartmentId")
        VALUES ($1, $2, $3, $4, $5, $6::jsonb, $7, $8, $9)
        """;

    public static async Task<ParseRuleRow?> LockAsync(EngineCommit commit, Guid profileId, CancellationToken cancellationToken) =>
        await ReadAsync(commit, LockSql, cancellationToken, Db.P(profileId));

    /// <summary>The row a statement returns with <see cref="Columns"/>, or null for none.</summary>
    public static async Task<ParseRuleRow?> ReadAsync(EngineCommit commit, string sql, CancellationToken cancellationToken, params NpgsqlParameter[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, commit.Connection, commit.Transaction);
        command.Parameters.AddRange(parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new ParseRuleRow(reader.GetGuid(0), reader.GetString(1), Db.Nullable<Guid>(reader, 2), reader.GetString(3), reader.GetString(4),
                reader.GetString(5), reader.GetString(6), Db.String(reader, 7))
            : null;
    }

    /// <summary>The refusal for a row the command may not change at <paramref name="level"/>, or null.</summary>
    public static string? Refusal(ParseRuleRow row, string level) =>
        row.Origin != ParseProfileOrigins.Manual ? NotManualCode
        : row.Classification != level ? LevelMismatchCode
        : null;

    public static Task AuditAsync(EngineCommit commit, string actor, string action, ParseRuleRow? before, ParseRuleRow after, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var details = new JsonObject { ["before"] = before?.Snapshot(), ["after"] = after.Snapshot() }.ToJsonString();
        return CommandCommit.ExecuteAsync(commit, AuditSql, cancellationToken,
            Db.P(now.ToUniversalTime()), Db.P(actor), Db.P(action), Db.P(AuditEntityType), Db.P(after.Id.ToString("D")), Db.P(details),
            Db.P(AuditService), Db.P(after.Classification), Db.P(after.DepartmentId));
    }

    public static ParseRuleCommandResult Result(ParseRuleRow row, bool changed, int reprocessSyncs) => new()
    {
        ProfileId = row.Id,
        State = row.State == ParseProfileStates.Active ? ParseRuleState.Active : ParseRuleState.Disabled,
        Revision = row.Revision,
        Changed = changed,
        ReprocessSyncs = reprocessSyncs,
    };

    /// <summary>Completes the command; once committed, this process's rule cache is dropped so its next parse reads the change.</summary>
    public static async Task<JobResult> CompleteAsync(JobContext context, EngineCommit commit, IJobQueue queue, WorkerIdentity identity,
        ParseRuleCommandResult result, CancellationToken cancellationToken)
    {
        var completed = await CommandCommit.CompleteAsync(commit, queue, context.Job, identity, result, cancellationToken);
        if (completed is JobResult.Committed && result.Changed)
        {
            context.Services.GetService<ParseRuleCatalog>()?.Invalidate();
        }

        return completed;
    }
}

/// <summary>
/// What a parse-rule change sets off (processing design §3.5; engine design §9.2): a <c>sync</c> of kind <c>reprocess</c>
/// (trigger <c>manual</c>, as <c>reprocess-document</c>; dedup <see cref="JobDedupKeys.Sync"/>) for each active source with
/// live parser-route documents that the rule's old or new scope matches, at the rule's level and in its department. The
/// match is the catalog's own (<see cref="AdminParseRuleSet.Candidates"/>): domain, path glob and media type, never the text
/// pattern, because a rule with a text pattern is a candidate of every document in its scope and so re-keys all of them.
/// The reprocess run's <c>EnsureCurrent</c> then re-processes the documents whose wanted chunk set changed with the rule
/// (the rule part of its salt, <see cref="ChunkSetSalt"/>); a re-parse whose chunks come out as before re-keys the live set.
/// The change moves the config version (<c>TR_ParseProfiles_ConfigChanged</c>), and the syncs wait <see cref="Settle"/>, a few
/// of the config monitor's polls, so the instance that runs them has applied that version (and so read the rules again).
/// </summary>
internal static class ParseRuleFollowUp
{
    /// <summary>How long the syncs wait for every instance to apply the change's config version.</summary>
    public static readonly TimeSpan Settle = 5 * ConfigVersionMonitor.PollInterval;

    private const string DocumentsSql = """
        SELECT d."SourceId", s."Classification", c."DepartmentId", d."Url", d."MediaType"
        FROM content."Documents" d
        JOIN config."Sources" s ON s."Id" = d."SourceId"
        JOIN config."Collections" c ON c."Id" = s."CollectionId"
        WHERE d."Classification" = $1 AND d."DeletedAt" IS NULL AND d."MediaType" = ANY($2)
          AND s."State" = 'active' AND s."DeletedAt" IS NULL AND c."DeletedAt" IS NULL
        ORDER BY d."SourceId"
        """;

    /// <summary>Enqueues the syncs in <paramref name="commit"/>; returns how many were requested.</summary>
    public static async Task<int> EnqueueAsync(EngineCommit commit, IJobQueue queue, string level, IEnumerable<AdminParseRule?> rules,
        CancellationToken cancellationToken)
    {
        var scopes = new AdminParseRuleSet(rules.OfType<AdminParseRule>());
        if (scopes.Count == 0)
        {
            return 0;
        }

        var jobs = new List<JobRequest>();
        var seen = new HashSet<Guid>();
        await using (var command = new NpgsqlCommand(DocumentsSql, commit.Connection, commit.Transaction)
        {
            Parameters = { Db.P(level), Db.P(ProcessingMediaTypes.ParserTypes.ToArray()) },
        })
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var sourceId = reader.GetGuid(0);
                if (seen.Contains(sourceId))
                {
                    continue;
                }

                var departmentId = reader.GetGuid(2);
                var url = Db.String(reader, 3) is { } text && Uri.TryCreate(text, UriKind.Absolute, out var parsed) ? parsed : null;
                if (scopes.Candidates(level, departmentId, url, reader.GetString(4)).Count == 0)
                {
                    continue;
                }

                seen.Add(sourceId);
                jobs.Add(new JobRequest(JobClasses.Sync, departmentId, reader.GetString(1), ContractJson.Serialize(new SyncPayload
                {
                    SourceId = sourceId,
                    Kind = SyncKind.Reprocess,
                    Trigger = RunTrigger.Manual,
                }))
                {
                    DedupKey = JobDedupKeys.Sync(sourceId),
                    Delay = Settle,
                });
            }
        }

        await commit.EnqueueAsync(queue, jobs, cancellationToken);
        return jobs.Count;
    }
}

/// <summary>
/// <c>edit-parse-rule</c> (processing design §3.5, admin design §4.7): the payload's rule becomes the content of the
/// <c>manual</c> <c>ParseProfiles</c> row <c>profileId</c> at the job's level, active; the first edit creates the row. The
/// rule is read as the catalog reads it first, so one the catalog would refuse fails with its <c>parse_rule.*</c> code and
/// writes nothing. A built-in row or one at another level is refused. When the content, department or state changes, the row,
/// its audit entry (the new revision) and the reprocess syncs of the old and the new scope (<see cref="ParseRuleFollowUp"/>)
/// commit with the job; an edit that changes nothing writes nothing. Result <see cref="ParseRuleCommandResult"/>.
/// </summary>
public sealed class EditParseRuleHandler(JobDatabase database, IJobQueue queue, WorkerIdentity identity, TimeProvider time) : ICommandHandler
{
    public const string ConcurrentCreate = "parse_rule.concurrent_create";

    private const string CreateSql = $"""
        INSERT INTO content."ParseProfiles" ("Id", "Classification", "DepartmentId", "Match", "Strategy", "CleanupRules", "Origin", "State")
        VALUES ($1, $2, $3, $4::jsonb, $5::jsonb, $6::jsonb, '{ParseProfileOrigins.Manual}', '{ParseProfileStates.Active}')
        ON CONFLICT ("Id") DO NOTHING
        RETURNING {ParseRuleRows.Columns}
        """;

    private const string UpdateSql = $"""
        UPDATE content."ParseProfiles" SET "DepartmentId" = $2, "Match" = $3::jsonb, "Strategy" = $4::jsonb, "CleanupRules" = $5::jsonb,
                                           "State" = '{ParseProfileStates.Active}'
        WHERE "Id" = $1
          AND ("DepartmentId", "Match", "Strategy", "CleanupRules", "State") IS DISTINCT FROM ($2, $3::jsonb, $4::jsonb, $5::jsonb, '{ParseProfileStates.Active}')
        RETURNING {ParseRuleRows.Columns}
        """;

    public string Kind => CommandKinds.EditParseRule;

    public async Task<JobResult> ExecuteAsync(JobContext context, JsonElement payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (CommandPayload.Read<EditParseRuleCommand>(payload) is not { } edit)
        {
            return new JobResult.FatalError(CommandJobHandler.InvalidPayload);
        }

        var level = context.Job.Classification;
        var match = ContractJson.Serialize(edit.Match);
        var strategy = ContractJson.Serialize(edit.Strategy);
        var cleanup = edit.CleanupRules is null ? null : ContractJson.Serialize(edit.CleanupRules);
        if (AdminParseRuleReader.Read(edit.ProfileId, level, edit.DepartmentId, match, strategy, cleanup, out var error) is null)
        {
            return new JobResult.FatalError(error!);
        }

        var now = time.GetUtcNow();
        await using var commit = await EngineCommit.BeginAsync(database, cancellationToken);
        var before = await ParseRuleRows.LockAsync(commit, edit.ProfileId, cancellationToken);
        if (before is not null && ParseRuleRows.Refusal(before, level) is { } refusal)
        {
            return new JobResult.FatalError(refusal);
        }

        ParseRuleRow? after;
        if (before is null)
        {
            after = await ParseRuleRows.ReadAsync(commit, CreateSql, cancellationToken,
                Db.P(edit.ProfileId), Db.P(level), Db.P(edit.DepartmentId), Db.P(match), Db.P(strategy), Db.P(cleanup));
            if (after is null)
            {
                // Another first edit of the rule won; the retry meets its row.
                return new JobResult.TransientError(ConcurrentCreate, TimeSpan.FromSeconds(1), CountsAttempt: false);
            }
        }
        else
        {
            after = await ParseRuleRows.ReadAsync(commit, UpdateSql, cancellationToken,
                Db.P(edit.ProfileId), Db.P(edit.DepartmentId), Db.P(match), Db.P(strategy), Db.P(cleanup));
            if (after is null)
            {
                return await ParseRuleRows.CompleteAsync(context, commit, queue, identity, ParseRuleRows.Result(before, changed: false, 0), cancellationToken);
            }
        }

        var syncs = await ParseRuleFollowUp.EnqueueAsync(commit, queue, level, [before?.Applied, after.Applied], cancellationToken);
        await ParseRuleRows.AuditAsync(commit, edit.DecidedBy ?? DeletionProposalLifecycle.System, before is null ? "create" : "update", before, after, now,
            cancellationToken);
        return await ParseRuleRows.CompleteAsync(context, commit, queue, identity, ParseRuleRows.Result(after, changed: true, syncs), cancellationToken);
    }
}

/// <summary>
/// <c>disable-parse-rule</c> (processing design §3.5, admin design §4.7): the <c>manual</c> rule's <c>State</c> becomes
/// <c>disabled</c>, so the catalog no longer serves it; the row, its audit entry and the reprocess syncs of its scope
/// (<see cref="ParseRuleFollowUp"/>) commit with the job. A rule that is disabled already is the result as it is, with nothing
/// written. An unknown, built-in or other-level row is refused. Result <see cref="ParseRuleCommandResult"/>.
/// </summary>
public sealed class DisableParseRuleHandler(JobDatabase database, IJobQueue queue, WorkerIdentity identity, TimeProvider time) : ICommandHandler
{
    private const string DisableSql = $"""
        UPDATE content."ParseProfiles" SET "State" = '{ParseProfileStates.Disabled}' WHERE "Id" = $1
        RETURNING {ParseRuleRows.Columns}
        """;

    public string Kind => CommandKinds.DisableParseRule;

    public async Task<JobResult> ExecuteAsync(JobContext context, JsonElement payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (CommandPayload.Read<DisableParseRuleCommand>(payload) is not { } disable)
        {
            return new JobResult.FatalError(CommandJobHandler.InvalidPayload);
        }

        var level = context.Job.Classification;
        var now = time.GetUtcNow();
        await using var commit = await EngineCommit.BeginAsync(database, cancellationToken);
        if (await ParseRuleRows.LockAsync(commit, disable.ProfileId, cancellationToken) is not { } before)
        {
            return new JobResult.FatalError(ParseRuleRows.UnknownCode);
        }

        if (ParseRuleRows.Refusal(before, level) is { } refusal)
        {
            return new JobResult.FatalError(refusal);
        }

        if (before.State == ParseProfileStates.Disabled)
        {
            return await ParseRuleRows.CompleteAsync(context, commit, queue, identity, ParseRuleRows.Result(before, changed: false, 0), cancellationToken);
        }

        var after = (await ParseRuleRows.ReadAsync(commit, DisableSql, cancellationToken, Db.P(disable.ProfileId)))!;
        var syncs = await ParseRuleFollowUp.EnqueueAsync(commit, queue, level, [before.Applied], cancellationToken);
        await ParseRuleRows.AuditAsync(commit, disable.DecidedBy ?? DeletionProposalLifecycle.System, "disable", before, after, now, cancellationToken);
        return await ParseRuleRows.CompleteAsync(context, commit, queue, identity, ParseRuleRows.Result(after, changed: true, syncs), cancellationToken);
    }
}
