using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Primitives;
using Ume.Rag.Domain;

namespace Ume.Rag.FakeGateway;

/// <summary>
/// An authenticated call: the key's level and the request's ceiling, if it sent one (the narrower one applies; without
/// a ceiling the key's level alone).
/// </summary>
public sealed record FakeCall(Residency KeyLevel, Residency? Ceiling)
{
    public Residency Max => Ceiling is { } ceiling && ceiling < KeyLevel ? ceiling : KeyLevel;
}

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

    /// <summary>Authenticates the key and reads the ceiling; null result = refused (the error is in <paramref name="error"/>).</summary>
    public static FakeCall? Authorize(HttpContext context, FakeGatewayOptions options, out IResult? error)
    {
        var presented = BearerOrApiKey(context.Request);
        var level = presented is null ? (Residency?)null : options.Keys
            .Where(k => FixedTimeEquals(k.Value, presented))
            .Select(k => (Residency?)k.Key).FirstOrDefault();
        if (level is null)
        {
            error = Error(context, StatusCodes.Status401Unauthorized, "invalid_api_key",
                "The API key is missing or invalid. Send it as 'Authorization: Bearer ume-sk-…'.");
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

        if (options.StrictKeyLevel && ceiling is { } sent && sent != level)
        {
            error = Error(context, StatusCodes.Status403Forbidden, "key_level_mismatch",
                $"FakeGateway: the {level} key was sent with the ceiling {ResidencyCeiling.ToHeader(sent)}.");
            return null;
        }

        // Echoed as the gateway does from release 2 (gateway design §5.2); a session id is echoed for streaming tests.
        foreach (var echo in (string[])[OnBehalfOfHeader, SessionHeader])
        {
            if (context.Request.Headers.TryGetValue(echo, out var value) && !StringValues.IsNullOrEmpty(value))
            {
                context.Response.Headers[echo] = value;
            }
        }

        error = null;
        return new FakeCall(level.Value, ceiling);
    }

    /// <summary>
    /// Resolves <paramref name="modelName"/> for an endpoint serving <paramref name="kind"/>, selects its target within the
    /// call's ceiling and sets <c>x-ume-residency</c>: 404 <c>model_not_found</c> for an unknown model (a rerank model
    /// while <see cref="FakeGatewayOptions.RerankEnabled"/> is off included), 400 <c>invalid_request</c> for a model of
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

        if (model.Kind != kind)
        {
            error = Error(context, StatusCodes.Status400BadRequest, "invalid_request", $"The model '{model.Name}' cannot be used with this endpoint.");
            return null;
        }

        var candidates = model.Targets.Where(t => t.Residency <= call.Max && !t.Drained).ToList();
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

        if (!model.Targets.Any(t => t.Residency <= call.Max))
        {
            return Error(context, StatusCodes.Status403Forbidden, "max_residency_not_met",
                $"No provider for '{model.Name}' is configured at or below the residency '{ResidencyCeiling.ToHeader(ceiling)}' that the request allows.");
        }

        context.Response.Headers.RetryAfter = Math.Max(1, options.RetryAfterSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Error(context, StatusCodes.Status503ServiceUnavailable, "no_eligible_provider",
            $"No provider for '{model.Name}' within the residency '{ResidencyCeiling.ToHeader(ceiling)}' is available right now. Try again later.");
    }

    /// <summary>The residencies a model is servable at within the call (for <c>/v1/models</c>).</summary>
    public static Residency[] ServableResidencies(FakeModel model, FakeCall call) =>
        [.. model.Targets.Where(t => !t.Drained && t.Residency <= call.Max).Select(t => t.Residency).Distinct().Order()];

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
