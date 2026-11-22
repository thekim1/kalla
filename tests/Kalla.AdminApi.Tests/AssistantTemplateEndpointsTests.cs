using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Kalla.Domain.Admin;
using Kalla.TestKit;

namespace Kalla.AdminApi.Tests;

/// <summary>
/// <c>/api/assistant-templates</c> (admin design §4.5, B-54): every role reads them, platform admins change them, a name is
/// unique, the version guards a change, and every change is audited <c>assistanttemplate.*</c>. Which role gets which answer
/// on every route is <see cref="PermissionMatrixTests"/>'s.
/// </summary>
[Collection(AdminDatabaseGroup4.Name)]
public sealed class AssistantTemplateEndpointsTests(AdminDatabaseFixture database, AdminHost host) : IClassFixture<AdminHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    [Fact]
    public async Task A_platform_admin_adds_changes_and_removes_a_template_and_every_role_reads_them()
    {
        var factory = host.Factory;
        var name = "Skolstöd " + Random.Shared.Next(100_000, 999_999);
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);
        using var viewer = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Viewer, "1000");

        using var created = await admin.PostAsJsonAsync("/api/assistant-templates", new
        {
            name = "  " + name + " ", description = "Lärare med frågor om skolans rutiner.", systemPrompt = "Svara lärare kort.",
            notFound = new { sv = "Det hittar jag inte.", links = new[] { new { title = "Skolkontoret", url = "https://www.umea.se/skola" } } },
            querySteps = new { answer = new { maxOutputTokens = 800 } },
        }, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        var template = await Json(created);
        var id = template.GetProperty("id").GetGuid();
        template.GetProperty("name").GetString().ShouldBe(name);
        template.GetProperty("isActive").GetBoolean().ShouldBeTrue();
        template.GetProperty("notFound").GetProperty("links")[0].GetProperty("title").GetString().ShouldBe("Skolkontoret");
        template.GetProperty("querySteps").GetProperty("answer").GetProperty("maxOutputTokens").GetInt32().ShouldBe(800);

        // Every role reads the list (the create-assistant drawer offers them); only a platform admin changes one.
        var listed = await viewer.GetFromJsonAsync<JsonElement>("/api/assistant-templates", Ct);
        listed.EnumerateArray().Select(t => t.GetProperty("id").GetGuid()).ShouldContain(id);
        (await viewer.PostAsJsonAsync("/api/assistant-templates", new { name = name + " 2" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // A name is unique; the version guards a change.
        using (var twin = await admin.PostAsJsonAsync("/api/assistant-templates", new { name }, Ct))
        {
            // A field fault, like an assistant's slug: 400 with already_exists on the name.
            twin.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await Json(twin)).GetProperty("errorCodes").GetProperty("name")[0].GetProperty("code").GetString().ShouldBe("already_exists");
        }

        var version = template.GetProperty("version").GetString();
        using (var stale = await admin.PutAsJsonAsync($"/api/assistant-templates/{id}", new { name, systemPrompt = "x", version = "1" }, Ct))
        {
            stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        using (var notAnObject = await admin.PutAsJsonAsync($"/api/assistant-templates/{id}", new { name, notFound = new[] { "sv" }, version }, Ct))
        {
            notAnObject.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        using var changed = await admin.PutAsJsonAsync($"/api/assistant-templates/{id}", new { name, systemPrompt = "Svara lärare mycket kort.", isActive = false, version }, Ct);
        changed.StatusCode.ShouldBe(HttpStatusCode.OK, await changed.Content.ReadAsStringAsync(Ct));
        var after = await Json(changed);
        (after.GetProperty("systemPrompt").GetString(), after.GetProperty("isActive").GetBoolean(), after.GetProperty("notFound").EnumerateObject().Count())
            .ShouldBe(("Svara lärare mycket kort.", false, 0));
        (await viewer.GetFromJsonAsync<JsonElement>($"/api/assistant-templates/{id}", Ct)).GetProperty("description").GetString().ShouldBe("");

        (await admin.DeleteAsync($"/api/assistant-templates/{id}?version=1", Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await admin.DeleteAsync($"/api/assistant-templates/{id}?version={after.GetProperty("version").GetString()}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await admin.GetAsync($"/api/assistant-templates/{id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        await using var db = TestPostgres.Context(database.Connection(null));
        (await db.AuditLog.AsNoTracking().Where(a => a.EntityType == "assistanttemplate" && a.EntityId == id.ToString()).OrderBy(a => a.Id)
                .Select(a => a.Action).ToListAsync(Ct))
            .ShouldBe(["create", "update", "delete"]);
    }
}
