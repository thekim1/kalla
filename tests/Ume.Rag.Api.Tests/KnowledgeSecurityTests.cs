using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Ume.Rag.Answering;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Access;
using Ume.Rag.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using Ume.Rag.Infrastructure.Config;
using Ume.Rag.TestKit;

namespace Ume.Rag.Api.Tests;

/// <summary>
/// Knowledge design §16.6 (security and negative cases), the security events of §4.7 (5001, 5002, 5004, 5005, 5006),
/// the auth-failure throttle (identity §6.3) and the telemetry scan of §16.3 (the query text is never logged).
/// </summary>
public sealed class KnowledgeSecurityTests(KnowledgeFixture db) : IClassFixture<KnowledgeFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private (WebApplicationFactory<Program> Api, CapturingLoggerProvider Logs) Host(PolicySnapshot? snapshot = null, bool confirmed = true,
        params (string Key, string Value)[] settings)
    {
        var logs = new CapturingLoggerProvider();
        var api = db.ApiHost(snapshot, confirmed).WithWebHostBuilder(web =>
        {
            foreach (var (key, value) in settings)
            {
                web.UseSetting(key, value);
            }

            web.ConfigureLogging(logging => logging.AddProvider(logs));
        });
        return (api, logs);
    }

    private static HttpClient Client(WebApplicationFactory<Program> api, string? key)
    {
        var client = api.CreateHttpsClient();
        if (key is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        return client;
    }

    /// <summary>The snapshot with rag-internal's ceiling lowered to internal: the sensitive collection is then above it.</summary>
    private PolicySnapshot LowCeiling(Func<PolicySnapshot, PolicySnapshot>? more = null)
    {
        var snapshot = db.Snapshot();
        snapshot = snapshot with
        {
            Clients = snapshot.Clients.ToDictionary(c => c.Key, c => c.Key == db.InternalClient ? c.Value with { Ceiling = Classification.Internal } : c.Value),
        };
        return more?.Invoke(snapshot) ?? snapshot;
    }

    private static IEnumerable<LogEntry> Events(CapturingLoggerProvider logs, int id) =>
        logs.Entries.Where(e => e.Category == SecurityEvents.Category && e.EventId.Id == id);

    private static async Task<string> Body(HttpResponseMessage response)
    {
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!.AsObject();
        json.Remove("trace_id");
        json.Remove("collection"); // the echoed slug is the caller's own input
        return json.ToJsonString();
    }

    private static Task<HttpResponseMessage> Retrieve(HttpClient client, object body) =>
        client.PostAsync(new Uri("/v1/retrieve", UriKind.Relative), JsonContent.Create(body), Ct);

    /// <remarks>
    /// A query of only <c>'</c> or <c>\</c> is 400 by §3.1 (at least one letter or digit), so those characters come with a word.
    /// </remarks>
    [Fact]
    public async Task Queries_with_sql_and_tsquery_syntax_are_plain_text()
    {
        var (api, _) = Host();
        using var _ = api;
        using var client = Client(api, db.InternalKey);
        var slug = db.Snapshot().Collections[db.PublicCollection].Slug;

        foreach (var query in new[] { "'); DROP TABLE content.\"Chunks\"; --", "a & !b", "bygglov <-> altan", "altan'", "\\ bygglov", new string('&', 1999) + "a" })
        {
            using var response = await Retrieve(client, new { query, collections = new[] { slug } });
            response.StatusCode.ShouldBe(HttpStatusCode.OK, query);
        }
    }

    [Fact]
    public async Task Hidden_collections_answer_identical_404s_and_the_events_carry_the_real_reason()
    {
        var snapshot = LowCeiling(s => s with
        {
            Collections = s.Collections.ToDictionary(c => c.Key, c => c.Key == db.DeptBCollection ? c.Value with { Deleted = true } : c.Value),
        });
        var (api, logs) = Host(snapshot);
        using var _ = api;
        using var client = Client(api, db.InternalKey);
        string Slug(Guid id) => snapshot.Collections[id].Slug;

        var bodies = new List<string>();
        foreach (var slug in new[] { Slug(db.SensitiveCollection), Slug(db.UnpublishedCollection), Slug(db.DeptBCollection), "finns-inte" })
        {
            using var response = await Retrieve(client, new { query = "bygglov", collections = new[] { slug } });
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound, slug);
            bodies.Add(await Body(response));
        }

        bodies.Distinct().ShouldHaveSingleItem();
        var ceiling = Events(logs, 5005).ShouldHaveSingleItem();
        ceiling.Message.ShouldContain(db.SensitiveCollection.ToString("D"));
        ceiling.Message.ShouldContain("at sensitive above client ceiling internal");
        var denied = Events(logs, 5002).Select(e => e.Message).ToList();
        denied.ShouldContain(m => m.Contains("collection_not_live", StringComparison.Ordinal) && m.Contains(db.UnpublishedCollection.ToString("D"), StringComparison.Ordinal));
        denied.ShouldContain(m => m.Contains(db.DeptBCollection.ToString("D"), StringComparison.Ordinal)); // deleted: the policy's own reason
        denied.ShouldContain(m => m.Contains("collection_unknown", StringComparison.Ordinal) && m.Contains("finns-inte", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Documents_above_the_levels_invisible_or_malformed_answer_identical_404s()
    {
        var (api, logs) = Host(LowCeiling());
        using var _ = api;
        using var client = Client(api, db.InternalKey);

        var bodies = new List<string>();
        foreach (var id in new[] { db.Sensitive.Id.ToString(), db.Hidden.Id.ToString(), "not-a-guid", Guid.NewGuid().ToString() })
        {
            using var response = await client.GetAsync(new Uri($"/v1/documents/{id}", UriKind.Relative), Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound, id);
            bodies.Add(await Body(response));
        }

        bodies.Distinct().ShouldHaveSingleItem();
        Events(logs, 5002).ShouldContain(e => e.Message.Contains(db.Sensitive.Id.ToString("D"), StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_direct_caller_never_sees_a_collection_the_policy_denies()
    {
        // #127 review: "no collections" means every searchable collection, each through Decide; the sensitive one is above the ceiling here.
        var (api, _) = Host(LowCeiling());
        using var _ = api;
        using var client = Client(api, db.InternalKey);

        using var response = await Retrieve(client, new { query = "Bygglov för altan hos en enskild person" });
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var results = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!["results"]!.AsArray();
        results.ShouldNotBeEmpty();
        results.Select(r => r!["document"]!["id"]!.GetValue<Guid>()).ShouldNotContain(db.Sensitive.Id);
        var sensitiveSlug = db.Snapshot().Collections[db.SensitiveCollection].Slug;
        results.Select(r => r!["collection"]!.GetValue<string>()).ShouldNotContain(sensitiveSlug);

        var listed = await client.GetFromJsonAsync<JsonNode>(new Uri("/v1/collections", UriKind.Relative), Ct);
        listed!["data"]!.AsArray().Select(c => c!["slug"]!.GetValue<string>()).ShouldNotContain(sensitiveSlug);
    }

    [Fact]
    public async Task Assistant_id_from_an_integration_key_is_400()
    {
        var (api, _) = Host(settings: [("Knowledge:OpenToClientKinds:0", "internal"), ("Knowledge:OpenToClientKinds:1", "integration")]);
        using var _ = api;
        using var client = Client(api, db.IntegrationKey);

        using var response = await Retrieve(client, new { query = "bygglov", assistant_id = db.AssistantId });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!["code"]!.GetValue<string>().ShouldBe("assistant_id_not_allowed");
    }

    [Fact]
    public async Task A_grant_of_another_deployment_or_past_its_expiry_is_401_and_counted()
    {
        var (api, logs) = Host();
        using var _ = api;
        using var client = Client(api, null);
        var foreign = new DownloadGrants(new DownloadGrantOptions { Keys = new Dictionary<string, byte[]> { ["k1"] = Encoding.UTF8.GetBytes(new string('x', 32)) } },
            TimeProvider.System).Issue(db.Upload.Id, db.PublicCollection, null, db.InternalClient, null, Classification.Public);
        var past = new FakeTimeProvider(DateTimeOffset.UtcNow.AddDays(-8));
        var expired = new DownloadGrants(new DownloadGrantOptions
        {
            Keys = new Dictionary<string, byte[]> { ["k1"] = Encoding.UTF8.GetBytes(KnowledgeFixture.GrantKey) },
        }, past).Issue(db.Upload.Id, db.PublicCollection, null, db.InternalClient, null, Classification.Public);

        foreach (var (grant, code) in new[] { (foreign, "grant_invalid"), (expired, "grant_expired") })
        {
            using var response = await client.GetAsync(new Uri($"/v1/documents/{db.Upload.Id}/content?grant={grant}", UriKind.Relative), Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!["code"]!.GetValue<string>().ShouldBe(code);
        }

        var failed = Events(logs, 5001).Select(e => e.Message).ToList();
        failed.ShouldContain(m => m.Contains("grant_invalid (grant)", StringComparison.Ordinal));
        failed.ShouldContain(m => m.Contains("grant_expired (grant)", StringComparison.Ordinal));
        logs.Text.ShouldNotContain(foreign);
        logs.Text.ShouldNotContain(expired);
    }

    [Fact]
    public async Task Each_departments_citation_shows_its_own_file_name()
    {
        var (api, _) = Host();
        using var _ = api;
        using var client = Client(api, db.InternalKey);
        var snapshot = db.Snapshot();

        using var response = await Retrieve(client, new
        {
            query = "tomtkarta fastighetsgränser servitut", mode = "keyword",
            collections = new[] { snapshot.Collections[db.DeptACollection].Slug, snapshot.Collections[db.DeptBCollection].Slug },
        });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var results = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!["results"]!.AsArray();
        results.Select(r => r!["document"]!["display_name"]!.GetValue<string>()).ShouldBe(["tomtkarta-bygg.pdf", "karta-miljo.pdf"], ignoreOrder: true);
        results.Select(r => r!["document"]!["id"]!.GetValue<Guid>()).Distinct().Count().ShouldBe(2);
    }

    [Fact]
    public async Task Refused_keys_are_bucketed_and_an_address_over_the_limit_gets_only_cached_keys()
    {
        var (api, logs) = Host(settings: [("Api:AuthFailuresPerMinute", "3")]);
        using var _ = api;
        using var valid = Client(api, db.InternalKey);
        using var wrong = Client(api, "ume-rag-sk-" + new string('w', 43));
        var collections = new Uri("/v1/collections", UriKind.Relative);
        // The config monitor's first load is a full reload, which clears the key cache (KeyStore.ApplyAsync). Wait for it, so
        // it can't land after the key below is cached and make the last request miss the cache (#187).
        var state = api.Services.GetRequiredService<ConfigVersionState>();
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (state.LastConfirmedAt is null && waited.Elapsed < TimeSpan.FromSeconds(30))
        {
            await Task.Delay(50, Ct);
        }

        state.LastConfirmedAt.ShouldNotBeNull();
        (await valid.GetAsync(collections, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK); // now in the key cache

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            using var response = await wrong.GetAsync(collections, Ct);
            statuses.Add(response.StatusCode);
        }

        statuses.ShouldBe([HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized,
            HttpStatusCode.TooManyRequests]);
        using (var limited = await wrong.GetAsync(collections, Ct))
        {
            limited.Headers.RetryAfter!.Delta.ShouldBe(TimeSpan.FromSeconds(60));
        }

        (await valid.GetAsync(collections, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK); // a cached key still works
        Events(logs, 5006).ShouldHaveSingleItem();
        Events(logs, 5001).Count(e => e.Message.Contains("invalid_key", StringComparison.Ordinal)).ShouldBe(1); // one bucket per minute
        logs.Text.ShouldNotContain(new string('w', 43));
    }

    /// <summary><see cref="Host"/> with the connection address taken from <see cref="RemoteAddressFromHeader"/>.</summary>
    private (WebApplicationFactory<Program> Api, CapturingLoggerProvider Logs) AddressedHost(PolicySnapshot? snapshot = null,
        params (string Key, string Value)[] settings)
    {
        var (host, logs) = Host(snapshot, true, settings);
        return (host.WithWebHostBuilder(web => web.ConfigureTestServices(services => services.AddSingleton<IStartupFilter, RemoteAddressFromHeader>())), logs);
    }

    /// <summary>GET /v1/collections from <paramref name="address"/> (the connection), optionally with <c>X-Forwarded-For</c>.</summary>
    private static async Task<(HttpStatusCode Status, string? Code)> Collections(HttpClient client, string? address, string? forwardedFor = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/v1/collections", UriKind.Relative));
        if (address is not null)
        {
            request.Headers.Add(RemoteAddressFromHeader.Header, address);
        }

        if (forwardedFor is not null)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        }

        using var response = await client.SendAsync(request, Ct);
        var code = response.IsSuccessStatusCode ? null : JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!["code"]!.GetValue<string>();
        return (response.StatusCode, code);
    }

    /// <summary>The snapshot with the reverse proxy 192.0.2.0/24 trusted (<c>Settings.TrustedProxies</c>).</summary>
    private PolicySnapshot BehindProxy() => db.Snapshot() with { TrustedProxies = [IPNetwork.Parse("192.0.2.0/24")] };

    [Fact]
    public async Task A_key_with_address_ranges_works_only_from_inside_them_and_a_revoked_one_is_reported_as_revoked()
    {
        var (api, logs) = AddressedHost();
        using var _ = api;
        using var restricted = Client(api, db.RestrictedKey);
        using var revoked = Client(api, db.RevokedRestrictedKey);

        foreach (var inside in new[] { "203.0.113.7", "::ffff:203.0.113.8", "2001:db8:0:1::5" })
        {
            (await Collections(restricted, inside)).ShouldBe((HttpStatusCode.OK, null), inside);
        }

        foreach (var outside in new[] { "198.51.100.1", "2001:db9::5" })
        {
            (await Collections(restricted, outside)).ShouldBe((HttpStatusCode.Forbidden, "address_not_allowed"), outside);
        }

        (await Collections(restricted, null)).ShouldBe((HttpStatusCode.Forbidden, "address_not_allowed")); // no address is refused too
        (await Collections(revoked, "198.51.100.1")).ShouldBe((HttpStatusCode.Unauthorized, "key_revoked"));

        var failed = Events(logs, 5001).Select(e => e.Message).ToList();
        failed.ShouldContain(m => m.Contains("address_not_allowed (sk)", StringComparison.Ordinal) && m.Contains("198.51.100.0", StringComparison.Ordinal));
        failed.ShouldContain(m => m.Contains("key_revoked (sk)", StringComparison.Ordinal));
        failed.ShouldNotContain(m => m.Contains("203.0.113.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Behind_a_trusted_proxy_the_allow_list_and_the_events_see_the_forwarded_address()
    {
        var (api, logs) = AddressedHost(BehindProxy());
        using var _ = api;
        using var restricted = Client(api, db.RestrictedKey);

        // The right-most untrusted hop is the client: a prepended entry is never believed.
        (await Collections(restricted, "192.0.2.10", "203.0.113.7")).ShouldBe((HttpStatusCode.OK, null));
        (await Collections(restricted, "192.0.2.10", "203.0.113.7, 192.0.2.11")).ShouldBe((HttpStatusCode.OK, null));
        (await Collections(restricted, "192.0.2.10", "2001:db8::5")).ShouldBe((HttpStatusCode.OK, null));
        (await Collections(restricted, "192.0.2.10", "203.0.113.7, 198.51.100.1")).ShouldBe((HttpStatusCode.Forbidden, "address_not_allowed"));

        var failed = Events(logs, 5001).ShouldHaveSingleItem().Message;
        failed.ShouldContain("address_not_allowed (sk)");
        failed.ShouldContain("from 198.51.100.0"); // the forwarded client, truncated; not the proxy
    }

    [Fact]
    public async Task X_forwarded_for_from_an_untrusted_connection_or_with_no_trusted_proxies_is_ignored()
    {
        foreach (var snapshot in new[] { BehindProxy(), db.Snapshot() })
        {
            var (api, _) = AddressedHost(snapshot);
            using var __ = api;
            using var restricted = Client(api, db.RestrictedKey);

            (await Collections(restricted, "198.51.100.1", "203.0.113.7")).ShouldBe((HttpStatusCode.Forbidden, "address_not_allowed"));
            (await Collections(restricted, "203.0.113.7", "198.51.100.1")).ShouldBe((HttpStatusCode.OK, null));
        }

        // No trusted proxies: even the proxy's own connection is taken as the client.
        var (direct, _) = AddressedHost();
        using var ___ = direct;
        using var client = Client(direct, db.RestrictedKey);
        (await Collections(client, "192.0.2.10", "203.0.113.7")).ShouldBe((HttpStatusCode.Forbidden, "address_not_allowed"));
    }

    [Fact]
    public async Task Behind_a_trusted_proxy_the_throttle_counts_each_forwarded_client_on_its_own()
    {
        var (api, logs) = AddressedHost(BehindProxy(), ("Api:AuthFailuresPerMinute", "2"));
        using var _ = api;
        using var wrong = Client(api, "ume-rag-sk-" + new string('w', 43));

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            statuses.Add((await Collections(wrong, "192.0.2.10", "198.51.100.1")).Status);
        }

        statuses.ShouldBe([HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests]);
        // Another client behind the same proxy is not throttled.
        (await Collections(wrong, "192.0.2.10", "198.51.200.1")).Status.ShouldBe(HttpStatusCode.Unauthorized);
        var throttled = Events(logs, 5006).ShouldHaveSingleItem().Message;
        throttled.ShouldContain("198.51.100.0");
        throttled.ShouldNotContain("192.0.2.");
    }

    /// <summary>The test server has no connection address; this sets it from a test header, before the app's pipeline.</summary>
    private sealed class RemoteAddressFromHeader : IStartupFilter
    {
        public const string Header = "X-Test-Remote-Address";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((http, nextMiddleware) =>
            {
                if (IPAddress.TryParse(http.Request.Headers[Header].ToString(), out var address))
                {
                    http.Connection.RemoteIpAddress = address;
                }

                return nextMiddleware(http);
            });
            next(app);
        };
    }

    [Fact]
    public async Task An_unconfirmed_snapshot_is_503_with_event_5004()
    {
        var (api, logs) = Host(confirmed: false);
        using var _ = api;
        using var client = Client(api, db.InternalKey);

        using var response = await client.GetAsync(new Uri("/v1/collections", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        Events(logs, 5004).ShouldNotBeEmpty();
    }

    [Fact]
    public async Task The_query_text_never_reaches_logs_or_problem_bodies()
    {
        const string Marker = "zqxmarkör4711";
        var (api, logs) = Host(settings: [("Logging:LogLevel:Default", "Trace")]);
        using var _ = api;
        using var client = Client(api, db.InternalKey);
        var slug = db.Snapshot().Collections[db.PublicCollection].Slug;

        using (var ok = await Retrieve(client, new { query = Marker + " bygglov altan", collections = new[] { slug }, expand = "section" }))
        {
            ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using var invalid = await Retrieve(client, new { query = Marker + " bygglov", top_k = 0 });
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await invalid.Content.ReadAsStringAsync(Ct)).ShouldNotContain(Marker);

        logs.Entries.ShouldNotBeEmpty();
        logs.Text.ShouldNotContain(Marker);
    }
}
