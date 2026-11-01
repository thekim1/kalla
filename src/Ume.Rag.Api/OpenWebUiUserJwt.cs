using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ume.Rag.Api;

/// <summary>
/// <c>OpenWebUi:ClientId</c> (the registry client of the Open WebUI instance; empty = not configured, #95),
/// <c>OpenWebUi:JwtSecret</c> (Open WebUI's <c>FORWARD_USER_INFO_HEADER_JWT_SECRET</c>, ≥ 32 bytes) and
/// <c>OpenWebUi:MaxLifetimeSeconds</c> (600), identity design §5.2.
/// </summary>
public sealed class OpenWebUiOptions
{
    public const string SectionName = "OpenWebUi";

    public string? ClientId { get; set; }
    public string? JwtSecret { get; set; }
    public int MaxLifetimeSeconds { get; set; } = 600;

    /// <summary>The configured client, or null when <c>ClientId</c> is empty or not a GUID.</summary>
    public Guid? Client => Guid.TryParse(ClientId, out var id) ? id : null;
}

/// <summary>An Open WebUI user from a valid <c>X-OpenWebUI-User-Jwt</c>.</summary>
public sealed record OpenWebUiUser(string Subject, string? Name, string? Email, string Role);

/// <summary>
/// Validates Open WebUI's per-request user JWT (identity design §5.2) without a JWT library: exactly one header of ≤ 4 KB;
/// <c>alg</c> pinned to <c>HS256</c> (no <c>kid</c>); the signature compared in constant time with <c>OpenWebUi:JwtSecret</c>;
/// <c>iss = open-webui</c>; <c>exp</c> and <c>iat</c> required with 30 s skew, a lifetime ≤ <c>MaxLifetimeSeconds</c> and
/// <c>iat</c> not in the future; <c>sub</c> printable, 1–100 characters; <c>role</c> <c>user</c> or <c>admin</c>. Refusal
/// codes: <c>user_jwt_missing</c>, <c>user_jwt_invalid</c>, <c>user_jwt_expired</c>, <c>user_jwt_role</c>.
/// </summary>
public static class OpenWebUiUserJwt
{
    public const string Header = "X-OpenWebUI-User-Jwt";
    private const int MaxLength = 4096;
    private static readonly TimeSpan Skew = TimeSpan.FromSeconds(30);

    /// <summary>The user, or the refusal code (the JWT is never a credential on its own; the caller checked the key first).</summary>
    public static (OpenWebUiUser? User, string? Refusal) Validate(HttpRequest request, OpenWebUiOptions options, DateTimeOffset now)
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

        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            return (null, "user_jwt_invalid");
        }

        try
        {
            using var header = JsonDocument.Parse(Base64Url(parts[0]));
            if (!header.RootElement.TryGetProperty("alg", out var alg) || alg.GetString() != "HS256" || header.RootElement.TryGetProperty("kid", out _))
            {
                return (null, "user_jwt_invalid");
            }

            var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]));
            if (!CryptographicOperations.FixedTimeEquals(expected, Base64Url(parts[2])))
            {
                return (null, "user_jwt_invalid");
            }

            using var payload = JsonDocument.Parse(Base64Url(parts[1]));
            var claims = payload.RootElement;
            if (Text(claims, "iss") != "open-webui" || !claims.TryGetProperty("exp", out var expClaim) || !claims.TryGetProperty("iat", out var iatClaim) ||
                !expClaim.TryGetInt64(out var exp) || !iatClaim.TryGetInt64(out var iat))
            {
                return (null, "user_jwt_invalid");
            }

            var issued = DateTimeOffset.FromUnixTimeSeconds(iat);
            var expires = DateTimeOffset.FromUnixTimeSeconds(exp);
            if (issued > now + Skew || expires - issued > TimeSpan.FromSeconds(options.MaxLifetimeSeconds) || expires <= issued)
            {
                return (null, "user_jwt_invalid");
            }

            if (expires + Skew < now)
            {
                return (null, "user_jwt_expired");
            }

            var subject = Text(claims, "sub");
            if (subject is not { Length: > 0 and <= 100 } || subject.Any(char.IsControl))
            {
                return (null, "user_jwt_invalid");
            }

            var role = Text(claims, "role");
            return role is "user" or "admin"
                ? (new OpenWebUiUser(subject, Text(claims, "name"), Text(claims, "email"), role), null)
                : (null, "user_jwt_role");
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentOutOfRangeException)
        {
            return (null, "user_jwt_invalid");
        }
    }

    private static string? Text(JsonElement claims, string name) =>
        claims.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static byte[] Base64Url(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
    }
}
