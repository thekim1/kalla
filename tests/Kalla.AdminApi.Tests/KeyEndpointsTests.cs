using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Kalla.Domain;
using Kalla.Domain.Admin;
using Kalla.Domain.Entities;
using Kalla.Domain.Services;
using Kalla.TestKit;

namespace Kalla.AdminApi.Tests;

/// <summary>
/// API keys (identity design §6.4, adapted from the gateway's <c>KeyEndpoints</c>; P1a item 14c, J7): the secret is shown once
/// and stored only as the pepper's HMAC (and Data-Protection-encrypted for reveal), department admins manage the keys of
/// their teams, V23 scopes, rotation with grace, reveal by platform admins only and audited, revoke.
/// </summary>
[Collection(AdminDatabaseGroup4.Name)]
public sealed class KeyEndpointsTests(AdminDatabaseFixture database)
{
    private const string Pepper = "key-endpoints-test-pepper-0123456789abcdef";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Factory() => database.HeaderFactory().WithWebHostBuilder(web => web.UseSetting("Security:KeyPepper", Pepper));

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    /// <summary>A department with a team, a collection in it, a collection elsewhere, and a client.</summary>
    private async Task<(string Code, Guid Team, Guid Mine, Guid Other, Guid Client)> SeedAsync()
    {
        var code = "K" + Random.Shared.Next(100_000, 999_999);
        await using var db = TestPostgres.Context(database.Connection(null));
        var department = new Department { Id = Guid.CreateVersion7(), Name = "Keys " + code, CostCenterCode = code, SyncedAt = DateTimeOffset.UtcNow };
        var elsewhere = new Department { Id = Guid.CreateVersion7(), Name = "Else " + code, CostCenterCode = "E" + code, SyncedAt = DateTimeOffset.UtcNow };
        var team = new Team { Id = Guid.CreateVersion7(), DepartmentId = department.Id, Name = "Team " + code, SyncedAt = DateTimeOffset.UtcNow };
        var profile = await db.EmbeddingProfiles.Select(p => (Guid?)p.Id).FirstOrDefaultAsync(Ct);
        if (profile is null)
        {
            var created = new EmbeddingProfile
            {
                Name = "keys-" + code, GatewayModel = "bge-m3", Dimensions = 1024, MaxInputTokens = 8192, MaxResidency = Residency.Eu,
                CanaryText = EmbeddingCanaryText.Default, IsDefault = true, UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.EmbeddingProfiles.Add(created);
            profile = created.Id;
        }

        Collection Collection(Guid departmentId, string slug) => new()
        {
            Slug = slug, Name = slug, DepartmentId = departmentId, Classification = Classification.Public, EmbeddingProfileId = profile.Value, UpdatedAt = DateTimeOffset.UtcNow,
        };

        var mine = Collection(department.Id, "keys-mine-" + code.ToLowerInvariant());
        var other = Collection(elsewhere.Id, "keys-other-" + code.ToLowerInvariant());
        var client = new Client { Name = "Integration " + code, Kind = ClientKinds.Internal, ClassificationCeiling = Classification.Internal, UpdatedAt = DateTimeOffset.UtcNow };
        db.AddRange(department, elsewhere, team, mine, other, client);
        await db.SaveChangesAsync(Ct);
        return (code, team.Id, mine.Id, other.Id, client.Id);
    }

    [Fact]
    public async Task A_department_admin_creates_a_scoped_key_whose_secret_is_shown_once_and_stored_as_its_hmac()
    {
        var (code, team, mine, other, client) = await SeedAsync();
        await using var factory = Factory();
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, code);
        using var stranger = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, "Z" + code);

        using (var teamless = await admin.PostAsJsonAsync("/api/keys", new { clientId = client, name = "Utan team" }, Ct))
        {
            teamless.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await Json(teamless)).GetProperty("errorCodes").GetProperty("teamId")[0].GetProperty("code").GetString().ShouldBe("not_in_scope");
        }

        // Clients are global: a department admin's key must be scoped to the team's department, or it would reach other departments.
        using (var unscoped = await admin.PostAsJsonAsync("/api/keys", new { clientId = client, teamId = team, name = "Oavgränsad" }, Ct))
        {
            unscoped.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await Json(unscoped)).GetProperty("errorCodes").GetProperty("scopes")[0].GetProperty("code").GetString().ShouldBe("key_scope_required");
        }

        using (var outside = await admin.PostAsJsonAsync("/api/keys", new { clientId = client, teamId = team, name = "Fel", scopes = new { collectionIds = new[] { other } } }, Ct))
        {
            outside.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await Json(outside)).GetProperty("errorCodes").GetProperty("scopes")[0].GetProperty("code").GetString().ShouldBe("key_scope_invalid");
        }

        using var created = await admin.PostAsJsonAsync("/api/keys", new { clientId = client, teamId = team, name = "Bygglov-portalen", scopes = new { collectionIds = new[] { mine } } }, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        var body = await Json(created);
        var secret = body.GetProperty("secret").GetString()!;
        var id = body.GetProperty("key").GetProperty("id").GetGuid();
        secret.ShouldStartWith("ume-rag-sk-");
        body.GetProperty("key").GetProperty("prefix").GetString().ShouldBe(secret[..15]);
        body.GetProperty("key").GetProperty("status").GetString().ShouldBe("active");
        body.GetProperty("key").GetProperty("clientName").GetString().ShouldBe("Integration " + code);

        await using (var db = TestPostgres.Context(database.Connection(null)))
        {
            var stored = await db.Keys.AsNoTracking().SingleAsync(k => k.Id == id, Ct);
            new KeyHasher(Encoding.UTF8.GetBytes(Pepper)).Verify(secret, stored.KeyHash).ShouldBeTrue(); // what rag-api checks
            stored.EncryptedSecret.ShouldNotBeNull();
            stored.EncryptedSecret.ShouldNotContain(secret[11..]);
            stored.Scopes.CollectionIds.ShouldBe([mine]);
            var audit = await db.AuditLog.AsNoTracking().SingleAsync(a => a.EntityType == "key" && a.EntityId == id.ToString(), Ct);
            audit.Details!.ShouldNotContain(secret[11..]);
            audit.Details!.ShouldNotContain(stored.KeyHash);
        }

        var listed = (await admin.GetFromJsonAsync<JsonElement[]>($"/api/keys?clientId={client}", Ct))!.ShouldHaveSingleItem();
        listed.TryGetProperty("secret", out _).ShouldBeFalse();
        listed.GetProperty("clientName").GetString().ShouldBe("Integration " + code); // the Keys page's client column
        (await admin.GetFromJsonAsync<JsonElement>($"/api/keys/{id}", Ct)).GetProperty("clientName").GetString().ShouldBe("Integration " + code);
        using (var cleared = await admin.PutAsJsonAsync($"/api/keys/{id}", new { name = "Bygglov-portalen", scopes = new { collectionIds = Array.Empty<Guid>() } }, Ct))
        {
            cleared.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await Json(cleared)).GetProperty("errorCodes").GetProperty("scopes")[0].GetProperty("code").GetString().ShouldBe("key_scope_required");
        }

        (await admin.PutAsJsonAsync($"/api/keys/{id}", new { name = "Bygglov-portal" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK); // null keeps the scopes
        (await stranger.GetAsync($"/api/keys/{id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await stranger.GetFromJsonAsync<JsonElement[]>($"/api/keys?clientId={client}", Ct))!.ShouldBeEmpty();

        // Reveal: platform admins only, audited; a department admin gets 403.
        (await admin.PostAsJsonAsync($"/api/keys/{id}/reveal", new { }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using var platform = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);
        (await platform.PostAsJsonAsync("/api/keys", new { clientId = client, name = "Plattformsintegration" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created); // unscoped, team-less
        using (var revealed = await platform.PostAsJsonAsync($"/api/keys/{id}/reveal", new { purpose = "Copy" }, Ct))
        {
            revealed.StatusCode.ShouldBe(HttpStatusCode.OK, await revealed.Content.ReadAsStringAsync(Ct));
            (await Json(revealed)).GetProperty("secret").GetString().ShouldBe(secret);
        }

        await using (var db = TestPostgres.Context(database.Connection(null)))
        {
            (await db.AuditLog.AsNoTracking().CountAsync(a => a.EntityType == "key" && a.EntityId == id.ToString() && a.Action == "copy", Ct)).ShouldBe(1);
        }
    }

    [Fact]
    public async Task Rotation_with_grace_keeps_the_old_key_for_24_hours_and_revoke_ends_it()
    {
        var (code, team, mine, _, client) = await SeedAsync();
        await using var factory = Factory();
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, code);
        using var created = await admin.PostAsJsonAsync("/api/keys", new { clientId = client, teamId = team, name = "Rotera", scopes = new { collectionIds = new[] { mine } } }, Ct);
        var id = (await Json(created)).GetProperty("key").GetProperty("id").GetGuid();

        using var rotated = await admin.PostAsJsonAsync($"/api/keys/{id}/rotate", new { mode = "Grace24Hours" }, Ct);
        rotated.StatusCode.ShouldBe(HttpStatusCode.OK, await rotated.Content.ReadAsStringAsync(Ct));
        var body = await Json(rotated);
        body.GetProperty("secret").GetString()!.ShouldStartWith("ume-rag-sk-");
        body.GetProperty("previous").GetProperty("status").GetString().ShouldBe("in-grace-period");
        var replacement = body.GetProperty("key").GetProperty("id").GetGuid();
        body.GetProperty("previous").GetProperty("rotatedToKeyId").GetGuid().ShouldBe(replacement);
        body.GetProperty("key").GetProperty("clientName").GetString().ShouldBe("Integration " + code);

        using (var again = await admin.PostAsJsonAsync($"/api/keys/{id}/rotate", new { }, Ct))
        {
            again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await Json(again)).GetProperty("code").GetString().ShouldBe("key_not_rotatable");
        }

        using var revoked = await admin.PostAsync($"/api/keys/{replacement}/revoke", null, Ct);
        var revokedBody = await Json(revoked);
        revokedBody.GetProperty("status").GetString().ShouldBe("revoked");
        revokedBody.GetProperty("clientName").GetString().ShouldBe("Integration " + code);

        await using var db = TestPostgres.Context(database.Connection(null));
        (await db.AuditLog.AsNoTracking().Where(a => a.EntityType == "key" && (a.EntityId == id.ToString() || a.EntityId == replacement.ToString()))
                .OrderBy(a => a.Id).Select(a => a.Action).ToListAsync(Ct))
            .ShouldBe(["create", "rotate", "revoke"]);
    }

    [Fact]
    public async Task Without_a_valid_pepper_keys_are_503_keys_unavailable()
    {
        var (_, _, _, _, client) = await SeedAsync();
        await using var factory = database.HeaderFactory();
        using var platform = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);
        using var refused = await platform.PostAsJsonAsync("/api/keys", new { clientId = client, name = "Ingen peppar" }, Ct);
        refused.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await Json(refused)).GetProperty("code").GetString().ShouldBe("keys_unavailable");
    }
}
