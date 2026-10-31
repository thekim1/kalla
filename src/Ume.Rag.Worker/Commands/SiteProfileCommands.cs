using System.Text.Json;
using Npgsql;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Infrastructure.Jobs;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Pipelines.Json;
using Ume.Rag.Worker.Connectors.Crawl;
using Ume.Rag.Worker.Connectors.Deletion;
using Ume.Rag.Worker.Connectors.Persistence;
using Ume.Rag.Worker.Engine;
using Ume.Rag.Worker.Jobs;
using Ume.Rag.Worker.Processing.Detect;
using SiteProfile = Ume.Rag.Pipelines.SiteProfiles.SiteProfile;

namespace Ume.Rag.Worker.Commands;

/// <summary>
/// What a new applied site-profile version of a (level, host) sets off (engine design §2.3, §9.2, §15; connectors design
/// §5.6, §12.3): a <c>sync</c> of kind <c>reprocess</c>, trigger <c>site-profile</c>, for each active source with live HTML
/// documents on the host at that level, enqueued in the command's transaction with the admin's key for a re-process
/// (<see cref="JobDedupKeys.Sync"/>). The reprocess run iterates the source's live documents and runs <c>EnsureCurrent</c>,
/// which re-extracts only the HTML documents of the host (their wanted set names the new version) from stored raw content.
/// When the version changes the host's <c>excludedTypes</c> (scope) or <c>identityRule</c>/<c>ignoreQueryParams</c>
/// (identity), each crawl source with live documents of any type on the host gets a <c>sync</c> of kind <c>sync</c> instead:
/// its traversal admits by the new profile, its fetches run <c>EnsureCurrent</c> on what they reach, and its reconcile
/// removes the documents of a newly excluded type without a request (<c>scope-narrowed</c>) and the identities the new
/// rules no longer produce (<c>identity-changed</c>), both exempt (<see cref="Connectors.Deletion.SettingsCandidates"/>).
/// A document is on the host when its URL's host, without a port or a trailing dot, is the profile's key
/// (<see cref="Connectors.Urls.SiteProfileHost"/>; stored URLs are canonical, so their hosts are lower-case A-labels already).
/// </summary>
internal static class SiteProfileFollowUp
{
    private const string SourcesSql = $"""
        SELECT DISTINCT d."SourceId", s."Classification", c."DepartmentId", s."Type"
        FROM content."Documents" d
        JOIN config."Sources" s ON s."Id" = d."SourceId"
        JOIN config."Collections" c ON c."Id" = s."CollectionId"
        WHERE d."Classification" = $1 AND d."IdentityKind" = '{IdentityKinds.Url}' AND d."DeletedAt" IS NULL
          AND (d."MediaType" IN ('{ProcessingMediaTypes.Html}', '{ProcessingMediaTypes.Xhtml}') OR ($3 AND s."Type" = '{SourceTypes.Crawl}'))
          AND regexp_replace(lower(substring(d."Url" FROM '^[A-Za-z][A-Za-z0-9+.-]*://([^/?#]+)')), '\.?(:[0-9]*)?$', '') = $2
          AND s."State" = 'active' AND s."DeletedAt" IS NULL AND c."DeletedAt" IS NULL
        ORDER BY d."SourceId"
        """;

    private const string ProfileSql = """SELECT "Profile"::text FROM content."SiteProfileVersions" WHERE "Id" = $1""";

    /// <summary>The profile of a version, or null for none.</summary>
    public static async Task<SiteProfile?> ProfileAsync(EngineCommit commit, Guid? versionId, CancellationToken cancellationToken)
    {
        if (versionId is not { } id || await CommandCommit.ScalarAsync(commit, ProfileSql, cancellationToken, Db.P(id)) is not string json)
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        return ContractJson.Deserialize<SiteProfile>(document.RootElement);
    }

    /// <summary>
    /// Whether the crawl's scope or identity on the host changes from <paramref name="before"/> (null: the host had no
    /// profile, so the default <c>excludedTypes</c>) to <paramref name="after"/> (connectors design §12.3).
    /// </summary>
    public static bool ChangesCrawl(SiteProfile? before, SiteProfile after)
    {
        ArgumentNullException.ThrowIfNull(after);
        return !SameSet(before?.ExcludedTypes ?? CrawlScope.DefaultExcludedTypes, after.ExcludedTypes)
            || !SameSet(before?.IgnoreQueryParams ?? [], after.IgnoreQueryParams)
            || before?.IdentityRule != after.IdentityRule;
    }

    /// <summary>Enqueues the follow-up syncs in <paramref name="commit"/>; <paramref name="crawlSync"/>: <see cref="ChangesCrawl"/>.</summary>
    public static async Task EnqueueAsync(EngineCommit commit, IJobQueue queue, string classification, string host, bool crawlSync,
        CancellationToken cancellationToken)
    {
        var jobs = new List<JobRequest>();
        await using (var command = new NpgsqlCommand(SourcesSql, commit.Connection, commit.Transaction)
        {
            Parameters = { Db.P(classification), Db.P(host), Db.P(crawlSync) },
        })
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var sourceId = reader.GetGuid(0);
                jobs.Add(new JobRequest(JobClasses.Sync, reader.GetGuid(2), reader.GetString(1), ContractJson.Serialize(new SyncPayload
                {
                    SourceId = sourceId,
                    Kind = crawlSync && reader.GetString(3) == SourceTypes.Crawl ? SyncKind.Sync : SyncKind.Reprocess,
                    Trigger = RunTrigger.SiteProfile,
                }))
                {
                    DedupKey = JobDedupKeys.Sync(sourceId),
                });
            }
        }

        await commit.EnqueueAsync(queue, jobs, cancellationToken);
    }

    private static bool SameSet(IReadOnlyList<string> a, IReadOnlyList<string> b) =>
        new HashSet<string>(a, StringComparer.OrdinalIgnoreCase).SetEquals(b);
}

/// <summary>The rows both site-profile commands lock and write (data-model §2.2; connectors design §5.6).</summary>
internal static class SiteProfileRows
{
    public const string ConflictCode = "site_profile.version_conflict";
    public const string UnknownCode = "site_profile.unknown";

    private const string ByIdSql = """
        SELECT p."Id", p."Classification", p."Host", p."AppliedVersionId", v."Number"
        FROM content."SiteProfiles" p LEFT JOIN content."SiteProfileVersions" v ON v."Id" = p."AppliedVersionId"
        WHERE p."Id" = $1
        FOR UPDATE OF p
        """;

    private const string ByHostSql = """
        SELECT p."Id", p."Classification", p."Host", p."AppliedVersionId", v."Number"
        FROM content."SiteProfiles" p LEFT JOIN content."SiteProfileVersions" v ON v."Id" = p."AppliedVersionId"
        WHERE p."Classification" = $1 AND p."Host" = $2
        FOR UPDATE OF p
        """;

    private const string SupersedeSql = """
        UPDATE content."SiteProfileVersions" SET "State" = 'superseded' WHERE "Id" = $1 AND "State" = 'applied'
        """;

    private const string PointerSql = """
        UPDATE content."SiteProfiles" SET "AppliedVersionId" = $2, "PresetId" = coalesce($3, "PresetId"), "UpdatedAt" = $4 WHERE "Id" = $1
        """;

    public static Task<LockedProfile?> LockAsync(EngineCommit commit, Guid siteProfileId, CancellationToken cancellationToken) =>
        ReadAsync(commit, ByIdSql, cancellationToken, Db.P(siteProfileId));

    public static Task<LockedProfile?> LockAsync(EngineCommit commit, string classification, string host, CancellationToken cancellationToken) =>
        ReadAsync(commit, ByHostSql, cancellationToken, Db.P(classification), Db.P(host));

    /// <summary>The applied version moves to <paramref name="versionId"/>; the one it replaces is superseded.</summary>
    public static async Task ApplyAsync(EngineCommit commit, LockedProfile profile, Guid versionId, string? presetId, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (profile.AppliedVersionId is { } previous && previous != versionId)
        {
            await CommandCommit.ExecuteAsync(commit, SupersedeSql, cancellationToken, Db.P(previous));
        }

        await CommandCommit.ExecuteAsync(commit, PointerSql, cancellationToken, Db.P(profile.Id), Db.P(versionId), Db.P(presetId), Db.P(now.ToUniversalTime()));
    }

    private static async Task<LockedProfile?> ReadAsync(EngineCommit commit, string sql, CancellationToken cancellationToken, params NpgsqlParameter[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, commit.Connection, commit.Transaction);
        command.Parameters.AddRange(parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new LockedProfile(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), Db.Nullable<Guid>(reader, 3), Db.Nullable<int>(reader, 4))
            : null;
    }
}

/// <summary>A <c>SiteProfiles</c> row under its row lock, with the number of its applied version.</summary>
internal sealed record LockedProfile(Guid Id, string Classification, string Host, Guid? AppliedVersionId, int? AppliedNumber);

/// <summary>
/// <c>edit-site-profile</c> (connectors design §5.6, admin design §4.5, §4.7): the profile becomes a new <c>manual</c>
/// version of the host's site profile at the job's level and is applied; the first edit of a host creates its
/// <c>SiteProfiles</c> row. <c>baseVersion</c> is an optimistic check against the applied version's number (null = the
/// host has none): a mismatch fails with <c>site_profile.version_conflict</c> and writes nothing. The version, the pointer
/// and the host's reprocess syncs (<see cref="SiteProfileFollowUp"/>) commit with the job. Result
/// <see cref="SiteProfileCommandResult"/>.
/// </summary>
public sealed class EditSiteProfileHandler(JobDatabase database, IJobQueue queue, WorkerIdentity identity, TimeProvider time) : ICommandHandler
{
    public const string HostMismatch = "site_profile.host_mismatch";
    public const string ConcurrentCreate = "site_profile.concurrent_create";

    private const string CreateSql = """
        INSERT INTO content."SiteProfiles" ("Id", "Classification", "Host", "PresetId", "UpdatedAt") VALUES ($1, $2, $3, $4, $5)
        ON CONFLICT ("Classification", "Host") DO NOTHING
        """;

    private const string VersionSql = """
        INSERT INTO content."SiteProfileVersions" ("Id", "SiteProfileId", "Number", "Profile", "Origin", "State", "CreatedBy", "CreatedAt", "AppliedAt", "AppliedBy")
        SELECT $1, $2, coalesce(max("Number"), 0) + 1, $3::jsonb, 'manual', 'applied', $4, $5, $5, $4
        FROM content."SiteProfileVersions" WHERE "SiteProfileId" = $2
        RETURNING "Number"
        """;

    public string Kind => CommandKinds.EditSiteProfile;

    public async Task<JobResult> ExecuteAsync(JobContext context, JsonElement payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (CommandPayload.Read<EditSiteProfileCommand>(payload) is not { } edit)
        {
            return new JobResult.FatalError(CommandJobHandler.InvalidPayload);
        }

        var level = context.Job.Classification;
        var now = time.GetUtcNow();
        await using var commit = await EngineCommit.BeginAsync(database, cancellationToken);
        LockedProfile? profile;
        if (edit.SiteProfileId is { } id)
        {
            profile = await SiteProfileRows.LockAsync(commit, id, cancellationToken);
            if (profile is null)
            {
                return new JobResult.FatalError(SiteProfileRows.UnknownCode);
            }

            if (profile.Host != edit.Host || profile.Classification != level)
            {
                return new JobResult.FatalError(HostMismatch);
            }
        }
        else
        {
            profile = await SiteProfileRows.LockAsync(commit, level, edit.Host, cancellationToken);
        }

        if (profile?.AppliedNumber != edit.BaseVersion)
        {
            return new JobResult.FatalError(SiteProfileRows.ConflictCode);
        }

        if (profile is null)
        {
            var created = Guid.CreateVersion7();
            if (await CommandCommit.ExecuteAsync(commit, CreateSql, cancellationToken,
                    Db.P(created), Db.P(level), Db.P(edit.Host), Db.P(edit.PresetId), Db.P(now.ToUniversalTime())) == 0)
            {
                // Another first edit of the host won; the retry meets its version as a conflict.
                return new JobResult.TransientError(ConcurrentCreate, TimeSpan.FromSeconds(1), CountsAttempt: false);
            }

            profile = new LockedProfile(created, level, edit.Host, null, null);
        }

        var actor = edit.DecidedBy ?? DeletionProposalLifecycle.System;
        var crawlSync = SiteProfileFollowUp.ChangesCrawl(await SiteProfileFollowUp.ProfileAsync(commit, profile.AppliedVersionId, cancellationToken), edit.Profile);
        var versionId = Guid.CreateVersion7();
        var number = (int)(await CommandCommit.ScalarAsync(commit, VersionSql, cancellationToken,
            Db.P(versionId), Db.P(profile.Id), Db.P(ContractJson.Serialize(edit.Profile)), Db.P(actor), Db.P(now.ToUniversalTime())))!;
        await SiteProfileRows.ApplyAsync(commit, profile, versionId, edit.PresetId, now, cancellationToken);
        await SiteProfileFollowUp.EnqueueAsync(commit, queue, level, edit.Host, crawlSync, cancellationToken);
        return await CommandCommit.CompleteAsync(commit, queue, context.Job, identity,
            new SiteProfileCommandResult { SiteProfileId = profile.Id, AppliedVersionId = versionId, AppliedVersion = number }, cancellationToken);
    }
}

/// <summary>
/// <c>rollback-site-profile</c> (connectors design §5.6): an earlier version of the profile is applied again (one click);
/// the version it replaces is superseded and the host's reprocess syncs are enqueued, all with the job. A version that is
/// already applied is the result as it is, with nothing enqueued. Result <see cref="SiteProfileCommandResult"/>.
/// </summary>
public sealed class RollbackSiteProfileHandler(JobDatabase database, IJobQueue queue, WorkerIdentity identity, TimeProvider time) : ICommandHandler
{
    public const string VersionUnknown = "site_profile.version_unknown";

    private const string VersionSql = """
        SELECT "Id" FROM content."SiteProfileVersions" WHERE "SiteProfileId" = $1 AND "Number" = $2
        """;

    private const string ApplySql = """
        UPDATE content."SiteProfileVersions" SET "State" = 'applied', "AppliedAt" = $2, "AppliedBy" = $3 WHERE "Id" = $1
        """;

    public string Kind => CommandKinds.RollbackSiteProfile;

    public async Task<JobResult> ExecuteAsync(JobContext context, JsonElement payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (CommandPayload.Read<RollbackSiteProfileCommand>(payload) is not { } rollback)
        {
            return new JobResult.FatalError(CommandJobHandler.InvalidPayload);
        }

        var now = time.GetUtcNow();
        await using var commit = await EngineCommit.BeginAsync(database, cancellationToken);
        if (await SiteProfileRows.LockAsync(commit, rollback.SiteProfileId, cancellationToken) is not { } profile)
        {
            return new JobResult.FatalError(SiteProfileRows.UnknownCode);
        }

        if (await CommandCommit.ScalarAsync(commit, VersionSql, cancellationToken, Db.P(profile.Id), Db.P(rollback.ToVersion)) is not Guid versionId)
        {
            return new JobResult.FatalError(VersionUnknown);
        }

        if (versionId != profile.AppliedVersionId)
        {
            var crawlSync = SiteProfileFollowUp.ChangesCrawl(await SiteProfileFollowUp.ProfileAsync(commit, profile.AppliedVersionId, cancellationToken),
                (await SiteProfileFollowUp.ProfileAsync(commit, versionId, cancellationToken))!);
            await CommandCommit.ExecuteAsync(commit, ApplySql, cancellationToken,
                Db.P(versionId), Db.P(now.ToUniversalTime()), Db.P(rollback.DecidedBy ?? DeletionProposalLifecycle.System));
            await SiteProfileRows.ApplyAsync(commit, profile, versionId, null, now, cancellationToken);
            await SiteProfileFollowUp.EnqueueAsync(commit, queue, profile.Classification, profile.Host, crawlSync, cancellationToken);
        }

        return await CommandCommit.CompleteAsync(commit, queue, context.Job, identity,
            new SiteProfileCommandResult { SiteProfileId = profile.Id, AppliedVersionId = versionId, AppliedVersion = rollback.ToVersion }, cancellationToken);
    }
}
