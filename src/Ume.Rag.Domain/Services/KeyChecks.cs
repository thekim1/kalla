using Ume.Rag.Domain.Entities;

namespace Ume.Rag.Domain.Services;

/// <summary>
/// The status step of key authentication (identity design §6.3 step 3, as the gateway's
/// <c>GatewayRequestHandler.AuthenticateAsync</c>): the refusal reason for a found key, or null when it may be used.
/// </summary>
public static class KeyChecks
{
    public const string InvalidKey = "invalid_key";
    public const string KeyRevoked = "key_revoked";
    public const string KeyDisabled = "key_disabled";
    public const string KeyExpired = "key_expired";
    public const string OwnerInactive = "owner_inactive";

    /// <summary>The reason to refuse <paramref name="key"/> at <paramref name="now"/>; <paramref name="presentedKey"/> is the key as presented, whose prefix must match the stored kind.</summary>
    public static string? Refusal(ApiKey key, string presentedKey, DateTimeOffset now)
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
            _ => null,
        };
    }
}
