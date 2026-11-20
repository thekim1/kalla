using Kalla.Domain.Entities;
using Kalla.Domain.Ports;
using Kalla.Worker.Connectors;
using Kalla.Worker.Connectors.Hosts;
using Kalla.Worker.Connectors.Render;

namespace Kalla.Worker.Tests.Connectors;

/// <summary>A rendered page reports no latency to the host budget; an HTTP page reports its elapsed time (A-13, connectors §6.3).</summary>
public sealed class PageLatencyTests
{
    private static readonly TimeSpan Elapsed = TimeSpan.FromSeconds(7);

    [Fact]
    public void An_http_page_reports_its_elapsed_time()
    {
        PageLatency.Of(Content(FetchModes.Http), Elapsed).ShouldBe(Elapsed);
        PageLatency.Of(new FetchOutcome.Transient("http.5xx", null, true), Elapsed).ShouldBe(Elapsed);
    }

    [Fact]
    public void A_rendered_page_or_a_failed_render_reports_none()
    {
        PageLatency.Of(Content(FetchModes.Browser), Elapsed).ShouldBeNull();
        PageLatency.Of(new FetchOutcome.Transient(RenderReasons.Timeout, null, true), Elapsed).ShouldBeNull();
        PageLatency.Of(new FetchOutcome.Transient(RenderReasons.Unavailable, null, true), Elapsed).ShouldBeNull();
    }

    private static FetchOutcome.Content Content(string mode) =>
        new(new RawContentRef(new BlobKey("raw/public/page"), "hash", 1, "text/html", 10), Validators.None,
            new FetchFacts(new Uri("https://example.org/a"), "https://example.org/a", [], null) { FetchMode = mode });
}
