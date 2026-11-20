using System.ComponentModel.DataAnnotations;
using System.Text;
using Kalla.Domain.Services;

namespace Kalla.Infrastructure.Security;

/// <summary>Secrets of Källa's key handling (as the gateway's <c>GatewaySecurityOptions</c>).</summary>
public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    /// <summary>
    /// Server-side secret mixed into key hashes (HMAC); Källa's own, never the gateway's. Identical for api and admin
    /// API, at least 32 characters, never committed to source control. Changing it invalidates all keys.
    /// </summary>
    [Required]
    [MinLength(32)]
    public string KeyPepper { get; set; } = string.Empty;
}

public static class KeyHasherFactory
{
    public static KeyHasher Create(SecurityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new KeyHasher(Encoding.UTF8.GetBytes(options.KeyPepper));
    }
}
