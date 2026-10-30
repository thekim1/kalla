using Ume.Rag.Domain.Entities;

namespace Ume.Rag.Domain.Services;

/// <summary>Rotation and revocation, copied from the gateway's <c>KeyRotation</c> (identity design §6.1).</summary>
public static class KeyRotation
{
    public static readonly TimeSpan GracePeriod = TimeSpan.FromHours(24);

    /// <summary>
    /// Creates the replacement key (copying client, scopes, origins, address ranges and rate limit) and retires the old key
    /// according to <paramref name="mode"/>. The caller persists both entities and shows <c>generated.PlainText</c> once.
    /// </summary>
    public static ApiKey Rotate(ApiKey oldKey, GeneratedKey generated, KeyRotationMode mode, DateTimeOffset now, string? actor)
    {
        ArgumentNullException.ThrowIfNull(oldKey);
        ArgumentNullException.ThrowIfNull(generated);

        if (!oldKey.IsUsable(now))
        {
            throw new InvalidOperationException("Only active keys can be rotated.");
        }

        if (generated.Kind != oldKey.Kind)
        {
            throw new InvalidOperationException("A key is rotated to a key of the same kind.");
        }

        var replacement = new ApiKey
        {
            ClientId = oldKey.ClientId,
            TeamId = oldKey.TeamId,
            Kind = oldKey.Kind,
            Name = oldKey.Name,
            Description = oldKey.Description,
            Prefix = generated.Prefix,
            KeyHash = generated.Hash,
            CreatedAt = now,
            CreatedBy = actor,
            ExpiresAt = oldKey.ExpiresAt,
            Scopes = oldKey.Scopes with { AssistantIds = [.. oldKey.Scopes.AssistantIds], CollectionIds = [.. oldKey.Scopes.CollectionIds] },
            AllowedOrigins = [.. oldKey.AllowedOrigins],
            AllowedIpRanges = [.. oldKey.AllowedIpRanges],
            RequestsPerMinute = oldKey.RequestsPerMinute,
        };

        oldKey.RotatedToKeyId = replacement.Id;
        switch (mode)
        {
            case KeyRotationMode.Grace24Hours:
                oldKey.GraceUntil = now + GracePeriod;
                break;
            default:
                oldKey.RevokedAt = now;
                break;
        }

        return replacement;
    }

    public static void Revoke(ApiKey key, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(key);
        key.RevokedAt ??= now;
    }
}
