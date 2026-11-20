using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Kalla.Domain;
using Kalla.Domain.Admin;
using Kalla.Domain.Entities;
using Kalla.TestKit;

namespace Kalla.AdminApi.Tests;

/// <summary>
/// Explain access (identity design §4.9, P1b item 9): the pure policy steps against the snapshot at the current version, for
/// a client in the assistant's audience (allowed, with level and ceiling), one outside it, and an unknown one; department
/// admins only for their own departments' assistants.
/// </summary>
[Collection(AdminDatabaseGroup4.Name)]
public sealed class ExplainAccessTests(AdminDatabaseFixture database, AdminHost host) : IClassFixture<AdminHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Seed(string Code, Guid Assistant, Guid Audience, Guid Outside);

    private async Task<Seed> SeedAsync()
    {
        var code = "E" + Random.Shared.Next(100_000, 999_999);
        await using var db = TestPostgres.Context(database.Connection(null));
        var department = new Department { Id = Guid.CreateVersion7(), Name = "Förklara " + code, CostCenterCode = code, SyncedAt = DateTimeOffset.UtcNow };
        var profile = await db.EmbeddingProfiles.Select(p => (Guid?)p.Id).FirstOrDefaultAsync(Ct);
        if (profile is null)
        {
            var created = new EmbeddingProfile
            {
                Name = "expl-" + code, GatewayModel = "bge-m3", Dimensions = 1024, MaxInputTokens = 8192, MaxResidency = Residency.Eu,
                CanaryText = EmbeddingCanaryText.Default, UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.EmbeddingProfiles.Add(created);
            profile = created.Id;
        }

        var collection = new Collection
        {
            Slug = "expl-" + code.ToLowerInvariant(), Name = "Förklara " + code, DepartmentId = department.Id, Classification = Classification.Internal,
            EmbeddingProfileId = profile.Value, PublishedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var audience = new Client { Name = "Intranät " + code, Kind = ClientKinds.Integration, ClassificationCeiling = Classification.Internal, UpdatedAt = DateTimeOffset.UtcNow };
        var outside = new Client { Name = "Annan " + code, Kind = ClientKinds.Integration, ClassificationCeiling = Classification.Internal, UpdatedAt = DateTimeOffset.UtcNow };
        var assistant = new Assistant
        {
            Slug = "expl-" + code.ToLowerInvariant(), Name = "Förklaring " + code, DepartmentId = department.Id, Classification = Classification.Internal,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.AddRange(department, collection, audience, outside, assistant);
        await db.SaveChangesAsync(Ct);
        var version = new AssistantVersion
        {
            AssistantId = assistant.Id, Number = 1, State = AssistantVersionStates.Published, GatewayModel = "fors/chat-eu", SystemPrompt = "Svara kort.",
            CollectionLinks = [CollectionLink.Whole(collection.Id)], PublishedAt = DateTimeOffset.UtcNow, PublishedBy = "o-seed",
        };
        db.Add(version);
        db.Add(new AssistantAudience { AssistantId = assistant.Id, Kind = AssistantAudienceKinds.Client, SubjectId = audience.Id });
        await db.SaveChangesAsync(Ct);
        assistant.PublishedVersionId = version.Id;
        await db.SaveChangesAsync(Ct);
        return new Seed(code, assistant.Id, audience.Id, outside.Id);
    }

    private static object Ask(Guid client, Guid assistant, string kind = "entra-user") =>
        new { clientId = client, assistantId = assistant, subject = new { kind, id = "o-explain" } };

    [Fact]
    public async Task An_audience_client_is_allowed_with_level_and_ceiling_and_others_get_their_failing_step()
    {
        var seed = await SeedAsync();
        var factory = host.Factory;
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, seed.Code);

        using var allowed = await admin.PostAsJsonAsync("/api/access/explain", Ask(seed.Audience, seed.Assistant), Ct);
        allowed.StatusCode.ShouldBe(HttpStatusCode.OK, await allowed.Content.ReadAsStringAsync(Ct));
        var yes = await allowed.Content.ReadFromJsonAsync<JsonElement>(Ct);
        (yes.GetProperty("allowed").GetBoolean(), yes.GetProperty("status").GetInt32(), yes.GetProperty("effectiveLevel").GetString(),
            yes.GetProperty("maxResidency").GetString()).ShouldBe((true, 200, Classification.Internal, "Eu"));
        yes.GetProperty("configVersion").GetInt64().ShouldBeGreaterThan(0);

        var no = await (await admin.PostAsJsonAsync("/api/access/explain", Ask(seed.Outside, seed.Assistant), Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        no.GetProperty("allowed").GetBoolean().ShouldBeFalse();
        no.GetProperty("step").GetString().ShouldNotBeNullOrEmpty();
        no.GetProperty("reason").GetString().ShouldNotBe("ok");
        no.GetProperty("status").GetInt32().ShouldBe(404); // the data plane hides what the caller may not use

        var unknown = await (await admin.PostAsJsonAsync("/api/access/explain", Ask(Guid.NewGuid(), seed.Assistant), Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        (unknown.GetProperty("allowed").GetBoolean(), unknown.GetProperty("step").GetString(), unknown.GetProperty("reason").GetString())
            .ShouldBe((false, "1", Kalla.Domain.Access.DenyReasons.ClientUnknown));
    }

    [Fact]
    public async Task Only_admins_of_the_assistant_s_department_may_ask_and_the_subject_kind_is_checked()
    {
        var seed = await SeedAsync();
        var factory = host.Factory;
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, seed.Code);
        using var stranger = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, "X" + seed.Code);
        using var editor = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Editor, seed.Code);
        using var platform = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);

        (await editor.PostAsJsonAsync("/api/access/explain", Ask(seed.Audience, seed.Assistant), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await stranger.PostAsJsonAsync("/api/access/explain", Ask(seed.Audience, seed.Assistant), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await platform.PostAsJsonAsync("/api/access/explain", Ask(seed.Audience, seed.Assistant), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.PostAsJsonAsync("/api/access/explain", Ask(seed.Audience, seed.Assistant, kind: "robot"), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.PostAsJsonAsync("/api/access/explain", Ask(seed.Audience, Guid.NewGuid()), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
