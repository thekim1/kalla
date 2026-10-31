using System.Net;
using System.Text.Json;
using Ume.Rag.Worker.Connectors;
using Ume.Rag.Worker.Connectors.Http;
using Ume.Rag.Worker.Connectors.UrlList;

namespace Ume.Rag.Worker.Tests.Connectors;

/// <summary>URL-list listing (connectors design §4, §2.3) and the response table of §4 / outcomes of §15.</summary>
public sealed class UrlListConnectorTests
{
    [Fact]
    public async Task Lists_each_canonical_url_once_with_a_final_checkpoint()
    {
        var events = await ListAsync(new UrlListCheckpoint(), "https://www.umea.se/a?utm_source=x", "HTTPS://WWW.UMEA.SE/a", "ftp://x/", "https://www.umea.se/b");

        events.OfType<ListingEvent<UrlListCheckpoint>.Item>().Select(i => i.Work.CanonicalIdentity)
            .ShouldBe(["https://www.umea.se/a", "https://www.umea.se/b"]);
        events.OfType<ListingEvent<UrlListCheckpoint>.Failure>().Single().ShouldBe(new ListingEvent<UrlListCheckpoint>.Failure("ftp://x/", "url.scheme"));
        events[^2].ShouldBe(new ListingEvent<UrlListCheckpoint>.Checkpoint(new UrlListCheckpoint { SchemaVersion = 1, Position = 4 }));
        events[^1].ShouldBe(new ListingEvent<UrlListCheckpoint>.Done(Completeness.Full));
        events.OfType<ListingEvent<UrlListCheckpoint>.Item>().ShouldAllBe(i => i.Work.Depth == 0 && i.Work.Kind == IdentityKind.Url);
    }

    [Fact]
    public async Task Checkpoints_every_thousand_and_resumes_after_the_last()
    {
        var urls = Enumerable.Range(0, 2500).Select(i => $"https://www.umea.se/p{i}").ToArray();
        var all = await ListAsync(new UrlListCheckpoint(), urls);

        all.OfType<ListingEvent<UrlListCheckpoint>.Checkpoint>().Select(c => c.State.Position).ShouldBe([1000, 2000, 2500]);

        var resumed = await ListAsync(new UrlListCheckpoint { SchemaVersion = 1, Position = 2000 }, urls);
        resumed.OfType<ListingEvent<UrlListCheckpoint>.Item>().Select(i => i.Work.CanonicalIdentity).First().ShouldBe("https://www.umea.se/p2000");
        resumed.OfType<ListingEvent<UrlListCheckpoint>.Item>().Count().ShouldBe(500);
    }

    [Fact]
    public async Task An_unknown_checkpoint_version_restarts_the_listing()
    {
        var resumed = await ListAsync(new UrlListCheckpoint { SchemaVersion = 99, Position = 1 }, "https://www.umea.se/a", "https://www.umea.se/b");

        resumed.OfType<ListingEvent<UrlListCheckpoint>.Item>().Count().ShouldBe(2);
    }

    [Fact]
    public async Task A_resumed_listing_does_not_repeat_a_url_listed_before_the_checkpoint()
    {
        var resumed = await ListAsync(new UrlListCheckpoint { SchemaVersion = 1, Position = 1 }, "https://www.umea.se/a", "https://www.umea.se/a#x", "https://www.umea.se/b");

        resumed.OfType<ListingEvent<UrlListCheckpoint>.Item>().Select(i => i.Work.CanonicalIdentity).ShouldBe(["https://www.umea.se/b"]);
    }

    [Theory]
    [InlineData(404, "gone", "http.404", false)]
    [InlineData(410, "gone", "http.410", false)]
    [InlineData(401, "failed", "http.401", false)]
    [InlineData(403, "failed", "http.403", false)]
    [InlineData(400, "failed", "http.400", false)]
    [InlineData(429, "transient", "rate_limited", false)]
    [InlineData(503, "transient", "rate_limited", false)]
    [InlineData(500, "transient", "http.5xx", true)]
    [InlineData(502, "transient", "http.5xx", true)]
    public void Maps_final_statuses(int code, string kind, string reason, bool countsAttempt)
    {
        var outcome = FetchOutcomes.FromStatus(new HttpFetchResult.Status((HttpStatusCode)code, null, new Uri("https://www.umea.se/")));

        switch (outcome)
        {
            case FetchOutcome.Gone gone:
                kind.ShouldBe("gone");
                gone.Reason.ShouldBe(reason);
                break;
            case FetchOutcome.Failed failed:
                kind.ShouldBe("failed");
                failed.Reason.ShouldBe(reason);
                break;
            case FetchOutcome.Transient transient:
                kind.ShouldBe("transient");
                transient.Reason.ShouldBe(reason);
                transient.CountsAttempt.ShouldBe(countsAttempt);
                break;
            default:
                throw new ShouldAssertException($"Unexpected {outcome}.");
        }
    }

    [Fact]
    public void A_rate_limit_without_retry_after_waits_60_seconds_and_keeps_a_given_one()
    {
        var url = new Uri("https://www.umea.se/");

        FetchOutcomes.FromStatus(new HttpFetchResult.Status(HttpStatusCode.TooManyRequests, null, url))
            .ShouldBe(new FetchOutcome.Transient("rate_limited", TimeSpan.FromSeconds(60), CountsAttempt: false));
        FetchOutcomes.FromStatus(new HttpFetchResult.Status(HttpStatusCode.ServiceUnavailable, TimeSpan.FromSeconds(7), url))
            .ShouldBe(new FetchOutcome.Transient("rate_limited", TimeSpan.FromSeconds(7), CountsAttempt: false));
    }

    internal static JsonElement Settings(params string[] urls) =>
        JsonSerializer.SerializeToElement(new { urls, conditionalGet = true }, JsonSerializerOptions.Web);

    private static async Task<List<ListingEvent<UrlListCheckpoint>>> ListAsync(UrlListCheckpoint from, params string[] urls)
    {
        await using var site = await TestSite.StartAsync(_ => { });
        await using var path = site.Fetch();
        var events = new List<ListingEvent<UrlListCheckpoint>>();
        await foreach (var e in path.Connector.ListAsync(new ListingContext(Guid.NewGuid(), "public", Guid.NewGuid(), Settings(urls)), from, TestContext.Current.CancellationToken))
        {
            events.Add(e);
        }

        return events;
    }
}
