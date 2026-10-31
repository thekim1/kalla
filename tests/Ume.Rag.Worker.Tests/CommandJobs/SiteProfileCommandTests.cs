using Ume.Rag.Domain;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Pipelines.Json;
using Ume.Rag.Worker.Commands;
using Ume.Rag.Worker.Jobs;
using Ume.Rag.Worker.Processing.Html;
using Ume.Rag.Worker.Tests.Engine;

namespace Ume.Rag.Worker.Tests.CommandJobs;

/// <summary>
/// <c>edit-site-profile</c> and <c>rollback-site-profile</c> (P1a item 10; connectors design §5.6): the first edit creates the
/// host's profile, every edit is a new applied <c>manual</c> version behind the <c>baseVersion</c> check, a rollback applies
/// an earlier version, and each new applied version enqueues a <c>reprocess</c> sync only for the sources with live HTML
/// documents on that host at that level. Writes commit with the job or not at all.
/// </summary>
[Collection(RagDatabaseGroup.Name)]
public sealed class SiteProfileCommandTests(RagDatabaseFixture database) : IAsyncLifetime
{
    private const string Umea = "www.umea.se";

    private CommandRunner _runner = null!;

    public async ValueTask InitializeAsync()
    {
        // After the reset: it disposes the data sources the runner's queue and database use.
        await database.ResetAsync();
        _runner = new CommandRunner(database);
    }

    public ValueTask DisposeAsync() => _runner.DisposeAsync();

    [Fact]
    public async Task The_first_edit_of_a_host_creates_its_profile_and_applies_version_1()
    {
        var host = Host();
        var run = await _runner.RunAsync(Edit(host, null, null), Guid.NewGuid());

        run.Result.ShouldBeOfType<JobResult.Committed>();
        var result = await CommandSeeds.ResultAsync(database, run.Job.Id);
        var profileId = result.GetProperty("siteProfileId").GetGuid();
        result.GetProperty("appliedVersion").GetInt32().ShouldBe(1);
        (await Sql.ScalarAsync<Guid>(database.Connection(null), """SELECT "AppliedVersionId" FROM content."SiteProfiles" WHERE "Id" = $1""", profileId))
            .ShouldBe(result.GetProperty("appliedVersionId").GetGuid());
        (await Sql.ScalarAsync<string>(database.Connection(null), """
            SELECT p."Classification" || '|' || p."Host" || '|' || p."PresetId" || '|' || v."Origin" || '|' || v."State" || '|' || v."CreatedBy" || '|' || v."AppliedBy"
                   || '|' || (v."Profile" ->> 'mainRoot')
            FROM content."SiteProfiles" p JOIN content."SiteProfileVersions" v ON v."Id" = p."AppliedVersionId" WHERE p."Id" = $1
            """, profileId)).ShouldBe($"internal|{host}|sitevision|manual|applied|admin-oid|admin-oid|{SitevisionPreset.Profile.MainRoot}");
    }

    [Fact]
    public async Task An_edit_of_the_umea_se_profile_enqueues_reprocess_work_only_for_the_hosts_html_documents()
    {
        // A: HTML and a PDF on the host; B: HTML on another host; C: only a PDF on the host; D: HTML on the host at another level;
        // E: the host's HTML, but soft-deleted.
        var a = await EngineKit.SeedUrlListAsync(database, []);
        var b = await EngineKit.SeedUrlListAsync(database, []);
        var c = await EngineKit.SeedUrlListAsync(database, []);
        var d = await EngineKit.SeedUrlListAsync(database, [], Classification.Public);
        var e = await EngineKit.SeedUrlListAsync(database, []);
        try
        {
            await CommandSeeds.DocumentAsync(database, a, $"https://{Umea}/bygga/{Guid.NewGuid():N}");
            await CommandSeeds.DocumentAsync(database, a, $"https://{Umea}/{Guid.NewGuid():N}.pdf", "application/pdf");
            await CommandSeeds.DocumentAsync(database, b, $"https://www.example.org/{Guid.NewGuid():N}");
            await CommandSeeds.DocumentAsync(database, c, $"https://{Umea}/{Guid.NewGuid():N}.pdf", "application/pdf");
            await CommandSeeds.DocumentAsync(database, d, $"https://{Umea}/{Guid.NewGuid():N}");
            var deleted = await CommandSeeds.DocumentAsync(database, e, $"https://{Umea}/{Guid.NewGuid():N}");
            await Sql.ExecuteAsync(database.Connection(null), """UPDATE content."Documents" SET "DeletedAt" = now() WHERE "Id" = $1""", deleted);
            await Sql.ExecuteAsync(database.Connection(null), """DELETE FROM content."SiteProfiles" WHERE "Host" = $1""", Umea);
            var umea = UmeaProfile();
            var first = await _runner.RunAsync(Edit(Umea, null, null) with { Profile = umea }, a.DepartmentId);
            first.Result.ShouldBeOfType<JobResult.Committed>();
            var profileId = (await CommandSeeds.ResultAsync(database, first.Job.Id)).GetProperty("siteProfileId").GetGuid();
            await Sql.ExecuteAsync(database.Connection(null), """DELETE FROM content."Jobs" WHERE "Class" = 'sync'""");

            var edit = await _runner.RunAsync(Edit(Umea, profileId, 1) with { Profile = umea with { LastmodReliable = true } }, a.DepartmentId);

            edit.Result.ShouldBeOfType<JobResult.Committed>();
            (await CommandSeeds.ResultAsync(database, edit.Job.Id)).GetProperty("appliedVersion").GetInt32().ShouldBe(2);
            (await CommandSeeds.SyncJobsAsync(database, a.SourceId)).ShouldBe(1);
            foreach (var other in new[] { b, c, d, e })
            {
                (await CommandSeeds.SyncJobsAsync(database, other.SourceId)).ShouldBe(0);
            }

            var payload = await Sql.ScalarAsync<string>(database.Connection(null), """
                SELECT "Payload"::text || '|' || "DedupKey" || '|' || "Classification" || '|' || "DepartmentId"::text FROM content."Jobs" WHERE "Class" = 'sync'
                """);
            var parts = payload.Split('|');
            var sync = SyncJobsRead(parts[0]);
            (sync.SourceId, sync.Kind, sync.Trigger, sync.RunId).ShouldBe((a.SourceId, SyncKind.Reprocess, RunTrigger.SiteProfile, (Guid?)null));
            parts[1..].ShouldBe([JobDedupKeys.Sync(a.SourceId), a.Classification, a.DepartmentId.ToString()]);
            (await Sql.ScalarAsync<long>(database.Connection(null), """SELECT count(*) FROM content."Jobs" WHERE "Class" <> 'command'""")).ShouldBe(1);
        }
        finally
        {
            foreach (var seed in new[] { a, b, c, d, e })
            {
                await EngineKit.PauseAsync(database, seed);
            }
        }
    }

    [Fact]
    public async Task An_edit_on_a_stale_base_version_is_a_conflict_and_writes_nothing()
    {
        var host = Host();
        var created = await _runner.RunAsync(Edit(host, null, null), Guid.NewGuid());
        var profileId = (await CommandSeeds.ResultAsync(database, created.Job.Id)).GetProperty("siteProfileId").GetGuid();
        (await _runner.RunAsync(Edit(host, profileId, 1), Guid.NewGuid())).Result.ShouldBeOfType<JobResult.Committed>();

        var stale = await _runner.RunAsync(Edit(host, profileId, 1), Guid.NewGuid());
        var noBase = await _runner.RunAsync(Edit(host, null, null), Guid.NewGuid());

        stale.Result.ShouldBe(new JobResult.FatalError(SiteProfileRows.ConflictCode));
        noBase.Result.ShouldBe(new JobResult.FatalError(SiteProfileRows.ConflictCode));
        (await VersionsAsync(profileId)).ShouldBe(2);
        (await AppliedNumberAsync(profileId)).ShouldBe(2);
    }

    [Fact]
    public async Task An_unknown_profile_or_a_profile_of_another_host_is_refused()
    {
        var host = Host();
        var created = await _runner.RunAsync(Edit(host, null, null), Guid.NewGuid());
        var profileId = (await CommandSeeds.ResultAsync(database, created.Job.Id)).GetProperty("siteProfileId").GetGuid();

        (await _runner.RunAsync(Edit(host, Guid.NewGuid(), 1), Guid.NewGuid())).Result.ShouldBe(new JobResult.FatalError(SiteProfileRows.UnknownCode));
        (await _runner.RunAsync(Edit(Host(), profileId, 1), Guid.NewGuid())).Result.ShouldBe(new JobResult.FatalError(EditSiteProfileHandler.HostMismatch));
        (await _runner.RunAsync(Edit(host, profileId, 1), Guid.NewGuid(), Classification.Public)).Result
            .ShouldBe(new JobResult.FatalError(EditSiteProfileHandler.HostMismatch));
        (await _runner.RunAsync(Edit("Not A Host", null, null), Guid.NewGuid())).Result.ShouldBe(new JobResult.FatalError(CommandJobHandler.InvalidPayload));
        (await VersionsAsync(profileId)).ShouldBe(1);
    }

    [Fact]
    public async Task An_edit_whose_lease_was_lost_writes_nothing_and_its_retry_applies_once()
    {
        var host = Host();
        var department = Guid.NewGuid();
        var job = await _runner.LeaseAsync(Edit(host, null, null), department, Classification.Internal);
        await _runner.StealLeaseAsync(job.Id);

        (await _runner.RunAsync(job)).ShouldBeOfType<JobResult.Success>();
        (await Sql.ScalarAsync<long>(database.Connection(null), """SELECT count(*) FROM content."SiteProfiles" WHERE "Host" = $1""", host)).ShouldBe(0);

        var retry = await _runner.RunAsync(Edit(host, null, null), department);
        retry.Result.ShouldBeOfType<JobResult.Committed>();
        var profileId = (await CommandSeeds.ResultAsync(database, retry.Job.Id)).GetProperty("siteProfileId").GetGuid();
        (await VersionsAsync(profileId)).ShouldBe(1);
    }

    [Fact]
    public async Task A_rollback_applies_the_earlier_version_and_reprocesses_the_hosts_sources()
    {
        var seed = await EngineKit.SeedUrlListAsync(database, []);
        try
        {
            var host = Host();
            await CommandSeeds.DocumentAsync(database, seed, $"https://{host}/sida");
            var created = await _runner.RunAsync(Edit(host, null, null), seed.DepartmentId);
            var first = await CommandSeeds.ResultAsync(database, created.Job.Id);
            var profileId = first.GetProperty("siteProfileId").GetGuid();
            (await _runner.RunAsync(Edit(host, profileId, 1), seed.DepartmentId)).Result.ShouldBeOfType<JobResult.Committed>();
            await Sql.ExecuteAsync(database.Connection(null), """DELETE FROM content."Jobs" WHERE "Class" = 'sync'""");

            var rollback = await _runner.RunAsync(Rollback(profileId, 1), seed.DepartmentId);

            rollback.Result.ShouldBeOfType<JobResult.Committed>();
            var result = await CommandSeeds.ResultAsync(database, rollback.Job.Id);
            result.GetProperty("appliedVersionId").GetGuid().ShouldBe(first.GetProperty("appliedVersionId").GetGuid());
            (await AppliedNumberAsync(profileId)).ShouldBe(1);
            (await Sql.ScalarAsync<string>(database.Connection(null), """
                SELECT string_agg("Number" || ':' || "State" || ':' || coalesce("AppliedBy", '-'), ',' ORDER BY "Number") FROM content."SiteProfileVersions" WHERE "SiteProfileId" = $1
                """, profileId)).ShouldBe("1:applied:admin-oid,2:superseded:admin-oid");
            (await CommandSeeds.SyncJobsAsync(database, seed.SourceId)).ShouldBe(1);

            await Sql.ExecuteAsync(database.Connection(null), """DELETE FROM content."Jobs" WHERE "Class" = 'sync'""");
            var again = await _runner.RunAsync(Rollback(profileId, 1), seed.DepartmentId);
            again.Result.ShouldBeOfType<JobResult.Committed>();
            (await CommandSeeds.SyncJobsAsync(database, seed.SourceId)).ShouldBe(0, "the version was applied already");
            (await _runner.RunAsync(Rollback(profileId, 7), seed.DepartmentId)).Result.ShouldBe(new JobResult.FatalError(RollbackSiteProfileHandler.VersionUnknown));
            (await _runner.RunAsync(Rollback(Guid.NewGuid(), 1), seed.DepartmentId)).Result.ShouldBe(new JobResult.FatalError(SiteProfileRows.UnknownCode));
            (await VersionsAsync(profileId)).ShouldBe(2);
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    private static EditSiteProfileCommand Edit(string host, Guid? siteProfileId, int? baseVersion) => new()
    {
        SiteProfileId = siteProfileId,
        Host = host,
        BaseVersion = baseVersion,
        PresetId = "sitevision",
        Profile = SitevisionPreset.Profile,
        DecidedBy = "admin-oid",
    };

    private static RollbackSiteProfileCommand Rollback(Guid siteProfileId, int toVersion) =>
        new() { SiteProfileId = siteProfileId, ToVersion = toVersion, DecidedBy = "admin-oid" };

    private static Pipelines.SiteProfiles.SiteProfile UmeaProfile()
    {
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Processing.UmeaCorpusTests.ProfilePath));
        return ContractJson.Deserialize<Pipelines.SiteProfiles.SiteProfile>(document.RootElement);
    }

    private static string Host() => $"h{Guid.NewGuid():N}.example.se";

    private static SyncPayload SyncJobsRead(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        return ContractJson.Deserialize<SyncPayload>(document.RootElement);
    }

    private Task<long> VersionsAsync(Guid profileId) =>
        Sql.ScalarAsync<long>(database.Connection(null), """SELECT count(*) FROM content."SiteProfileVersions" WHERE "SiteProfileId" = $1""", profileId);

    private Task<int> AppliedNumberAsync(Guid profileId) =>
        Sql.ScalarAsync<int>(database.Connection(null), """
            SELECT v."Number" FROM content."SiteProfiles" p JOIN content."SiteProfileVersions" v ON v."Id" = p."AppliedVersionId" WHERE p."Id" = $1
            """, profileId);
}
