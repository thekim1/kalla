using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ume.Rag.Domain.Admin;
using Ume.Rag.Domain.Entities;

namespace Ume.Rag.AdminApi.Tests;

/// <summary>
/// An Open WebUI client's feedback retention (chat design §13.3, #343): created without a value it keeps 90 days, it cannot
/// be cleared, and another value is kept; other kinds may leave it empty.
/// </summary>
[Collection(AdminDatabaseGroup.Name)]
public sealed class ClientEndpointsTests(AdminDatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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

    [Fact]
    public async Task An_open_webui_client_keeps_its_feedback_90_days_unless_another_value_is_set()
    {
        var code = "C" + Random.Shared.Next(100_000, 999_999);
        await using var factory = database.HeaderFactory();
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin, code);

        var created = await Ok(await admin.PostAsJsonAsync("/api/clients",
            new { name = "Open WebUI " + code, kind = ClientKinds.OpenWebUi, classificationCeiling = "internal" }, Ct), HttpStatusCode.Created);
        created.GetProperty("clientRetentionDays").GetInt32().ShouldBe(90);
        var id = created.GetProperty("id").GetGuid();

        var cleared = await admin.PutAsJsonAsync($"/api/clients/{id}", Body(created, null), Ct);
        cleared.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await cleared.Content.ReadAsStringAsync(Ct));

        var changed = await Ok(await admin.PutAsJsonAsync($"/api/clients/{id}", Body(created, 30), Ct));
        changed.GetProperty("clientRetentionDays").GetInt32().ShouldBe(30);

        var integration = await Ok(await admin.PostAsJsonAsync("/api/clients",
            new { name = "Integration " + code, kind = ClientKinds.Integration, classificationCeiling = "internal" }, Ct), HttpStatusCode.Created);
        integration.GetProperty("clientRetentionDays").ValueKind.ShouldBe(JsonValueKind.Null);
    }
}
