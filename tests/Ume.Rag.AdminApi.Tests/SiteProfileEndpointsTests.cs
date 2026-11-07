using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Admin;
using Ume.Rag.Domain.Entities;
using Ume.Rag.TestKit;

namespace Ume.Rag.AdminApi.Tests;

/// <summary>
/// Site profiles (admin design §4.5, connectors design §5.6; P1a item 14c): the profile is checked against
/// <c>contracts/site-profiles/v1</c>, changes are <c>edit-site-profile</c> / <c>rollback-site-profile</c> commands carrying
/// <c>decidedBy</c> (#116) with their dedup keys, and the versions are read back.
/// </summary>
[Collection(AdminDatabaseGroup.Name)]
public sealed class SiteProfileEndpointsTests(AdminDatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonNode UmeaProfile() =>
        JsonNode.Parse(File.ReadAllText(TestPostgres.RepositoryFile(Path.Combine("contracts", "site-profiles", "v1", "www.umea.se.profile.json"))))!;

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private async Task EnsureDepartmentAsync()
    {
        await using var db = TestPostgres.Context(database.Connection(null));
        if (!await db.Departments.AnyAsync(Ct))
        {
            db.Departments.Add(new Department { Id = Guid.CreateVersion7(), Name = "Any", CostCenterCode = "SP" + Random.Shared.Next(100_000, 999_999), SyncedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(Ct);
        }
    }

    [Fact]
    public async Task A_hosts_first_profile_is_an_edit_command_with_decided_by()
    {
        await EnsureDepartmentAsync();
        var host = $"www.p{Random.Shared.Next(100_000, 999_999)}.se";
        await using var factory = database.HeaderFactory();
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);

        var broken = UmeaProfile();
        broken["mainRoot"] = "";
        using (var invalid = await admin.PostAsJsonAsync("/api/site-profiles", new { host, level = "public", profile = broken }, Ct))
        {
            invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await Json(invalid)).GetProperty("errorCodes").GetProperty("profile.mainRoot")[0].GetProperty("code").GetString().ShouldBe("site_profile_invalid");
        }

        // #476: the identity rule runs on the non-backtracking engine (as the worker compiles it), which lacks lookarounds.
        var lookahead = UmeaProfile();
        lookahead["identityRule"]!["pattern"] = @"^/(?=\d)(?<id>\d+)\.html$";
        using (var invalid = await admin.PostAsJsonAsync("/api/site-profiles", new { host, level = "public", profile = lookahead }, Ct))
        {
            invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await invalid.Content.ReadAsStringAsync(Ct));
            (await Json(invalid)).GetProperty("errorCodes").GetProperty("profile.identityRule.pattern")[0].GetProperty("code").GetString().ShouldBe("site_profile_invalid");
        }

        using var created = await admin.PostAsJsonAsync("/api/site-profiles", new { host, level = "public", presetId = "sitevision", profile = UmeaProfile() }, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Accepted, await created.Content.ReadAsStringAsync(Ct));
        var jobId = (await Json(created)).GetProperty("jobId").GetGuid();
        using (var again = await admin.PostAsJsonAsync("/api/site-profiles", new { host, level = "public", profile = UmeaProfile() }, Ct))
        {
            again.StatusCode.ShouldBe(HttpStatusCode.Accepted); // the profile isn't written yet: the queued command answers
            (await Json(again)).GetProperty("jobId").GetGuid().ShouldBe(jobId);
        }

        await using var db = TestPostgres.Context(database.Connection(null));
        var job = await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == jobId, Ct);
        (job.Class, job.Classification, job.DedupKey).ShouldBe((JobClasses.Command, "public", $"site-profile:public:{host}"));
        using var payload = JsonDocument.Parse(job.Payload);
        payload.RootElement.GetProperty("kind").GetString().ShouldBe("edit-site-profile");
        payload.RootElement.GetProperty("host").GetString().ShouldBe(host);
        payload.RootElement.GetProperty("decidedBy").GetString().ShouldBe("test-user");
        payload.RootElement.GetProperty("profile").GetProperty("cms").GetString().ShouldBe("sitevision");
        Ume.Rag.Pipelines.Schemas.ContractSchemas.Validator(typeof(Ume.Rag.Pipelines.Jobs.EditSiteProfileCommand)).Validate(payload.RootElement).ShouldBeEmpty();
        (await db.AuditLog.AsNoTracking().CountAsync(a => a.EntityType == "siteprofile" && a.EntityId == $"public:{host}" && a.Action == "site-profile.create", Ct)).ShouldBe(2); // both requests audited
    }

    [Fact]
    public async Task Edit_and_rollback_carry_the_base_version_and_decided_by()
    {
        await EnsureDepartmentAsync();
        var host = $"www.q{Random.Shared.Next(100_000, 999_999)}.se";
        var profileId = Guid.CreateVersion7();
        var profile = UmeaProfile().ToJsonString();
        await using (var db = TestPostgres.Context(database.Connection(null)))
        {
            var v1 = new SiteProfileVersion { SiteProfileId = profileId, Number = 1, Profile = profile, State = SiteProfileVersionStates.Superseded, CreatedBy = "system:worker", CreatedAt = DateTimeOffset.UtcNow };
            var v2 = new SiteProfileVersion { SiteProfileId = profileId, Number = 2, Profile = profile, CreatedBy = "o-admin", CreatedAt = DateTimeOffset.UtcNow, AppliedAt = DateTimeOffset.UtcNow, AppliedBy = "o-admin" };
            db.SiteProfiles.Add(new SiteProfile { Id = profileId, Classification = Classification.Public, Host = host, PresetId = "sitevision", UpdatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(Ct);
            db.SiteProfileVersions.AddRange(v1, v2);
            await db.SaveChangesAsync(Ct);
            (await db.SiteProfiles.SingleAsync(p => p.Id == profileId, Ct)).AppliedVersionId = v2.Id;
            await db.SaveChangesAsync(Ct);
        }

        await using var factory = database.HeaderFactory();
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);

        var listed = (await admin.GetFromJsonAsync<JsonElement[]>($"/api/site-profiles?host={host.ToUpperInvariant()}", Ct))!.ShouldHaveSingleItem();
        (listed.GetProperty("appliedVersion").GetInt32(), listed.GetProperty("origin").GetString()).ShouldBe((2, "manual"));
        (await admin.GetFromJsonAsync<JsonElement[]>($"/api/site-profiles/{profileId}/versions", Ct))!.Select(v => v.GetProperty("number").GetInt32()).ShouldBe([2, 1]);
        (await admin.GetFromJsonAsync<JsonElement>($"/api/site-profiles/{profileId}/versions/1", Ct)).GetProperty("profile").GetProperty("cms").GetString().ShouldBe("sitevision");
        (await admin.PostAsJsonAsync("/api/site-profiles", new { host, level = "public", profile = UmeaProfile() }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest); // exists

        using (var stale = await admin.PutAsJsonAsync($"/api/site-profiles/{profileId}", new { version = 1, profile = UmeaProfile() }, Ct))
        {
            stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await Json(stale)).GetProperty("version").GetInt32().ShouldBe(2);
        }

        using (var edit = await admin.PutAsJsonAsync($"/api/site-profiles/{profileId}", new { version = 2, profile = UmeaProfile() }, Ct))
        {
            edit.StatusCode.ShouldBe(HttpStatusCode.Accepted, await edit.Content.ReadAsStringAsync(Ct));
        }

        (await admin.PostAsJsonAsync($"/api/site-profiles/{profileId}/rollback", new { toVersion = 2 }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest); // applied

        await using var check = TestPostgres.Context(database.Connection(null));
        var editJob = await check.Jobs.AsNoTracking().SingleAsync(j => j.DedupKey == $"site-profile:{profileId}", Ct);
        using (var payload = JsonDocument.Parse(editJob.Payload))
        {
            (payload.RootElement.GetProperty("kind").GetString(), payload.RootElement.GetProperty("baseVersion").GetInt32(),
                payload.RootElement.GetProperty("siteProfileId").GetGuid(), payload.RootElement.GetProperty("decidedBy").GetString())
                .ShouldBe(("edit-site-profile", 2, profileId, "test-user"));
        }

        // The edit's job holds site-profile:{id} until the worker runs it; mark it done to send the rollback.
        await check.Database.ExecuteSqlRawAsync("""UPDATE content."Jobs" SET "State" = 'succeeded' WHERE "Id" = {0}""", [editJob.Id], Ct);
        using (var rollback = await admin.PostAsJsonAsync($"/api/site-profiles/{profileId}/rollback", new { toVersion = 1 }, Ct))
        {
            rollback.StatusCode.ShouldBe(HttpStatusCode.Accepted, await rollback.Content.ReadAsStringAsync(Ct));
            var jobId = (await Json(rollback)).GetProperty("jobId").GetGuid();
            using var payload = JsonDocument.Parse((await check.Jobs.AsNoTracking().SingleAsync(j => j.Id == jobId, Ct)).Payload);
            (payload.RootElement.GetProperty("kind").GetString(), payload.RootElement.GetProperty("toVersion").GetInt32(), payload.RootElement.GetProperty("decidedBy").GetString())
                .ShouldBe(("rollback-site-profile", 1, "test-user"));
        }
    }
}
