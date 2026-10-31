using System.IO.Compression;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Worker.Connectors;
using Ume.Rag.Worker.Connectors.Egress;
using Ume.Rag.Worker.Connectors.Http;
using Ume.Rag.Worker.Connectors.Malware;
using Ume.Rag.Worker.Connectors.Urls;

namespace Ume.Rag.Worker.Tests.Connectors;

/// <summary>
/// The URL-list fetch path end to end against an in-process Kestrel site (connectors design §3.2, §4, §7, §11, §12, §18):
/// conditional GET, redirects re-checked per hop, robots, limits counted after decompression, cookies, the malware scan
/// before storage, and the egress guard's pinned connect.
/// </summary>
public sealed class FetchPathTests : IAsyncLifetime
{
    private const string Html = "<html><head><title>Förskola</title><link rel=\"canonical\" href=\"/page?utm_source=x\"></head><body><main><p>Ansök om plats.</p></main></body></html>";

    private TestSite _site = null!;
    private FetchPath _path = null!;
    private int _changingETag;

    public async ValueTask InitializeAsync()
    {
        _site = await TestSite.StartAsync(Map);
        _path = _site.Fetch();
    }

    public async ValueTask DisposeAsync()
    {
        await _path.DisposeAsync();
        await _site.DisposeAsync();
    }

    [Fact]
    public async Task Stores_new_content_and_answers_304_to_the_stored_validators()
    {
        var first = (await FetchAsync(_site.Url("/page"))).ShouldBeOfType<FetchOutcome.Content>();

        first.Validators.ShouldBe(new Validators("W/\"v1\"", "Thu, 15 Jan 2026 10:00:00 GMT"));
        first.Raw.MediaType.ShouldBe("text/html");
        first.Raw.Key.Value.ShouldBe($"raw/public/{first.Raw.ContentHash}");
        first.Facts.CanonicalHint.ShouldBe(_site.Url("/page").AbsoluteUri);
        (await _path.Blobs.ExistsAsync(first.Raw.Key, TestContext.Current.CancellationToken)).ShouldBeTrue();

        var second = await FetchAsync(_site.Url("/page"), first.Validators);
        second.ShouldBe(new FetchOutcome.NotModified(first.Validators));
        _site.Requests["/page"]["If-None-Match"].ToString().ShouldBe("W/\"v1\"");
        _site.Requests["/page"]["If-Modified-Since"].ToString().ShouldBe("Thu, 15 Jan 2026 10:00:00 GMT");
    }

    [Fact]
    public async Task A_revalidating_run_sends_no_validators()
    {
        var outcome = await FetchAsync(_site.Url("/page"), new Validators("W/\"v1\"", "Thu, 15 Jan 2026 10:00:00 GMT"), revalidate: true);

        outcome.ShouldBeOfType<FetchOutcome.Content>();
        _site.Requests["/page"].ContainsKey("If-None-Match").ShouldBeFalse();
        _site.Requests["/page"].ContainsKey("If-Modified-Since").ShouldBeFalse();
    }

    [Fact]
    public async Task Last_modified_alone_gives_a_304_too()
    {
        var first = (await FetchAsync(_site.Url("/lastmod"))).ShouldBeOfType<FetchOutcome.Content>();
        first.Validators.ShouldBe(new Validators(null, "Wed, 14 Jan 2026 08:00:00 GMT"));

        (await FetchAsync(_site.Url("/lastmod"), first.Validators)).ShouldBeOfType<FetchOutcome.NotModified>();
    }

    [Fact]
    public async Task A_server_with_changing_etags_and_noise_keeps_the_content_hash()
    {
        var first = (await FetchAsync(_site.Url("/changing"))).ShouldBeOfType<FetchOutcome.Content>();
        var second = (await FetchAsync(_site.Url("/changing"), first.Validators)).ShouldBeOfType<FetchOutcome.Content>();

        second.Validators.ETag.ShouldNotBe(first.Validators.ETag);
        second.Raw.ContentHash.ShouldBe(first.Raw.ContentHash);
        second.Raw.Key.ShouldBe(first.Raw.Key);
    }

    [Fact]
    public async Task Identity_is_the_final_url_after_redirects()
    {
        var outcome = (await FetchAsync(_site.Url("/redirect/3"))).ShouldBeOfType<FetchOutcome.Content>();

        outcome.Facts.CanonicalIdentity.ShouldBe(_site.Url("/page").AbsoluteUri);
        outcome.Facts.Redirects.Select(u => u.AbsolutePath).ShouldBe(["/redirect/3", "/redirect/2", "/redirect/1", "/redirect/0"]);
    }

    [Fact]
    public async Task Five_hops_are_followed_and_the_sixth_fails()
    {
        (await FetchAsync(_site.Url("/redirect/4"))).ShouldBeOfType<FetchOutcome.Content>();
        (await FetchAsync(_site.Url("/redirect/5"))).ShouldBe(new FetchOutcome.Failed("redirect.limit"));
        (await FetchAsync(_site.Url("/loop-a"))).ShouldBe(new FetchOutcome.Failed("redirect.limit"));
    }

    [Fact]
    public async Task A_redirect_to_the_metadata_address_is_blocked()
    {
        (await FetchAsync(_site.Url("/to-metadata"))).ShouldBe(new FetchOutcome.Refused(RunItemOutcome.Blocked, "egress.private_address"));
        (await FetchAsync(_site.Url("/to-metadata-decimal"))).ShouldBe(new FetchOutcome.Refused(RunItemOutcome.Blocked, "egress.private_address"));
    }

    [Fact]
    public async Task An_https_page_redirecting_to_http_is_a_downgrade()
    {
        (await FetchAsync(_site.SecureUrl("/downgrade"))).ShouldBe(new FetchOutcome.Refused(RunItemOutcome.Blocked, "redirect.downgrade"));
    }

    [Fact]
    public async Task Robots_skips_a_disallowed_url_and_blocks_a_redirect_into_one()
    {
        (await FetchAsync(_site.Url("/private/x"))).ShouldBe(new FetchOutcome.Skipped("robots.disallow"));
        (await FetchAsync(_site.Url("/to-private"))).ShouldBe(new FetchOutcome.Refused(RunItemOutcome.Blocked, "robots.disallow"));
        _site.Requests.ContainsKey("/private/x").ShouldBeFalse();
    }

    [Theory]
    [InlineData(500, false)]
    [InlineData(503, false)]
    [InlineData(429, false)]
    [InlineData(404, true)]
    [InlineData(403, true)]
    public async Task Robots_unreachable_disallows_everything_and_absent_allows_everything(int robotsStatus, bool allowed)
    {
        await using var site = await TestSite.StartAsync(app =>
        {
            app.MapGet("/robots.txt", () => Results.StatusCode(robotsStatus));
            app.MapGet("/page", () => Results.Content(Html, "text/html"));
        });
        await using var path = site.Fetch();

        var outcome = await path.Connector.FetchAsync(Context(), Item(site.Url("/page")), TestContext.Current.CancellationToken);

        if (allowed)
        {
            outcome.ShouldBeOfType<FetchOutcome.Content>();
        }
        else
        {
            outcome.ShouldBe(new FetchOutcome.Skipped("robots.disallow"));
        }
    }

    [Theory]
    [InlineData("/missing", "gone:http.404")]
    [InlineData("/gone", "gone:http.410")]
    [InlineData("/forbidden", "failed:http.403")]
    [InlineData("/error", "transient:http.5xx")]
    [InlineData("/limited", "transient:rate_limited")]
    [InlineData("/zip", "refused:Unsupported:http.content_type")]
    [InlineData("/doc", "refused:Unsupported:http.format_deferred")]
    [InlineData("/bomb", "refused:TooLarge:http.body_limit")]
    [InlineData("/declared-huge", "refused:TooLarge:http.body_limit")]
    public async Task Maps_responses_to_outcomes(string pathAndQuery, string expected)
    {
        var outcome = await FetchAsync(_site.Url(pathAndQuery));

        Describe(outcome).ShouldBe(expected);
    }

    [Fact]
    public async Task Retry_after_is_passed_on()
    {
        (await FetchAsync(_site.Url("/limited"))).ShouldBe(new FetchOutcome.Transient("rate_limited", TimeSpan.FromSeconds(7), CountsAttempt: false));
    }

    [Fact]
    public async Task Brotli_and_gzip_bodies_are_stored_decompressed()
    {
        var brotli = (await FetchAsync(_site.Url("/brotli"))).ShouldBeOfType<FetchOutcome.Content>();
        var plain = (await FetchAsync(_site.Url("/page"))).ShouldBeOfType<FetchOutcome.Content>();

        brotli.Raw.ContentHash.ShouldBe(plain.Raw.ContentHash);
        brotli.Raw.Bytes.ShouldBe(Encoding.UTF8.GetByteCount(Html));
        _site.Requests["/brotli"]["Accept-Encoding"].ToString().ShouldContain("br");
    }

    [Fact]
    public async Task Cookies_are_never_sent_back()
    {
        (await FetchAsync(_site.Url("/cookie"))).ShouldBeOfType<FetchOutcome.Content>();

        _site.Requests["/cookie-check"].ContainsKey("Cookie").ShouldBeFalse();
        _site.Requests["/cookie-check"]["User-Agent"].ToString().ShouldBe(HttpFetcher.UserAgent);
    }

    [Fact]
    public async Task Files_are_scanned_before_they_are_stored_and_html_is_not()
    {
        var scanner = new ScriptedScanner(ScanResult.Clean);
        await using var path = _site.Fetch(scanner);

        var pdf = (await path.Connector.FetchAsync(Context(), Item(_site.Url("/file.pdf")), TestContext.Current.CancellationToken)).ShouldBeOfType<FetchOutcome.Content>();
        scanner.Scans.ShouldBe(1);
        pdf.Raw.ContentHash.ShouldBe(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(PdfBytes)));

        (await path.Connector.FetchAsync(Context(), Item(_site.Url("/page")), TestContext.Current.CancellationToken)).ShouldBeOfType<FetchOutcome.Content>();
        scanner.Scans.ShouldBe(1);
    }

    [Fact]
    public async Task Malware_is_quarantined_and_never_stored_as_content()
    {
        await using var path = _site.Fetch(new EicarOnlyScanner());
        var sourceId = Guid.NewGuid();

        var outcome = (await path.Connector.FetchAsync(Context(sourceId), Item(_site.Url("/eicar.pdf")), TestContext.Current.CancellationToken))
            .ShouldBeOfType<FetchOutcome.Quarantined>();

        outcome.Signature.ShouldBe(EicarOnlyScanner.Signature);
        outcome.Key.Value.ShouldBe($"quarantine/{sourceId:D}/{outcome.Sha256}");
        (await path.Blobs.ExistsAsync(outcome.Key, TestContext.Current.CancellationToken)).ShouldBeTrue();
        (await path.Blobs.ExistsAsync(new BlobKey($"raw/public/{outcome.Sha256}"), TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [Theory]
    [InlineData("clamav.unavailable", false)]
    [InlineData("clamav.timeout", true)]
    public async Task Without_a_verdict_the_file_waits_and_nothing_is_stored(string reason, bool countsAttempt)
    {
        await using var path = _site.Fetch(new ScriptedScanner(new ScanResult.Unavailable(reason, countsAttempt)));

        var outcome = await path.Connector.FetchAsync(Context(), Item(_site.Url("/file.pdf")), TestContext.Current.CancellationToken);

        outcome.ShouldBe(new FetchOutcome.Transient(reason, RawContentWriter.ScanRetry, countsAttempt));
        Directory.EnumerateFiles(path.BlobRoot.FullName, "*", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_size_verdict_from_clamav_is_too_large()
    {
        await using var path = _site.Fetch(new ScriptedScanner(new ScanResult.TooLarge()));

        (await path.Connector.FetchAsync(Context(), Item(_site.Url("/file.pdf")), TestContext.Current.CancellationToken))
            .ShouldBe(new FetchOutcome.Refused(RunItemOutcome.TooLarge, "clamav.limit"));
    }

    [Fact]
    public async Task Tls_uses_the_url_host_for_sni_and_negotiates_http2_over_the_pinned_connection()
    {
        (await FetchAsync(_site.SecureUrl("/page"))).ShouldBeOfType<FetchOutcome.Content>();

        _site.ServerNames.ShouldContain(TestSite.SecureHost);
        _site.Protocols["/page"].ShouldBe("HTTP/2");
    }

    [Fact]
    public async Task An_untrusted_certificate_fails_without_an_ignore_option()
    {
        var guard = ConnectorTestKit.Guard(_site.Resolver(), new FixedEgressPolicy(_site.Port, _site.SecurePort), "127.0.0.1/32");
        using var client = new CrawlHttpClient(guard);
        var fetcher = new HttpFetcher(client, guard, TimeProvider.System);

        var result = await fetcher.FetchAsync(Request(_site.SecureUrl("/page")), TestContext.Current.CancellationToken);

        result.ShouldBe(new HttpFetchResult.Error("tls.invalid_certificate", Transient: false));
    }

    [Fact]
    public async Task Loopback_is_blocked_unless_the_deployment_allows_it()
    {
        await using var path = _site.Fetch(allowedPrivateRanges: null);

        (await path.Connector.FetchAsync(Context(), Item(_site.Url("/page")), TestContext.Current.CancellationToken))
            .ShouldBe(new FetchOutcome.Refused(RunItemOutcome.Blocked, "egress.private_address"));
        _site.Requests.ContainsKey("/page").ShouldBeFalse();
    }

    [Fact]
    public async Task A_port_outside_the_policy_is_blocked_before_connecting()
    {
        var guard = ConnectorTestKit.Guard(_site.Resolver(), new FixedEgressPolicy(), "127.0.0.1/32");
        using var client = new CrawlHttpClient(guard);
        var fetcher = new HttpFetcher(client, guard, TimeProvider.System);

        (await fetcher.FetchAsync(Request(_site.Url("/page")), TestContext.Current.CancellationToken))
            .ShouldBe(new HttpFetchResult.Refused(RunItemOutcome.Blocked, "egress.port"));
    }

    [Fact]
    public async Task Connects_only_to_the_checked_address_and_a_rebound_answer_is_refused()
    {
        // rebind.test exists only in the fake resolver: a connect by name (a second lookup) could not reach the site.
        var dns = _site.Resolver().Add("rebind.test", call => call == 0 ? ["127.0.0.1"] : ["169.254.169.254"]);
        var guard = ConnectorTestKit.Guard(dns, new FixedEgressPolicy(_site.Port), "127.0.0.1/32");
        var url = new Uri($"http://rebind.test:{_site.Port}/plain");

        using (var client = new CrawlHttpClient(guard))
        {
            var fetcher = new HttpFetcher(client, guard, TimeProvider.System);
            var first = await fetcher.FetchAsync(Request(url), TestContext.Current.CancellationToken);
            await using ((first.ShouldBeOfType<HttpFetchResult.Ok>()).Body)
            {
            }

            dns.Calls("rebind.test").ShouldBe(1);
        }

        using (var client = new CrawlHttpClient(guard))
        {
            var fetcher = new HttpFetcher(client, guard, TimeProvider.System);
            (await fetcher.FetchAsync(Request(url), TestContext.Current.CancellationToken))
                .ShouldBe(new HttpFetchResult.Refused(RunItemOutcome.Blocked, "egress.private_address"));
            dns.Calls("rebind.test").ShouldBe(2);
        }
    }

    private static readonly byte[] PdfBytes = Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj << >> endobj\ntrailer << >>\n%%EOF\n");

    private static HttpFetchRequest Request(Uri url) =>
        new(url, EgressPurpose.Fetch, null, null, CanonicalizerOptions.Default, null);

    private static FetchContext Context(Guid? sourceId = null, bool revalidate = false) =>
        new(sourceId ?? Guid.NewGuid(), "public", Guid.NewGuid(), UrlListConnectorTests.Settings(), revalidate);

    private static WorkItem Item(Uri url, Validators? stored = null) =>
        new(IdentityKind.Url, UrlCanonicalizer.Canonicalize(url.AbsoluteUri).Value!, url, 0, new WorkHints(stored));

    private async Task<FetchOutcome> FetchAsync(Uri url, Validators? stored = null, bool revalidate = false) =>
        await _path.Connector.FetchAsync(Context(revalidate: revalidate), Item(url, stored), TestContext.Current.CancellationToken);

    private static string Describe(FetchOutcome outcome) => outcome switch
    {
        FetchOutcome.Gone g => "gone:" + g.Reason,
        FetchOutcome.Failed f => "failed:" + f.Reason,
        FetchOutcome.Transient t => "transient:" + t.Reason,
        FetchOutcome.Refused r => $"refused:{r.Outcome}:{r.Reason}",
        _ => outcome.ToString(),
    };

    private void Map(WebApplication app)
    {
        app.MapGet("/robots.txt", () => Results.Text("User-agent: *\nDisallow: /private\n"));
        app.MapGet("/page", (HttpContext context) =>
        {
            context.Response.Headers.ETag = "W/\"v1\"";
            context.Response.Headers.LastModified = "Thu, 15 Jan 2026 10:00:00 GMT";
            return context.Request.Headers.IfNoneMatch == "W/\"v1\"" ? Results.StatusCode(304) : Results.Content(Html, "text/html; charset=utf-8");
        });
        app.MapGet("/plain", () => Results.Content(Html, "text/html"));
        app.MapGet("/lastmod", (HttpContext context) =>
        {
            context.Response.Headers.LastModified = "Wed, 14 Jan 2026 08:00:00 GMT";
            return context.Request.Headers.IfModifiedSince == "Wed, 14 Jan 2026 08:00:00 GMT" ? Results.StatusCode(304) : Results.Content(Html, "text/html");
        });
        app.MapGet("/changing", (HttpContext context) =>
        {
            var n = Interlocked.Increment(ref _changingETag);
            context.Response.Headers.ETag = $"\"{n}\"";
            return Results.Content($"<html><head><meta name=\"csrf-token\" content=\"t{n}\"><script>var n={n}</script></head><body><main>Samma text</main></body></html>", "text/html");
        });
        app.MapGet("/redirect/{n:int}", (int n) => Results.Redirect(n == 0 ? "/page?utm_source=redirect" : $"/redirect/{n - 1}"));
        app.MapGet("/loop-a", () => Results.Redirect("/loop-b"));
        app.MapGet("/loop-b", () => Results.Redirect("/loop-a"));
        app.MapGet("/to-metadata", () => Results.Redirect("http://169.254.169.254/latest/meta-data/"));
        app.MapGet("/to-metadata-decimal", () => Results.Redirect("http://2852039166/latest/meta-data/"));
        app.MapGet("/downgrade", () => Results.Redirect($"http://{TestSite.Host}:{_site.Port}/page"));
        app.MapGet("/to-private", () => Results.Redirect("/private/x"));
        app.MapGet("/private/x", () => Results.Content(Html, "text/html"));
        app.MapGet("/missing", () => Results.NotFound());
        app.MapGet("/gone", () => Results.StatusCode(410));
        app.MapGet("/forbidden", () => Results.StatusCode(403));
        app.MapGet("/error", () => Results.StatusCode(500));
        app.MapGet("/limited", (HttpContext context) =>
        {
            context.Response.Headers.RetryAfter = "7";
            return Results.StatusCode(429);
        });
        app.MapGet("/zip", () => Results.Bytes("PK\u0003\u0004"u8.ToArray(), "application/zip"));
        app.MapGet("/doc", () => Results.Bytes([0xD0, 0xCF, 0x11, 0xE0], "application/msword"));
        app.MapGet("/bomb", async (HttpContext context) =>
        {
            // 11 MB of HTML in about 11 KB of gzip: the 10 MB cap counts decompressed bytes.
            context.Response.ContentType = "text/html";
            context.Response.Headers.ContentEncoding = "gzip";
            await using var gzip = new GZipStream(context.Response.Body, CompressionLevel.SmallestSize, leaveOpen: true);
            var block = new byte[1024 * 1024];
            Array.Fill(block, (byte)' ');
            for (var i = 0; i < 11; i++)
            {
                await gzip.WriteAsync(block, context.RequestAborted);
            }
        });
        app.MapGet("/declared-huge", async (HttpContext context) =>
        {
            context.Response.ContentType = "application/pdf";
            context.Response.ContentLength = 60L * 1024 * 1024;
            await context.Response.Body.WriteAsync(PdfBytes, context.RequestAborted);
        });
        app.MapGet("/brotli", async (HttpContext context) =>
        {
            context.Response.ContentType = "text/html";
            context.Response.Headers.ContentEncoding = "br";
            await using var brotli = new BrotliStream(context.Response.Body, CompressionLevel.Fastest, leaveOpen: true);
            await brotli.WriteAsync(Encoding.UTF8.GetBytes(Html), context.RequestAborted);
        });
        app.MapGet("/cookie", (HttpContext context) =>
        {
            context.Response.Headers.SetCookie = "session=secret; Path=/";
            return Results.Redirect("/cookie-check");
        });
        app.MapGet("/cookie-check", () => Results.Content(Html, "text/html"));
        app.MapGet("/file.pdf", () => Results.Bytes(PdfBytes, "application/pdf"));
        app.MapGet("/eicar.pdf", () => Results.Bytes(
            Encoding.ASCII.GetBytes(@"X5O!P%@AP[4\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*"), "application/pdf"));
    }
}
