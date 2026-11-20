using Microsoft.Extensions.Options;
using Kalla.Domain;

namespace Kalla.Infrastructure.Gateway;

/// <summary>
/// The highest ceiling this service's model calls may carry (Decisions "Residency per level", identity design §4.7; M-17
/// C9): its credential key's residencies from its own <c>GET /v1/ume/context</c> (<see cref="GatewayFeatures.CredentialMaximum"/>),
/// lowered to the service's own maximum (<see cref="GatewayOptions.ServiceMaximum"/>, the worker's <c>Eu</c>). The gateway
/// owns the ceiling: there is no second copy in Källa's configuration that could disagree. Before the first context answer
/// it is <c>OnPrem</c> (fail closed); the first call waits for that answer (<see cref="FirstReadWait"/>) instead of running
/// at <c>OnPrem</c> right after the start.
/// </summary>
public sealed class GatewayCeiling(GatewayFeatures features, GatewayContextReader reader, IOptionsMonitor<GatewayOptions> options)
{
    /// <summary>How long a call waits for the first context answer (<c>Gateway:FirstContextWait</c>, default 10 s).</summary>
    public static readonly TimeSpan FirstReadWait = TimeSpan.FromSeconds(10);

    /// <summary>The maximum as known now: <c>OnPrem</c> until the first context answer.</summary>
    public Residency Maximum
    {
        get
        {
            var credential = features.CredentialMaximum ?? Residency.OnPrem;
            return options.CurrentValue.ServiceMaximum is { } service && service < credential ? service : credential;
        }
    }

    /// <summary>The maximum, after waiting for the first context answer when there is none yet and a key is configured.</summary>
    public async ValueTask<Residency> MaximumAsync(CancellationToken cancellationToken)
    {
        var gateway = options.CurrentValue;
        if (features.CredentialMaximum is null && !string.IsNullOrEmpty(gateway.Key) && !string.IsNullOrWhiteSpace(gateway.BaseUrl))
        {
            await reader.EnsureFirstReadAsync(gateway.FirstContextWait ?? FirstReadWait, cancellationToken);
        }

        return Maximum;
    }

    /// <summary><paramref name="ceiling"/>, lowered to <see cref="MaximumAsync"/> when that is lower (it only ever lowers).</summary>
    public async ValueTask<Residency> CapAsync(Residency ceiling, CancellationToken cancellationToken)
    {
        var maximum = await MaximumAsync(cancellationToken);
        return maximum < ceiling ? maximum : ceiling;
    }
}
