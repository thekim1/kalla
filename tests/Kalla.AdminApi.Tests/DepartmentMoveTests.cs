using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Kalla.Infrastructure.Gateway;
using Kalla.Domain;
using Kalla.Domain.Admin;
using Kalla.Domain.Entities;
using Kalla.TestKit;

namespace Kalla.AdminApi.Tests;

/// <summary>
/// Changing the owner department of a knowledge base or an assistant (#811; admin design §3.2 "Changing the owner
/// department"; Decisions "Moving to another department"): platform admins only, the impact and its token, the shares that
/// keep every link working, the pipeline and owner-team refusals, V39 on the new department's key, and the audit under both
/// departments.
/// </summary>
[Collection(AdminDatabaseGroup7.Name)]
public sealed class DepartmentMoveTests(AdminDatabaseFixture database, AdminHost host) : IClassFixture<AdminHost>
{
    private const string Model = "fors/chat-eu";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Seed(Guid Old, string OldCode, Guid User, Guid Target, Guid TargetTeam, Guid Collection, Guid Pipeline);

    /// <summary>
    /// Departments <c>Old</c> (owner of an internal collection with a source on a platform pipeline), <c>User</c> and
    /// <c>Target</c> (with a team).
    /// </summary>
    private async Task<Seed> SeedAsync()
    {
        var n = Random.Shared.Next(100_000, 999_999);
        await using var writer = new ContentFixtureWriter(database.Connection(RagDatabase.Worker), database.Connection(RagDatabase.Admin));
        var profile = await writer.AddEmbeddingProfileAsync(8, Ct);
        await using var db = TestPostgres.Context(database.Connection(null));
        Department NewDepartment(string prefix) =>
            new() { Id = Guid.CreateVersion7(), Name = $"{prefix} {n}", CostCenterCode = $"{prefix[0]}{prefix[1]}{n}", SyncedAt = DateTimeOffset.UtcNow };
        var old = NewDepartment("Fritid");
        var user = NewDepartment("Skola");
        var target = NewDepartment("Vuxen");
        var team = new Team { Id = Guid.CreateVersion7(), Name = $"Komvux {n}", DepartmentId = target.Id, SyncedAt = DateTimeOffset.UtcNow };
        var collection = new Collection
        {
            Slug = $"vuxen-{n}", Name = $"Vuxenutbildning {n}", DepartmentId = old.Id, Classification = Classification.Internal, EmbeddingProfileId = profile,
            PublishedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var pipeline = new Pipeline { Name = $"move-{n}", TemplateId = "website", UpdatedAt = DateTimeOffset.UtcNow };
        var source = new Source
        {
            CollectionId = collection.Id, Type = SourceTypes.Crawl, Name = "umea.se vuxen", Classification = Classification.Internal, PipelineId = pipeline.Id,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.AddRange(old, user, target, team, collection, pipeline, source);
        if (!await db.GatewayModels.AnyAsync(m => m.Name == Model, Ct))
        {
            db.GatewayModels.Add(new GatewayModel { Name = Model, Residencies = ["Eu"], Source = GatewayModelSources.Catalog });
        }

        await db.SaveChangesAsync(Ct);
        return new Seed(old.Id, old.CostCenterCode, user.Id, target.Id, team.Id, collection.Id, pipeline.Id);
    }

    /// <summary>An assistant of <paramref name="department"/> whose published version (or only draft) links <paramref name="collection"/>.</summary>
    private async Task<Guid> AssistantAsync(Guid department, Guid collection, bool published = true)
    {
        await using var db = TestPostgres.Context(database.Connection(null));
        var assistant = new Assistant
        {
            Slug = "move-" + Guid.NewGuid().ToString("N")[..12], Name = "Studievägledning " + Random.Shared.Next(1000, 9999), DepartmentId = department,
            Classification = Classification.Internal, ContactName = "A. Andersson", ContactEmail = "vux@umea.se", UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Add(assistant);
        await db.SaveChangesAsync(Ct);
        var version = new AssistantVersion
        {
            AssistantId = assistant.Id, Number = 1, State = published ? AssistantVersionStates.Published : AssistantVersionStates.Draft, GatewayModel = Model,
            SystemPrompt = "Svara kort.", CollectionLinks = [new CollectionLink(collection, [])], CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            PublishedAt = published ? DateTimeOffset.UtcNow : null, PublishedBy = published ? "test" : null,
        };
        db.Add(version);
        await db.SaveChangesAsync(Ct);
        var linked = db.Assistants.Where(a => a.Id == assistant.Id);
        _ = published
            ? await linked.ExecuteUpdateAsync(u => u.SetProperty(a => a.PublishedVersionId, version.Id), Ct)
            : await linked.ExecuteUpdateAsync(u => u.SetProperty(a => a.DraftVersionId, version.Id), Ct);
        return assistant.Id;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static async Task<string> VersionAsync(HttpClient client, string path) =>
        (await client.GetFromJsonAsync<JsonElement>(path, Ct)).GetProperty("version").GetString()!;

    [Fact]
    public async Task A_collection_moves_with_the_impact_token_and_its_links_stay_working_through_shares()
    {
        var seed = await SeedAsync();
        // Shared with User (whose assistant links it) and with Target (the new owner: that row goes).
        await using (var db = TestPostgres.Context(database.Connection(null)))
        {
            await db.Collections.Where(c => c.Id == seed.Collection).ExecuteUpdateAsync(u => u.SetProperty(c => c.SharedWith, CollectionSharingLevels.Selected), Ct);
            db.CollectionShares.AddRange(
                new CollectionShare { CollectionId = seed.Collection, DepartmentId = seed.User, SharedBy = "test", SharedAt = DateTimeOffset.UtcNow },
                new CollectionShare { CollectionId = seed.Collection, DepartmentId = seed.Target, SharedBy = "test", SharedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(Ct);
        }

        var oldAssistant = await AssistantAsync(seed.Old, seed.Collection);
        await AssistantAsync(seed.User, seed.Collection, published: false);
        var factory = host.Factory;
        using var platform = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);
        using var oldAdmin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, seed.OldCode);
        var path = $"/api/collections/{seed.Collection}";
        var version = await VersionAsync(platform, path);
        var request = new { departmentId = seed.Target, ownerTeamId = seed.TargetTeam, gatewayKeyId = (Guid?)null, version };

        // Department admins cannot move, not even out of their own department.
        (await oldAdmin.PostAsJsonAsync($"{path}/move/impact", request, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await oldAdmin.PostAsJsonAsync($"{path}/move", request, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        JsonElement impact;
        using (var asked = await platform.PostAsJsonAsync($"{path}/move/impact", request, Ct))
        {
            asked.StatusCode.ShouldBe(HttpStatusCode.OK, await asked.Content.ReadAsStringAsync(Ct));
            impact = await Json(asked);
        }

        // Only Old loses it (User is shared already): one share, for Old.
        var share = impact.GetProperty("shares").EnumerateArray().ShouldHaveSingleItem();
        (share.GetProperty("collectionId").GetGuid(), share.GetProperty("departmentId").GetGuid()).ShouldBe((seed.Collection, seed.Old));
        impact.GetProperty("sourcesOnDepartmentPipelines").GetArrayLength().ShouldBe(0);
        var token = impact.GetProperty("impactToken").GetString()!;

        using (var unconfirmed = await platform.PostAsJsonAsync($"{path}/move", request, Ct))
        {
            unconfirmed.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await Json(unconfirmed)).GetProperty("code").GetString().ShouldBe(AdminErrorCodes.MoveNotConfirmed.Code);
        }

        using (var moved = await platform.PostAsJsonAsync($"{path}/move", new { request.departmentId, request.ownerTeamId, request.gatewayKeyId, version, impactToken = token }, Ct))
        {
            moved.StatusCode.ShouldBe(HttpStatusCode.OK, await moved.Content.ReadAsStringAsync(Ct));
            var body = await Json(moved);
            body.GetProperty("departmentId").GetGuid().ShouldBe(seed.Target);
            body.GetProperty("sharedWith").GetString().ShouldBe(CollectionSharingLevels.Selected);
        }

        await using (var db = TestPostgres.Context(database.Connection(null)))
        {
            var collection = await db.Collections.AsNoTracking().SingleAsync(c => c.Id == seed.Collection, Ct);
            (collection.DepartmentId, collection.OwnerTeamId).ShouldBe((seed.Target, (Guid?)seed.TargetTeam));
            (await db.CollectionShares.AsNoTracking().Where(s => s.CollectionId == seed.Collection).Select(s => s.DepartmentId).ToListAsync(Ct))
                .ShouldBe([seed.User, seed.Old], ignoreOrder: true, "the old department gets a share; the new owner's row goes");

            // Audited under both departments; the added share audited on the collection with the move as its reason.
            var entries = await db.AuditLog.AsNoTracking().Where(e => e.EntityId == seed.Collection.ToString()).ToListAsync(Ct);
            entries.Where(e => e.Action == DepartmentMoves.MoveAction).Select(e => e.DepartmentId).ShouldBe([seed.Target, seed.Old], ignoreOrder: true);
            entries.ShouldContain(e => e.Action == "share" && e.Reason == DepartmentMoves.MoveAction);
        }

        // The old department now sees it as shared with it: its assistant's link is linkable again (V11 holds through the share).
        (await oldAdmin.GetFromJsonAsync<JsonElement>(path, Ct)).GetProperty("shared").GetBoolean().ShouldBeTrue();
        (await oldAdmin.GetAsync($"/api/assistants/{oldAssistant}", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // The token was for that version: used again, the move is refused (a second move needs a new impact).
        using var again = await platform.PostAsJsonAsync($"{path}/move",
            new { departmentId = seed.Old, ownerTeamId = (Guid?)null, gatewayKeyId = (Guid?)null, version = await VersionAsync(platform, path), impactToken = token }, Ct);
        (await Json(again)).GetProperty("code").GetString().ShouldBe(AdminErrorCodes.MoveNotConfirmed.Code);
    }

    [Fact]
    public async Task A_collection_with_a_source_on_a_department_pipeline_is_not_moved_and_bad_targets_are_refused()
    {
        var seed = await SeedAsync();
        await using (var db = TestPostgres.Context(database.Connection(null)))
        {
            await db.Pipelines.Where(p => p.Id == seed.Pipeline).ExecuteUpdateAsync(u => u.SetProperty(p => p.DepartmentId, seed.Old), Ct);
        }

        using var platform = await AdminDatabaseFixture.UserAsync(host.Factory, RagRoles.PlatformAdmin);
        var path = $"/api/collections/{seed.Collection}";
        var version = await VersionAsync(platform, path);

        var impact = await Json(await platform.PostAsJsonAsync($"{path}/move/impact", new { departmentId = seed.Target, version }, Ct));
        impact.GetProperty("sourcesOnDepartmentPipelines").EnumerateArray().ShouldHaveSingleItem().GetString()!.ShouldStartWith("umea.se vuxen");
        impact.GetProperty("impactToken").ValueKind.ShouldBe(JsonValueKind.Null);
        using (var refused = await platform.PostAsJsonAsync($"{path}/move", new { departmentId = seed.Target, version, impactToken = "x" }, Ct))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await Json(refused)).GetProperty("code").GetString().ShouldBe(AdminErrorCodes.MovePipelineDepartment.Code);
        }

        // The same department, and an owner team of another department, are invalid values.
        (await platform.PostAsJsonAsync($"{path}/move/impact", new { departmentId = seed.Old, version }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await platform.PostAsJsonAsync($"{path}/move/impact", new { departmentId = seed.User, ownerTeamId = seed.TargetTeam, version }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // Owner team (main session's review on #811): none named takes the new department's only active team, none when it
        // has no team; with several active teams the admin must choose one.
        (await Json(await platform.PostAsJsonAsync($"{path}/move/impact", new { departmentId = seed.Target, version }, Ct)))
            .GetProperty("ownerTeamId").GetGuid().ShouldBe(seed.TargetTeam);
        (await Json(await platform.PostAsJsonAsync($"{path}/move/impact", new { departmentId = seed.User, version }, Ct)))
            .GetProperty("ownerTeamId").ValueKind.ShouldBe(JsonValueKind.Null);
        await using (var db = TestPostgres.Context(database.Connection(null)))
        {
            db.Teams.Add(new Team { Id = Guid.CreateVersion7(), Name = "Second " + seed.Target, DepartmentId = seed.Target, SyncedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(Ct);
        }

        using var choose = await platform.PostAsJsonAsync($"{path}/move/impact", new { departmentId = seed.Target, version }, Ct);
        choose.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await choose.Content.ReadAsStringAsync(Ct)).ShouldContain("ownerTeamId");
    }

    [Fact]
    public async Task An_assistant_moves_its_links_are_shared_with_the_new_department_and_a_key_of_another_department_is_refused()
    {
        var seed = await SeedAsync();
        // Old's collection is "only our department", with a stale row kept from an earlier "selected" level.
        await using (var db = TestPostgres.Context(database.Connection(null)))
        {
            db.CollectionShares.Add(new CollectionShare { CollectionId = seed.Collection, DepartmentId = seed.User, SharedBy = "test", SharedAt = DateTimeOffset.UtcNow });
            db.GatewayKeys.Add(new GatewayKey
            {
                Id = Guid.CreateVersion7(), Name = "Fritid key", TeamId = Guid.CreateVersion7(), DepartmentId = seed.Old, Status = "Active", OwnerActive = true,
                Usable = true, PiiPolicy = "Off", ContentGuardPolicy = "Off", SyncedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(Ct);
        }

        Guid oldKey;
        await using (var db = TestPostgres.Context(database.Connection(null)))
        {
            oldKey = await db.GatewayKeys.Where(k => k.DepartmentId == seed.Old).Select(k => k.Id).SingleAsync(Ct);
        }

        var assistant = await AssistantAsync(seed.Old, seed.Collection, published: false);
        using var platform = await AdminDatabaseFixture.UserAsync(host.Factory, RagRoles.PlatformAdmin);
        var path = $"/api/assistants/{assistant}";
        var version = await VersionAsync(platform, path);

        // A key of the old department is not one of the new department's (V39).
        var withKey = new { departmentId = seed.Target, gatewayKeyId = oldKey, version };
        var keyImpact = await Json(await platform.PostAsJsonAsync($"{path}/move/impact", withKey, Ct));
        using (var refused = await platform.PostAsJsonAsync($"{path}/move",
                   new { withKey.departmentId, withKey.gatewayKeyId, version, impactToken = keyImpact.GetProperty("impactToken").GetString() }, Ct))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Conflict, await refused.Content.ReadAsStringAsync(Ct));
            (await Json(refused)).GetProperty("code").GetString().ShouldBe(AdminErrorCodes.GatewayKeyInvalid.Code);
        }

        // No owner team named: the new department's only team.
        var request = new { departmentId = seed.Target, version };
        var impact = await Json(await platform.PostAsJsonAsync($"{path}/move/impact", request, Ct));
        impact.GetProperty("ownerTeamId").GetGuid().ShouldBe(seed.TargetTeam);
        var share = impact.GetProperty("shares").EnumerateArray().ShouldHaveSingleItem();
        (share.GetProperty("collectionId").GetGuid(), share.GetProperty("departmentId").GetGuid()).ShouldBe((seed.Collection, seed.Target));
        using (var moved = await platform.PostAsJsonAsync($"{path}/move",
                   new { request.departmentId, version, impactToken = impact.GetProperty("impactToken").GetString() }, Ct))
        {
            moved.StatusCode.ShouldBe(HttpStatusCode.OK, await moved.Content.ReadAsStringAsync(Ct));
            var body = await Json(moved);
            (body.GetProperty("departmentId").GetGuid(), body.GetProperty("ownerTeamId").GetGuid()).ShouldBe((seed.Target, seed.TargetTeam));
        }

        await using (var db = TestPostgres.Context(database.Connection(null)))
        {
            var collection = await db.Collections.AsNoTracking().SingleAsync(c => c.Id == seed.Collection, Ct);
            (collection.DepartmentId, collection.SharedWith).ShouldBe((seed.Old, CollectionSharingLevels.Selected), "the collection stays; it is shared");
            (await db.CollectionShares.AsNoTracking().Where(s => s.CollectionId == seed.Collection).Select(s => s.DepartmentId).ToListAsync(Ct))
                .ShouldBe([seed.Target], "the stale row of the department level does not come back");
            var entries = await db.AuditLog.AsNoTracking().Where(e => e.EntityId == assistant.ToString() && e.Action == DepartmentMoves.MoveAction).ToListAsync(Ct);
            entries.Select(e => e.DepartmentId).ShouldBe([seed.Target, seed.Old], ignoreOrder: true);
        }
    }

    [Fact]
    public async Task Only_links_that_work_today_get_shares_and_broken_links_scoped_keys_and_cost_rows_are_kept_as_they_are()
    {
        var seed = await SeedAsync();
        var userTeam = Guid.CreateVersion7();
        Guid key;
        await using (var db = TestPostgres.Context(database.Connection(null)))
        {
            // The old department is inactive (disbanded); a client key of User's team is scoped to the collection; a cost row of yesterday.
            await db.Departments.Where(d => d.Id == seed.Old).ExecuteUpdateAsync(u => u.SetProperty(d => d.IsActive, false), Ct);
            db.Teams.Add(new Team { Id = userTeam, Name = "Elevhälsa " + seed.User, DepartmentId = seed.User, SyncedAt = DateTimeOffset.UtcNow });
            var client = new Client { Name = "Skolportalen " + seed.User, Kind = ClientKinds.Integration, ClassificationCeiling = Classification.Internal, UpdatedAt = DateTimeOffset.UtcNow };
            var apiKey = new ApiKey
            {
                ClientId = client.Id, TeamId = userTeam, Name = "skolportal " + seed.User, Prefix = "sk-test", KeyHash = Guid.NewGuid().ToString("N"),
                Scopes = new KeyScopes([], [seed.Collection]), CreatedAt = DateTimeOffset.UtcNow,
            };
            key = apiKey.Id;
            db.AddRange(client, apiKey);
            db.CollectionDailyUsage.Add(new CollectionDailyUsage
            {
                Day = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)), CollectionId = seed.Collection, Classification = Classification.Internal, Step = "embed",
                DepartmentId = seed.Old, Calls = 3, CostUsd = 0.02m,
            });
            await db.SaveChangesAsync(Ct);
        }

        await AssistantAsync(seed.Old, seed.Collection);
        // User's draft links the collection, which is not shared with User: that link does not work today.
        var broken = await AssistantAsync(seed.User, seed.Collection, published: false);
        using var platform = await AdminDatabaseFixture.UserAsync(host.Factory, RagRoles.PlatformAdmin);
        var path = $"/api/collections/{seed.Collection}";
        var version = await VersionAsync(platform, path);
        var request = new { departmentId = seed.Target, version };

        var impact = await Json(await platform.PostAsJsonAsync($"{path}/move/impact", request, Ct));
        impact.GetProperty("shares").GetArrayLength().ShouldBe(0, "no share for an inactive department, none to bring a broken link into reach");
        impact.GetProperty("brokenLinks").EnumerateArray().ShouldHaveSingleItem().GetProperty("assistantId").GetGuid().ShouldBe(broken);
        impact.GetProperty("scopedKeys").EnumerateArray().ShouldHaveSingleItem().GetProperty("keyId").GetGuid().ShouldBe(key);
        var stale = impact.GetProperty("impactToken").GetString();

        // The plan changes at the same version (Old is active again, so its assistant's link needs a share): the token no longer fits.
        await using (var db = TestPostgres.Context(database.Connection(null)))
        {
            await db.Departments.Where(d => d.Id == seed.Old).ExecuteUpdateAsync(u => u.SetProperty(d => d.IsActive, true), Ct);
        }

        using (var refused = await platform.PostAsJsonAsync($"{path}/move", new { request.departmentId, version, impactToken = stale }, Ct))
        {
            (await Json(refused)).GetProperty("code").GetString().ShouldBe(AdminErrorCodes.MoveNotConfirmed.Code);
        }

        impact = await Json(await platform.PostAsJsonAsync($"{path}/move/impact", request, Ct));
        impact.GetProperty("shares").EnumerateArray().ShouldHaveSingleItem().GetProperty("departmentId").GetGuid().ShouldBe(seed.Old);
        using (var moved = await platform.PostAsJsonAsync($"{path}/move", new { request.departmentId, version, impactToken = impact.GetProperty("impactToken").GetString() }, Ct))
        {
            moved.StatusCode.ShouldBe(HttpStatusCode.OK, await moved.Content.ReadAsStringAsync(Ct));
        }

        await using (var db = TestPostgres.Context(database.Connection(null)))
        {
            (await db.CollectionShares.AsNoTracking().Where(s => s.CollectionId == seed.Collection).Select(s => s.DepartmentId).ToListAsync(Ct))
                .ShouldBe([seed.Old], "User's broken link is not shared into reach");
            (await db.Keys.AsNoTracking().SingleAsync(k => k.Id == key, Ct)).Scopes.CollectionIds.ShouldBe([seed.Collection], "the move changes no key");
            (await db.CollectionDailyUsage.AsNoTracking().SingleAsync(u => u.CollectionId == seed.Collection, Ct)).DepartmentId
                .ShouldBe(seed.Old, "cost rows keep the department of their calls");
            var share = await db.AuditLog.AsNoTracking().SingleAsync(e => e.EntityId == seed.Collection.ToString() && e.Action == "share", Ct);
            (share.Reason, share.Classification).ShouldBe((DepartmentMoves.MoveAction, Classification.Internal));
        }
    }

    [Fact]
    public async Task A_selected_collection_left_with_no_share_falls_back_to_its_department_and_an_assistant_s_broken_link_stays_broken()
    {
        var seed = await SeedAsync();
        await using (var db = TestPostgres.Context(database.Connection(null)))
        {
            await db.Collections.Where(c => c.Id == seed.Collection).ExecuteUpdateAsync(u => u.SetProperty(c => c.SharedWith, CollectionSharingLevels.Selected), Ct);
            db.CollectionShares.Add(new CollectionShare { CollectionId = seed.Collection, DepartmentId = seed.Target, SharedBy = "test", SharedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(Ct);
        }

        using var platform = await AdminDatabaseFixture.UserAsync(host.Factory, RagRoles.PlatformAdmin);
        var path = $"/api/collections/{seed.Collection}";
        var version = await VersionAsync(platform, path);
        var impact = await Json(await platform.PostAsJsonAsync($"{path}/move/impact", new { departmentId = seed.Target, version }, Ct));
        (await platform.PostAsJsonAsync($"{path}/move", new { departmentId = seed.Target, version, impactToken = impact.GetProperty("impactToken").GetString() }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        await using (var db = TestPostgres.Context(database.Connection(null)))
        {
            (await db.Collections.AsNoTracking().SingleAsync(c => c.Id == seed.Collection, Ct)).SharedWith.ShouldBe(CollectionSharingLevels.Department);
            (await db.CollectionShares.AsNoTracking().CountAsync(s => s.CollectionId == seed.Collection, Ct)).ShouldBe(0);
        }

        // An assistant of User with a draft link to Target's collection, which it cannot reach: moving it shares nothing.
        var assistant = await AssistantAsync(seed.User, seed.Collection, published: false);
        var assistantPath = $"/api/assistants/{assistant}";
        var assistantVersion = await VersionAsync(platform, assistantPath);
        var assistantImpact = await Json(await platform.PostAsJsonAsync($"{assistantPath}/move/impact", new { departmentId = seed.Old, version = assistantVersion }, Ct));
        assistantImpact.GetProperty("shares").GetArrayLength().ShouldBe(0);
        assistantImpact.GetProperty("brokenLinks").EnumerateArray().ShouldHaveSingleItem().GetProperty("collectionId").GetGuid().ShouldBe(seed.Collection);
    }

    [Fact]
    public async Task A_published_assistant_is_not_moved_without_a_key_once_the_gateway_acts_as_keys()
    {
        var seed = await SeedAsync();
        var assistant = await AssistantAsync(seed.Old, seed.Collection);
        var features = host.Factory.Services.GetRequiredService<GatewayFeatures>();
        var before = features.Current.ToList();
        features.Update([.. before, GatewayFeatures.ActAsKeyFeature]);
        try
        {
            using var platform = await AdminDatabaseFixture.UserAsync(host.Factory, RagRoles.PlatformAdmin);
            var path = $"/api/assistants/{assistant}";
            var version = await VersionAsync(platform, path);
            var impact = await Json(await platform.PostAsJsonAsync($"{path}/move/impact", new { departmentId = seed.Target, version }, Ct));
            using var refused = await platform.PostAsJsonAsync($"{path}/move",
                new { departmentId = seed.Target, version, impactToken = impact.GetProperty("impactToken").GetString() }, Ct);
            refused.StatusCode.ShouldBe(HttpStatusCode.Conflict, await refused.Content.ReadAsStringAsync(Ct));
            (await Json(refused)).GetProperty("code").GetString().ShouldBe(AdminErrorCodes.GatewayKeyRequired.Code);
        }
        finally
        {
            features.Update(before);
        }
    }
}
