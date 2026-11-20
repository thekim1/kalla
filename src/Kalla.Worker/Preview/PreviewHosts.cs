using Kalla.Worker.Connectors;
using Kalla.Worker.Connectors.Hosts;
using Kalla.Worker.Connectors.Http;
using Kalla.Worker.Connectors.Render;

namespace Kalla.Worker.Preview;

/// <summary>
/// The host politeness of a preview's requests (engine design §11; connectors design §6.3): a slot of the host's budget,
/// waited for in the job (a preview runs once and is not requeued for politeness), and the response fed back to the
/// host's back-off and breaker.
/// </summary>
internal static class PreviewHosts
{
    /// <summary>The host's lease, or the refusal (<c>crawl.host_blocked</c>, <c>crawl.host_paused</c>).</summary>
    public static async Task<(HostLease? Lease, string? Refusal)> SlotAsync(HostBudget budget, string host, TimeProvider time, CancellationToken cancellationToken)
    {
        while (true)
        {
            switch (await budget.AcquireAsync(host, cancellationToken))
            {
                case HostSlot.Granted granted:
                    return (granted.Lease, null);
                case HostSlot.Later later:
                    await Task.Delay(later.Delay, time, cancellationToken);
                    break;
                case HostSlot.Refused refused:
                    return (null, refused.Reason);
                default:
                    throw new InvalidOperationException("Unexpected host slot.");
            }
        }
    }

    /// <summary>
    /// A render slot for a page of <paramref name="host"/> (connectors design §8.3), waited for in the job while every slot
    /// is busy; the refusal (<c>render.not_configured</c>) when there is no renderer.
    /// </summary>
    public static async Task<(RenderLease? Lease, string? Refusal)> RenderSlotAsync(RenderSlots renders, string host, TimeProvider time,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            switch (renders.TryAcquire(host))
            {
                case RenderSlot.Granted granted:
                    return (granted.Lease, null);
                case RenderSlot.Refused { Reason: RenderReasons.Busy, Delay: var delay }:
                    await Task.Delay(delay, time, cancellationToken);
                    break;
                case RenderSlot.Refused refused:
                    return (null, refused.Reason);
                default:
                    throw new InvalidOperationException("Unexpected render slot.");
            }
        }
    }

    /// <summary>What a fetch outcome tells the host budget (as the URL-list fetch reports it).</summary>
    public static HostResponse Response(FetchOutcome outcome, TimeSpan? latency) => outcome switch
    {
        FetchOutcome.Content or FetchOutcome.NotModified => new HostResponse(HostResponseKind.Success, latency),
        FetchOutcome.Transient { Reason: "rate_limited" or FetchOutcomes.Challenge, RetryAfter: var retryAfter } => new HostResponse(HostResponseKind.RateLimited, latency, retryAfter),
        FetchOutcome.Transient { Reason: "http.5xx" or "net.timeout" } or FetchOutcome.Gone { Reason: "http.404" } => new HostResponse(HostResponseKind.Failure, latency),
        _ => new HostResponse(HostResponseKind.Neutral, latency),
    };
}
