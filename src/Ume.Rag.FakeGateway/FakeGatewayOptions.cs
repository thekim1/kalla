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
}

public sealed class FakeUpstream
{
    public string? Embeddings { get; set; }
}

public sealed class FakeModel
{
    public string Name { get; set; } = "";

    /// <summary><c>chat</c>, <c>embedding</c> or <c>rerank</c>.</summary>
    public string Kind { get; set; } = "chat";

    public List<FakeTarget> Targets { get; set; } = [];
}

/// <summary>One provider target of a model, as a gateway route target.</summary>
public sealed class FakeTarget
{
    public Residency Residency { get; set; }

    /// <summary>A drained (or circuit-open) target: in the ceiling but unavailable, so a call answers 503.</summary>
    public bool Drained { get; set; }
}
