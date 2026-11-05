using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Admin;
using Ume.Rag.Domain.Entities;
using Ume.Rag.TestKit;

namespace Ume.Rag.AdminApi.Tests;

/// <summary>
/// Organisation in the admin API (P1b item 35; admin design §3.2, §4.5, §4.6): the sharing route with V35, <c>view=</c>,
/// topics, the Map route, contact fields, V11 widened to shared collections and topics, and the owner team defaulted on
/// create. Another department sees a shared collection's summary and topics and gets 404 on its sources and documents.
/// </summary>
[Collection(AdminDatabaseGroup.Name)]
public sealed class OrganisationSharingTests(AdminDatabaseFixture database)
{
    private const string Model = "ume/chat-eu";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Seed(
        Guid Owner, string OwnerCode, Guid User, string UserCode, Guid Third, string ThirdCode, Guid Collection, Guid Source, Guid Document, Guid UserCollection);

    /// <summary>
    /// Departments: <c>Owner</c> with a published internal collection (contact, a source whose path rule tags
    /// <c>bygglov</c>, two live documents tagged <c>bygglov</c> and <c>altan</c>), <c>User</c> with an unlinked collection of
    /// its own, and <c>Third</c>.
    /// </summary>
    private async Task<Seed> SeedAsync()
    {
        var n = Random.Shared.Next(100_000, 999_999);
        await using var writer = new ContentFixtureWriter(database.Connection(RagDatabase.Worker), database.Connection(RagDatabase.Admin));
        var profile = await writer.AddEmbeddingProfileAsync(8, Ct);
        await using var db = TestPostgres.Context(database.Connection(null));
        Department NewDepartment(string prefix) =>
            new() { Id = Guid.CreateVersion7(), Name = $"{prefix} {n}", CostCenterCode = $"{prefix[0]}{n}", SyncedAt = DateTimeOffset.UtcNow };
        if (!await db.EmbeddingProfiles.AnyAsync(p => p.IsDefault, Ct))
        {
            db.EmbeddingProfiles.Add(new EmbeddingProfile
            {
                Name = $"default-{n}", GatewayModel = "bge-m3", Dimensions = 1024, MaxInputTokens = 8192, MaxResidency = Residency.Eu,
                CanaryText = EmbeddingCanaryText.Default, IsDefault = true, UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        var owner = NewDepartment("Kommunikation");
        var user = NewDepartment("Bygg");
        var third = NewDepartment("Teknik");
        var collection = new Collection
        {
            Slug = $"umea-se-{n}", Name = $"umea.se {n}", DepartmentId = owner.Id, Classification = Classification.Internal, EmbeddingProfileId = profile,
            PublishedAt = DateTimeOffset.UtcNow, ContactName = "K. Lund", ContactEmail = "kommunikation@umea.se", UpdatedAt = DateTimeOffset.UtcNow,
        };
        var own = new Collection
        {
            Slug = $"blanketter-{n}", Name = $"Blanketter {n}", DepartmentId = user.Id, Classification = Classification.Internal, EmbeddingProfileId = profile,
            PublishedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var pipeline = new Pipeline { Name = $"share-{n}", TemplateId = "website", UpdatedAt = DateTimeOffset.UtcNow };
        var version = new PipelineVersion
        {
            PipelineId = pipeline.Id, Number = 1, State = PipelineVersionStates.Published, PublishedAt = DateTimeOffset.UtcNow,
            Definition = """{"schema":1,"template":"website","templateVersion":1,"params":{"pathRules":[{"match":"/bygga-bo/bygglov/**","on":"path","tags":["bygglov"]}]}}""",
        };
        var source = new Source
        {
            CollectionId = collection.Id, Type = SourceTypes.Crawl, Name = "umea.se bygga", Classification = Classification.Internal, PipelineId = pipeline.Id,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.AddRange(owner, user, third, collection, own, pipeline, version, source);
        await db.SaveChangesAsync(Ct);
        await db.Pipelines.Where(p => p.Id == pipeline.Id).ExecuteUpdateAsync(u => u.SetProperty(p => p.PublishedVersionId, version.Id), Ct);
        if (!await db.GatewayModels.AnyAsync(m => m.Name == Model, Ct))
        {
            db.GatewayModels.Add(new GatewayModel { Name = Model, Residencies = ["Eu"], Source = GatewayModelSources.Catalog });
            await db.SaveChangesAsync(Ct);
        }

        var document = await writer.AddDocumentAsync(new FixtureDocumentSpec
        {
            CollectionId = collection.Id, EmbeddingProfileId = profile, SourceId = source.Id, Classification = Classification.Internal, Title = "Bygglov",
            Url = $"https://www.umea.se/{n}/bygglov",
        }, [new FixtureChunk("Du behöver bygglov för en altan.") { Tags = ["bygglov", "altan"] }, new FixtureChunk("Ansök i e-tjänsten.") { Tags = ["bygglov"] }], Ct);
        await writer.AddDocumentAsync(new FixtureDocumentSpec
        {
            CollectionId = collection.Id, EmbeddingProfileId = profile, SourceId = source.Id, Classification = Classification.Internal, Title = "Strandskydd",
            Url = $"https://www.umea.se/{n}/strandskydd",
        }, [new FixtureChunk("Strandskydd gäller vid vatten.") { Tags = ["bygglov"] }], Ct);
        return new Seed(owner.Id, owner.CostCenterCode, user.Id, user.CostCenterCode, third.Id, third.CostCenterCode, collection.Id, source.Id, document.Id, own.Id);
    }

    /// <summary>An assistant of <paramref name="department"/> whose published version (or only draft) links the collection with <paramref name="tags"/>.</summary>
    private async Task<Guid> AssistantAsync(Guid department, Guid collection, string[] tags, bool published = true)
    {
        await using var db = TestPostgres.Context(database.Connection(null));
        var assistant = new Assistant
        {
            Slug = "share-" + Guid.NewGuid().ToString("N")[..12], Name = (published ? "Bygglovsassistenten " : "Kundtjänst bygg ") + Random.Shared.Next(1000, 9999),
            DepartmentId = department, Classification = Classification.Internal, ContactName = "A. Andersson", ContactEmail = "bygg@umea.se", UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Add(assistant);
        await db.SaveChangesAsync(Ct);
        var version = new AssistantVersion
        {
            AssistantId = assistant.Id, Number = 1, State = published ? AssistantVersionStates.Published : AssistantVersionStates.Draft, GatewayModel = Model,
            SystemPrompt = "Svara kort.", CollectionLinks = [new CollectionLink(collection, tags)], CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
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

    private static async Task<string> VersionAsync(HttpClient owner, Guid collection) =>
        (await owner.GetFromJsonAsync<JsonElement>($"/api/collections/{collection}", Ct)).GetProperty("version").GetString()!;

    private static async Task<HttpResponseMessage> ShareAsync(HttpClient owner, Guid collection, string sharedWith, params Guid[] departmentIds) =>
        await owner.PutAsJsonAsync($"/api/collections/{collection}/sharing",
            new { sharedWith, departmentIds, version = await VersionAsync(owner, collection) }, Ct);

    private static List<Guid> Ids(JsonElement page) => [.. page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid())];

    [Fact]
    public async Task A_shared_collection_shows_its_summary_and_topics_to_another_department_and_its_sources_and_documents_stay_404()
    {
        var seed = await SeedAsync();
        await using var factory = database.HeaderFactory();
        using var owner = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, seed.OwnerCode);
        using var user = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, seed.UserCode);

        // Only our department (the default): the other department does not see it at all.
        Ids(await user.GetFromJsonAsync<JsonElement>("/api/collections?view=all&pageSize=200", Ct)).ShouldNotContain(seed.Collection);
        (await user.GetAsync($"/api/collections/{seed.Collection}/topics", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        using (var shared = await ShareAsync(owner, seed.Collection, CollectionSharingLevels.Municipality))
        {
            shared.StatusCode.ShouldBe(HttpStatusCode.OK, await shared.Content.ReadAsStringAsync(Ct));
            (await Json(shared)).GetProperty("sharedWith").GetString().ShouldBe(CollectionSharingLevels.Municipality);
        }

        // view=shared: the shared summary (name, owner, level, contact, document count, topics); mine has only its own; all has both.
        var sharedPage = await user.GetFromJsonAsync<JsonElement>("/api/collections?view=shared&pageSize=200", Ct);
        var summary = sharedPage.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == seed.Collection);
        summary.GetProperty("shared").GetBoolean().ShouldBeTrue();
        summary.GetProperty("departmentName").GetString()!.ShouldStartWith("Kommunikation");
        summary.GetProperty("classification").GetString().ShouldBe(Classification.Internal);
        summary.GetProperty("contactName").GetString().ShouldBe("K. Lund");
        summary.GetProperty("contactEmail").GetString().ShouldBe("kommunikation@umea.se");
        summary.GetProperty("documentCount").GetInt32().ShouldBe(2);
        summary.GetProperty("topics").EnumerateArray().Select(t => t.GetString()).ShouldBe(["altan", "bygglov"]);
        Ids(sharedPage).ShouldNotContain(seed.UserCollection);
        Ids(await user.GetFromJsonAsync<JsonElement>("/api/collections?pageSize=200", Ct)).ShouldBe([seed.UserCollection]);
        // (Other tests' municipality-wide collections are in the same database, so "all" is checked for these two.)
        var all = Ids(await user.GetFromJsonAsync<JsonElement>("/api/collections?view=all&pageSize=200", Ct));
        all.ShouldContain(seed.UserCollection);
        all.ShouldContain(seed.Collection);
        using (var bad = await user.GetAsync("/api/collections?view=everything", Ct))
        {
            bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        // Topics: tags with document counts; the path rules only for the owner.
        var topics = await user.GetFromJsonAsync<JsonElement>($"/api/collections/{seed.Collection}/topics", Ct);
        var bygglov = topics.GetProperty("topics").EnumerateArray().Single(t => t.GetProperty("tag").GetString() == "bygglov");
        bygglov.GetProperty("documents").GetInt32().ShouldBe(2);
        bygglov.GetProperty("rules").ValueKind.ShouldBe(JsonValueKind.Null);
        var ownTopics = await owner.GetFromJsonAsync<JsonElement>($"/api/collections/{seed.Collection}/topics", Ct);
        var rule = ownTopics.GetProperty("topics").EnumerateArray().Single(t => t.GetProperty("tag").GetString() == "bygglov").GetProperty("rules")
            .EnumerateArray().ShouldHaveSingleItem();
        rule.GetProperty("match").GetString().ShouldBe("/bygga-bo/bygglov/**");
        rule.GetProperty("sourceId").GetGuid().ShouldBe(seed.Source);
        ownTopics.GetProperty("topics").EnumerateArray().Single(t => t.GetProperty("tag").GetString() == "altan").GetProperty("rules").GetArrayLength().ShouldBe(0);

        // Its detail is the shared summary (no settings, no usedBy); never its sources, documents or evaluation; never a write.
        var read = await user.GetFromJsonAsync<JsonElement>($"/api/collections/{seed.Collection}", Ct);
        read.GetProperty("shared").GetBoolean().ShouldBeTrue();
        read.GetProperty("documentCount").GetInt32().ShouldBe(2);
        read.TryGetProperty("retrievalSettings", out _).ShouldBeFalse();
        read.TryGetProperty("usedBy", out _).ShouldBeFalse();
        (await owner.GetFromJsonAsync<JsonElement>($"/api/collections/{seed.Collection}", Ct)).GetProperty("shared").GetBoolean().ShouldBeFalse();
        foreach (var path in new[]
                 {
                     $"/api/collections/{seed.Collection}/sources", $"/api/sources/{seed.Source}",
                     $"/api/documents/{seed.Document}", $"/api/collections/{seed.Collection}/evaluation",
                 })
        {
            (await user.GetAsync(path, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound, path);
        }

        (await user.GetFromJsonAsync<JsonElement>($"/api/documents?collectionId={seed.Collection}", Ct)).GetProperty("items").GetArrayLength().ShouldBe(0);
        using (var write = await user.PutAsJsonAsync($"/api/collections/{seed.Collection}/sharing", new { sharedWith = "department", version = "1" }, Ct))
        {
            write.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        // A third department's assistant sees it as well (municipality); selected narrows it to the named departments.
        using var third = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Viewer, seed.ThirdCode);
        Ids(await third.GetFromJsonAsync<JsonElement>("/api/collections?view=shared&pageSize=200", Ct)).ShouldContain(seed.Collection);
        (await ShareAsync(owner, seed.Collection, CollectionSharingLevels.Selected, seed.User)).StatusCode.ShouldBe(HttpStatusCode.OK);
        Ids(await third.GetFromJsonAsync<JsonElement>("/api/collections?view=shared&pageSize=200", Ct)).ShouldNotContain(seed.Collection);
        Ids(await user.GetFromJsonAsync<JsonElement>("/api/collections?view=shared&pageSize=200", Ct)).ShouldContain(seed.Collection);
        var detail = await owner.GetFromJsonAsync<JsonElement>($"/api/collections/{seed.Collection}", Ct);
        detail.GetProperty("sharedWithDepartmentIds").EnumerateArray().Select(d => d.GetGuid()).ShouldBe([seed.User]);

        await using var db = TestPostgres.Context(database.Connection(RagDatabase.Admin));
        var audit = await db.AuditLog.AsNoTracking().Where(a => a.EntityType == "collection" && a.EntityId == seed.Collection.ToString() && a.Action == "share")
            .OrderBy(a => a.Id).ToListAsync(Ct);
        audit.Count.ShouldBe(2);
        audit[1].Details!.ShouldContain(seed.User.ToString());
        audit[1].DepartmentId.ShouldBe(seed.Owner);
    }

    [Fact]
    public async Task Narrowing_the_sharing_is_refused_while_another_departments_published_assistant_links_it_v35()
    {
        var seed = await SeedAsync();
        await using var factory = database.HeaderFactory();
        using var owner = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, seed.OwnerCode);
        (await ShareAsync(owner, seed.Collection, CollectionSharingLevels.Municipality)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var linking = await AssistantAsync(seed.User, seed.Collection, ["bygglov"]);
        await AssistantAsync(seed.Third, seed.Collection, [], published: false); // a draft-only link does not block

        foreach (var (level, departments) in new (string, Guid[])[]
                 {
                     (CollectionSharingLevels.Department, []), (CollectionSharingLevels.Selected, [seed.Third]),
                 })
        {
            using var refused = await ShareAsync(owner, seed.Collection, level, departments);
            refused.StatusCode.ShouldBe(HttpStatusCode.Conflict, level);
            var problem = await Json(refused);
            problem.GetProperty("code").GetString().ShouldBe("collection_shared_in_use");
            var affected = problem.GetProperty("assistants").EnumerateArray().ShouldHaveSingleItem();
            affected.GetProperty("id").GetGuid().ShouldBe(linking);
            affected.GetProperty("departmentId").GetGuid().ShouldBe(seed.User);
            problem.GetProperty("affected")[0].GetString()!.ShouldContain($"(Bygg ");
        }

        // Keeping the linking department is allowed; then removing it from the list is refused again.
        (await ShareAsync(owner, seed.Collection, CollectionSharingLevels.Selected, seed.User)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ShareAsync(owner, seed.Collection, CollectionSharingLevels.Selected, seed.Third)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // Field rules: departmentIds only with selected, never the owner, at least one.
        foreach (var (level, departments) in new (string, Guid[])[]
                 {
                     (CollectionSharingLevels.Municipality, [seed.User]), (CollectionSharingLevels.Selected, [seed.Owner]), (CollectionSharingLevels.Selected, []),
                     ("everyone", []),
                 })
        {
            (await ShareAsync(owner, seed.Collection, level, departments)).StatusCode.ShouldBe(HttpStatusCode.BadRequest, level);
        }

        // Once the assistant no longer links it (archived assistants still do: only a new version unlinks), the owner may narrow.
        await using (var db = TestPostgres.Context(database.Connection(null)))
        {
            await db.Assistants.Where(a => a.Id == linking).ExecuteUpdateAsync(u => u.SetProperty(a => a.PublishedVersionId, (Guid?)null), Ct);
        }

        (await ShareAsync(owner, seed.Collection, CollectionSharingLevels.Department)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_assistant_links_a_shared_collection_with_topics_and_v11_warns_on_an_unshared_one_or_a_bad_topic()
    {
        var seed = await SeedAsync();
        await using var factory = database.HeaderFactory();
        using var owner = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, seed.OwnerCode);
        using var user = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, seed.UserCode);
        using var created = await user.PostAsJsonAsync("/api/assistants", new
        {
            name = "Bygglovsassistenten " + seed.UserCode, departmentId = seed.User, classification = Classification.Internal,
            description = "Svarar om bygglov.", contactName = "A. Andersson", contactEmail = "bygg@umea.se",
        }, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        var id = (await Json(created)).GetProperty("id").GetGuid();

        async Task<JsonElement> SaveAsync(object[] links)
        {
            var current = await user.GetFromJsonAsync<JsonElement>($"/api/assistants/{id}/draft", Ct);
            using var saved = await user.PutAsJsonAsync($"/api/assistants/{id}/draft", new
            {
                gatewayModel = Model, systemPrompt = "Svara kort.", collectionLinks = links, version = current.GetProperty("version").GetString(),
            }, Ct);
            saved.StatusCode.ShouldBe(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync(Ct));
            return await Json(saved);
        }

        static List<string> Codes(JsonElement draft) => [.. draft.GetProperty("warnings").EnumerateArray().Select(w => w.GetProperty("code").GetString()!)];

        // Not shared yet: V11 names it.
        var draft = await SaveAsync([new { collectionId = seed.Collection, tags = new[] { "bygglov" } }]);
        Codes(draft).ShouldContain("collection_not_shared");

        (await ShareAsync(owner, seed.Collection, CollectionSharingLevels.Selected, seed.User)).StatusCode.ShouldBe(HttpStatusCode.OK);
        draft = await SaveAsync([new { collectionId = seed.Collection, tags = new[] { "bygglov" } }, new { collectionId = seed.UserCollection, tags = Array.Empty<string>() }]);
        Codes(draft).ShouldNotContain("collection_not_shared");
        Codes(draft).ShouldNotContain("topic_invalid");
        var links = draft.GetProperty("collectionLinks").EnumerateArray().ToList();
        links.Count.ShouldBe(2);
        links[0].GetProperty("collectionId").GetGuid().ShouldBe(seed.Collection);
        links[0].GetProperty("tags").EnumerateArray().Select(t => t.GetString()).ShouldBe(["bygglov"]);

        // V11 topics: the chunk tag format, at most 20 per link.
        foreach (var tags in new[] { new[] { "Bygglov" }, ["bygg lov"], [new string('a', 51)], [.. Enumerable.Range(0, 21).Select(i => "t" + i)] })
        {
            draft = await SaveAsync([new { collectionId = seed.Collection, tags }]);
            var warning = draft.GetProperty("warnings").EnumerateArray().Single(w => w.GetProperty("code").GetString() == "topic_invalid");
            warning.GetProperty("affected")[0].GetString().ShouldBe($"umea.se {seed.OwnerCode[1..]}");
        }

        // The collection's owner sees the other department's assistant in usedBy.
        var detail = await owner.GetFromJsonAsync<JsonElement>($"/api/collections/{seed.Collection}", Ct);
        var usedBy = detail.GetProperty("usedBy").EnumerateArray().ShouldHaveSingleItem();
        usedBy.GetProperty("id").GetGuid().ShouldBe(id);
        usedBy.GetProperty("departmentId").GetGuid().ShouldBe(seed.User);
        usedBy.GetProperty("draft").GetBoolean().ShouldBeTrue();
        usedBy.GetProperty("topics").GetArrayLength().ShouldBe(21);

        // A third department, not on the list, gets V11 for the same link.
        using var third = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, seed.ThirdCode);
        using var other = await third.PostAsJsonAsync("/api/assistants", new { name = "Teknik " + seed.ThirdCode, departmentId = seed.Third, classification = "internal" }, Ct);
        var otherId = (await Json(other)).GetProperty("id").GetGuid();
        var otherDraft = await third.GetFromJsonAsync<JsonElement>($"/api/assistants/{otherId}/draft", Ct);
        using var refused = await third.PutAsJsonAsync($"/api/assistants/{otherId}/draft", new
        {
            gatewayModel = Model, collectionLinks = new[] { new { collectionId = seed.Collection, tags = Array.Empty<string>() } },
            version = otherDraft.GetProperty("version").GetString(),
        }, Ct);
        Codes(await Json(refused)).ShouldContain("collection_not_shared");
    }

    [Fact]
    public async Task Create_defaults_the_owner_team_to_the_creators_first_team_and_keeps_the_contact()
    {
        var seed = await SeedAsync();
        var teamCode = "T" + seed.UserCode;
        await using (var db = TestPostgres.Context(database.Connection(null)))
        {
            db.Teams.AddRange(
                new Team { Id = Guid.CreateVersion7(), DepartmentId = seed.User, Name = "Ö-laget", Code = teamCode + "b", SyncedAt = DateTimeOffset.UtcNow },
                new Team { Id = Guid.CreateVersion7(), DepartmentId = seed.User, Name = "A-laget", Code = teamCode + "a", SyncedAt = DateTimeOffset.UtcNow },
                new Team { Id = Guid.CreateVersion7(), DepartmentId = seed.User, Name = "0-ej medlem", Code = teamCode + "x", SyncedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(Ct);
        }

        var first = await TeamIdAsync(teamCode + "a");
        await using var factory = database.HeaderFactory();
        using var user = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, seed.UserCode);
        user.DefaultRequestHeaders.Add(HeaderAuthenticationHandler.TeamHeader, $"{teamCode}b,{teamCode}a");

        using var collection = await user.PostAsJsonAsync("/api/collections", new
        {
            name = "Detaljplaner " + seed.UserCode, departmentId = seed.User, classification = Classification.Public, contactName = " P. Plan ", contactEmail = "plan@umea.se",
        }, Ct);
        collection.StatusCode.ShouldBe(HttpStatusCode.Created, await collection.Content.ReadAsStringAsync(Ct));
        var created = await Json(collection);
        created.GetProperty("ownerTeamId").GetGuid().ShouldBe(first);
        created.GetProperty("contactName").GetString().ShouldBe("P. Plan");
        created.GetProperty("sharedWith").GetString().ShouldBe(CollectionSharingLevels.Department);

        using (var updated = await user.PutAsJsonAsync($"/api/collections/{created.GetProperty("id").GetGuid()}", new
               {
                   name = "Detaljplaner", classification = Classification.Public, ownerTeamId = first, contactName = "", contactEmail = "plan2@umea.se",
                   version = created.GetProperty("version").GetString(),
               }, Ct))
        {
            updated.StatusCode.ShouldBe(HttpStatusCode.OK);
            var body = await Json(updated);
            body.GetProperty("contactName").ValueKind.ShouldBe(JsonValueKind.Null);
            body.GetProperty("contactEmail").GetString().ShouldBe("plan2@umea.se");
        }

        using var assistant = await user.PostAsJsonAsync("/api/assistants", new { name = "Planassistenten " + seed.UserCode, departmentId = seed.User, classification = "public" }, Ct);
        (await Json(assistant)).GetProperty("ownerTeamId").GetGuid().ShouldBe(first);

        // Without a team in the department, no owner team; a named one still wins.
        using var noTeam = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, seed.UserCode);
        using var plain = await noTeam.PostAsJsonAsync("/api/collections", new { name = "Utan lag " + seed.UserCode, departmentId = seed.User, classification = "public" }, Ct);
        (await Json(plain)).GetProperty("ownerTeamId").ValueKind.ShouldBe(JsonValueKind.Null);
        var named = await TeamIdAsync(teamCode + "x");
        using var chosen = await user.PostAsJsonAsync("/api/collections", new { name = "Valt lag " + seed.UserCode, departmentId = seed.User, classification = "public", ownerTeamId = named }, Ct);
        (await Json(chosen)).GetProperty("ownerTeamId").GetGuid().ShouldBe(named);
    }

    private async Task<Guid> TeamIdAsync(string code)
    {
        await using var db = TestPostgres.Context(database.Connection(null));
        return await db.Teams.Where(t => t.Code == code).Select(t => t.Id).SingleAsync(Ct);
    }

    [Fact]
    public async Task The_map_has_a_row_per_department_knowledge_base_assistant_and_audience_path()
    {
        var seed = await SeedAsync();
        await using var factory = database.HeaderFactory();
        using var owner = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, seed.OwnerCode);
        (await ShareAsync(owner, seed.Collection, CollectionSharingLevels.Municipality)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var permits = await AssistantAsync(seed.User, seed.Collection, ["bygglov"]);
        var draftOnly = await AssistantAsync(seed.User, seed.Collection, [], published: false);
        Guid client;
        await using (var db = TestPostgres.Context(database.Connection(null)))
        {
            var owui = new Client { Name = "Open WebUI " + seed.UserCode, Kind = ClientKinds.OpenWebUi, ClassificationCeiling = Classification.Internal, UpdatedAt = DateTimeOffset.UtcNow };
            db.Add(owui);
            await db.SaveChangesAsync(Ct);
            db.AssistantAudiences.Add(new AssistantAudience { AssistantId = permits, Kind = AssistantAudienceKinds.Client, SubjectId = owui.Id });
            await db.SaveChangesAsync(Ct);
            client = owui.Id;
        }

        using var viewer = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Viewer, seed.UserCode);
        var map = await viewer.GetFromJsonAsync<JsonElement>($"/api/organisation/departments/{seed.User}/map", Ct);
        var rows = map.GetProperty("rows").EnumerateArray().ToList();
        rows.Count.ShouldBe(3);
        // Own knowledge base first, unlinked: the path ends there.
        rows[0].GetProperty("collection").GetProperty("id").GetGuid().ShouldBe(seed.UserCollection);
        rows[0].GetProperty("assistant").ValueKind.ShouldBe(JsonValueKind.Null);
        var shared = rows.Where(r => r.GetProperty("collection").GetProperty("id").GetGuid() == seed.Collection).ToList();
        shared.Count.ShouldBe(2);
        foreach (var row in shared)
        {
            var c = row.GetProperty("collection");
            c.GetProperty("shared").GetBoolean().ShouldBeTrue();
            c.GetProperty("departmentId").GetGuid().ShouldBe(seed.Owner);
            c.GetProperty("contactName").GetString().ShouldBe("K. Lund");
        }

        var published = shared.Single(r => r.GetProperty("assistant").GetProperty("id").GetGuid() == permits);
        published.GetProperty("assistant").GetProperty("draftOnly").GetBoolean().ShouldBeFalse();
        published.GetProperty("assistant").GetProperty("publishedVersion").GetInt32().ShouldBe(1);
        published.GetProperty("assistant").GetProperty("topics").EnumerateArray().Select(t => t.GetString()).ShouldBe(["bygglov"]);
        published.GetProperty("assistant").GetProperty("contactEmail").GetString().ShouldBe("bygg@umea.se");
        published.GetProperty("audience").GetProperty("clientId").GetGuid().ShouldBe(client);
        var draft = shared.Single(r => r.GetProperty("assistant").GetProperty("id").GetGuid() == draftOnly);
        draft.GetProperty("assistant").GetProperty("draftOnly").GetBoolean().ShouldBeTrue();
        draft.GetProperty("audience").ValueKind.ShouldBe(JsonValueKind.Null);

        // The owner's map: its knowledge base, no assistant of its own; another department's map is 404 for it.
        var ownerMap = await owner.GetFromJsonAsync<JsonElement>($"/api/organisation/departments/{seed.Owner}/map", Ct);
        var ownerRow = ownerMap.GetProperty("rows").EnumerateArray().ShouldHaveSingleItem();
        ownerRow.GetProperty("collection").GetProperty("sharedWith").GetString().ShouldBe(CollectionSharingLevels.Municipality);
        ownerRow.GetProperty("assistant").ValueKind.ShouldBe(JsonValueKind.Null);
        (await owner.GetAsync($"/api/organisation/departments/{seed.User}/map", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using var platform = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);
        (await platform.GetAsync($"/api/organisation/departments/{seed.User}/map", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_archived_assistant_is_off_the_map_and_archived_in_used_by()
    {
        var seed = await SeedAsync();
        await using var factory = database.HeaderFactory();
        using var owner = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, seed.OwnerCode);
        (await ShareAsync(owner, seed.Collection, CollectionSharingLevels.Municipality)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var active = await AssistantAsync(seed.User, seed.Collection, ["bygglov"]);
        var archived = await AssistantAsync(seed.User, seed.Collection, [], published: false);
        // An archived assistant of the owner links its own knowledge base only through the draft (the #374 case).
        var ownArchived = await AssistantAsync(seed.Owner, seed.Collection, [], published: false);
        await using (var db = TestPostgres.Context(database.Connection(null)))
        {
            await db.Assistants.Where(a => a.Id == archived || a.Id == ownArchived)
                .ExecuteUpdateAsync(u => u.SetProperty(a => a.State, AssistantStates.Archived), Ct);
        }

        // The Map: who uses what now. The user's map has the active assistant only; the owner's knowledge base ends its path.
        using var viewer = await AdminDatabaseFixture.UserAsync(factory, RagRoles.Viewer, seed.UserCode);
        var map = await viewer.GetFromJsonAsync<JsonElement>($"/api/organisation/departments/{seed.User}/map", Ct);
        var assistants = map.GetProperty("rows").EnumerateArray().Where(r => r.GetProperty("assistant").ValueKind != JsonValueKind.Null)
            .Select(r => r.GetProperty("assistant").GetProperty("id").GetGuid()).ToList();
        assistants.ShouldBe([active]);
        var ownerRow = (await owner.GetFromJsonAsync<JsonElement>($"/api/organisation/departments/{seed.Owner}/map", Ct)).GetProperty("rows").EnumerateArray()
            .ShouldHaveSingleItem();
        ownerRow.GetProperty("collection").GetProperty("id").GetGuid().ShouldBe(seed.Collection);
        ownerRow.GetProperty("assistant").ValueKind.ShouldBe(JsonValueKind.Null);

        // Used by keeps the archived ones (they block deletion and can be restored), with their state.
        var usedBy = (await owner.GetFromJsonAsync<JsonElement>($"/api/collections/{seed.Collection}", Ct)).GetProperty("usedBy").EnumerateArray()
            .ToDictionary(u => u.GetProperty("id").GetGuid(), u => u.GetProperty("state").GetString());
        usedBy.Count.ShouldBe(3);
        (usedBy[active], usedBy[archived], usedBy[ownArchived]).ShouldBe((AssistantStates.Active, AssistantStates.Archived, AssistantStates.Archived));
    }

    [Fact]
    public async Task The_list_the_detail_and_the_source_list_count_the_same_live_documents()
    {
        var seed = await SeedAsync();
        // One of the two documents loses its live chunk set: it is still a document, but it cannot be found.
        await using (var db = TestPostgres.Context(database.Connection(null)))
        {
            await db.Documents.Where(d => d.Id == seed.Document).ExecuteUpdateAsync(u => u.SetProperty(d => d.LiveChunkSetId, (Guid?)null), Ct);
        }

        await using var factory = database.HeaderFactory();
        using var owner = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, seed.OwnerCode);
        var row = (await owner.GetFromJsonAsync<JsonElement>("/api/collections?pageSize=200", Ct)).GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("id").GetGuid() == seed.Collection);
        row.GetProperty("documentCount").GetInt32().ShouldBe(1);
        (await owner.GetFromJsonAsync<JsonElement>($"/api/collections/{seed.Collection}", Ct)).GetProperty("documentCount").GetInt32().ShouldBe(1);
        var source = (await owner.GetFromJsonAsync<JsonElement[]>($"/api/collections/{seed.Collection}/sources", Ct))!.ShouldHaveSingleItem();
        source.GetProperty("documents").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task A_department_admin_picks_from_every_active_department_by_name_for_sharing()
    {
        var seed = await SeedAsync();
        await using (var db = TestPostgres.Context(database.Connection(null)))
        {
            await db.Departments.Where(d => d.Id == seed.Third).ExecuteUpdateAsync(u => u.SetProperty(d => d.IsActive, false), Ct);
        }

        await using var factory = database.HeaderFactory();
        using var owner = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, seed.OwnerCode);
        var names = (await owner.GetFromJsonAsync<JsonElement>("/api/organisation/departments/names", Ct)).EnumerateArray().ToList();
        var ids = names.Select(n => n.GetProperty("id").GetGuid()).ToList();
        ids.ShouldContain(seed.User); // outside the admin's scope, still offered for sharing
        ids.ShouldContain(seed.Owner);
        ids.ShouldNotContain(seed.Third); // inactive
        names.Single(n => n.GetProperty("id").GetGuid() == seed.User).EnumerateObject().Select(p => p.Name).ShouldBe(["id", "name"]);
    }

    [Fact]
    public async Task The_organisation_routes_carry_the_policies_the_permission_matrix_tests_check()
    {
        await using var factory = database.HeaderFactory();
        var routes = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .GroupBy(e => $"{e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods[0]} {e.RoutePattern.RawText?.TrimEnd('/')}")
            .ToDictionary(g => g.Key, g => g.SelectMany(e => e.Metadata.GetOrderedMetadata<IAuthorizeData>()).Select(a => a.Policy).OfType<string>().ToHashSet());
        routes["PUT /api/collections/{id:guid}/sharing"].ShouldContain(AdminPolicies.Manage);
        routes["GET /api/collections/{id:guid}/topics"].ShouldBe(new HashSet<string> { AdminPolicies.Read });
        routes["GET /api/organisation/departments/{id:guid}/map"].ShouldBe(new HashSet<string> { AdminPolicies.Read });
        routes["GET /api/organisation/departments/names"].ShouldBe(new HashSet<string> { AdminPolicies.Read });
        routes["GET /api/collections"].ShouldBe(new HashSet<string> { AdminPolicies.Read });
    }
}
