using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Primitives;
using Ume.Rag.Domain;

namespace Ume.Rag.FakeGateway;

/// <summary>
/// An authenticated call: the key's level (a credential key's highest residency) and the request's ceiling, if it sent one
/// (the narrower one applies; without a ceiling the key's level alone).
/// </summary>
public sealed record FakeCall(Residency KeyLevel, Residency? Ceiling, bool Credential = false)
{
    public Residency Max => Ceiling is { } ceiling && ceiling < KeyLevel ? ceiling : KeyLevel;

    /// <summary>The assigned key the request runs as (<c>x-ume-on-behalf-of</c>, gateway phase 2), or null.</summary>
    public FakeAssignedKey? Acting { get; init; }

    /// <summary>The presenting credential key's id (the "via" key of the usage record), or null for a per-level key.</summary>
    public Guid? CredentialId { get; init; }

    /// <summary>
    /// A residency the request may be served at: within <see cref="Max"/> and, when acting, within the assigned key's own
    /// residencies (the credential key's bound is <see cref="KeyLevel"/>; gateway-changes §5.3, feature credential-residencies).
    /// </summary>
    public bool Allows(Residency residency) =>
        residency <= Max && (Acting is null || Acting.AllowedResidencies.Count == 0 || Acting.AllowedResidencies.Contains(residency));
}

/// <summary>An authenticated key: a per-level dev key (its level) or a credential key (its highest residency).</summary>
public sealed record FakeKey(Residency Level, FakeCredentialKey? Credential);

/// <summary>
/// The gateway's step 1 and 1b and candidate selection as release 1 builds them (gateway PR thekim1/llmgateway#66:
/// <c>RequestStages.ReadMaxResidency</c>, <c>RouteResolver.SelectCandidatesAsync</c>, <c>GatewayErrors</c>): the key, then
/// <c>x-ume-max-residency</c> (required for every dev key, as <c>RequireMaxResidency</c>), then the model, its kind, and
/// the targets within the ceiling with the 403/503 split, then the attempts (502/504 when every one fails). Errors use the
/// gateway's OpenAI error shape and messages.
/// </summary>
public static class FakeAccess
{
    public const string MaxResidencyHeader = "x-ume-max-residency";
    public const string ResidencyHeader = "x-ume-residency";
    public const string OnBehalfOfHeader = "x-ume-on-behalf-of";
    public const string SessionHeader = "x-ume-session";
    public const string RequestIdHeader = "x-request-id";

    /// <summary>
    /// Authenticates the key only (the gateway's <c>RequestStages.AuthenticateAsync</c>, which <c>/v1/ume/context</c> uses
    /// without a ceiling): a per-level dev key or a credential key; null = 401 (the error is in <paramref name="error"/>).
    /// </summary>
    public static FakeKey? Authenticate(HttpContext context, FakeGatewayOptions options, out IResult? error)
    {
        var presented = BearerOrApiKey(context.Request);
        error = null;
        if (presented is not null)
        {
            foreach (var (level, key) in options.Keys)
            {
                if (FixedTimeEquals(key, presented))
                {
                    return new FakeKey(level, null);
                }
            }

            if (options.CredentialKeys.Values.FirstOrDefault(k => k.Key.Length > 0 && FixedTimeEquals(k.Key, presented)) is { } credential)
            {
                return new FakeKey(credential.MaxResidency, credential);
            }
        }

        error = Error(context, StatusCodes.Status401Unauthorized, "invalid_api_key",
            "The API key is missing or invalid. Send it as 'Authorization: Bearer ume-sk-…'.");
        return null;
    }

    /// <summary>Authenticates the key and reads the ceiling; null result = refused (the error is in <paramref name="error"/>).</summary>
    public static FakeCall? Authorize(HttpContext context, FakeGatewayOptions options, out IResult? error)
    {
        if (Authenticate(context, options, out error) is not { } authenticated)
        {
            return null;
        }

        var (level, credential) = (authenticated.Level, authenticated.Credential is not null);
        if (!TryResolveActing(context, options, authenticated, out var acting, out error))
        {
            return null;
        }

        // As RequestStages.ReadMaxResidency: absent is "no ceiling" unless the key requires one; present, it must be exactly
        // one of the three names (an empty value, a list or a repeated header is refused, never ignored).
        Residency? ceiling = null;
        var header = context.Request.Headers[MaxResidencyHeader];
        if (header.Count == 0)
        {
            if (options.RequireMaxResidency)
            {
                error = Error(context, StatusCodes.Status400BadRequest, "invalid_request",
                    "This API key requires the x-ume-max-residency header (OnPrem, Eu or External).");
                return null;
            }
        }
        else if (header.Count > 1 || !ResidencyCeiling.TryParse(header[0] ?? "", out var parsed))
        {
            error = Error(context, StatusCodes.Status400BadRequest, "invalid_request", "x-ume-max-residency must be one of OnPrem, Eu, External.");
            return null;
        }
        else
        {
            ceiling = parsed;
        }

        if (options.StrictKeyLevel && !credential && ceiling is { } sent && sent != level)
        {
            error = Error(context, StatusCodes.Status403Forbidden, "key_level_mismatch",
                $"FakeGateway: the {level} key was sent with the ceiling {ResidencyCeiling.ToHeader(sent)}.");
            return null;
        }

        // A session id is echoed for streaming tests.
        if (context.Request.Headers.TryGetValue(SessionHeader, out var session) && !StringValues.IsNullOrEmpty(session))
        {
            context.Response.Headers[SessionHeader] = session;
        }

        if (acting?.BudgetExceeded is { Length: > 0 } scope)
        {
            // The gateway's 402 (gateway-changes §5.4): the scope only in the message, after the echo is set.
            error = Error(context, StatusCodes.Status402PaymentRequired, "budget_exceeded", scope switch
            {
                "Team" => "The team's budget (USD 100 per month) is used up. Ask the team's administrator to raise it.",
                "Department" => "The department's budget (USD 1000 per month) is used up. Ask the department's administrator to raise it.",
                _ => "The key's budget (USD 50 per month) is used up. Ask the key's administrator to raise it.",
            });
            return null;
        }

        error = null;
        return new FakeCall(level, ceiling, credential) { Acting = acting, CredentialId = authenticated.Credential is { } c ? CredentialId(c) : null };
    }

    /// <summary>
    /// <c>x-ume-on-behalf-of</c> as the gateway resolves it right after authentication (gateway v0.10.0
    /// <c>RequestStages.cs:118-180</c>; gateway-changes §5.2): none = the presented key; not one GUID or repeated = 400; a
    /// caller that is no credential key, an id not assigned to the integration, or no residency in common = 403
    /// <c>delegation_not_allowed</c>; a disabled key or an inactive owner = 403 <c>key_disabled</c>; revoked or expired = 401.
    /// A usable key is echoed in the D form on every later answer, unless <see cref="FakeGatewayOptions.DropOnBehalfOfEcho"/>.
    /// </summary>
    private static bool TryResolveActing(HttpContext context, FakeGatewayOptions options, FakeKey key, out FakeAssignedKey? acting, out IResult? error)
    {
        acting = null;
        error = null;
        var header = context.Request.Headers[OnBehalfOfHeader];
        if (header.Count == 0)
        {
            return true;
        }

        if (header.Count > 1 || !Guid.TryParse(header[0], out var id))
        {
            error = Error(context, StatusCodes.Status400BadRequest, "invalid_request", "x-ume-on-behalf-of must be one key id (a GUID).");
            return false;
        }

        var assigned = key.Credential is null ? null : options.AssignedKeys.FirstOrDefault(k => k.Id == id);
        var credentialResidencies = Enum.GetValues<Residency>().Where(r => r <= key.Level).ToList();
        if (assigned is null || (assigned.AllowedResidencies.Count > 0 && !assigned.AllowedResidencies.Any(credentialResidencies.Contains)))
        {
            error = Error(context, StatusCodes.Status403Forbidden, "delegation_not_allowed", "This key may not make requests on behalf of that key.");
            return false;
        }

        if (assigned.Status == "Revoked")
        {
            error = Error(context, StatusCodes.Status401Unauthorized, "key_revoked", "The API key has been revoked.");
            return false;
        }

        if (assigned.Status == "Expired" || assigned.ExpiresAt is { } expires && expires <= DateTimeOffset.UtcNow)
        {
            error = Error(context, StatusCodes.Status401Unauthorized, "key_expired", "The API key has expired.");
            return false;
        }

        var team = FakeCatalog.OrganisationTeams.FirstOrDefault(t => t.Id == assigned.TeamId);
        var department = team is null ? null : FakeCatalog.OrganisationDepartments.FirstOrDefault(d => d.Id == team.DepartmentId);
        if (assigned.Status == "Disabled" || team is not { IsActive: true } || department is not { IsActive: true })
        {
            error = Error(context, StatusCodes.Status403Forbidden, "key_disabled", "The API key is disabled.");
            return false;
        }

        if (!options.DropOnBehalfOfEcho)
        {
            context.Response.Headers[OnBehalfOfHeader] = assigned.Id.ToString("D");
        }

        acting = assigned;
        return true;
    }

    /// <summary>A credential key's id as the context shows it (a stable one per secret when none is set).</summary>
    public static Guid CredentialId(FakeCredentialKey key) =>
        key.Id ?? new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("fake-key:" + key.Key)).AsSpan(0, 16));

    /// <summary>
    /// Resolves <paramref name="modelName"/> for an endpoint serving <paramref name="kind"/>, selects its target within the
    /// call's ceiling and sets <c>x-ume-residency</c>: 404 <c>model_not_found</c> for an unknown model (a rerank model
    /// while <see cref="FakeGatewayOptions.RerankEnabled"/> is off included), 403 <c>model_not_allowed</c> for a key whose
    /// level is not in <see cref="FakeModel.KeyLevels"/>, 400 <c>invalid_request</c> for a model of
    /// another kind, then, with no candidate, 403 <c>max_residency_not_met</c> when no target is configured within the
    /// ceiling or 503 <c>no_eligible_provider</c> (with <c>Retry-After</c> only when a ceiling was sent) when the ones
    /// within it are drained; the candidates are tried in order, and when every one fails the answer is 502
    /// <c>all_providers_failed</c>, 504 when the last one timed out. <c>fake-wrong-residency*</c> answers with
    /// <c>External</c> whatever the ceiling.
    /// </summary>
    public static FakeModel? Route(HttpContext context, FakeGatewayOptions options, FakeCall call, string? modelName, string kind, out IResult? error)
    {
        var model = options.Served.FirstOrDefault(m => string.Equals(m.Name, modelName, StringComparison.Ordinal));
        if (model is null)
        {
            error = Error(context, StatusCodes.Status404NotFound, "model_not_found",
                $"The model '{modelName}' does not exist. See GET /v1/models for the models you can use.");
            return null;
        }

        if (model.KeyLevels.Count > 0 && !model.KeyLevels.Contains(call.KeyLevel))
        {
            error = Error(context, StatusCodes.Status403Forbidden, "model_not_allowed", $"The API key may not use the model '{model.Name}'.");
            return null;
        }

        if (call.Acting is { AllowedModels.Count: > 0 } acting && !acting.AllowedModels.Contains(model.Name))
        {
            error = Error(context, StatusCodes.Status403Forbidden, "model_not_allowed", $"The API key may not use the model '{model.Name}'.");
            return null;
        }

        if (model.Kind != kind)
        {
            error = Error(context, StatusCodes.Status400BadRequest, "invalid_request", $"The model '{model.Name}' cannot be used with this endpoint.");
            return null;
        }

        var candidates = model.Targets.Where(t => call.Allows(t.Residency) && !t.Drained).ToList();
        if (candidates.Count == 0)
        {
            error = NoCandidate(context, options, call, model);
            return null;
        }

        var target = candidates.FirstOrDefault(t => t.Fails == FakeFailure.None);
        if (target is null)
        {
            // Every attempt failed (circuit-open targets are tried too): the same answer with or without a ceiling.
            var timedOut = candidates[^1].Fails == FakeFailure.Timeout;
            var attempts = candidates.Count;
            error = Error(context, timedOut ? StatusCodes.Status504GatewayTimeout : StatusCodes.Status502BadGateway, "all_providers_failed",
                $"All {attempts} attempt{(attempts == 1 ? "" : "s")} for '{model.Name}' failed. Last error: {(timedOut ? "timeout" : "http_500")}.");
            return null;
        }

        context.Response.Headers[ResidencyHeader] = ResidencyCeiling.ToHeader(model.Name.StartsWith("fake-wrong-residency", StringComparison.Ordinal) ? Residency.External : target.Residency);
        error = null;
        return model;
    }

    /// <summary>
    /// No candidate (gateway <c>RouteResolver.SelectCandidatesAsync</c> and <c>RequestStages.ApplyRoutingAsync</c>): with a
    /// ceiling, 403 when no target within it is configured at all (whatever its state), else 503 with <c>Retry-After</c>;
    /// without one, the plain 503 and no <c>Retry-After</c>.
    /// </summary>
    private static IResult NoCandidate(HttpContext context, FakeGatewayOptions options, FakeCall call, FakeModel model)
    {
        if (call.Ceiling is not { } ceiling)
        {
            return Error(context, StatusCodes.Status503ServiceUnavailable, "no_eligible_provider",
                "No available provider can handle the request for this alias within the key's restrictions.");
        }

        if (!model.Targets.Any(t => call.Allows(t.Residency)))
        {
            return Error(context, StatusCodes.Status403Forbidden, "max_residency_not_met",
                $"No provider for '{model.Name}' is configured at or below the residency '{ResidencyCeiling.ToHeader(ceiling)}' that the request allows.");
        }

        context.Response.Headers.RetryAfter = Math.Max(1, options.RetryAfterSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Error(context, StatusCodes.Status503ServiceUnavailable, "no_eligible_provider",
            $"No provider for '{model.Name}' within the residency '{ResidencyCeiling.ToHeader(ceiling)}' is available right now. Try again later.");
    }

    /// <summary>
    /// The residencies a model is servable at within the call (for <c>/v1/models</c>); none for a model the acting key may
    /// not use (gateway-changes §5.5: the assigned key's models within both keys' residencies).
    /// </summary>
    public static Residency[] ServableResidencies(FakeModel model, FakeCall call) =>
        call.Acting is { AllowedModels.Count: > 0 } acting && !acting.AllowedModels.Contains(model.Name)
            ? []
            : [.. model.Targets.Where(t => !t.Drained && call.Allows(t.Residency)).Select(t => t.Residency).Distinct().Order()];

    /// <summary>The gateway's OpenAI error body (<c>GatewayErrors.Create</c>): <c>type</c> from the status, the request id.</summary>
    public static IResult Error(HttpContext context, int status, string code, string message) =>
        Results.Json(new
        {
            error = new
            {
                message,
                type = TypeFor(status),
                code,
                param = (string?)null,
                request_id = context.Response.Headers[RequestIdHeader].ToString(),
                doc_url = (string?)null,
            },
        }, statusCode: status);

    /// <summary>The gateway's <c>GatewayErrors.TypeFor</c> for the OpenAI-style endpoints.</summary>
    private static string TypeFor(int status) => status switch
    {
        400 or 413 or 415 => "invalid_request_error",
        401 => "authentication_error",
        402 => "insufficient_quota",
        403 => "permission_error",
        404 => "not_found_error",
        429 => "rate_limit_error",
        503 => "service_unavailable",
        _ => "api_error",
    };

    public static string? BearerOrApiKey(HttpRequest request)
    {
        var auth = request.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return auth["Bearer ".Length..].Trim();
        }

        foreach (var header in (string[])["x-api-key", "api-key"])
        {
            if (request.Headers.TryGetValue(header, out var value) && !StringValues.IsNullOrEmpty(value))
            {
                return value.ToString().Trim();
            }
        }

        return null;
    }

    public static bool FixedTimeEquals(string expected, string presented) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(presented));
}
