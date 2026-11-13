using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Admin;
using Ume.Rag.Domain.Entities;
using Ume.Rag.TestKit;

namespace Ume.Rag.AdminApi.Tests;

/// <summary>
/// B-36 (#580, cost-attribution B4): the key picker's list (admin design §4.5 <c>GET /api/gateway/keys</c>) and V39 on an
/// assistant's and a knowledge base's gateway key (§4.6), against keys of the <c>GatewayKeys</c> mirror.
/// </summary>
[Collection(AdminDatabaseGroup.Name)]
public sealed class GatewayKeyRulesTests(AdminDatabaseFixture database)
{
    private const string AnswerModel = "ume/chat-eu";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Seed(string Code, Guid Department, string ProfileModel, Guid Active, Guid Disabled, Guid Unusable, Guid Foreign, Guid OnPremOnly,
        Guid EuOnly, Guid AnswerOnly, Guid WithSystemPrompt);

    private async Task<Seed> SeedAsync()
    {
        var code = "K" + Random.Shared.Next(100_000, 999_999);
        await using var db = TestPostgres.Context(database.Connection(null));
        var department = new Department { Id = Guid.CreateVersion7(), Name = "Keys " + code, CostCenterCode = code, SyncedAt = DateTimeOffset.UtcNow };
        var other = new Department { Id = Guid.CreateVersion7(), Name = "Other " + code, CostCenterCode = "O" + code, SyncedAt = DateTimeOffset.UtcNow };
        var team = new Team { Id = Guid.CreateVersion7(), DepartmentId = department.Id, Name = "Team " + code, SyncedAt = DateTimeOffset.UtcNow };
        var otherTeam = new Team { Id = Guid.CreateVersion7(), DepartmentId = other.Id, Name = "Team O" + code, SyncedAt = DateTimeOffset.UtcNow };
        var profile = await db.EmbeddingProfiles.Where(p => p.IsDefault && p.State == EmbeddingProfileStates.Active).Select(p => p.GatewayModel).FirstOrDefaultAsync(Ct);
        if (profile is null)
        {
            db.EmbeddingProfiles.Add(new EmbeddingProfile
            {
                Name = "keys-" + code, GatewayModel = "bge-m3", Dimensions = 1024, MaxInputTokens = 8192, MaxResidency = Residency.Eu,
                CanaryText = EmbeddingCanaryText.Default, IsDefault = true, UpdatedAt = DateTimeOffset.UtcNow,
            });
            profile = "bge-m3";
        }

        if (!await db.GatewayModels.AnyAsync(m => m.Name == AnswerModel, Ct))
        {
            db.GatewayModels.Add(new GatewayModel { Name = AnswerModel, Residencies = ["Eu"], Source = GatewayModelSources.Catalog });
        }

        GatewayKey Key(string name, Team owner, string status = "Active", bool usable = true, string[]? residencies = null, string[]? models = null,
            bool systemPrompt = false) => new()
        {
            Id = Guid.CreateVersion7(), Name = $"{name} {code}", TeamId = owner.Id, DepartmentId = owner.DepartmentId, Status = status, OwnerActive = true,
            Usable = usable, AllowedResidencies = residencies ?? [], AllowedModels = models ?? [], PiiPolicy = "Off", ContentGuardPolicy = "Off",
            HasSystemPrompt = systemPrompt, MaxOutputTokens = 4096, SyncedAt = DateTimeOffset.UtcNow,
        };

        var keys = new[]
        {
            Key("Active", team), Key("Disabled", team, status: "Disabled"), Key("Unusable", team, usable: false), Key("Foreign", otherTeam),
            Key("OnPrem only", team, residencies: ["OnPrem"]), Key("Eu only", team, residencies: ["Eu"]), Key("Answer only", team, models: [AnswerModel]),
            Key("System prompt", team, systemPrompt: true),
        };
        db.AddRange(department, other, team, otherTeam);
        db.GatewayKeys.AddRange(keys);
        await db.SaveChangesAsync(Ct);
        return new Seed(code, department.Id, profile, keys[0].Id, keys[1].Id, keys[2].Id, keys[3].Id, keys[4].Id, keys[5].Id, keys[6].Id, keys[7].Id);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static async Task ShouldRefuseAsync(HttpResponseMessage response, AdminErrorCode code, string? reason = null, string[]? affected = null)
    {
        response.StatusCode.ShouldBe((HttpStatusCode)code.Status, await response.Content.ReadAsStringAsync(Ct));
        var problem = await Json(response);
        problem.GetProperty("code").GetString().ShouldBe(code.Code);
        if (reason is not null)
        {
            problem.GetProperty("params").GetProperty("reason").GetString().ShouldBe(reason);
        }

        if (affected is not null)
        {
            problem.GetProperty("affected").EnumerateArray().Select(a => a.GetString()).ShouldBe(affected);
        }
    }

    [Fact]
    public async Task The_picker_lists_the_departments_keys_with_their_settings_and_unusable_ones_marked()
    {
        var seed = await SeedAsync();
        await using var factory = database.HeaderFactory();
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, seed.Code);
        using var stranger = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, "Y" + seed.Code);

        var list = await admin.GetFromJsonAsync<JsonElement>($"/api/gateway/keys?departmentId={seed.Department}", Ct);
        var keys = list.GetProperty("keys").EnumerateArray().ToDictionary(k => k.GetProperty("id").GetGuid());
        keys.Count.ShouldBe(7); // the other department's key is not listed
        keys.ShouldNotContainKey(seed.Foreign);
        keys[seed.Active].GetProperty("selectable").GetBoolean().ShouldBeTrue();
        keys[seed.Active].GetProperty("maxOutputTokens").GetInt32().ShouldBe(4096);
        keys[seed.Active].GetProperty("teamName").GetString().ShouldBe("Team " + seed.Code);
        (keys[seed.Disabled].GetProperty("selectable").GetBoolean(), keys[seed.Disabled].GetProperty("reason").GetString())
            .ShouldBe((false, GatewayKeyRules.Reasons.NotActive));
        (keys[seed.Unusable].GetProperty("selectable").GetBoolean(), keys[seed.Unusable].GetProperty("reason").GetString())
            .ShouldBe((false, GatewayKeyRules.Reasons.NotUsable));
        keys[seed.WithSystemPrompt].GetProperty("hasSystemPrompt").GetBoolean().ShouldBeTrue();
        list.GetProperty("keys").EnumerateArray().Any(k => k.TryGetProperty("secret", out _)).ShouldBeFalse();

        (await stranger.GetAsync($"/api/gateway/keys?departmentId={seed.Department}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task V39_refuses_a_knowledge_bases_key_of_another_department_disabled_unusable_out_of_ceiling_or_without_its_models()
    {
        var seed = await SeedAsync();
        await using var factory = database.HeaderFactory();
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, seed.Code);
        object Body(Guid key, string level = Classification.Internal) =>
            new { name = $"KB {Guid.NewGuid():N}", departmentId = seed.Department, classification = level, gatewayKeyId = key };

        using (var r = await admin.PostAsJsonAsync("/api/collections", Body(seed.Foreign), Ct))
        {
            await ShouldRefuseAsync(r, AdminErrorCodes.GatewayKeyInvalid, GatewayKeyRules.Reasons.OtherDepartment);
        }

        using (var r = await admin.PostAsJsonAsync("/api/collections", Body(seed.Disabled), Ct))
        {
            await ShouldRefuseAsync(r, AdminErrorCodes.GatewayKeyInvalid, GatewayKeyRules.Reasons.NotActive);
        }

        using (var r = await admin.PostAsJsonAsync("/api/collections", Body(seed.Unusable), Ct))
        {
            await ShouldRefuseAsync(r, AdminErrorCodes.GatewayKeyInvalid, GatewayKeyRules.Reasons.NotUsable);
        }

        using (var r = await admin.PostAsJsonAsync("/api/collections", Body(Guid.NewGuid()), Ct))
        {
            await ShouldRefuseAsync(r, AdminErrorCodes.GatewayKeyInvalid, GatewayKeyRules.Reasons.Unknown);
        }

        // Sensitive runs at OnPrem: a key that allows only Eu reaches none of it.
        using (var r = await admin.PostAsJsonAsync("/api/collections", Body(seed.EuOnly, Classification.Sensitive), Ct))
        {
            await ShouldRefuseAsync(r, AdminErrorCodes.GatewayKeyResidency, affected: ["Eu"]);
        }

        // The knowledge base embeds with its profile's model, which the key does not allow (the platform's evaluation models
        // may be missing too, depending on the shared database's settings).
        using (var r = await admin.PostAsJsonAsync("/api/collections", Body(seed.AnswerOnly), Ct))
        {
            await ShouldRefuseAsync(r, AdminErrorCodes.GatewayKeyModel);
        }

        // An OnPrem key is within an internal (Eu) ceiling; the saved key comes back, and a change to another is checked.
        using var created = await admin.PostAsJsonAsync("/api/collections", Body(seed.OnPremOnly), Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        var collection = await Json(created);
        collection.GetProperty("gatewayKeyId").GetGuid().ShouldBe(seed.OnPremOnly);
        using var changed = await admin.PutAsJsonAsync($"/api/collections/{collection.GetProperty("id").GetGuid()}", new
        {
            name = collection.GetProperty("name").GetString(), classification = Classification.Internal, gatewayKeyId = seed.Disabled,
            version = collection.GetProperty("version").GetString(),
        }, Ct);
        await ShouldRefuseAsync(changed, AdminErrorCodes.GatewayKeyInvalid, GatewayKeyRules.Reasons.NotActive);
    }

    [Fact]
    public async Task An_assistants_key_is_checked_against_its_models_and_a_missing_key_or_a_system_prompt_is_a_warning()
    {
        var seed = await SeedAsync();
        await using var factory = database.HeaderFactory();
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, seed.Code);

        using (var refused = await admin.PostAsJsonAsync("/api/assistants", new
               {
                   name = "Foreign " + seed.Code, departmentId = seed.Department, classification = Classification.Internal, gatewayKeyId = seed.Foreign,
               }, Ct))
        {
            await ShouldRefuseAsync(refused, AdminErrorCodes.GatewayKeyInvalid, GatewayKeyRules.Reasons.OtherDepartment);
        }

        // Without a key: the draft warns that the calls are charged to the platform's owner team.
        using var created = await admin.PostAsJsonAsync("/api/assistants", new { name = "Keyless " + seed.Code, departmentId = seed.Department, classification = Classification.Internal }, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        var assistant = await Json(created);
        var id = assistant.GetProperty("id").GetGuid();
        using (var draft = await admin.PutAsJsonAsync($"/api/assistants/{id}/draft", new
               {
                   gatewayModel = AnswerModel, systemPrompt = "Svara kort.", version = assistant.GetProperty("draft").GetProperty("version").GetString(),
               }, Ct))
        {
            draft.StatusCode.ShouldBe(HttpStatusCode.OK, await draft.Content.ReadAsStringAsync(Ct));
            (await Json(draft)).GetProperty("warnings").EnumerateArray().Select(w => w.GetProperty("code").GetString()).ShouldContain(AdminErrorCodes.GatewayKeyRequired.Code);
        }

        async Task<HttpResponseMessage> SetKeyAsync(Guid key)
        {
            var current = await admin.GetFromJsonAsync<JsonElement>($"/api/assistants/{id}", Ct);
            return await admin.PutAsJsonAsync($"/api/assistants/{id}", new
            {
                name = current.GetProperty("name").GetString(), classification = Classification.Internal, gatewayKeyId = key,
                version = current.GetProperty("version").GetString(),
            }, Ct);
        }

        using (var disabled = await SetKeyAsync(seed.Disabled))
        {
            await ShouldRefuseAsync(disabled, AdminErrorCodes.GatewayKeyInvalid, GatewayKeyRules.Reasons.NotActive);
        }

        using (var saved = await SetKeyAsync(seed.WithSystemPrompt))
        {
            saved.StatusCode.ShouldBe(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync(Ct));
            (await Json(saved)).GetProperty("gatewayKeyId").GetGuid().ShouldBe(seed.WithSystemPrompt);
        }

        var warnings = (await admin.GetFromJsonAsync<JsonElement>($"/api/assistants/{id}/draft", Ct)).GetProperty("warnings").EnumerateArray()
            .Select(w => w.GetProperty("code").GetString()).ToList();
        warnings.ShouldContain(AdminErrorCodes.GatewayKeySystemPrompt.Code);
        warnings.ShouldNotContain(AdminErrorCodes.GatewayKeyRequired.Code);
    }

    [Fact]
    public async Task A_draft_that_uses_a_model_the_key_does_not_allow_warns_and_the_key_change_is_refused()
    {
        var seed = await SeedAsync();
        await using var factory = database.HeaderFactory();
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, seed.Code);
        using var created = await admin.PostAsJsonAsync("/api/assistants", new
        {
            name = "Models " + seed.Code, departmentId = seed.Department, classification = Classification.Internal, gatewayKeyId = seed.AnswerOnly,
        }, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        var assistant = await Json(created);
        var id = assistant.GetProperty("id").GetGuid();

        // A collection on the default profile: the key allows the answer model but not the profile's embedding model.
        using var collection = await admin.PostAsJsonAsync("/api/collections", new { name = "KB " + seed.Code, departmentId = seed.Department, classification = Classification.Internal }, Ct);
        collection.StatusCode.ShouldBe(HttpStatusCode.Created, await collection.Content.ReadAsStringAsync(Ct));
        var collectionId = (await Json(collection)).GetProperty("id").GetGuid();
        using var draft = await admin.PutAsJsonAsync($"/api/assistants/{id}/draft", new
        {
            gatewayModel = AnswerModel, systemPrompt = "Svara kort.", collectionLinks = new[] { new { collectionId, tags = Array.Empty<string>() } },
            version = assistant.GetProperty("draft").GetProperty("version").GetString(),
        }, Ct);
        draft.StatusCode.ShouldBe(HttpStatusCode.OK, await draft.Content.ReadAsStringAsync(Ct));
        var warning = (await Json(draft)).GetProperty("warnings").EnumerateArray().Single(w => w.GetProperty("code").GetString() == AdminErrorCodes.GatewayKeyModel.Code);
        warning.GetProperty("affected").EnumerateArray().Select(a => a.GetString()).ShouldContain(seed.ProfileModel); // and the platform's models it lacks

        // Changing to another key that lacks a model of the draft is refused; one that allows every model saves.
        var current = await admin.GetFromJsonAsync<JsonElement>($"/api/assistants/{id}", Ct);
        using var refused = await admin.PutAsJsonAsync($"/api/assistants/{id}", new
        {
            name = current.GetProperty("name").GetString(), classification = Classification.Sensitive, gatewayKeyId = seed.EuOnly,
            version = current.GetProperty("version").GetString(),
        }, Ct);
        await ShouldRefuseAsync(refused, AdminErrorCodes.GatewayKeyResidency, affected: ["Eu"]);
        using var saved = await admin.PutAsJsonAsync($"/api/assistants/{id}", new
        {
            name = current.GetProperty("name").GetString(), classification = Classification.Internal, gatewayKeyId = seed.Active,
            version = current.GetProperty("version").GetString(),
        }, Ct);
        saved.StatusCode.ShouldBe(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Without_an_admin_url_in_the_context_the_links_use_Admin_GatewayAdminUrl()
    {
        // #619 (admin design §2.3): the context's admin_url, else Admin:GatewayAdminUrl, wherever an adminUrl is returned.
        var seed = await SeedAsync();
        await using (var plain = database.HeaderFactory())
        {
            using var admin = await AdminDatabaseFixture.UserAsync(plain, RagRoles.DepartmentAdmin, seed.Code);
            (await admin.GetFromJsonAsync<JsonElement>($"/api/gateway/keys?departmentId={seed.Department}", Ct)).GetProperty("adminUrl").ValueKind
                .ShouldBe(JsonValueKind.Null); // no context read here, nothing configured
        }

        await using var factory = database.HeaderFactory().WithWebHostBuilder(web => web.UseSetting("Admin:GatewayAdminUrl", "https://gateway.example.invalid"));
        using var departmentAdmin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, seed.Code);
        using var platform = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);

        (await departmentAdmin.GetFromJsonAsync<JsonElement>($"/api/gateway/keys?departmentId={seed.Department}", Ct)).GetProperty("adminUrl").GetString()
            .ShouldBe("https://gateway.example.invalid");
        var status = await platform.GetFromJsonAsync<JsonElement>("/api/organisation/sync", Ct);
        status.GetProperty("adminUrl").GetString().ShouldBe("https://gateway.example.invalid");
    }
}
