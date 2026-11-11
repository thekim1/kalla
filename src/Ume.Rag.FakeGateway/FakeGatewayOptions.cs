using Ume.Rag.Domain;

namespace Ume.Rag.FakeGateway;

/// <summary>The fake's configuration (<c>FakeGateway:*</c>, repo-and-deployment §4.3).</summary>
public sealed class FakeGatewayOptions
{
    public const string SectionName = "FakeGateway";

    /// <summary>
    /// The three per-level dev keys (<c>FakeGateway:Keys:OnPrem</c>, <c>:Eu</c>, <c>:External</c>), like the pilot team's
    /// gateway keys: every one has <c>RequireMaxResidency</c>, and is allowed residencies up to its level only.
    /// </summary>
    public Dictionary<Residency, string> Keys { get; set; } = [];

    /// <summary>
    /// Fake-only strictness: refuse with 403 <c>key_level_mismatch</c> when the ceiling is not the key's level. The real
    /// gateway would narrow silently; the fake makes a client that sends the wrong key for a level fail loudly.
    /// </summary>
    public bool StrictKeyLevel { get; set; } = true;

    /// <summary>
    /// The dev keys' <c>RequireMaxResidency</c> (default on, as every Ume.Rag key): a request without
    /// <c>x-ume-max-residency</c> is refused with 400 <c>invalid_request</c>. Off, such a request has no ceiling and reaches
    /// what the key's level allows.
    /// </summary>
    public bool RequireMaxResidency { get; set; } = true;

    /// <summary>
    /// Serve the models of kind <c>rerank</c> (<c>FakeGateway:RerankEnabled</c>). Off by default: the gateway as most
    /// organisations run it, with no rerank model configured, so <c>/v1/rerank</c> answers 404 <c>model_not_found</c> for
    /// any rerank model name (and 400 for a model of another kind), and neither <c>/v1/models</c> nor the catalogue lists
    /// one (rerank is optional, Decisions "Gateway release 1 and shared Keycloak as built").
    /// </summary>
    public bool RerankEnabled { get; set; }

    /// <summary>The catalogue's Data API key (<c>ume-dk-…</c>); null accepts any bearer.</summary>
    public string? DataApiKey { get; set; }

    /// <summary>Length of the <c>fake-stream-stall</c> pause (above the chat idle timeout of 60 s by default).</summary>
    public int StallSeconds { get; set; } = 65;

    /// <summary><c>Retry-After</c> of a 503 (the gateway's <c>CircuitBreaker:BreakDuration</c>, 30 s).</summary>
    public int RetryAfterSeconds { get; set; } = 30;

    /// <summary>Base URL (incl. <c>/v1</c>) of an OpenAI-compatible server for real embeddings (dev: Ollama BGE-M3).</summary>
    public FakeUpstream Upstream { get; set; } = new();

    /// <summary>The models and their targets (appsettings.json).</summary>
    public List<FakeModel> Models { get; set; } = [];

    /// <summary>The models the fake serves: <see cref="Models"/> without the rerank models unless <see cref="RerankEnabled"/>.</summary>
    public IEnumerable<FakeModel> Served => Models.Where(m => RerankEnabled || m.Kind != FakeModel.Rerank);
}

public sealed class FakeUpstream
{
    public string? Embeddings { get; set; }
}

public sealed class FakeModel
{
    public const string Chat = "chat";
    public const string Embedding = "embedding";
    public const string Rerank = "rerank";

    public string Name { get; set; } = "";

    /// <summary><c>chat</c>, <c>embedding</c> or <c>rerank</c>.</summary>
    public string Kind { get; set; } = "chat";

    /// <summary>
    /// The levels whose key may use the model, as the gateway's per-key model allowlist; empty: every key. Another key gets
    /// 403 <c>model_not_allowed</c> (gateway <c>RouteResolver.ResolveModel</c>).
    /// </summary>
    public List<Residency> KeyLevels { get; set; } = [];

    public List<FakeTarget> Targets { get; set; } = [];
}

/// <summary>One provider target of a model, as a gateway route target.</summary>
public sealed class FakeTarget
{
    public Residency Residency { get; set; }

    /// <summary>
    /// A drained or disabled target: not a candidate. When every target within the ceiling is drained, a call answers 503
    /// <c>no_eligible_provider</c> with <c>Retry-After</c> (gateway design §3.3).
    /// </summary>
    public bool Drained { get; set; }

    /// <summary>
    /// A target that is tried and fails (a provider that is down, an open circuit that is still down): the next target
    /// within the ceiling is tried, and when all fail the call answers 502 (or 504 when the last one timed out)
    /// <c>all_providers_failed</c>, as the gateway does with or without a ceiling (#390 Feature B point 1).
    /// </summary>
    public FakeFailure Fails { get; set; }
}

/// <summary>How a <see cref="FakeTarget"/> fails when it is tried.</summary>
public enum FakeFailure
{
    None,

    /// <summary>The provider answers 5xx (the gateway's last error <c>http_500</c>).</summary>
    Error,

    /// <summary>The provider does not answer in time (the gateway's last error <c>timeout</c>, 504).</summary>
    Timeout,
}
