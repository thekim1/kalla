using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Kalla.Domain.Entities;
using Kalla.Pipelines.Jobs;
using Kalla.Worker.Tests.Connectors;
using Kalla.Worker.Tests.Processing;

namespace Kalla.Worker.Tests.Engine;

/// <summary>
/// #228 (stream A; follow-up to #229, P1a item 20 pilot finding): on a host whose site profile has a verified Sitevision
/// identity rule, the crawler keeps the path form a URL was found under (<c>CrawlFrontier.FetchUrl</c>) and requests it,
/// so <c>Documents.Url</c> is the readable path while the identity and document ids keep the node id; scope re-checks
/// (<c>scope-narrowed</c>, retry-failed admission) and the crawl report's <c>demote</c> counts read the path form, and rows
/// and documents without one keep the identity check.
/// </summary>
[Collection(RagDatabaseGroup3.Name)]
public sealed class FetchUrlEngineTests(RagDatabaseFixture database, FakeGatewayFactory gateway) : IClassFixture<FakeGatewayFactory>, IAsyncLifetime
{
    private const string IdentityPattern = @"^/(?:.*/)?(?:[^/]+\.)?(?<id>[0-9]+\.[0-9a-f]{10,})\.html$";

    // The worker's own scheduler stays out of the way: a settings change gets only the run the test starts.
    private static readonly Dictionary<string, string?> QuietScheduler = new() { ["Worker:SchedulerSeconds"] = "600" };

    private readonly DirectoryInfo _blobs = Directory.CreateTempSubdirectory("kalla-fetchurl-blobs-");
    private readonly ConcurrentQueue<string> _requests = new();
    private readonly ConcurrentDictionary<string, int> _status = new(StringComparer.Ordinal);
    private readonly string _tag = Guid.NewGuid().ToString("N");
    private int _notModified;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await database.ResetAsync();

    public ValueTask DisposeAsync()
    {
        _blobs.Delete(recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_crawl_requests_the_path_form_stores_it_as_the_document_url_and_keeps_the_node_id_identity()
    {
        await using var site = await SiteAsync();
        var seed = await EngineKit.SeedCrawlAsync(database, Settings(site, demote: [Glob(site, "/annan/**")]));
        await EngineKit.PauseOtherSourcesAsync(database, seed);
        await ApplyIdentityRuleAsync(seed);
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs, settings: QuietScheduler);
        try
        {
            var firstId = await EngineKit.SyncNowAsync(database, seed);
            (await EngineKit.WaitForRunAsync(database, firstId)).ShouldBe(new RunCounters(RunStates.Succeeded, 6, 0, 0, 0, 0, 0));

            (await FrontierAsync(seed)).ShouldBe(
                [
                    $"{U(site, "/4.10000000000a.html")} {U(site, "/sektion/sida-a.4.10000000000a.html")}",
                    $"{U(site, "/4.20000000000b.html")} {U(site, "/sektion/sida-b.4.20000000000b.html")}",
                    $"{U(site, "/4.30000000000c.html")} {U(site, "/annan/sida-c.4.30000000000c.html")}",
                    $"{U(site, "/4.40000000000d.html")} {U(site, "/annan/sida-d.4.40000000000d.html")}",
                    $"{U(site, "/sektion")} -",
                    $"{U(site, "/sektion/vanlig")} -",
                ],
                "a node row keeps the path it was first found under (the sitemap's, the first link's); a page without the rule keeps none");
            var documents = await DocumentsAsync(seed);
            documents.Select(d => $"{d.Identity} {d.Url}").ShouldBe(
                [
                    $"{U(site, "/4.10000000000a.html")} {U(site, "/sektion/sida-a.4.10000000000a.html")}",
                    $"{U(site, "/4.20000000000b.html")} {U(site, "/sektion/sida-b.4.20000000000b.html")}",
                    $"{U(site, "/4.30000000000c.html")} {U(site, "/annan/sida-c.4.30000000000c.html")}",
                    $"{U(site, "/4.40000000000d.html")} {U(site, "/annan/sida-d.4.40000000000d.html")}",
                    $"{U(site, "/sektion")} {U(site, "/sektion")}",
                    $"{U(site, "/sektion/vanlig")} {U(site, "/sektion/vanlig")}",
                ]);
            _requests.ShouldContain("/sektion/sida-a.4.10000000000a.html");
            _requests.ShouldContain("/annan/sida-c.4.30000000000c.html");
            _requests.ShouldNotContain(r => r.StartsWith("/4.", StringComparison.Ordinal), "no request goes to a bare node id");
            using (var report = await ReportAsync(firstId))
            {
                report.RootElement.GetProperty("demote").EnumerateArray().Single().GetProperty("documents").GetInt32()
                    .ShouldBe(2, "the path glob counts the two node pages under /annan by their Documents.Url");
            }

            var second = await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, seed));

            second.ShouldBe(new RunCounters(RunStates.Succeeded, 0, 0, 6, 0, 0, 0));
            (await DocumentsAsync(seed)).ShouldBe(documents, "the second crawl keeps every document and its id: no duplicate");
        }
        finally
        {
            await CleanUpAsync(seed);
        }
    }

    [Fact]
    public async Task A_304_or_a_lastmod_skip_on_the_path_form_refreshes_an_old_node_id_document_url_without_reprocessing()
    {
        await using var site = await SiteAsync();
        var seed = await EngineKit.SeedCrawlAsync(database, Settings(site));
        await EngineKit.PauseOtherSourcesAsync(database, seed);
        await ApplyIdentityRuleAsync(seed, lastmodReliable: true);
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs, settings: QuietScheduler);
        try
        {
            (await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, seed))).Added.ShouldBe(6);
            // As the pilot's documents were stored before #228: no FetchUrl, the node id as the document URL.
            await Sql.ExecuteAsync(database.Connection(null), """UPDATE content."CrawlFrontier" SET "FetchUrl" = NULL WHERE "SourceId" = $1""", seed.SourceId);
            await Sql.ExecuteAsync(database.Connection(null), """UPDATE content."Documents" SET "Url" = "CanonicalIdentity" WHERE "SourceId" = $1""", seed.SourceId);
            var before = await LiveSetsAsync(seed);
            _requests.Clear();

            var run = await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, seed));

            run.ShouldBe(new RunCounters(RunStates.Succeeded, 0, 0, 6, 0, 0, 0));
            _requests.ShouldNotContain(r => r.Contains("sida-b", StringComparison.Ordinal) || r.Contains("sida-c", StringComparison.Ordinal),
                "B and C (sitemap lastmod not newer than the last fetch) are skipped without a request");
            Volatile.Read(ref _notModified).ShouldBe(2, "A and D are requested on their path form and answer 304");
            (await DocumentsAsync(seed)).Select(d => d.Url).ShouldBe(
                [
                    U(site, "/sektion/sida-a.4.10000000000a.html"), U(site, "/sektion/sida-b.4.20000000000b.html"), U(site, "/annan/sida-c.4.30000000000c.html"),
                    U(site, "/annan/sida-d.4.40000000000d.html"), U(site, "/sektion"), U(site, "/sektion/vanlig"),
                ],
                "the 304s and the lastmod skips set the path form as the document URL");
            (await LiveSetsAsync(seed)).ShouldBe(before, "same documents, same live chunk sets: nothing re-processed");
        }
        finally
        {
            await CleanUpAsync(seed);
        }
    }

    [Fact]
    public async Task Narrowing_the_include_removes_the_documents_of_the_left_section_as_scope_narrowed_and_keeps_the_rest()
    {
        await using var site = await SiteAsync();
        var seed = await EngineKit.SeedCrawlAsync(database, Settings(site));
        await EngineKit.PauseOtherSourcesAsync(database, seed);
        await ApplyIdentityRuleAsync(seed);
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs, settings: QuietScheduler);
        try
        {
            (await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, seed))).Added.ShouldBe(6);
            _requests.Clear();
            await SetSettingsAsync(seed, Settings(site, include: [Glob(site, "/sektion"), Glob(site, "/sektion/**")]));

            var narrowing = await EngineKit.SyncNowAsync(database, seed);
            var run = await EngineKit.WaitForRunAsync(database, narrowing);

            run.ShouldBe(new RunCounters(RunStates.Succeeded, 0, 0, 4, 2, 0, 0));
            (await ProposalsAsync(narrowing)).ShouldBe("scope-narrowed:True:applied:2");
            _requests.ShouldNotContain(r => r.StartsWith("/annan/", StringComparison.Ordinal), "no request to a URL the narrowed scope dropped");
            (await DocumentsAsync(seed)).Select(d => d.Url).ShouldBe(
                [U(site, "/sektion/sida-a.4.10000000000a.html"), U(site, "/sektion/sida-b.4.20000000000b.html"), U(site, "/sektion"), U(site, "/sektion/vanlig")]);
        }
        finally
        {
            await CleanUpAsync(seed);
        }
    }

    [Fact]
    public async Task Old_rows_without_a_path_form_keep_the_identity_check_and_leave_only_through_not_seen()
    {
        await using var site = await SiteAsync();
        var seed = await EngineKit.SeedCrawlAsync(database, Settings(site));
        await EngineKit.PauseOtherSourcesAsync(database, seed);
        await ApplyIdentityRuleAsync(seed);
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs, settings: QuietScheduler);
        try
        {
            (await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, seed))).Added.ShouldBe(6);
            // As written before #228: no FetchUrl, and the document URL is the node id that was fetched.
            await Sql.ExecuteAsync(database.Connection(null), """UPDATE content."CrawlFrontier" SET "FetchUrl" = NULL WHERE "SourceId" = $1""", seed.SourceId);
            await Sql.ExecuteAsync(database.Connection(null), """UPDATE content."Documents" SET "Url" = "CanonicalIdentity" WHERE "SourceId" = $1""", seed.SourceId);
            await SetSettingsAsync(seed, Settings(site, include: [Glob(site, "/sektion"), Glob(site, "/sektion/**")]));

            var narrowing = await EngineKit.SyncNowAsync(database, seed);
            await EngineKit.WaitForRunAsync(database, narrowing);

            var proposals = await ProposalsAsync(narrowing);
            proposals.ShouldStartWith("not-seen:False:", customMessage: "a node-id document without a path form is never scope-narrowed: the complete traversal misses it");
            proposals.ShouldNotContain("scope-narrowed");
        }
        finally
        {
            await CleanUpAsync(seed);
        }
    }

    [Fact]
    public async Task Retry_failed_admits_a_failed_page_whose_path_form_is_in_scope_and_rejects_one_whose_path_form_is_not()
    {
        _status["4.20000000000b.html"] = StatusCodes.Status403Forbidden;
        _status["4.30000000000c.html"] = StatusCodes.Status403Forbidden;
        await using var site = await SiteAsync();
        var seed = await EngineKit.SeedCrawlAsync(database, Settings(site));
        await EngineKit.PauseOtherSourcesAsync(database, seed);
        await ApplyIdentityRuleAsync(seed);
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs, settings: QuietScheduler);
        try
        {
            (await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, seed))).ShouldBe(new RunCounters(RunStates.Degraded, 4, 0, 0, 0, 2, 0));
            _status.Clear();
            _requests.Clear();
            await SetSettingsAsync(seed, Settings(site, include: [Glob(site, "/sektion"), Glob(site, "/sektion/**")]));

            var retry = await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, seed, SyncKind.RetryFailed));

            retry.ShouldBe(new RunCounters(RunStates.Succeeded, 1, 0, 0, 0, 0, 0), "B (path in the section) is retried, C (path outside it) is not");
            _requests.ShouldContain("/sektion/sida-b.4.20000000000b.html");
            _requests.ShouldNotContain(r => r.Contains("sida-c", StringComparison.Ordinal) || r.Contains("4.30000000000c", StringComparison.Ordinal));
            (await DocumentsAsync(seed)).Single(d => d.Identity == U(site, "/4.20000000000b.html")).Url.ShouldBe(U(site, "/sektion/sida-b.4.20000000000b.html"));
        }
        finally
        {
            await CleanUpAsync(seed);
        }
    }

    private static string U(TestSite site, string path) => site.Url(path).AbsoluteUri;

    private static string Glob(TestSite site, string path) => $"{TestSite.Host}:{site.Port}{path}";

    private static object Settings(TestSite site, string[]? include = null, string[]? demote = null) => new
    {
        startUrls = new[] { site.Url("/sektion").AbsoluteUri },
        include = include ?? [],
        demote = (demote ?? []).Select(p => new { pattern = p, factor = 0.5 }).ToArray(),
    };

    private Task SetSettingsAsync(SourceSeed seed, object settings) =>
        Sql.ExecuteAsync(database.Connection(null), """UPDATE config."Sources" SET "Settings" = $2::jsonb WHERE "Id" = $1""", seed.SourceId,
            JsonSerializer.Serialize(settings));

    /// <summary>The umea.se profile's verified Sitevision identity rule on the test host, applied as <c>edit-site-profile</c> does.</summary>
    private async Task ApplyIdentityRuleAsync(SourceSeed seed, bool lastmodReliable = false)
    {
        var profileId = Guid.CreateVersion7();
        var version = Guid.CreateVersion7();
        var profile = new Pipelines.SiteProfiles.SiteProfile
        {
            MainRoot = "main",
            IdentityRule = new() { Pattern = IdentityPattern, Replacement = "/${id}.html", Verified = true },
            LastmodReliable = lastmodReliable,
        };
        await Sql.ExecuteAsync(database.Connection(null), """
            INSERT INTO content."SiteProfiles" ("Id", "Classification", "Host", "PresetId", "UpdatedAt") VALUES ($1, $2, $3, NULL, now())
            """, profileId, seed.Classification, TestSite.Host);
        await Sql.ExecuteAsync(database.Connection(null), """
            INSERT INTO content."SiteProfileVersions" ("Id", "SiteProfileId", "Number", "Profile", "Origin", "State", "CreatedBy", "CreatedAt", "AppliedAt", "AppliedBy")
            VALUES ($1, $2, 1, $3::jsonb, 'manual', 'applied', 'test', now(), now(), 'test')
            """, version, profileId, Pipelines.Json.ContractJson.Serialize(profile));
        await Sql.ExecuteAsync(database.Connection(null), """UPDATE content."SiteProfiles" SET "AppliedVersionId" = $1 WHERE "Id" = $2""", version, profileId);
    }

    private async Task CleanUpAsync(SourceSeed seed)
    {
        await EngineKit.PauseAsync(database, seed);
        // The key has no port (SiteProfileHost), so the profile would reach every later test on the test site.
        await Sql.ExecuteAsync(database.Connection(null), """DELETE FROM content."SiteProfiles" WHERE "Classification" = $1 AND "Host" = $2""",
            seed.Classification, TestSite.Host);
    }

    /// <summary>The source's frontier rows as <c>Url FetchUrl</c> (<c>-</c> for none), in URL order.</summary>
    private async Task<List<string>> FrontierAsync(SourceSeed seed)
    {
        await using var connection = new Npgsql.NpgsqlConnection(database.Connection(null));
        await connection.OpenAsync(Ct);
        await using var command = new Npgsql.NpgsqlCommand("""
            SELECT "Url" || ' ' || coalesce("FetchUrl", '-') FROM content."CrawlFrontier" WHERE "SourceId" = $1 ORDER BY "Url"
            """, connection) { Parameters = { new Npgsql.NpgsqlParameter { Value = seed.SourceId } } };
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    /// <summary>The source's live documents (id, identity, URL), in identity order.</summary>
    private async Task<List<(Guid Id, string Identity, string? Url)>> DocumentsAsync(SourceSeed seed)
    {
        await using var connection = new Npgsql.NpgsqlConnection(database.Connection(null));
        await connection.OpenAsync(Ct);
        await using var command = new Npgsql.NpgsqlCommand("""
            SELECT "Id", "CanonicalIdentity", "Url" FROM content."Documents" WHERE "SourceId" = $1 AND "DeletedAt" IS NULL ORDER BY "CanonicalIdentity"
            """, connection) { Parameters = { new Npgsql.NpgsqlParameter { Value = seed.SourceId } } };
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var documents = new List<(Guid, string, string?)>();
        while (await reader.ReadAsync(Ct))
        {
            documents.Add((reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        return documents;
    }

    /// <summary>The source's live documents' ids with their live chunk set, in id order.</summary>
    private async Task<List<(Guid Id, Guid? Set)>> LiveSetsAsync(SourceSeed seed)
    {
        await using var connection = new Npgsql.NpgsqlConnection(database.Connection(null));
        await connection.OpenAsync(Ct);
        await using var command = new Npgsql.NpgsqlCommand("""
            SELECT "Id", "LiveChunkSetId" FROM content."Documents" WHERE "SourceId" = $1 AND "DeletedAt" IS NULL ORDER BY "Id"
            """, connection) { Parameters = { new Npgsql.NpgsqlParameter { Value = seed.SourceId } } };
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var sets = new List<(Guid, Guid?)>();
        while (await reader.ReadAsync(Ct))
        {
            sets.Add((reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetGuid(1)));
        }

        return sets;
    }

    private async Task<JsonDocument> ReportAsync(Guid runId) =>
        JsonDocument.Parse(await Sql.ScalarAsync<string>(database.Connection(null), """SELECT "Report"::text FROM content."Runs" WHERE "Id" = $1""", runId));

    /// <summary>The run's deletion proposals as <c>reason:exempt:decision:count</c>, one group per line.</summary>
    private Task<string> ProposalsAsync(Guid runId) =>
        Sql.ScalarAsync<string>(database.Connection(null), """
            SELECT coalesce(string_agg(g, E'\n' ORDER BY g), '') FROM (
                SELECT "Reason" || ':' || CASE WHEN "Exempt" THEN 'True' ELSE 'False' END || ':' || "Decision" || ':' || count(*) AS g
                FROM content."DeletionProposals" WHERE "RunId" = $1 GROUP BY "Reason", "Exempt", "Decision") x
            """, runId);

    /// <summary>
    /// A Sitevision-like site: a section page linking two paths of one node, a node in another section and a page without
    /// the rule; a sitemap with a node in each section. A node is served under any path that ends in its id.
    /// </summary>
    private async Task<TestSite> SiteAsync() => await TestSite.StartAsync(app =>
    {
        app.Use(async (context, next) =>
        {
            _requests.Enqueue(context.Request.Path.Value!);
            await next(context);
        });
        app.MapGet("/robots.txt", (HttpContext context) =>
            Results.Text($"User-agent: *\nAllow: /\nSitemap: http://{TestSite.Host}:{context.Request.Host.Port}/sitemap.xml\n", "text/plain"));
        app.MapGet("/sitemap.xml", (HttpContext context) => Results.Content(
            """<?xml version="1.0" encoding="UTF-8"?><urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">"""
            + $"<url><loc>http://{TestSite.Host}:{context.Request.Host.Port}/sektion/sida-b.4.20000000000b.html</loc><lastmod>2026-01-15</lastmod></url>"
            + $"<url><loc>http://{TestSite.Host}:{context.Request.Host.Port}/annan/sida-c.4.30000000000c.html</loc><lastmod>2026-01-15</lastmod></url></urlset>",
            "application/xml"));
        app.MapGet("/sektion", () => EngineKit.Html(EngineKit.Page($"sektion {_tag}",
            """<a href="/sektion/sida-a.4.10000000000a.html">A</a> <a href="/sektion/under/sida-a.4.10000000000a.html">A igen</a> """
            + """<a href="/annan/sida-d.4.40000000000d.html">D</a> <a href="/sektion/vanlig">Vanlig</a>""")));
        app.MapGet("/sektion/vanlig", () => EngineKit.Html(EngineKit.Page($"vanlig sida {_tag}")));
        // Node pages carry an ETag per node and answer a matching If-None-Match with 304.
        app.MapGet("/{**path}", (string path, HttpContext context) => Sitevision.NodeOf(path) switch
        {
            var node when _status.TryGetValue(node, out var status) => Results.StatusCode(status),
            ("4.10000000000a.html" or "4.20000000000b.html" or "4.30000000000c.html" or "4.40000000000d.html") and var node => Node(node, context),
            _ => Results.NotFound(),
        });
    });

    private IResult Node(string node, HttpContext context)
    {
        var etag = $"\"{node}\"";
        if (context.Request.Headers.IfNoneMatch == etag)
        {
            Interlocked.Increment(ref _notModified);
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        context.Response.Headers.ETag = etag;
        return EngineKit.Html(EngineKit.Page($"nod {node} {_tag}"));
    }
}
