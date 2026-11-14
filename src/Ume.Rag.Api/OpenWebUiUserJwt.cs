using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Ume.Rag.Api;

/// <summary>
/// <c>OpenWebUi:JwtSecret</c> (Open WebUI's <c>FORWARD_USER_INFO_HEADER_JWT_SECRET</c>, ≥ 32 bytes) and
/// <c>OpenWebUi:MaxLifetimeSeconds</c> (600), identity design §5.2. The Open WebUI instance is the deployment's own client
/// (<c>Clients.IsManagedByDeployment</c>, M-17 C4), not a setting.
/// </summary>
public sealed class OpenWebUiOptions
{
    public const string SectionName = "OpenWebUi";

    public string? JwtSecret { get; set; }
    public int MaxLifetimeSeconds { get; set; } = 600;

    /// <summary>Open WebUI's address for the feedback import and pruning (chat design §14.2); unset: no import.</summary>
    public Uri? BaseUrl { get; set; }

    /// <summary>The feedback service account's API key (<c>OpenWebUi__AdminApiKey</c>, written by <c>deploy/openwebui-feedback-key.sh</c>).</summary>
    public string? AdminApiKey { get; set; }

    /// <summary>The import's schedule (§14.2: every 15 minutes).</summary>
    public TimeSpan FeedbackImportInterval { get; set; } = TimeSpan.FromMinutes(15);
}

/// <summary>An Open WebUI user from a valid <c>X-OpenWebUI-User-Jwt</c>.</summary>
public sealed record OpenWebUiUser(string Subject, string? Name, string? Email, string Role);

/// <summary>
/// Validates Open WebUI's per-request user JWT (identity design §5.2) with <c>Microsoft.IdentityModel.JsonWebTokens</c>
/// (#512): exactly one header of ≤ 4 KB; <c>alg</c> pinned to <c>HS256</c> and no <c>kid</c> (the library would try the
/// key for any <c>kid</c>); the signature checked with <c>OpenWebUi:JwtSecret</c>; <c>iss = open-webui</c>. The time rules
/// are checked here against the host's clock (the library's lifetime check reads the system clock; its
/// <c>TimeProvider</c> is internal in 8.19): <c>exp</c> and <c>iat</c> required with 30 s skew, a lifetime ≤
/// <c>MaxLifetimeSeconds</c> and <c>iat</c> not in the future. <c>sub</c> printable, 1–100 characters; <c>role</c>
/// <c>user</c> or <c>admin</c>.
/// Refusal codes: <c>user_jwt_missing</c>, <c>user_jwt_invalid</c>, <c>user_jwt_expired</c>, <c>user_jwt_role</c>.
/// </summary>
public static class OpenWebUiUserJwt
{
    public const string Header = "X-OpenWebUI-User-Jwt";
    private const int MaxLength = 4096;
    private static readonly TimeSpan Skew = TimeSpan.FromSeconds(30);
    private static readonly JsonWebTokenHandler Handler = new();

    /// <summary>The user, or the refusal code (the JWT is never a credential on its own; the caller checked the key first).</summary>
    public static async Task<(OpenWebUiUser? User, string? Refusal)> ValidateAsync(HttpRequest request, OpenWebUiOptions options, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        var values = request.Headers[Header];
        if (values.Count == 0)
        {
            return (null, "user_jwt_missing");
        }

        if (values.Count != 1 || values[0] is not { Length: > 0 and <= MaxLength } token || options.JwtSecret is not { Length: >= 32 } secret)
        {
            return (null, "user_jwt_invalid");
        }

        var result = await Handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ValidIssuer = "open-webui",
            ValidateAudience = false,
            ValidateLifetime = false,
        });
        if (!result.IsValid)
        {
            return (null, "user_jwt_invalid");
        }

        var jwt = (JsonWebToken)result.SecurityToken;
        if (jwt.TryGetHeaderValue<object>(JwtHeaderParameterNames.Kid, out _)
            || !jwt.TryGetPayloadValue<long>(JwtRegisteredClaimNames.Iat, out _) || !jwt.TryGetPayloadValue<long>(JwtRegisteredClaimNames.Exp, out _))
        {
            return (null, "user_jwt_invalid");
        }

        // ValidTo is clamped to DateTime.MaxValue for an exp beyond it, so an absurd exp is a too-long lifetime, not a 500.
        var (issued, expires) = (jwt.IssuedAt, jwt.ValidTo);
        if (issued > now.UtcDateTime + Skew || expires - issued > TimeSpan.FromSeconds(options.MaxLifetimeSeconds) || expires <= issued)
        {
            return (null, "user_jwt_invalid");
        }

        if (expires + Skew < now.UtcDateTime)
        {
            return (null, "user_jwt_expired");
        }

        var subject = Text(jwt, JwtRegisteredClaimNames.Sub);
        if (subject is not { Length: > 0 and <= 100 } || subject.Any(char.IsControl))
        {
            return (null, "user_jwt_invalid");
        }

        var role = Text(jwt, "role");
        return role is "user" or "admin"
            ? (new OpenWebUiUser(subject, Text(jwt, "name"), Text(jwt, "email"), role), null)
            : (null, "user_jwt_role");
    }

    private static string? Text(JsonWebToken jwt, string name) => jwt.TryGetPayloadValue<string>(name, out var value) ? value : null;
}
