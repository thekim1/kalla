using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Kalla.Domain;
using Kalla.Domain.Admin;
using Kalla.Domain.Entities;
using Kalla.TestKit;

namespace Kalla.AdminApi.Tests;

/// <summary>
/// B-64 (admin design §4.5, §5.5.2; Decisions "Topics editor"): <c>GET …/topic-suggestions</c>, the sections of the sources'
/// live documents (both levels, the cut-off at 5, an upload's folders, the topics that cover a section, 404 for a department
/// the collection is shared with) and <c>pending</c> on <c>GET …/topics</c> while a source is processed again.
/// </summary>
[Collection(AdminDatabaseGroup8.Name)]
public sealed class TopicSuggestionsTests(AdminDatabaseFixture database, AdminHost host) : IClassFixture<AdminHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Seed(string OwnerCode, string SharedCode, Guid Collection, Guid Website, Guid Upload);

    /// <summary>
    /// <c>Owner</c>'s collection, shared with <c>Shared</c>, with a website source whose published rules tag <c>/bygga/</c>
    /// <c>bygga</c> and <c>/kommun/</c> <c>kommun</c>, and an upload source without rules. Website documents: <c>/bygga/</c>
    /// (the section's page, titled) and five under <c>/bygga/bygglov/</c>; four under <c>/skola/</c> (under the cut-off);
    /// five under <c>/kommun/</c>; five under <c>/äldre/</c>, three of them written percent-encoded; one at the root. Upload
    /// documents: five in <c>HR/Policys/</c>, one in <c>HR/</c>, one written <c>/HR/g.pdf</c> (no <c>HR/</c> rule
    /// matches it, so it is in no section), one at the top. Only the <c>/bygga/</c> documents carry a tag yet.
    /// </summary>
    private async Task<Seed> SeedAsync()
    {
        var n = Random.Shared.Next(100_000, 999_999);
        await using var writer = new ContentFixtureWriter(database.Connection(RagDatabase.Worker), database.Connection(RagDatabase.Admin));
        var profile = await writer.AddEmbeddingProfileAsync(8, Ct);
        await using var db = TestPostgres.Context(database.Connection(null));
        Department NewDepartment(string prefix) =>
            new() { Id = Guid.CreateVersion7(), Name = $"{prefix} {n}", CostCenterCode = $"{prefix[0]}{n}", SyncedAt = DateTimeOffset.UtcNow };
        var owner = NewDepartment("Kommunikation");
        var shared = NewDepartment("Bygg");
        var collection = new Collection
        {
            Slug = $"umea-se-{n}", Name = $"umea.se {n}", DepartmentId = owner.Id, Classification = Classification.Internal, EmbeddingProfileId = profile,
            SharedWith = CollectionSharingLevels.Selected, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var website = new Pipeline { Name = $"topics-web-{n}", TemplateId = "website", UpdatedAt = DateTimeOffset.UtcNow };
        var websiteVersion = new PipelineVersion
        {
            PipelineId = website.Id, Number = 1, State = PipelineVersionStates.Published, PublishedAt = DateTimeOffset.UtcNow,
            Definition = """
                {"schema":1,"template":"website","templateVersion":1,"params":{"pathRules":[
                  {"match":"/bygga/","on":"path","tags":["bygga"]},
                  {"match":"www.umea.se/kommun/**","on":"path","tags":["Kommun"]},
                  {"match":"Protokoll","on":"title-regex","tags":["protokoll"]}]}}
                """,
        };
        var files = new Pipeline { Name = $"topics-files-{n}", TemplateId = "files", UpdatedAt = DateTimeOffset.UtcNow };
        var filesVersion = new PipelineVersion
        {
            PipelineId = files.Id, Number = 1, State = PipelineVersionStates.Published, PublishedAt = DateTimeOffset.UtcNow,
            Definition = """{"schema":1,"template":"files","templateVersion":1,"params":{}}""",
        };
        var web = new Source
        {
            CollectionId = collection.Id, Type = SourceTypes.Crawl, Name = "umea.se", Classification = Classification.Internal, PipelineId = website.Id,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var upload = new Source
        {
            CollectionId = collection.Id, Type = SourceTypes.Upload, Name = "Filer", Classification = Classification.Internal, PipelineId = files.Id,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.AddRange(owner, shared, collection, website, websiteVersion, files, filesVersion, web, upload);
        db.CollectionShares.Add(new CollectionShare { CollectionId = collection.Id, DepartmentId = shared.Id, SharedBy = "test", SharedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(Ct);
        await db.Pipelines.Where(p => p.Id == website.Id).ExecuteUpdateAsync(u => u.SetProperty(p => p.PublishedVersionId, websiteVersion.Id), Ct);
        await db.Pipelines.Where(p => p.Id == files.Id).ExecuteUpdateAsync(u => u.SetProperty(p => p.PublishedVersionId, filesVersion.Id), Ct);

        async Task Page(string path, string? title = null, params string[] tags) =>
            await writer.AddDocumentAsync(new FixtureDocumentSpec
            {
                CollectionId = collection.Id, EmbeddingProfileId = profile, SourceId = web.Id, Classification = Classification.Internal,
                Title = title ?? path, Url = $"https://www.umea.se{path}",
            }, [new FixtureChunk($"Sidan {path}.") { Tags = [.. tags.Where(t => t.Length > 0)] }], Ct);
        async Task File(string displayName) =>
            await writer.AddDocumentAsync(new FixtureDocumentSpec
            {
                CollectionId = collection.Id, EmbeddingProfileId = profile, SourceId = upload.Id, Classification = Classification.Internal, IdentityKind = "upload",
                DisplayName = displayName, Title = displayName, MediaType = "application/pdf",
            }, [new FixtureChunk($"Filen {displayName}.")], Ct);

        await Page("/bygga/", "Bygga, bo och miljö", "bygga");
        foreach (var page in new[] { "bygglov/", "bygglov/altan", "bygglov/attefall", "bygglov/carport", "bygglov/plank" })
        {
            await Page("/bygga/" + page, null, "bygga");
        }

        foreach (var page in new[] { "a", "b", "c", "d" })
        {
            await Page("/skola/" + page);
        }

        foreach (var page in new[] { "", "a", "b", "c", "d" })
        {
            await Page("/kommun/" + page + "?ref=meny");
        }

        foreach (var page in new[] { "/%C3%A4ldre/a", "/%C3%A4ldre/b", "/%C3%A4ldre/c", "/äldre/d", "/äldre/e" })
        {
            // Two tags that sort apart: ordinally ä before å, in Swedish å before ä (#1059).
            await Page(page, null, page.EndsWith('a') ? "äldre" : page.EndsWith('d') ? "ålder" : "");
        }

        await Page("/");
        foreach (var file in new[] { "HR/Policys/a.pdf", "HR/Policys/b.pdf", "HR/Policys/c.pdf", "HR/Policys/d.pdf", "HR/Policys/e.pdf", "HR/f.pdf", "/HR/g.pdf", "h.pdf" })
        {
            await File(file);
        }

        return new Seed(owner.CostCenterCode, shared.CostCenterCode, collection.Id, web.Id, upload.Id);
    }

    private static List<JsonElement> Sections(JsonElement suggestions, Guid source) =>
        [.. suggestions.GetProperty("sources").EnumerateArray().Single(s => s.GetProperty("sourceId").GetGuid() == source).GetProperty("sections").EnumerateArray()];

    private static List<string> Strings(JsonElement array) => [.. array.EnumerateArray().Select(t => t.GetString()!)];

    [Fact]
    public async Task The_first_level_lists_each_sources_sections_of_5_documents_or_more_with_their_tag_title_and_the_topics_that_cover_them()
    {
        var seed = await SeedAsync();
        using var viewer = await AdminDatabaseFixture.UserAsync(host.Factory, RagRoles.Viewer, seed.OwnerCode);
        var suggestions = await viewer.GetFromJsonAsync<JsonElement>($"/api/collections/{seed.Collection}/topic-suggestions", Ct);

        var blocks = suggestions.GetProperty("sources").EnumerateArray().ToList();
        blocks.Select(b => b.GetProperty("sourceName").GetString()).ShouldBe(["Filer", "umea.se"]);
        var web = Sections(suggestions, seed.Website);
        // Most documents first; /skola/ (4) and the root page are left out; the encoded and the plain /äldre/ are one section.
        web.Select(s => s.GetProperty("path").GetString()).ShouldBe(["/bygga/", "/kommun/", "/äldre/"]);
        var bygga = web[0];
        bygga.GetProperty("documents").GetInt32().ShouldBe(6);
        bygga.GetProperty("title").GetString().ShouldBe("Bygga, bo och miljö");
        bygga.GetProperty("suggestedTag").GetString().ShouldBe("bygga");
        Strings(bygga.GetProperty("coveredBy")).ShouldBe(["bygga"]);
        // A host-first rule covers the section on its host; its tag as chunks carry it (lower case). A title rule covers no section.
        Strings(web[1].GetProperty("coveredBy")).ShouldBe(["kommun"]);
        web[1].GetProperty("documents").GetInt32().ShouldBe(5);
        web[2].GetProperty("documents").GetInt32().ShouldBe(5);
        web[2].GetProperty("suggestedTag").GetString().ShouldBe("äldre");
        web[2].GetProperty("coveredBy").GetArrayLength().ShouldBe(0);
        (!web[2].TryGetProperty("title", out var title) || title.ValueKind == JsonValueKind.Null).ShouldBeTrue(); // no page of its own

        // An upload's sections are the first folders of its display names, as the matcher reads them (/HR/g.pdf is not in HR/).
        var files = Sections(suggestions, seed.Upload);
        files.Select(s => s.GetProperty("path").GetString()).ShouldBe(["HR/"]);
        files[0].GetProperty("documents").GetInt32().ShouldBe(6);
        files[0].GetProperty("suggestedTag").GetString().ShouldBe("hr");
        blocks.Single(b => b.GetProperty("sourceId").GetGuid() == seed.Upload).GetProperty("kind").GetString().ShouldBe("upload");
    }

    [Fact]
    public async Task Under_gives_one_sections_next_level_for_one_source()
    {
        var seed = await SeedAsync();
        using var editor = await AdminDatabaseFixture.UserAsync(host.Factory, RagRoles.PipelineEditor, seed.OwnerCode);
        var url = $"/api/collections/{seed.Collection}/topic-suggestions";

        var bygga = await editor.GetFromJsonAsync<JsonElement>($"{url}?sourceId={seed.Website}&under=/bygga/", Ct);
        var block = bygga.GetProperty("sources").EnumerateArray().Single();
        block.GetProperty("under").GetString().ShouldBe("/bygga/");
        var sections = block.GetProperty("sections").EnumerateArray().ToList();
        sections.Select(s => s.GetProperty("path").GetString()).ShouldBe(["/bygga/bygglov/"]);
        sections[0].GetProperty("documents").GetInt32().ShouldBe(5);
        sections[0].GetProperty("suggestedTag").GetString().ShouldBe("bygglov");
        Strings(sections[0].GetProperty("coveredBy")).ShouldBe(["bygga"]); // a rule on the parent covers what is under it

        var hr = await editor.GetFromJsonAsync<JsonElement>($"{url}?sourceId={seed.Upload}&under={Uri.EscapeDataString("HR/")}", Ct);
        Sections(hr, seed.Upload).Select(s => s.GetProperty("path").GetString()).ShouldBe(["HR/Policys/"]);
        Sections(hr, seed.Upload)[0].GetProperty("suggestedTag").GetString().ShouldBe("policys");

        // A section under the cut-off has no next level to show either.
        (await editor.GetFromJsonAsync<JsonElement>($"{url}?sourceId={seed.Website}&under=/skola/", Ct)).GetProperty("sources")[0]
            .GetProperty("sections").GetArrayLength().ShouldBe(0);

        (await editor.GetAsync($"{url}?under=/bygga/", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest); // under= names one source
        (await editor.GetAsync($"{url}?sourceId={seed.Website}&under=bygga/", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await editor.GetAsync($"{url}?sourceId={seed.Website}&under=/bygga//", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await editor.GetAsync($"{url}?sourceId={seed.Website}&under=/", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await editor.GetAsync($"{url}?sourceId={Guid.NewGuid()}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_department_the_collection_is_shared_with_gets_404_and_sees_no_pending_topic()
    {
        var seed = await SeedAsync();
        using var shared = await AdminDatabaseFixture.UserAsync(host.Factory, RagRoles.DepartmentAdmin, seed.SharedCode);
        (await shared.GetAsync($"/api/collections/{seed.Collection}/topic-suggestions", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await shared.GetAsync($"/api/collections/{seed.Collection}/topic-suggestions?sourceId={seed.Website}&under=/bygga/", Ct)).StatusCode
            .ShouldBe(HttpStatusCode.NotFound);

        await EnqueueReprocessAsync(seed.Website);
        var topics = (await shared.GetFromJsonAsync<JsonElement>($"/api/collections/{seed.Collection}/topics", Ct)).GetProperty("topics").EnumerateArray().ToList();
        // The same Swedish order as the owner's list.
        topics.Select(t => t.GetProperty("tag").GetString()).ShouldBe(["bygga", "ålder", "äldre"]);
        topics[0].GetProperty("rules").ValueKind.ShouldBe(JsonValueKind.Null);
        topics[0].GetProperty("pending").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task A_tag_a_published_rule_sets_is_pending_while_its_source_is_processed_again_and_no_live_chunk_carries_it()
    {
        var seed = await SeedAsync();
        using var viewer = await AdminDatabaseFixture.UserAsync(host.Factory, RagRoles.Viewer, seed.OwnerCode);
        async Task<List<JsonElement>> TopicsAsync() =>
            [.. (await viewer.GetFromJsonAsync<JsonElement>($"/api/collections/{seed.Collection}/topics", Ct)).GetProperty("topics").EnumerateArray()];

        // No re-process: only the tags on live chunks.
        (await TopicsAsync()).Select(t => t.GetProperty("tag").GetString()).ShouldBe(["bygga", "ålder", "äldre"]);

        // The nightly drift sweep processes again without a rule change: nothing pending (#1059).
        var sweep = await EnqueueReprocessAsync(seed.Website, "schedule");
        await using (var sweepDb = TestPostgres.Context(database.Connection(null)))
        {
            sweepDb.Runs.Add(new Run
            {
                SourceId = seed.Website, Classification = Classification.Internal, Kind = RunKinds.Reprocess, Trigger = RunTriggers.Schedule, StartedAt = DateTimeOffset.UtcNow,
            });
            await sweepDb.SaveChangesAsync(Ct);
            await sweepDb.Jobs.Where(j => j.Id == sweep).ExecuteUpdateAsync(u => u.SetProperty(j => j.State, JobStates.Running), Ct);
        }

        (await TopicsAsync()).Select(t => t.GetProperty("tag").GetString()).ShouldBe(["bygga", "ålder", "äldre"]);

        var job = await EnqueueReprocessAsync(seed.Website);
        var topics = await TopicsAsync();
        topics.Select(t => t.GetProperty("tag").GetString()).ShouldBe(["bygga", "kommun", "protokoll", "ålder", "äldre"]);
        var bygga = topics[0];
        bygga.GetProperty("pending").GetBoolean().ShouldBeFalse();
        bygga.GetProperty("documents").GetInt32().ShouldBe(6);
        var kommun = topics[1];
        kommun.GetProperty("pending").GetBoolean().ShouldBeTrue();
        kommun.GetProperty("documents").ValueKind.ShouldBe(JsonValueKind.Null);
        kommun.GetProperty("rules").EnumerateArray().Single().GetProperty("match").GetString().ShouldBe("www.umea.se/kommun/**");

        // Running counts as well; once the job is done the tag is gone until a chunk carries it.
        await using var db = TestPostgres.Context(database.Connection(null));
        await db.Jobs.Where(j => j.Id == job).ExecuteUpdateAsync(u => u.SetProperty(j => j.State, JobStates.Running), Ct);
        (await TopicsAsync()).Count(t => t.GetProperty("pending").GetBoolean()).ShouldBe(2);
        await db.Jobs.Where(j => j.Id == job).ExecuteUpdateAsync(u => u.SetProperty(j => j.State, JobStates.Succeeded), Ct);
        (await TopicsAsync()).Select(t => t.GetProperty("tag").GetString()).ShouldBe(["bygga", "ålder", "äldre"]);

        // A reprocess run still running between its jobs keeps them pending.
        db.Runs.Add(new Run
        {
            SourceId = seed.Website, Classification = Classification.Internal, Kind = RunKinds.Reprocess, Trigger = RunTriggers.Publish, StartedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(Ct);
        (await TopicsAsync()).Count(t => t.GetProperty("pending").GetBoolean()).ShouldBe(2);
    }

    [Fact]
    public async Task The_suggestions_route_carries_the_read_policy()
    {
        var routes = host.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .GroupBy(e => $"{e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods[0]} {e.RoutePattern.RawText?.TrimEnd('/')}")
            .ToDictionary(g => g.Key, g => g.SelectMany(e => e.Metadata.GetOrderedMetadata<IAuthorizeData>()).Select(a => a.Policy).OfType<string>().ToHashSet());
        routes["GET /api/collections/{id:guid}/topic-suggestions"].ShouldBe(new HashSet<string> { AdminPolicies.Read });
        routes["POST /api/collections/{id:guid}/topic-rules/preview"].ShouldContain(AdminPolicies.EditPipelines);
    }

    [Fact]
    public async Task The_preview_counts_what_each_rule_kind_matches_with_the_first_ten_through_the_workers_matcher()
    {
        var seed = await SeedAsync();
        using var editor = await AdminDatabaseFixture.UserAsync(host.Factory, RagRoles.PipelineEditor, seed.OwnerCode);
        var url = $"/api/collections/{seed.Collection}/topic-rules/preview";
        async Task<JsonElement> Preview(Guid source, object rule)
        {
            var response = await editor.PostAsJsonAsync(url, new { sourceId = source, rule }, Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
            return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        }

        // 21 live website documents: /bygga/ 6, /skola/ 4, /kommun/ 5, /äldre/ 5 and the root.
        var under = await Preview(seed.Website, new { match = "/bygga/", on = "path", tags = new[] { "bygga" } });
        under.GetProperty("matched").GetInt32().ShouldBe(6);
        under.GetProperty("total").GetInt32().ShouldBe(21);
        under.GetProperty("sample").EnumerateArray().Select(d => d.GetProperty("url").GetString()).ShouldContain("https://www.umea.se/bygga/");
        (await Preview(seed.Website, new { match = "https://www.umea.se/kommun/?utm=x", on = "path", tags = new[] { "kommun" } }))
            .GetProperty("matched").GetInt32().ShouldBe(5); // a pasted address, as the worker reads it
        (await Preview(seed.Website, new { match = "^/skola/", on = "path-regex", tags = new[] { "skola" } })).GetProperty("matched").GetInt32().ShouldBe(4);
        (await Preview(seed.Website, new { match = "Bygga, bo", on = "title-regex", tags = new[] { "bygga" } })).GetProperty("matched").GetInt32().ShouldBe(1);
        (await Preview(seed.Website, new { match = "text/html", on = "mediaType", tags = new[] { "sidor" } })).GetProperty("matched").GetInt32().ShouldBe(21);
        // The sample stops at ten.
        var onHost = await Preview(seed.Website, new { match = "www.umea.se", on = "host", tags = new[] { "umea" } });
        onHost.GetProperty("matched").GetInt32().ShouldBe(21);
        onHost.GetProperty("sample").GetArrayLength().ShouldBe(10);
        (await Preview(seed.Website, new { match = "/finns-inte/", on = "path", tags = new[] { "inget" } })).GetProperty("matched").GetInt32().ShouldBe(0);

        // An upload's folders and file types, with its display names in the sample.
        var hr = await Preview(seed.Upload, new { match = "HR/", on = "path", tags = new[] { "hr" } });
        hr.GetProperty("matched").GetInt32().ShouldBe(6);
        hr.GetProperty("total").GetInt32().ShouldBe(8);
        hr.GetProperty("sample")[0].GetProperty("displayName").GetString()!.ShouldContain("HR/");
        (await Preview(seed.Upload, new { match = "application/pdf", on = "mediaType", tags = new[] { "pdf" } })).GetProperty("matched").GetInt32().ShouldBe(8);
    }

    [Fact]
    public async Task The_preview_refuses_an_invalid_rule_another_collections_source_and_whoever_may_not_edit_processing()
    {
        var seed = await SeedAsync();
        var url = $"/api/collections/{seed.Collection}/topic-rules/preview";
        var rule = new { match = "/bygga/", on = "path", tags = new[] { "bygga" } };
        using var editor = await AdminDatabaseFixture.UserAsync(host.Factory, RagRoles.PipelineEditor, seed.OwnerCode);

        var invalid = await editor.PostAsJsonAsync(url, new { sourceId = seed.Website, rule = new { match = "(", on = "path-regex", tags = new[] { "x" } } }, Ct);
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest); // V24: format regex, as at Save
        (await invalid.Content.ReadAsStringAsync(Ct)).ShouldContain("pipeline_params_invalid");
        (await editor.PostAsJsonAsync(url, new { sourceId = seed.Website, rule = new { match = "/a/", on = "nowhere", tags = new[] { "x" } } }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await editor.PostAsJsonAsync(url, new { sourceId = Guid.NewGuid(), rule }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var viewer = await AdminDatabaseFixture.UserAsync(host.Factory, RagRoles.Viewer, seed.OwnerCode);
        (await viewer.PostAsJsonAsync(url, new { sourceId = seed.Website, rule }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using var contentEditor = await AdminDatabaseFixture.UserAsync(host.Factory, RagRoles.Editor, seed.OwnerCode);
        (await contentEditor.PostAsJsonAsync(url, new { sourceId = seed.Website, rule }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using var shared = await AdminDatabaseFixture.UserAsync(host.Factory, RagRoles.DepartmentAdmin, seed.SharedCode);
        (await shared.PostAsJsonAsync(url, new { sourceId = seed.Website, rule }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("/bygga-bo-och-miljo/", "bygga-bo-och-miljo")]
    [InlineData("/Utbildning och barnomsorg/", "utbildning-och-barnomsorg")]
    [InlineData("HR/Policy_2024.v2/", "policy-2024-v2")]
    [InlineData("/!!!/", null)]
    public void The_suggested_tag_is_the_last_segment_in_the_tag_format(string path, string? tag) =>
        TopicSuggestions.SuggestedTag(path).ShouldBe(tag);

    [Fact]
    public void A_long_segment_is_cut_to_50_characters()
    {
        var tag = TopicSuggestions.SuggestedTag("/" + string.Join('-', Enumerable.Repeat("abcdefghi", 8)) + "/")!;
        tag.Length.ShouldBeLessThanOrEqualTo(50);
        tag.ShouldNotEndWith("-");
    }

    private async Task<Guid> EnqueueReprocessAsync(Guid source, string trigger = "publish")
    {
        await using var db = TestPostgres.Context(database.Connection(null));
        var department = await (from s in db.Sources join c in db.Collections on s.CollectionId equals c.Id where s.Id == source select c.DepartmentId).SingleAsync(Ct);
        var job = new Job
        {
            Class = JobClasses.Sync, DepartmentId = department, Classification = Classification.Internal,
            Payload = $$"""{"sourceId":"{{source}}","kind":"reprocess","trigger":"{{trigger}}"}""", EnqueuedAt = DateTimeOffset.UtcNow, State = JobStates.Queued,
        };
        db.Jobs.Add(job);
        await db.SaveChangesAsync(Ct);
        return job.Id;
    }
}
