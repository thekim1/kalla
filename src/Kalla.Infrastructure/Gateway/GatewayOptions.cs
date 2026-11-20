using Kalla.Domain;

namespace Kalla.Infrastructure.Gateway;

/// <summary>The gateway connection (<c>Gateway:*</c>, repo-and-deployment §3, §6.3).</summary>
public sealed class GatewayOptions
{
    public const string SectionName = "Gateway";

    /// <summary>The gateway's base URL (without <c>/v1</c>).</summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// This service's credential key of the gateway integration <c>Kalla</c> (<c>Gateway:Key</c>, secret file
    /// <c>Gateway__Key</c>; identity design §4.7, gateway-changes §5.1): one key for every call, at any ceiling up to
    /// <see cref="GatewayCeiling.Maximum"/>, which its own residencies set.
    /// </summary>
    public string? Key { get; set; }

    /// <summary>
    /// The worker's fixed maximum (<c>Eu</c>, set with <c>CapGatewayCalls</c>; its credential key is capped there too,
    /// identity §4.7), or null for rag-api and rag-admin, whose maximum is their credential key's residencies alone
    /// (<see cref="GatewayCeiling"/>). Set in code only: configuration does not bind it.
    /// </summary>
    public Residency? ServiceMaximum { get; internal set; }

    /// <summary>
    /// The pause before the one retry of a gateway <c>502</c>/<c>504</c> (<c>Gateway:RetryDelay</c>, <see cref="GatewayRetryHandler"/>):
    /// the gateway has already tried every target within the ceiling, so the retry waits a moment.
    /// </summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// A <c>2xx</c> or <c>304</c> answer to a call made as an assigned key must echo the key id in <c>x-ume-on-behalf-of</c>,
    /// or the call is aborted with event 5011 (<c>Gateway:RequireDelegationEcho</c>, default on; identity design §4.7): an
    /// older gateway instance during a rolling update ignores the header and would charge the credential key.
    /// </summary>
    public bool RequireDelegationEcho { get; set; } = true;

    /// <summary>
    /// How long a model call waits for the first context answer before it goes on at <c>OnPrem</c> (<c>Gateway:FirstContextWait</c>;
    /// null: <see cref="GatewayCeiling.FirstReadWait"/>, 10 s).
    /// </summary>
    public TimeSpan? FirstContextWait { get; set; }

    /// <summary>How often each service reads its own <c>GET /v1/ume/context</c> for the gateway's features (identity §4.7).</summary>
    public TimeSpan ContextInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The key a call at <paramref name="ceiling"/> is sent with, or null when this service may not call there: the
    /// credential key up to <paramref name="maximum"/> (<see cref="GatewayCeiling.Maximum"/>), none without one.
    /// </summary>
    public string? KeyFor(Residency ceiling, Residency maximum) => !string.IsNullOrEmpty(Key) && ceiling <= maximum ? Key : null;
}

/// <summary>Header names shared with the gateway (<c>Pipeline/GatewayHeaders.cs</c>, gateway design §2.1).</summary>
public static class GatewayHeaders
{
    public const string MaxResidency = "x-ume-max-residency";
    public const string Residency = "x-ume-residency";
    public const string OnBehalfOf = "x-ume-on-behalf-of";
    public const string RequestId = "x-request-id";

    /// <summary>The call's cost in USD on a complete JSON answer (eight decimals; gateway-changes §5.4).</summary>
    public const string CostUsd = "x-ume-cost-usd";

    /// <summary>
    /// Request header: exactly <c>1</c> asks the gateway to end a streamed answer with the SSE comment
    /// <c>: ume-cost-usd=&lt;usd&gt;</c> (gateway-changes §5.4); any other value asks for nothing.
    /// </summary>
    public const string StreamCost = "x-ume-stream-cost";
}
