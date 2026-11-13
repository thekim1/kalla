using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ume.Rag.TestKit;

namespace Ume.Rag.Api.Tests;

/// <summary>
/// Knowledge design §16.3: the routes over HTTP (WebApplicationFactory, FakeGateway, Postgres): the response shape, keys
/// and the client gate, validation, slugs, 404 rules, download grants and content headers, OpenAPI and Scalar.
/// </summary>
public sealed class KnowledgeApiTests(KnowledgeFixture db) : IClassFixture<KnowledgeFixture>, IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly WebApplicationFactory<Program> _api = db.ApiHost();

    public void Dispose() => _api.Dispose();

    private HttpClient Client(string? key)
    {
        var client = _api.CreateHttpsClient();
        if (key is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        return client;
    }

    private string Slug(Guid collection) => db.Snapshot().Collections[collection].Slug;

    private static async Task<JsonNode> Json(HttpResponseMessage response) => JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;

    private Task<HttpResponseMessage> Retrieve(HttpClient client, object body) =>
        client.PostAsync(new Uri("/v1/retrieve", UriKind.Relative), JsonContent.Create(body), Ct);

    [Fact]
    public async Task A_keys_use_is_recorded_as_its_last_use_at_most_every_five_minutes()
    {
        // Identity §6.2, admin §5.9.1: rag-api writes config."KeyLastUse" beside the request; the admin shows it per key and app.
        await using var api = db.ApiHost();
        await using var context = TestPostgres.Context(db.Connection(RagDatabase.Admin));
        var keyId = await context.Keys.AsNoTracking().Where(k => k.ClientId == db.InternalClient && k.Name == "rag-internal").Select(k => k.Id).SingleAsync(Ct);
        // Other tests of this class use the key too (each on its own host): only a use from now on counts.
        var started = DateTimeOffset.UtcNow;
        var uses = context.KeyLastUse.AsNoTracking().Where(u => u.KeyId == keyId && u.LastUsedAt >= started);

        // A refused key records nothing.
        using (var refused = await Client("ume-rag-sk-" + new string('x', 40)).GetAsync(new Uri("/v1/collections", UriKind.Relative), Ct))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using var client = api.CreateHttpsClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", db.InternalKey);
        using var response = await client.GetAsync(new Uri("/v1/collections", UriKind.Relative), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // The write runs beside the request: wait for it.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        while (!await uses.AnyAsync(Ct))
        {
            DateTimeOffset.UtcNow.ShouldBeLessThan(deadline, "no KeyLastUse row a minute after the key was used");
            await Task.Delay(100, Ct);
        }

        var at = await uses.Select(u => u.LastUsedAt).SingleAsync(Ct);
        at.ShouldBeLessThanOrEqualTo(DateTimeOffset.UtcNow);
        // Within the five minutes a further use writes nothing.
        api.Services.GetRequiredService<KeyLastUseRecorder>().TouchAsync(keyId).IsCompletedSuccessfully.ShouldBeTrue();
        (await uses.Select(u => u.LastUsedAt).SingleAsync(Ct)).ShouldBe(at);
    }

    [Fact]
    public async Task A_search_without_an_assistant_runs_as_the_first_searched_collections_gateway_key()
    {
        // B-42 (knowledge §5.2, cost-attribution §4.1): the query embedding of a Knowledge API search runs as the key of the
        // first searched collection; the FakeGateway's dev key "Kontaktcenter – chatbot".
        var key = new Guid("019a0000-0000-7000-8000-00000000b001");
        var snapshot = db.Snapshot();
        snapshot = snapshot with
        {
            Collections = new Dictionary<Guid, Ume.Rag.Domain.Access.CollectionEntry>(snapshot.Collections)
            {
                [db.PublicCollection] = snapshot.Collections[db.PublicCollection] with { GatewayKeyId = key },
            },
        };
        var sent = new System.Collections.Concurrent.ConcurrentQueue<string?>();
        await using var api = db.ApiHost(snapshot).WithWebHostBuilder(web => web.ConfigureTestServices(services =>
        {
            services.AddHttpClient(Ume.Rag.Answering.GatewayQueryEmbedder.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => new OnBehalfOfRecorder(db.Gateway.Server.CreateHandler(), sent));
            services.AddHttpClient(Ume.Rag.Infrastructure.Gateway.GatewayContextReader.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => db.Gateway.Server.CreateHandler());
        }));
        (await api.Services.GetRequiredService<Ume.Rag.Infrastructure.Gateway.GatewayContextReader>().ReadAsync(Ct)).ShouldBeTrue();
        using var client = api.CreateHttpsClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", db.InternalKey);

        using var response = await Retrieve(client, new { query = "Behöver jag bygglov för altan?", collections = new[] { Slug(db.PublicCollection) }, top_k = 5 });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        sent.ShouldNotBeEmpty();
        sent.ShouldAllBe(s => s == key.ToString("D"));
    }

    private sealed class OnBehalfOfRecorder(HttpMessageHandler inner, System.Collections.Concurrent.ConcurrentQueue<string?> sent) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            sent.Enqueue(request.Headers.TryGetValues(Ume.Rag.Infrastructure.Gateway.GatewayHeaders.OnBehalfOf, out var v) ? string.Join(",", v) : null);
            return base.SendAsync(request, cancellationToken);
        }
    }

    [Fact]
    public async Task Retrieve_answers_the_documented_snake_case_shape()
    {
        using var client = Client(db.InternalKey);

        using var response = await Retrieve(client, new { query = "Behöver jag bygglov för altan?", collections = new[] { Slug(db.PublicCollection) }, top_k = 5 });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        var json = await Json(response);
        json["config_version"]!.GetValue<long>().ShouldBe(3);
        json["degraded"].ShouldBeNull();
        json["sections"]!.AsArray().ShouldBeEmpty();
        json["coverage"]!["status"]!.GetValue<string>().ShouldBeOneOf("ok", "low", "unknown");
        var results = json["results"]!.AsArray();
        var altan = results.Single(r => r!["document"]!["id"]!.GetValue<Guid>() == db.Altan.Id)!;
        altan["heading_path"]!.GetValue<string>().ShouldBe("Bygglov › Altan");
        altan["collection"]!.GetValue<string>().ShouldBe(Slug(db.PublicCollection));
        altan["scores"]!["keyword_rank"].ShouldNotBeNull();
        altan["document"]!["source_type"]!.GetValue<string>().ShouldBe("web");
        altan["document"]!["download_url"].ShouldBeNull();
        var upload = results.Single(r => r!["document"]!["id"]!.GetValue<Guid>() == db.Upload.Id)!;
        upload["document"]!["download_url"]!.GetValue<string>().ShouldStartWith($"/v1/documents/{db.Upload.Id}/content?grant=g1.k1.");
    }

    [Fact]
    public async Task Section_expansion_comes_back_with_section_refs()
    {
        using var client = Client(db.InternalKey);

        using var response = await Retrieve(client, new
        {
            query = "plank bygglov 110 centimeter", collections = new[] { Slug(db.SectionCollection) }, top_k = 1, mode = "keyword", expand = "section",
        });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await Json(response);
        json["results"]![0]!["section_ref"]!.GetValue<int>().ShouldBe(0);
        var section = json["sections"]![0]!;
        (section["id"]!.GetValue<int>(), section["token_estimate"]!.GetValue<int>(), section["truncated"]!.GetValue<bool>()).ShouldBe((0, 400, false));
        section["chunk_ids"]!.AsArray().Count.ShouldBe(4);
    }

    [Fact]
    public async Task Missing_and_invalid_keys_are_401_with_a_bearer_challenge()
    {
        using var anonymous = Client(null);
        using var missing = await Retrieve(anonymous, new { query = "bygglov" });
        missing.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        missing.Headers.WwwAuthenticate.ShouldContain(h => h.Scheme == "Bearer");
        (await Json(missing))["code"]!.GetValue<string>().ShouldBe("missing_key");

        using var wrong = Client("ume-rag-sk-" + new string('x', 43));
        using var invalid = await wrong.GetAsync(new Uri("/v1/collections", UriKind.Relative), Ct);
        invalid.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await Json(invalid))["code"]!.GetValue<string>().ShouldBe("invalid_key");
    }

    [Fact]
    public async Task A_client_kind_not_open_for_knowledge_is_403_before_anything_else()
    {
        using var owui = Client(db.OwuiKey);

        using var response = await Retrieve(owui, new { query = "bygglov", collections = new[] { "finns-inte" } });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await Json(response))["code"]!.GetValue<string>().ShouldBe("client_not_allowed");
    }

    [Theory]
    [InlineData("""{"query":"bygglov","top-k":5}""", "body")]
    [InlineData("""{"query":"bygglov","language":"sv"}""", "body")]
    [InlineData("""{"query":"bygglov","expand":"parent"}""", "expand")]
    [InlineData("""{"query":"bygglov","top_k":0}""", "top_k")]
    [InlineData("""{"query":"!!!"}""", "query")]
    [InlineData("""{"query":"bygglov","mode":"fuzzy"}""", "mode")]
    [InlineData("""{"query":"bygglov","collections":[]}""", "collections")]
    [InlineData("""{"query":"bygglov","filters":{"document_types":["video"]}}""", "filters.document_types")]
    [InlineData("""{"query":"bygglov","filters":{"modified_after":"1989-12-31"}}""", "filters.modified_after")]
    [InlineData("""{"query":"bygglov","filters":{"min_score":0.5}}""", "body")]
    public async Task Invalid_requests_are_400_invalid_request_with_the_field(string body, string field)
    {
        using var client = Client(db.InternalKey);

        using var response = await client.PostAsync(new Uri("/v1/retrieve", UriKind.Relative), new StringContent(body, System.Text.Encoding.UTF8, "application/json"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var json = await Json(response);
        json["code"]!.GetValue<string>().ShouldBe("invalid_request");
        json["errors"]![field].ShouldNotBeNull();
    }

    [Fact]
    public async Task An_unknown_or_unpublished_slug_is_404_echoing_only_the_slug()
    {
        using var client = Client(db.InternalKey);

        foreach (var slug in new[] { "finns-inte", Slug(db.UnpublishedCollection) })
        {
            using var response = await Retrieve(client, new { query = "bygglov", collections = new[] { slug } });
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            var json = await Json(response);
            (json["code"]!.GetValue<string>(), json["collection"]!.GetValue<string>()).ShouldBe(("collection_not_found", slug));
            json.ToJsonString().ShouldNotContain("Samling");
        }
    }

    [Fact]
    public async Task Collections_lists_only_live_ones()
    {
        using var client = Client(db.InternalKey);

        var json = await client.GetFromJsonAsync<JsonNode>(new Uri("/v1/collections", UriKind.Relative), Ct);

        json!["data"]!.AsArray().Select(c => c!["slug"]!.GetValue<string>())
            .ShouldBe([Slug(db.PublicCollection), Slug(db.SensitiveCollection), Slug(db.SectionCollection), Slug(db.DeptACollection),
                Slug(db.DeptBCollection)], ignoreOrder: true);
    }

    [Fact]
    public async Task Documents_by_key_and_by_grant_and_malformed_ids_are_404()
    {
        using var client = Client(db.InternalKey);
        var document = await client.GetFromJsonAsync<JsonNode>(new Uri($"/v1/documents/{db.Upload.Id}", UriKind.Relative), Ct);
        (document!["title"]!.GetValue<string>(), document["page_count"]!.GetValue<int>()).ShouldBe(("taxa-bygglov-2026.pdf", 4));
        var link = document["download_url"]!.GetValue<string>();

        using var anonymous = Client(null);
        var grant = link[(link.IndexOf("grant=", StringComparison.Ordinal) + 6)..];
        var byGrant = await anonymous.GetFromJsonAsync<JsonNode>(new Uri($"/v1/documents/{db.Upload.Id}?grant={grant}", UriKind.Relative), Ct);
        byGrant!["download_url"]!.GetValue<string>().ShouldBe(link);

        foreach (var path in new[] { "/v1/documents/not-a-guid", $"/v1/documents/{Guid.NewGuid()}", $"/v1/documents/{db.Hidden.Id}", $"/v1/documents/{db.Altan.Id}?grant={grant}" })
        {
            using var response = await (path.Contains("grant=", StringComparison.Ordinal) ? anonymous : client).GetAsync(new Uri(path, UriKind.Relative), Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound, path);
            (await Json(response))["code"]!.GetValue<string>().ShouldBe("not_found");
        }
    }

    [Fact]
    public async Task Content_by_grant_streams_the_original_with_safe_headers()
    {
        using var client = Client(db.InternalKey);
        var link = (await client.GetFromJsonAsync<JsonNode>(new Uri($"/v1/documents/{db.Upload.Id}", UriKind.Relative), Ct))!["download_url"]!.GetValue<string>();
        using var browser = Client(null);

        using var response = await browser.GetAsync(new Uri(link, UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync(Ct)).ShouldBe(KnowledgeFixture.UploadBytes);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/pdf");
        response.Content.Headers.ContentLength.ShouldBe(KnowledgeFixture.UploadBytes.Length);
        response.Content.Headers.ContentDisposition!.ToString().ShouldStartWith("inline;");
        response.Headers.GetValues("X-Content-Type-Options").ShouldBe(["nosniff"]);
        (response.Headers.CacheControl!.Private, response.Headers.CacheControl.NoStore).ShouldBe((true, true));
        response.Headers.ETag!.Tag.ShouldBe($"\"ab{new string('0', 62)}\"");
        response.Headers.AcceptRanges.ShouldBe(["none"]);
    }

    [Fact]
    public async Task The_grant_never_reaches_the_logs_even_at_trace_level()
    {
        var logs = new CapturingLoggerProvider();
        using var api = db.ApiHost().WithWebHostBuilder(web =>
        {
            web.UseSetting("Logging:LogLevel:Default", "Trace");
            web.ConfigureLogging(logging => logging.AddProvider(logs));
        });
        using var client = api.CreateHttpsClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", db.InternalKey);
        var link = (await client.GetFromJsonAsync<JsonNode>(new Uri($"/v1/documents/{db.Upload.Id}", UriKind.Relative), Ct))!["download_url"]!.GetValue<string>();
        var grant = link[(link.IndexOf("grant=", StringComparison.Ordinal) + 6)..];
        client.DefaultRequestHeaders.Authorization = null;

        using var response = await client.GetAsync(new Uri(link, UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        logs.Entries.ShouldNotBeEmpty();
        logs.Text.ShouldNotContain(grant);
        logs.Text.ShouldNotContain(db.InternalKey);
    }

    [Fact]
    public async Task A_bad_grant_is_401_as_json_or_a_swedish_page_for_browsers()
    {
        using var client = Client(null);
        var path = new Uri($"/v1/documents/{db.Upload.Id}/content?grant=g1.k1.e30.{new string('A', 32)}", UriKind.Relative);

        using var api = await client.GetAsync(path, Ct);
        api.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await Json(api))["code"]!.GetValue<string>().ShouldBe("grant_invalid");

        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml");
        using var page = await client.SendAsync(request, Ct);
        page.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        page.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");
        (await page.Content.ReadAsStringAsync(Ct)).ShouldContain("Länken har gått ut");
        page.Headers.GetValues("Content-Security-Policy").ShouldBe(["default-src 'none'"]);
    }

    [Fact]
    public async Task A_web_page_is_not_served_as_content()
    {
        using var client = Client(db.InternalKey);

        using var response = await client.GetAsync(new Uri($"/v1/documents/{db.Altan.Id}/content", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Json(response))["code"]!.GetValue<string>().ShouldBe("content_not_served");
    }

    [Fact]
    public async Task An_unconfirmed_snapshot_is_503()
    {
        using var api = db.ApiHost(confirmed: false);
        using var client = api.CreateHttpsClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", db.InternalKey);

        using var response = await client.GetAsync(new Uri("/v1/collections", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await Json(response))["code"]!.GetValue<string>().ShouldBe("config_unconfirmed");
    }

    [Fact]
    public async Task The_openapi_document_and_scalar_are_published()
    {
        using var client = Client(null);

        var document = await client.GetFromJsonAsync<JsonDocument>(new Uri("/openapi/knowledge.json", UriKind.Relative), Ct);
        document!.RootElement.GetProperty("info").GetProperty("title").GetString().ShouldBe("Ume.Rag Knowledge API");
        document.RootElement.GetProperty("paths").TryGetProperty("/v1/retrieve", out _).ShouldBeTrue();

        using var scalar = await client.GetAsync(new Uri("/scalar/knowledge", UriKind.Relative), Ct);
        scalar.IsSuccessStatusCode.ShouldBeTrue();
        scalar.Headers.GetValues("Content-Security-Policy").Single().ShouldContain("script-src 'self' 'nonce-");
    }
}
