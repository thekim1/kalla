using System.Net;
using Kalla.Domain.Entities;

namespace Kalla.Domain.Services;

/// <summary>
/// The status and address steps of key authentication (identity design §6.3 steps 3–4, as the gateway's
/// <c>GatewayRequestHandler.AuthenticateAsync</c>): the refusal reason for a found key, or null when it may be used.
/// </summary>
public static class KeyChecks
{
    public const string InvalidKey = "invalid_key";
    public const string KeyRevoked = "key_revoked";
    public const string KeyDisabled = "key_disabled";
    public const string KeyExpired = "key_expired";
    public const string OwnerInactive = "owner_inactive";
    public const string AddressNotAllowed = "address_not_allowed";

    /// <summary>
    /// The reason to refuse <paramref name="key"/> at <paramref name="now"/> from <paramref name="address"/> (the connection
    /// address; null when unknown); <paramref name="presentedKey"/> is the key as presented, whose prefix must match the
    /// stored kind. The address comes after the status checks, so a revoked key is reported as revoked wherever it is used.
    /// </summary>
    public static string? Refusal(ApiKey key, string presentedKey, DateTimeOffset now, IPAddress? address)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (KeyHasher.KindOf(presentedKey) != key.Kind)
        {
            return InvalidKey;
        }

        return key.GetStatus(now) switch
        {
            KeyStatus.Revoked => KeyRevoked,
            KeyStatus.Disabled => KeyDisabled,
            KeyStatus.Expired => KeyExpired,
            _ when key.Client is not { IsEnabled: true } => OwnerInactive,
            _ when !key.IsAddressAllowed(address) => AddressNotAllowed,
            _ => null,
        };
    }
}
