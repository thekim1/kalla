using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Admin;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Domain.Services;
using Ume.Rag.TestKit;

namespace Ume.Rag.AdminApi.Tests;

/// <summary>
/// The clients page's API (admin design §5.9): an Open WebUI client's feedback retention (chat design §13.3, #343), no chat
/// history at <c>sensitive</c> as a field error, a client created with its first key in one transaction, the list's active keys
/// and the client's assistants.
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
}
