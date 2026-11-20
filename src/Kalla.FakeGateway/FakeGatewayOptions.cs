using Kalla.Domain;

namespace Kalla.FakeGateway;

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
    /// The services' credential keys of the integration <c>Kalla</c> (<c>FakeGateway:CredentialKeys:&lt;service&gt;:Key</c>
    /// and <c>:MaxResidency</c>; gateway-changes §5.1, the gateway's <c>docs/integrations.md</c> at <c>6a8f0a5</c>): one key
    /// for every ceiling up to its highest residency (the worker's <c>Eu</c>), with <c>RequireMaxResidency</c>. A higher
    /// ceiling is narrowed to the key's residencies, as the gateway does; <see cref="StrictKeyLevel"/> does not apply.
    /// </summary>
    public Dictionary<string, FakeCredentialKey> CredentialKeys { get; set; } = new(StringComparer.Ordinal);

    /// <summary>What <c>/v1/fors/context</c> says about the gateway (<c>gateway.version</c>, <c>admin_url</c>, <c>features</c>).</summary>
    public FakeGatewayInfo Gateway { get; set; } = new();

    /// <summary>The integration the credential keys belong to (<c>/v1/fors/context</c>'s <c>integration</c>).</summary>
    public FakeIntegration Integration { get; set; } = new();

    /// <summary>
    /// The integration's assigned keys (<c>FakeGateway:AssignedKeys</c>): a chatbot's or knowledge base's key, set up by its
    /// department in the gateway. A revoked one is not listed, as the gateway lists only the keys that are not revoked.
    /// </summary>
    public List<FakeAssignedKey> AssignedKeys { get; set; } = [];

    /// <summary>
    /// Fake-only strictness: refuse with 403 <c>key_level_mismatch</c> when the ceiling is not the key's level. The real
    /// gateway would narrow silently; the fake makes a client that sends the wrong key for a level fail loudly.
    /// </summary>
    public bool StrictKeyLevel { get; set; } = true;

    /// <summary>
    /// The dev keys' <c>RequireMaxResidency</c> (default on, as every Källa key): a request without
    /// <c>x-fors-max-residency</c> is refused with 400 <c>invalid_request</c>. Off, such a request has no ceiling and reaches
    /// what the key's level allows.
    /// </summary>
    public bool RequireMaxResidency { get; set; } = true;

    /// <summary>
    /// Serve the models of kind <c>rerank</c> (<c>FakeGateway:RerankEnabled</c>). Off by default: the gateway as most
    /// organisations run it, with no rerank model configured, so <c>/v1/rerank</c> answers 404 <c>model_not_found</c> for
    /// any rerank model name (and 400 for a model of another kind), and neither <c>/v1/models</c> nor the context lists
    /// one (rerank is optional, Decisions "Gateway release 1 and shared Keycloak as built").
    /// </summary>
    public bool RerankEnabled { get; set; }

    /// <summary>
    /// The prompt tokens every chat answer reports as cached (<c>FakeGateway:CachedInputTokens</c>, at most its prompt), in
    /// <c>usage.prompt_tokens_details.cached_tokens</c> and priced at the cached rate; 0 by default, as a prompt cache miss.
    /// Tests script it to check the cached-input price (chat design §8.3).
    /// </summary>
    public int CachedInputTokens { get; set; }

    /// <summary>
    /// Answers a request made as an assigned key without the <c>x-fors-on-behalf-of</c> echo (<c>FakeGateway:DropOnBehalfOfEcho</c>),
    /// as an older gateway instance during a rolling update would: Källa must abort the call (event 5011, identity §4.7).
    /// </summary>
    public bool DropOnBehalfOfEcho { get; set; }

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

    /// <summary>An alias's description on <c>/v1/models</c> (<c>fors.description</c>).</summary>
    public string? Description { get; set; }

    /// <summary><c>fors.context_window</c>; null when the gateway does not know it.</summary>
    public int? ContextWindow { get; set; }

    /// <summary><c>fors.max_output_tokens</c>; null when the gateway does not know it.</summary>
    public int? MaxOutputTokens { get; set; }

    /// <summary><c>fors.features</c> (<c>tools</c>, ...).</summary>
    public List<string> Features { get; set; } = [];
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

/// <summary>A credential key of the fake: its secret, the highest residency it allows and its integration scopes.</summary>
public sealed class FakeCredentialKey
{
    public string Key { get; set; } = "";

    public Residency MaxResidency { get; set; } = Residency.External;

    /// <summary>The key's id; a stable one per secret when not set.</summary>
    public Guid? Id { get; set; }

    public string? Name { get; set; }

    /// <summary><c>organisation.read</c> (kalla-admin's: every department and team in the context), later <c>usage.read</c>.</summary>
    public List<string> Scopes { get; set; } = [];
}

/// <summary>The gateway part of <c>/v1/fors/context</c>.</summary>
public sealed class FakeGatewayInfo
{
    public string? Version { get; set; } = "0.10.0-fake";

    public string? AdminUrl { get; set; }

    /// <summary>
    /// The organisation's name as the gateway's admin console brands it (the context's <c>organisation_name</c>, feature
    /// <c>organisation-name</c>, gateway <c>v0.10.2</c>); empty: null in the context.
    /// </summary>
    public string? OrganisationName { get; set; } = "Umeå kommun";

    /// <summary>
    /// The Integration API's base address as the context names it (<c>integration_api_url</c>, feature
    /// <c>integration-api-url</c>, gateway <c>v0.10.2</c>). Not set: this fake's own <c>/integration/v1</c> at the address the
    /// caller used; empty: null in the context (no read-back).
    /// </summary>
    public string? IntegrationApiUrl { get; set; }

    /// <summary>
    /// The address of the sign-in provider the gateway's admin console uses (the context's <c>oidc_authority</c>, feature
    /// <c>oidc-authority</c>, gateway setup mode, llmgateway#99). Not set or empty: null in the context, as a gateway whose sign-in
    /// is not set up yet; a test sets it.
    /// </summary>
    public string? OidcAuthority { get; set; }

    /// <summary>
    /// The capabilities the fake implements, of the gateway's list (<c>ContextEndpoint.Features</c> at
    /// thekim1/llmgateway <c>v0.10.2</c>), phase 2 included: <c>act-as-key</c> and <c>run-metadata</c> (B-42),
    /// <c>stream-cost</c> (B-38), <c>organisation-name</c> (M-17 C12), <c>integration-api-url</c> (M-17 C11) and
    /// <c>oidc-authority</c> (M-20 S8, after <c>v0.10.3</c>).
    /// </summary>
    public List<string> Features { get; set; } = ["max-residency", "model-facts", "integrations", "act-as-key", "run-metadata", "stream-cost", "credential-residencies", "organisation", "member-groups", "organisation-name", "integration-api-url", "oidc-authority"];
}

/// <summary>The integration <c>Kalla</c>, owned by the pilot team.</summary>
public sealed class FakeIntegration
{
    public Guid Id { get; set; } = new("019a0000-0000-7000-8000-00000000a001");

    public string Name { get; set; } = "Kalla";

    public Guid OwnerTeamId { get; set; } = FakeCatalog.DigitaliseringTeamId;
}

/// <summary>An assigned key as <c>/v1/fors/context</c> lists it (the contract's <c>AssignedKey</c>; never a secret).</summary>
public sealed class FakeAssignedKey
{
    public Guid Id { get; set; }

    public string Name { get; set; } = "";

    public string? Description { get; set; }

    public Guid TeamId { get; set; }

    /// <summary><c>Active</c>, <c>InGracePeriod</c>, <c>Disabled</c>, <c>Expired</c> or <c>Revoked</c> (not listed).</summary>
    public string Status { get; set; } = "Active";

    public DateTimeOffset? ExpiresAt { get; set; }

    public List<string> AllowedModels { get; set; } = [];

    /// <summary>The key's own residencies; empty = all.</summary>
    public List<Residency> AllowedResidencies { get; set; } = [];

    public List<string> AllowedProviders { get; set; } = [];

    public string PiiPolicy { get; set; } = "Block";

    public string ContentGuardPolicy { get; set; } = "Off";

    public string AttachmentPolicy { get; set; } = "Allow";

    public bool AllowProviderTools { get; set; }

    public bool AllowProviderStorage { get; set; }

    public int? RequestsPerMinute { get; set; }

    public int? TokensPerMinute { get; set; }

    public int? MaxConcurrentRequests { get; set; }

    public int? MaxOutputTokens { get; set; }

    public bool HasSystemPrompt { get; set; }

    /// <summary>
    /// A spent budget (<c>Key</c>, <c>Team</c> or <c>Department</c>; <c>FakeGateway:AssignedKeys:&lt;n&gt;:BudgetExceeded</c>): a
    /// request made as the key answers <c>402 budget_exceeded</c> with the gateway's message for that scope (gateway-changes §5.4).
    /// </summary>
    public string? BudgetExceeded { get; set; }

    /// <summary>
    /// The budgets that apply to the key, for <c>GET /integration/v1/budgets</c> (gateway phase 3, gateway-changes §5.6;
    /// <c>FakeGateway:AssignedKeys:&lt;n&gt;:Budgets:&lt;m&gt;</c>).
    /// </summary>
    public List<FakeBudget> Budgets { get; set; } = [];
}

/// <summary>A budget as the gateway's Integration API shows it (gateway-changes §5.6, <c>IntegrationBudget</c>).</summary>
public sealed class FakeBudget
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary><c>VirtualKey</c>, <c>Team</c> or <c>Department</c>.</summary>
    public string Scope { get; set; } = "VirtualKey";

    /// <summary>The key's, its team's or its department's id; derived from the key when unset.</summary>
    public Guid? ScopeId { get; set; }

    /// <summary><c>Usd</c> or <c>Tokens</c>.</summary>
    public string Unit { get; set; } = "Usd";

    public decimal Limit { get; set; } = 50m;
    public string? Residency { get; set; }

    /// <summary><c>Month</c>, <c>Week</c> or <c>Day</c>, as the gateway's budget periods.</summary>
    public string Period { get; set; } = "Month";

    public decimal Spent { get; set; }
}
