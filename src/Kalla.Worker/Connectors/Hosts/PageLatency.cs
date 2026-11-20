using Kalla.Worker.Connectors.Render;

namespace Kalla.Worker.Connectors.Hosts;

/// <summary>
/// The latency a page fetch reports to the host budget (connectors design §6.3, the latency term). An HTTP page's elapsed
/// time is its response time. A rendered page's is not: it holds the render, with every subrequest, its pacing and the
/// ready rule, and as the host's latency it would lengthen the interval of the next page's subrequests, which lengthens
/// the next render (A-13: 6.8 s on an external single-page app, every later page past <c>Render:PageTimeout</c>). Those
/// subrequests report their own response times (<see cref="RenderFetcher"/>), so a rendered page reports none.
/// </summary>
public static class PageLatency
{
    /// <summary><paramref name="elapsed"/>, or null when <paramref name="outcome"/> came from a render (rendered content or a render failure).</summary>
    public static TimeSpan? Of(FetchOutcome outcome, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return outcome switch
        {
            FetchOutcome.Content { Facts.FetchMode: Domain.Entities.FetchModes.Browser } => null,
            _ when RenderReasons.Of(outcome) is not null => null,
            _ => elapsed,
        };
    }
}
