using Ume.Rag.Domain;

namespace Ume.Rag.Infrastructure.Gateway;

/// <summary>The gateway connection (<c>Gateway:*</c>, repo-and-deployment §3, §6.3).</summary>
public sealed class GatewayOptions
{
    public const string SectionName = "Gateway";

    /// <summary>The gateway's base URL (without <c>/v1</c>).</summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// The per-level gateway keys, secret files <c>Gateway__Keys__OnPrem</c>, <c>__Eu</c>, <c>__External</c>: delegating keys
    /// from gateway release 2, the pilot team's own keys until then (weakness review §5). The worker never holds
    /// <c>External</c>.
    /// </summary>
    public Dictionary<Residency, string> Keys { get; set; } = [];

    /// <summary>
    /// The highest ceiling this deployment calls models at (<c>Gateway:MaxResidency</c>), or none. Under M2 (no on-prem model
    /// hardware, every model call EU-hosted; P1 README §1) the pilot sets <c>Eu</c>, so a public assistant, whose level
    /// asks for <c>External</c>, is checked (V8) and answered at <c>Eu</c> with the <c>Eu</c> key (#237). It only lowers a
    /// ceiling, never raises one.
    /// </summary>
    public Residency? MaxResidency { get; set; }

    /// <summary><paramref name="ceiling"/>, lowered to <see cref="MaxResidency"/> when that is set and lower.</summary>
    public Residency Cap(Residency ceiling) => MaxResidency is { } cap && cap < ceiling ? cap : ceiling;
}

/// <summary>Header names shared with the gateway (<c>Pipeline/GatewayHeaders.cs</c>, gateway design §2.1).</summary>
public static class GatewayHeaders
{
    public const string MaxResidency = "x-ume-max-residency";
    public const string Residency = "x-ume-residency";
    public const string OnBehalfOf = "x-ume-on-behalf-of";
    public const string RequestId = "x-request-id";
}
