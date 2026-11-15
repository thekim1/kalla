extern alias fakegateway;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Admin;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Domain.Services;
using Ume.Rag.Infrastructure.Gateway;
using Ume.Rag.TestKit;
using FakeGatewayProgram = fakegateway::Program;

namespace Ume.Rag.AdminApi.Tests;

/// <summary>
/// The clients page's API (admin design §5.9): an Open WebUI client's feedback retention (chat design §13.3, #343), no chat
/// history at <c>sensitive</c> as a field error, a client created with its first key in one transaction, the list's active keys
/// and the client's assistants; §5.9.1: the assistants a client offers set with it and from its side, the keys' last use, and
/// rag-api's address for "How to connect"; §4.5: a changed ceiling or history re-checks the assistants the client offers (#700), with
/// the gateway's lists fetched once per save and outside the config version lock (§4.4, #696).
/// </summary>
[Collection(AdminDatabaseGroup4.Name)]
public sealed class ClientEndpointsTests(AdminDatabaseFixture database)
{
    private const string Pepper = "client-endpoints-test-pepper-0123456789abcdef";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Factory() => database.HeaderFactory().WithWebHostBuilder(web => web.UseSetting("Security:KeyPepper", Pepper));

    private static async Task<JsonElement> Ok(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(expected, text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static object Body(JsonElement client, int? retentionDays) => new
    {
        name = client.GetProperty("name").GetString(),
        kind = client.GetProperty("kind").GetString(),
        classificationCeiling = client.GetProperty("classificationCeiling").GetString(),
        clientRetentionDays = retentionDays,
        version = client.GetProperty("version").GetString(),
    };

    /// <summary>A department with a team.</summary>
    private async Task<(string Code, Guid Department, Guid Team)> SeedTeamAsync()
    {
        var code = "C" + Random.Shared.Next(100_000, 999_999);
        await using var db = TestPostgres.Context(database.Connection(null));
        var department = new Department { Id = Guid.CreateVersion7(), Name = "Clients " + code, CostCenterCode = code, SyncedAt = DateTimeOffset.UtcNow };
        var team = new Team { Id = Guid.CreateVersion7(), DepartmentId = department.Id, Name = "Team " + code, SyncedAt = DateTimeOffset.UtcNow };
        db.AddRange(department, team);
        await db.SaveChangesAsync(Ct);
        return (code, department.Id, team.Id);
    }

    [Fact]
    public async Task An_open_webui_client_keeps_its_feedback_90_days_unless_another_value_is_set()
    {
        var code = "C" + Random.Shared.Next(100_000, 999_999);
        await using var factory = database.HeaderFactory();
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin, code);

        var created = (await Ok(await admin.PostAsJsonAsync("/api/clients",
            new { name = "Open WebUI " + code, kind = ClientKinds.OpenWebUi, classificationCeiling = "internal" }, Ct), HttpStatusCode.Created)).GetProperty("client");
        created.GetProperty("clientRetentionDays").GetInt32().ShouldBe(90);
        var id = created.GetProperty("id").GetGuid();

        var cleared = await admin.PutAsJsonAsync($"/api/clients/{id}", Body(created, null), Ct);
        cleared.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await cleared.Content.ReadAsStringAsync(Ct));

        var changed = await Ok(await admin.PutAsJsonAsync($"/api/clients/{id}", Body(created, 30), Ct));
        changed.GetProperty("clientRetentionDays").GetInt32().ShouldBe(30);

        var integration = (await Ok(await admin.PostAsJsonAsync("/api/clients",
            new { name = "Integration " + code, kind = ClientKinds.Integration, classificationCeiling = "internal" }, Ct), HttpStatusCode.Created)).GetProperty("client");
        integration.GetProperty("clientRetentionDays").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_client_that_keeps_chat_history_cannot_be_sensitive()
    {
        var name = "History " + Guid.NewGuid();
        await using var factory = database.HeaderFactory();
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);

        var problem = await Ok(await admin.PostAsJsonAsync("/api/clients",
            new { name, kind = ClientKinds.Integration, classificationCeiling = Classification.Sensitive, storesHistory = true }, Ct), HttpStatusCode.BadRequest);

        problem.GetProperty("errors").GetProperty("storesHistory")[0].GetString().ShouldBe("A client that keeps chat history cannot be sensitive.");
        problem.GetProperty("errorCodes").GetProperty("storesHistory")[0].GetProperty("code").GetString().ShouldBe(AdminErrorCodes.InvalidValue.Code);
        problem.TryGetProperty("code", out _).ShouldBeFalse(); // a field error, not the CHECK's platform_rule
        await using var db = TestPostgres.Context(database.Connection(null));
        (await db.Clients.AnyAsync(c => c.Name == name, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_client_is_created_with_its_first_key_in_one_transaction()
    {
        var (code, department, team) = await SeedTeamAsync();
        await using var factory = Factory();
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);

        var body = await Ok(await admin.PostAsJsonAsync("/api/clients", new
        {
            name = "Open WebUI " + code, kind = ClientKinds.OpenWebUi, classificationCeiling = Classification.Internal, storesHistory = true,
            firstKey = new { name = "Open WebUI " + code + " key", teamId = team },
        }, Ct), HttpStatusCode.Created);

        var client = body.GetProperty("client");
        var clientId = client.GetProperty("id").GetGuid();
        client.GetProperty("assistants").GetArrayLength().ShouldBe(0);
        var created = body.GetProperty("key");
        var secret = created.GetProperty("secret").GetString()!;
        secret.ShouldStartWith("ume-rag-sk-");
        var key = created.GetProperty("key");
        var keyId = key.GetProperty("id").GetGuid();
        key.GetProperty("clientId").GetGuid().ShouldBe(clientId);
        key.GetProperty("clientName").GetString().ShouldBe("Open WebUI " + code);
        key.GetProperty("teamId").GetGuid().ShouldBe(team);
        key.GetProperty("status").GetString().ShouldBe("active");
        key.GetProperty("prefix").GetString().ShouldBe(secret[..15]);

        await using (var db = TestPostgres.Context(database.Connection(null)))
        {
            var stored = await db.Keys.AsNoTracking().SingleAsync(k => k.ClientId == clientId, Ct);
            stored.Id.ShouldBe(keyId);
            new KeyHasher(Encoding.UTF8.GetBytes(Pepper)).Verify(secret, stored.KeyHash).ShouldBeTrue(); // what rag-api checks
            stored.EncryptedSecret.ShouldNotBeNull();

            // Two audit entries of the one write: the client's and the key's (with the team's department), never the secret.
            var clientAudit = await db.AuditLog.AsNoTracking().SingleAsync(a => a.EntityType == AuditEntityTypes.Client && a.EntityId == clientId.ToString(), Ct);
            var keyAudit = await db.AuditLog.AsNoTracking().SingleAsync(a => a.EntityType == AuditEntityTypes.Key && a.EntityId == keyId.ToString(), Ct);
            (clientAudit.Action, keyAudit.Action).ShouldBe(("create", "create"));
            keyAudit.ConfigVersion.ShouldBe(clientAudit.ConfigVersion);
            keyAudit.DepartmentId.ShouldBe(department);
            clientAudit.DepartmentId.ShouldBeNull();
            keyAudit.Classification.ShouldBe(Classification.Internal);
            keyAudit.Details!.ShouldNotContain(secret[11..]);
            keyAudit.Details!.ShouldNotContain(stored.KeyHash);
        }

        // The secret is shown this once.
        var listed = (await admin.GetFromJsonAsync<JsonElement[]>($"/api/keys?clientId={clientId}", Ct))!.ShouldHaveSingleItem();
        listed.TryGetProperty("secret", out _).ShouldBeFalse();

        // Without a first key: no key.
        var plain = await Ok(await admin.PostAsJsonAsync("/api/clients",
            new { name = "Integration " + code, kind = ClientKinds.Integration, classificationCeiling = Classification.Public }, Ct), HttpStatusCode.Created);
        plain.GetProperty("key").ValueKind.ShouldBe(JsonValueKind.Null);
        var plainId = plain.GetProperty("client").GetProperty("id").GetGuid();
        await using (var db = TestPostgres.Context(database.Connection(null)))
        {
            (await db.Keys.AnyAsync(k => k.ClientId == plainId, Ct)).ShouldBeFalse();
            (await db.AuditLog.CountAsync(a => a.EntityType == AuditEntityTypes.Client && a.EntityId == plainId.ToString(), Ct)).ShouldBe(1);
        }
    }

    [Fact]
    public async Task A_refused_first_key_leaves_no_client()
    {
        var (code, _, _) = await SeedTeamAsync();
        await using var factory = Factory();
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);

        // A team that does not exist: refused inside the write, after the client was added, so the whole transaction rolls back.
        var unknownTeam = await Ok(await admin.PostAsJsonAsync("/api/clients", new
        {
            name = "No team " + code, kind = ClientKinds.Integration, classificationCeiling = Classification.Internal,
            firstKey = new { name = "Key", teamId = Guid.NewGuid() },
        }, Ct), HttpStatusCode.BadRequest);
        unknownTeam.GetProperty("errorCodes").GetProperty("firstKey.teamId")[0].GetProperty("code").GetString().ShouldBe(AdminErrorCodes.NotInScope.Code);

        // A blank key name: a field error of the nested request.
        var blank = await Ok(await admin.PostAsJsonAsync("/api/clients", new
        {
            name = "Blank key " + code, kind = ClientKinds.Integration, classificationCeiling = Classification.Internal, firstKey = new { name = "" },
        }, Ct), HttpStatusCode.BadRequest);
        blank.GetProperty("errorCodes").GetProperty("firstKey.name")[0].GetProperty("code").GetString().ShouldBe(AdminErrorCodes.InvalidValue.Code);

        await using var db = TestPostgres.Context(database.Connection(null));
        (await db.Clients.AnyAsync(c => c.Name == "No team " + code || c.Name == "Blank key " + code, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task The_list_counts_active_keys_and_a_client_lists_the_assistants_of_its_audience()
    {
        var (code, department, _) = await SeedTeamAsync();
        var now = DateTimeOffset.UtcNow;
        Guid clientId, published, draft;
        await using (var db = TestPostgres.Context(database.Connection(null)))
        {
            var client = new Client { Name = "Keys " + code, Kind = ClientKinds.Internal, ClassificationCeiling = Classification.Internal, UpdatedAt = now };
            var other = new Client { Name = "Other " + code, Kind = ClientKinds.Internal, ClassificationCeiling = Classification.Internal, UpdatedAt = now };
            ApiKey Key(Client owner, string name) => new()
            {
                ClientId = owner.Id, Name = name, Prefix = "ume-rag-sk-" + name[..4], KeyHash = Guid.NewGuid().ToString("N"), CreatedAt = now.AddDays(-2),
            };

            // Active: one. Not active: revoked, disabled, expired, in a rotation's grace period, and another client's key.
            var revoked = Key(client, "revoked");
            revoked.RevokedAt = now.AddHours(-1);
            var disabled = Key(client, "disabled");
            disabled.IsEnabled = false;
            var expired = Key(client, "expired");
            expired.ExpiresAt = now.AddHours(-1);
            var grace = Key(client, "grace");
            grace.GraceUntil = now.AddHours(12);
            var expiring = Key(client, "active-until-tomorrow");
            expiring.ExpiresAt = now.AddDays(1);

            var shown = new Assistant { Slug = "cl-a-" + code.ToLowerInvariant(), Name = "A assistant " + code, DepartmentId = department, Classification = Classification.Internal, UpdatedAt = now };
            var unpublished = new Assistant { Slug = "cl-b-" + code.ToLowerInvariant(), Name = "B assistant " + code, DepartmentId = department, Classification = Classification.Internal, UpdatedAt = now };
            var elsewhere = new Assistant { Slug = "cl-c-" + code.ToLowerInvariant(), Name = "C assistant " + code, DepartmentId = department, Classification = Classification.Internal, UpdatedAt = now };
            db.AddRange(client, other, revoked, disabled, expired, grace, expiring, Key(other, "other"), shown, unpublished, elsewhere);
            await db.SaveChangesAsync(Ct);
            var version = new AssistantVersion
            {
                AssistantId = shown.Id, Number = 1, State = AssistantVersionStates.Published, GatewayModel = "ume/chat-eu", PublishedAt = now, PublishedBy = "seed",
                CreatedAt = now, UpdatedAt = now,
            };
            db.AddRange(version,
                new AssistantAudience { AssistantId = shown.Id, Kind = AssistantAudienceKinds.Client, SubjectId = client.Id },
                new AssistantAudience { AssistantId = unpublished.Id, Kind = AssistantAudienceKinds.Client, SubjectId = client.Id },
                new AssistantAudience { AssistantId = elsewhere.Id, Kind = AssistantAudienceKinds.Client, SubjectId = other.Id });
            await db.SaveChangesAsync(Ct);
            shown.PublishedVersionId = version.Id;
            await db.SaveChangesAsync(Ct);
            (clientId, published, draft) = (client.Id, shown.Id, unpublished.Id);
        }

        await using var factory = database.HeaderFactory();
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);

        var rows = (await admin.GetFromJsonAsync<JsonElement[]>("/api/clients", Ct))!;
        rows.Single(r => r.GetProperty("id").GetGuid() == clientId).GetProperty("activeKeys").GetInt32().ShouldBe(1);
        rows.Single(r => r.GetProperty("name").GetString() == "Other " + code).GetProperty("activeKeys").GetInt32().ShouldBe(1);

        var assistants = (await Ok(await admin.GetAsync($"/api/clients/{clientId}", Ct))).GetProperty("assistants");
        assistants.GetArrayLength().ShouldBe(2);
        var (first, second) = (assistants[0], assistants[1]);
        (first.GetProperty("id").GetGuid(), first.GetProperty("name").GetString(), first.GetProperty("departmentId").GetGuid(), first.GetProperty("published").GetBoolean())
            .ShouldBe((published, "A assistant " + code, department, true));
        (second.GetProperty("id").GetGuid(), second.GetProperty("published").GetBoolean()).ShouldBe((draft, false));
    }

    /// <summary>Draft assistants of a department: no published version, so only V7 and V9 apply to their audience.</summary>
    private async Task<Guid[]> SeedAssistantsAsync(string code, Guid department, params string[] levels)
    {
        await using var db = TestPostgres.Context(database.Connection(null));
        var assistants = levels.Select((level, i) => new Assistant
        {
            Slug = $"ca-{i}-{code.ToLowerInvariant()}", Name = $"Assistant {i} {code}", DepartmentId = department, Classification = level, UpdatedAt = DateTimeOffset.UtcNow,
        }).ToList();
        db.AddRange(assistants);
        await db.SaveChangesAsync(Ct);
        return [.. assistants.Select(a => a.Id)];
    }

    [Fact]
    public async Task A_client_is_created_with_the_assistants_it_offers_and_they_are_changed_from_its_side()
    {
        var (code, department, _) = await SeedTeamAsync();
        var ids = await SeedAssistantsAsync(code, department, Classification.Internal, Classification.Public, Classification.Internal);
        await using var factory = Factory();
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);

        var body = await Ok(await admin.PostAsJsonAsync("/api/clients", new
        {
            name = "Integration " + code, kind = ClientKinds.Integration, classificationCeiling = Classification.Internal, assistantIds = new[] { ids[0], ids[1] },
            firstKey = new { name = "Key " + code },
        }, Ct), HttpStatusCode.Created);
        var client = body.GetProperty("client");
        var clientId = client.GetProperty("id").GetGuid();
        var keyId = body.GetProperty("key").GetProperty("key").GetProperty("id").GetGuid();
        body.GetProperty("key").GetProperty("key").GetProperty("lastUsedAt").ValueKind.ShouldBe(JsonValueKind.Null);

        // The answer lists them with what "How to connect" shows: the model name (slug) and the level.
        var offered = client.GetProperty("assistants").EnumerateArray().ToList();
        offered.Select(a => a.GetProperty("id").GetGuid()).ShouldBe([ids[0], ids[1]]);
        (offered[0].GetProperty("slug").GetString(), offered[0].GetProperty("classification").GetString()).ShouldBe(($"ca-0-{code.ToLowerInvariant()}", Classification.Internal));

        await using (var db = TestPostgres.Context(database.Connection(null)))
        {
            (await db.AssistantAudiences.AsNoTracking().Where(a => a.SubjectId == clientId).Select(a => a.AssistantId).ToListAsync(Ct)).ShouldBe([ids[0], ids[1]], ignoreOrder: true);
            // One write: the client's entry and an audiences entry per assistant, in the assistant's department.
            var clientAudit = await db.AuditLog.AsNoTracking().SingleAsync(a => a.EntityType == AuditEntityTypes.Client && a.EntityId == clientId.ToString(), Ct);
            var audiences = await db.AuditLog.AsNoTracking()
                .Where(a => a.EntityType == AuditEntityTypes.Assistant && a.Action == "audiences" && a.ConfigVersion == clientAudit.ConfigVersion).ToListAsync(Ct);
            audiences.Select(a => a.EntityId).ShouldBe([ids[0].ToString(), ids[1].ToString()], ignoreOrder: true);
            audiences.ShouldAllBe(a => a.DepartmentId == department);
            db.KeyLastUse.Add(new KeyLastUse { KeyId = keyId, LastUsedAt = new DateTimeOffset(2026, 11, 13, 9, 30, 0, TimeSpan.Zero) });
            await db.SaveChangesAsync(Ct);
        }

        // The key's last use on the key and, as the latest of its keys, on the client's row.
        var used = new DateTimeOffset(2026, 11, 13, 9, 30, 0, TimeSpan.Zero);
        (await admin.GetFromJsonAsync<JsonElement[]>($"/api/keys?clientId={clientId}", Ct))!.ShouldHaveSingleItem().GetProperty("lastUsedAt").GetDateTimeOffset().ShouldBe(used);
        (await admin.GetFromJsonAsync<JsonElement>($"/api/keys/{keyId}", Ct)).GetProperty("lastUsedAt").GetDateTimeOffset().ShouldBe(used);
        var row = (await admin.GetFromJsonAsync<JsonElement[]>("/api/clients", Ct))!.Single(r => r.GetProperty("id").GetGuid() == clientId);
        row.GetProperty("lastUsedAt").GetDateTimeOffset().ShouldBe(used);

        // From the client's side: the first assistant goes, the third comes, the second stays untouched.
        var version = client.GetProperty("version").GetString();
        var changed = await Ok(await admin.PutAsJsonAsync($"/api/clients/{clientId}/assistants", new { assistantIds = new[] { ids[1], ids[2] }, version }, Ct));
        changed.GetProperty("assistants").EnumerateArray().Select(a => a.GetProperty("id").GetGuid()).ShouldBe([ids[1], ids[2]]);
        changed.GetProperty("version").GetString().ShouldNotBe(version);
        await using (var db = TestPostgres.Context(database.Connection(null)))
        {
            (await db.AssistantAudiences.AsNoTracking().Where(a => a.SubjectId == clientId).Select(a => a.AssistantId).ToListAsync(Ct)).ShouldBe([ids[1], ids[2]], ignoreOrder: true);
            var audit = await db.AuditLog.AsNoTracking().SingleAsync(a => a.EntityType == AuditEntityTypes.Client && a.EntityId == clientId.ToString() && a.Action == "assistants", Ct);
            (await db.AuditLog.AsNoTracking().Where(a => a.Action == "audiences" && a.ConfigVersion == audit.ConfigVersion).Select(a => a.EntityId).ToListAsync(Ct))
                .ShouldBe([ids[0].ToString(), ids[2].ToString()], ignoreOrder: true);
        }

        // The version read before that save is stale now; an assistant that does not exist is a field error.
        (await admin.PutAsJsonAsync($"/api/clients/{clientId}/assistants", new { assistantIds = Array.Empty<Guid>(), version }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var unknown = await Ok(await admin.PutAsJsonAsync($"/api/clients/{clientId}/assistants",
            new { assistantIds = new[] { Guid.NewGuid() }, version = changed.GetProperty("version").GetString() }, Ct), HttpStatusCode.BadRequest);
        unknown.GetProperty("errorCodes").GetProperty("assistantIds")[0].GetProperty("code").GetString().ShouldBe(AdminErrorCodes.InvalidValue.Code);
    }

    [Fact]
    public async Task An_assistant_above_the_clients_level_is_a_field_error_that_names_it_and_leaves_no_client()
    {
        var (code, department, _) = await SeedTeamAsync();
        var ids = await SeedAssistantsAsync(code, department, Classification.Public, Classification.Sensitive);
        await using var factory = Factory();
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);

        // V7: an internal client cannot show a sensitive assistant; nothing of the write stays, the allowed assistant's row included.
        var refused = await Ok(await admin.PostAsJsonAsync("/api/clients", new
        {
            name = "Too low " + code, kind = ClientKinds.Integration, classificationCeiling = Classification.Internal, assistantIds = ids,
            firstKey = new { name = "Key" },
        }, Ct), HttpStatusCode.BadRequest);
        refused.GetProperty("errors").GetProperty("assistantIds")[0].GetString()!.ShouldStartWith($"Assistant 1 {code}: ");
        var coded = refused.GetProperty("errorCodes").GetProperty("assistantIds")[0];
        coded.GetProperty("code").GetString().ShouldBe(AdminErrorCodes.AudienceAboveCeiling.Code);
        (coded.GetProperty("params").GetProperty("subject").GetString(), coded.GetProperty("params").GetProperty("level").GetString())
            .ShouldBe(($"Assistant 1 {code}", Classification.Sensitive));

        await using var db = TestPostgres.Context(database.Connection(null));
        (await db.Clients.AnyAsync(c => c.Name == "Too low " + code, Ct)).ShouldBeFalse();
        (await db.AssistantAudiences.AnyAsync(a => ids.Contains(a.AssistantId), Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task The_connection_names_rag_apis_public_address_when_the_deployment_sets_it()
    {
        await using var unset = Factory();
        using var admin = await AdminDatabaseFixture.UserAsync(unset, RagRoles.PlatformAdmin);
        (await admin.GetFromJsonAsync<JsonElement>("/api/clients/connection", Ct)).GetProperty("apiBaseUrl").ValueKind.ShouldBe(JsonValueKind.Null);

        await using var set = Factory().WithWebHostBuilder(web => web.UseSetting("Admin:ApiPublicUrl", "https://rag.example.se/"));
        using var other = await AdminDatabaseFixture.UserAsync(set, RagRoles.PlatformAdmin);
        (await other.GetFromJsonAsync<JsonElement>("/api/clients/connection", Ct)).GetProperty("apiBaseUrl").GetString().ShouldBe("https://rag.example.se");

        // Platform admins only: the address is for the person who connects an app.
        using var departmentAdmin = await AdminDatabaseFixture.UserAsync(set, RagRoles.DepartmentAdmin);
        (await departmentAdmin.GetAsync("/api/clients/connection", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_deployments_own_client_shows_as_managed_and_its_keys_and_kind_are_not_changed_here()
    {
        // Identity §5.2, M-17 C4: rag-migrate keeps the deployment's Open WebUI client and its one key. Its settings can be
        // edited; its kind, and making, rotating or revoking its keys, are refused with client_managed_by_deployment.
        var code = "M" + Random.Shared.Next(100_000, 999_999);
        Guid clientId, keyId;
        await using (var db = TestPostgres.Context(database.Connection(null)))
        {
            // At most one managed client per kind in a database: this test reuses one, or makes it.
            var managed = await db.Clients.SingleOrDefaultAsync(c => c.Kind == ClientKinds.OpenWebUi && c.IsManagedByDeployment, Ct);
            if (managed is null)
            {
                managed = new Client
                {
                    Name = "Open WebUI " + code, Kind = ClientKinds.OpenWebUi, ClassificationCeiling = Classification.Internal, StoresHistory = true,
                    ClientRetentionDays = 90, IsManagedByDeployment = true, UpdatedAt = DateTimeOffset.UtcNow,
                };
                db.Clients.Add(managed);
            }

            var key = new ApiKey
            {
                ClientId = managed.Id, Name = "deployment " + code, Prefix = "ume-rag-sk-" + code[..4], KeyHash = Guid.NewGuid().ToString("N"), CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Keys.Add(key);
            await db.SaveChangesAsync(Ct);
            (clientId, keyId) = (managed.Id, key.Id);
        }

        await using var factory = Factory();
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);
        var row = (await admin.GetFromJsonAsync<JsonElement[]>("/api/clients", Ct))!.Single(r => r.GetProperty("id").GetGuid() == clientId);
        row.GetProperty("isManagedByDeployment").GetBoolean().ShouldBeTrue();
        var client = await Ok(await admin.GetAsync($"/api/clients/{clientId}", Ct));
        client.GetProperty("isManagedByDeployment").GetBoolean().ShouldBeTrue();

        async Task Refused(HttpResponseMessage response)
        {
            var body = await Ok(response, HttpStatusCode.Conflict);
            body.GetProperty("code").GetString().ShouldBe(AdminErrorCodes.ClientManagedByDeployment.Code);
        }

        await Refused(await admin.PostAsJsonAsync("/api/keys", new { clientId, name = "Another" }, Ct));
        await Refused(await admin.PostAsJsonAsync($"/api/keys/{keyId}/rotate", new { mode = "RevokeImmediately" }, Ct));
        await Refused(await admin.PostAsync($"/api/keys/{keyId}/revoke", null, Ct));

        // Its settings are the admin's; its kind is the deployment's.
        var renamed = await Ok(await admin.PutAsJsonAsync($"/api/clients/{clientId}", new
        {
            name = "Chat " + code, kind = ClientKinds.OpenWebUi, classificationCeiling = Classification.Internal, storesHistory = true, clientRetentionDays = 30,
            version = client.GetProperty("version").GetString(),
        }, Ct));
        renamed.GetProperty("name").GetString().ShouldBe("Chat " + code);
        var kind = await Ok(await admin.PutAsJsonAsync($"/api/clients/{clientId}", new
        {
            name = "Chat " + code, kind = ClientKinds.Integration, classificationCeiling = Classification.Internal, version = renamed.GetProperty("version").GetString(),
        }, Ct), HttpStatusCode.BadRequest);
        kind.GetProperty("errorCodes").GetProperty("kind")[0].GetProperty("code").GetString().ShouldBe(AdminErrorCodes.ClientManagedByDeployment.Code);
    }

    private const string AdminKey = "fake-eu-key-client-checks-0123456"; // rag-admin's credential key
    private const string Listed = "ume/chat-eu"; // the FakeGateway serves it at Eu

    /// <summary>
    /// Counts the gateway's <c>/v1/models</c> calls on the way to the FakeGateway, and those made while a config write holds the
    /// version lock: a probe asks for the lock with <c>NOWAIT</c> before each call.
    /// </summary>
    private sealed class ModelListProbe(HttpMessageHandler inner, string connection) : DelegatingHandler(inner)
    {
        public int ModelLists;
        public int UnderLock;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/v1/models", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref ModelLists);
                await using var db = new NpgsqlConnection(connection);
                await db.OpenAsync(cancellationToken);
                await using var transaction = await db.BeginTransactionAsync(cancellationToken);
                await using var probe = new NpgsqlCommand("""SELECT 1 FROM config."ConfigVersion" WHERE "Id" = 1 FOR UPDATE NOWAIT""", db, transaction);
                try
                {
                    await probe.ExecuteScalarAsync(cancellationToken);
                }
                catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
                {
                    Interlocked.Increment(ref UnderLock);
                }
            }

            return await base.SendAsync(request, cancellationToken);
        }
    }

    private static WebApplicationFactory<FakeGatewayProgram> Gateway() => new WebApplicationFactory<FakeGatewayProgram>().WithWebHostBuilder(web =>
    {
        web.UseEnvironment("Development");
        web.UseSetting("FakeGateway:CredentialKeys:admin:Key", AdminKey);
    });

    /// <summary>The admin API with its credential key, its <c>/v1/models</c> calls through <paramref name="probe"/>.</summary>
    private WebApplicationFactory<Program> GatewayFactory(ModelListProbe probe) => Factory().WithWebHostBuilder(web =>
    {
        web.UseSetting("Gateway:BaseUrl", "http://fake-gateway/");
        web.UseSetting("Gateway:Key", AdminKey);
        web.ConfigureTestServices(services =>
        {
            services.UseGatewayCredentialCeiling();
            services.AddHttpClient(GatewayApi.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => probe);
        });
    });

    /// <summary>
    /// A client at <paramref name="ceiling"/> without history and published assistants of <paramref name="department"/> at
    /// <paramref name="levels"/> answering with <paramref name="model"/> (a mirror row with <paramref name="residency"/>), offered to the
    /// client when <paramref name="offered"/>.
    /// </summary>
    private async Task<(Guid Client, Guid[] Assistants)> SeedPublishedAsync(string code, Guid department, string ceiling, string model, string residency, bool offered,
        params string[] levels)
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = TestPostgres.Context(database.Connection(null));
        if (!await db.GatewayModels.AnyAsync(m => m.Name == model, Ct))
        {
            db.GatewayModels.Add(new GatewayModel { Name = model, ModelKind = GatewayModelModelKinds.Chat, Residencies = [residency], Source = GatewayModelSources.Catalog });
        }

        var client = new Client { Name = "Checked " + code, Kind = ClientKinds.Integration, ClassificationCeiling = ceiling, UpdatedAt = now };
        var assistants = levels.Select((level, i) => new Assistant
        {
            Slug = $"cp-{i}-{code.ToLowerInvariant()}", Name = $"Published {i} {code}", DepartmentId = department, Classification = level, UpdatedAt = now,
        }).ToList();
        db.Add(client);
        db.AddRange(assistants);
        await db.SaveChangesAsync(Ct);
        var versions = assistants.Select(a => new AssistantVersion
        {
            AssistantId = a.Id, Number = 1, State = AssistantVersionStates.Published, GatewayModel = model, PublishedAt = now, PublishedBy = "seed", CreatedAt = now,
            UpdatedAt = now,
        }).ToList();
        db.AddRange(versions);
        if (offered)
        {
            db.AddRange(assistants.Select(a => new AssistantAudience { AssistantId = a.Id, Kind = AssistantAudienceKinds.Client, SubjectId = client.Id }));
        }

        await db.SaveChangesAsync(Ct);
        foreach (var (assistant, version) in assistants.Zip(versions))
        {
            assistant.PublishedVersionId = version.Id;
        }

        await db.SaveChangesAsync(Ct);
        return (client.Id, [.. assistants.Select(a => a.Id)]);
    }

    private static object Update(JsonElement client, string ceiling, bool storesHistory) => new
    {
        name = client.GetProperty("name").GetString(), kind = client.GetProperty("kind").GetString(), classificationCeiling = ceiling, storesHistory,
        version = client.GetProperty("version").GetString(),
    };

    [Fact]
    public async Task A_client_save_lists_each_ceilings_models_once_and_before_the_config_lock()
    {
        // #696: three published assistants at one ceiling without keys need one list, fetched before the write takes the lock.
        var (code, department, _) = await SeedTeamAsync();
        var (clientId, ids) = await SeedPublishedAsync(code, department, Classification.Internal, Listed, "Eu", offered: false,
            Classification.Internal, Classification.Internal, Classification.Internal);
        await using var gateway = Gateway();
        var probe = new ModelListProbe(gateway.Server.CreateHandler(), database.Connection(null));
        await using var factory = GatewayFactory(probe);
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);
        var client = await Ok(await admin.GetAsync($"/api/clients/{clientId}", Ct));

        var offered = await Ok(await admin.PutAsJsonAsync($"/api/clients/{clientId}/assistants", new { assistantIds = ids, version = client.GetProperty("version").GetString() }, Ct));
        offered.GetProperty("assistants").GetArrayLength().ShouldBe(3);
        (probe.ModelLists, probe.UnderLock).ShouldBe((1, 0));

        // #700: switching on history re-checks the three (the internal ceiling stays Eu), again with one list outside the lock.
        var saved = await Ok(await admin.PutAsJsonAsync($"/api/clients/{clientId}", Update(offered, Classification.Internal, storesHistory: true), Ct));
        saved.GetProperty("storesHistory").GetBoolean().ShouldBeTrue();
        (probe.ModelLists, probe.UnderLock).ShouldBe((2, 0));

        // A save that changes neither the ceiling nor history checks nothing.
        await Ok(await admin.PutAsJsonAsync($"/api/clients/{clientId}", new
        {
            name = "Renamed " + code, kind = ClientKinds.Integration, classificationCeiling = Classification.Internal, storesHistory = true,
            version = saved.GetProperty("version").GetString(),
        }, Ct));
        probe.ModelLists.ShouldBe(2);
    }

    [Fact]
    public async Task Lowering_a_clients_ceiling_below_an_offered_assistants_level_is_refused()
    {
        // #700, V7: an internal assistant offered to the client would vanish from it at a public ceiling.
        var (code, department, _) = await SeedTeamAsync();
        var (clientId, _) = await SeedPublishedAsync(code, department, Classification.Internal, Listed, "Eu", offered: true, Classification.Public, Classification.Internal);
        await using var gateway = Gateway();
        var probe = new ModelListProbe(gateway.Server.CreateHandler(), database.Connection(null));
        await using var factory = GatewayFactory(probe);
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);
        var client = await Ok(await admin.GetAsync($"/api/clients/{clientId}", Ct));

        var refused = await Ok(await admin.PutAsJsonAsync($"/api/clients/{clientId}", Update(client, Classification.Public, storesHistory: false), Ct),
            HttpStatusCode.BadRequest);
        refused.GetProperty("errors").GetProperty("classificationCeiling")[0].GetString()!.ShouldStartWith($"Published 1 {code}: ");
        var coded = refused.GetProperty("errorCodes").GetProperty("classificationCeiling")[0];
        coded.GetProperty("code").GetString().ShouldBe(AdminErrorCodes.AudienceAboveCeiling.Code);
        (coded.GetProperty("params").GetProperty("subject").GetString(), coded.GetProperty("params").GetProperty("level").GetString())
            .ShouldBe(($"Published 1 {code}", Classification.Internal));
        probe.UnderLock.ShouldBe(0);

        await using var db = TestPostgres.Context(database.Connection(null));
        (await db.Clients.AsNoTracking().SingleAsync(c => c.Id == clientId, Ct)).ClassificationCeiling.ShouldBe(Classification.Internal);
    }

    [Fact]
    public async Task Switching_on_history_that_raises_an_offered_assistants_ceiling_above_its_model_is_refused()
    {
        // #700, V8: a public assistant's ceiling becomes the internal client's once the client stores history, and its model is
        // served only outside the EU.
        var (code, department, _) = await SeedTeamAsync();
        var external = "external-chat-" + code.ToLowerInvariant();
        var (clientId, _) = await SeedPublishedAsync(code, department, Classification.Internal, external, "External", offered: true, Classification.Public);
        await using var gateway = Gateway();
        var probe = new ModelListProbe(gateway.Server.CreateHandler(), database.Connection(null));
        await using var factory = GatewayFactory(probe);
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);
        var client = await Ok(await admin.GetAsync($"/api/clients/{clientId}", Ct));

        var refused = await Ok(await admin.PutAsJsonAsync($"/api/clients/{clientId}", Update(client, Classification.Internal, storesHistory: true), Ct),
            HttpStatusCode.BadRequest);
        refused.GetProperty("errors").GetProperty("storesHistory")[0].GetString()!.ShouldStartWith($"Published 0 {code}: ");
        var coded = refused.GetProperty("errorCodes").GetProperty("storesHistory")[0];
        coded.GetProperty("code").GetString().ShouldBe(AdminErrorCodes.ModelResidency.Code);
        coded.GetProperty("params").GetProperty("model").GetString().ShouldBe(external);

        await using (var db = TestPostgres.Context(database.Connection(null)))
        {
            (await db.Clients.AsNoTracking().SingleAsync(c => c.Id == clientId, Ct)).StoresHistory.ShouldBeFalse();
        }

        // Without history the public assistant keeps its External ceiling, so the client's other changes save.
        await Ok(await admin.PutAsJsonAsync($"/api/clients/{clientId}", new
        {
            name = "Renamed " + code, kind = ClientKinds.Integration, classificationCeiling = Classification.Internal, version = client.GetProperty("version").GetString(),
        }, Ct));
    }
}
