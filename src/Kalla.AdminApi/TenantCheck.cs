using System.Security.Claims;

namespace Kalla.AdminApi;

/// <summary>
/// The tenant and guest rule shared with the gateway (identity design §1.7), run before claim mapping. Keycloak and AD FS
/// emit neither <c>acct</c> nor <c>idp</c>, so the guest check is a no-op there.
/// </summary>
public static class TenantCheck
{
    public const string WrongTenant = "wrong_tenant";
    public const string GuestRefused = "guest_refused";

    /// <summary>The refusal reason, or null when the user may sign in.</summary>
    public static string? Refusal(ClaimsPrincipal principal, OidcOptions options)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(options);
        var tenant = principal.FindFirstValue("tid");
        if (!string.IsNullOrWhiteSpace(options.TenantId) && !string.Equals(tenant, options.TenantId, StringComparison.OrdinalIgnoreCase))
        {
            return WrongTenant;
        }

        if (!options.RefuseGuests)
        {
            return null;
        }

        if (principal.FindFirstValue("acct") == "1")
        {
            return GuestRefused;
        }

        // Entra sets idp for users from another tenant or identity provider.
        var idp = principal.FindFirstValue("idp");
        var issuer = principal.FindFirstValue("iss");
        return idp is not null && !string.Equals(idp, issuer, StringComparison.Ordinal) &&
               !string.Equals(idp, $"https://sts.windows.net/{tenant}/", StringComparison.OrdinalIgnoreCase)
            ? GuestRefused
            : null;
    }
}
