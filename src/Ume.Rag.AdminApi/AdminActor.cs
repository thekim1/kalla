using System.Security.Claims;
using System.Security.Cryptography;

namespace Ume.Rag.AdminApi;

/// <summary>
/// Who acted, from where and in which session, as written to audit entries and admin security events (adapted from the
/// gateway's <c>AdminActor</c>). The subject is the <c>oid</c>, else <c>sub</c>, else the name (identity design §1.1).
/// Never the cookie: the session is a random id given at sign-in (<see cref="AdminClaims.Session"/>).
/// </summary>
public sealed record AdminActor(string Subject, string? Name, string Roles, string Departments, string? SourceAddress, string? SessionId)
{
    public static AdminActor Of(HttpContext http) => Of(http, http?.User ?? throw new ArgumentNullException(nameof(http)));

    /// <summary>For <paramref name="user"/> while it is being signed in (<see cref="HttpContext.User"/> is not set yet).</summary>
    public static AdminActor Of(HttpContext http, ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(user);
        return new AdminActor(
            user.FindFirstValue(AdminClaims.ObjectId) ?? user.FindFirstValue(AdminClaims.Subject) ?? user.Identity?.Name ?? "unknown",
            Printable(user.Identity?.Name ?? user.FindFirstValue("email")),
            string.Join(',', user.FindAll(AdminClaims.Roles).Select(c => c.Value).Order(StringComparer.Ordinal)),
            string.Join(',', user.FindAll(AdminClaims.DepartmentCodes).Select(c => c.Value).Order(StringComparer.Ordinal)),
            AddressOf(http),
            user.FindFirstValue(AdminClaims.Session));
    }

    /// <summary>
    /// The connection's address. Behind the reverse proxy this is the proxy until the trusted-proxy resolution of the
    /// gateway's <c>AdminClientAddresses</c> is copied with the admin activity middleware (P0 #11).
    /// </summary>
    public static string? AddressOf(HttpContext http) => http?.Connection.RemoteIpAddress?.ToString();

    /// <summary>A new session id: 16 random bytes, hex.</summary>
    public static string NewSessionId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    /// <summary>Client-supplied text with control characters replaced, so it cannot start a forged log line.</summary>
    public static string? Printable(string? value) =>
        value is null || !value.Any(char.IsControl) ? value : string.Concat(value.Select(c => char.IsControl(c) ? '?' : c));
}
