using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Primitives;
using Ume.Rag.Domain;

namespace Ume.Rag.FakeGateway;

/// <summary>An authenticated call: the key's level and the request's ceiling (the narrower one applies).</summary>
public sealed record FakeCall(Residency KeyLevel, Residency Ceiling)
{
    public Residency Max => (Residency)Math.Min((int)KeyLevel, (int)Ceiling);
}

/// <summary>
/// The gateway's step 1 and 1b as release 1 defines them (gateway design §2.1, §3): the key, then
/// <c>x-ume-max-residency</c> (required for every dev key, as <c>RequireMaxResidency</c>), then candidate selection
/// within the ceiling with the 403/503 split. Errors use the gateway's OpenAI error shape.
/// </summary>
public static class FakeAccess
{
    public const string MaxResidencyHeader = "x-ume-max-residency";
    public const string ResidencyHeader = "x-ume-residency";
    public const string OnBehalfOfHeader = "x-ume-on-behalf-of";
    public const string SessionHeader = "x-ume-session";

    /// <summary>Authenticates the key and reads the ceiling; null result = refused (the error is in <paramref name="error"/>).</summary>
    public static FakeCall? Authorize(HttpContext context, FakeGatewayOptions options, out IResult? error)
    {
        var presented = BearerOrApiKey(context.Request);
        var level = presented is null ? (Residency?)null : options.Keys
            .Where(k => FixedTimeEquals(k.Value, presented))
            .Select(k => (Residency?)k.Key).FirstOrDefault();
        if (level is null)
        {
            error = Error(StatusCodes.Status401Unauthorized, presented is null ? "missing_key" : "invalid_key", "authentication_error",
                "Provide a valid API key.");
            return null;
        }

        var header = context.Request.Headers[MaxResidencyHeader];
        if (StringValues.IsNullOrEmpty(header))
        {
            error = Error(StatusCodes.Status400BadRequest, "invalid_request", "invalid_request_error",
                "This API key requires the x-ume-max-residency header.");
            return null;
        }

        if (header.Count > 1 || header.ToString().Contains(',', StringComparison.Ordinal) ||
            !ResidencyCeiling.TryParse(header.ToString(), out var ceiling))
        {
            error = Error(StatusCodes.Status400BadRequest, "invalid_request", "invalid_request_error",
                "x-ume-max-residency must be one of OnPrem, Eu, External.");
            return null;
        }

        if (options.StrictKeyLevel && ceiling != level)
        {
            error = Error(StatusCodes.Status403Forbidden, "key_level_mismatch", "permission_error",
                $"FakeGateway: the {level} key was sent with the ceiling {ResidencyCeiling.ToHeader(ceiling)}.");
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
    /// Selects the answering target of <paramref name="modelName"/> within the call's ceiling and sets
    /// <c>x-ume-residency</c>: 404 unknown model, 403 <c>max_residency_not_met</c> when no target is configured within it,
    /// 503 <c>no_eligible_provider</c> with <c>Retry-After</c> when the in-ceiling targets are drained (gateway design §3.3).
    /// <c>fake-wrong-residency*</c> answers with <c>External</c> whatever the ceiling.
    /// </summary>
    public static FakeModel? Route(HttpContext context, FakeGatewayOptions options, FakeCall call, string? modelName, string kind, out IResult? error)
    {
        var model = options.Models.FirstOrDefault(m => string.Equals(m.Name, modelName, StringComparison.Ordinal));
        if (model is null || model.Kind != kind)
        {
            error = Error(StatusCodes.Status404NotFound, "model_not_found", "invalid_request_error", $"The model '{modelName}' does not exist.");
            return null;
        }

        var inCeiling = model.Targets.Where(t => t.Residency <= call.Max).ToList();
        if (inCeiling.Count == 0)
        {
            error = Error(StatusCodes.Status403Forbidden, "max_residency_not_met", "permission_error",
                $"No provider for '{model.Name}' is configured at or below the residency '{ResidencyCeiling.ToHeader(call.Max)}' that the request allows.");
            return null;
        }

        var target = inCeiling.FirstOrDefault(t => !t.Drained);
        if (target is null)
        {
            context.Response.Headers.RetryAfter = options.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            error = Error(StatusCodes.Status503ServiceUnavailable, "no_eligible_provider", "server_error",
                $"No provider for '{model.Name}' within the residency '{ResidencyCeiling.ToHeader(call.Max)}' is available right now.");
            return null;
        }

        context.Response.Headers[ResidencyHeader] = ResidencyCeiling.ToHeader(model.Name.StartsWith("fake-wrong-residency", StringComparison.Ordinal) ? Residency.External : target.Residency);
        error = null;
        return model;
    }

    /// <summary>The residencies a model is servable at within the call (for <c>/v1/models</c>'s <c>ume.residencies</c>).</summary>
    public static Residency[] ServableResidencies(FakeModel model, FakeCall call) =>
        [.. model.Targets.Where(t => !t.Drained && t.Residency <= call.Max).Select(t => t.Residency).Distinct().Order()];

    public static IResult Error(int status, string code, string type, string message) =>
        Results.Json(new { error = new { message, type, code } }, statusCode: status);

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
