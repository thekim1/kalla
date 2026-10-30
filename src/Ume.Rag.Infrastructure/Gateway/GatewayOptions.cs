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
}

/// <summary>Header names shared with the gateway (<c>Pipeline/GatewayHeaders.cs</c>, gateway design §2.1).</summary>
public static class GatewayHeaders
{
    public const string MaxResidency = "x-ume-max-residency";
    public const string Residency = "x-ume-residency";
    public const string OnBehalfOf = "x-ume-on-behalf-of";
    public const string RequestId = "x-request-id";
}
