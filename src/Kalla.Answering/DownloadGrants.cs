using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kalla.Domain;
using Kalla.Domain.Access;

namespace Kalla.Answering;

/// <summary>Download grant settings (knowledge design §4.6).</summary>
public sealed class DownloadGrantOptions
{
    /// <summary><c>Security:DownloadGrantKeys:&lt;kid&gt;</c>, each ≥ 32 bytes; all verify, the signing key is <see cref="SigningKeyId"/>.</summary>
    public IReadOnlyDictionary<string, byte[]> Keys { get; init; } = new Dictionary<string, byte[]>();

    /// <summary>The newest key: the greatest kid in ordinal order (kids are dated, e.g. <c>2026-10</c>).</summary>
    public string SigningKeyId => Keys.Keys.Max(StringComparer.Ordinal) ?? throw new InvalidOperationException("No download grant key is configured.");

    /// <summary><c>Knowledge:DownloadGrantLifetime</c>, 7 days, for <c>public</c> and <c>internal</c>.</summary>
    public TimeSpan Lifetime { get; init; } = TimeSpan.FromDays(7);

    /// <summary>1 hour for <c>sensitive</c> (no <c>sensitive</c> client stores history).</summary>
    public TimeSpan SensitiveLifetime { get; init; } = TimeSpan.FromHours(1);

    /// <summary><c>Knowledge:PublicBaseUrl</c>: the origin the staff network reaches <c>kalla-api</c> at; null gives a relative link.</summary>
    public Uri? PublicBaseUrl { get; init; }
}

/// <summary>What a grant names: ids and the expiry only, no subject, name or title (§4.6).</summary>
public sealed record DownloadGrant(Guid DocumentId, Guid CollectionId, Guid? AssistantId, Guid ClientId, Guid? KeyId, string Level, DateTimeOffset ExpiresAt);

public enum GrantStatus { Valid, Invalid, Expired }

/// <summary>
/// The signed, short-lived download grant (knowledge design §4.6):
/// <c>g1.{kid}.{payload}.{mac}</c>, payload = base64url(JSON <c>{d, c, a?, cl, k?, l, e}</c>),
/// mac = the first 32 characters of base64url(HMAC-SHA256(key[kid], "g1." + kid + "." + payload)). The newest key signs
/// and every configured key verifies, so a key rotates with a one-lifetime overlap. The token check is only the first
/// half: the knowledge role re-runs the access check at the current config version on every use.
/// </summary>
public sealed class DownloadGrants
{
    public const string Prefix = "g1";
    private const int MacChars = 32;
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly DownloadGrantOptions _options;
    private readonly TimeProvider _time;

    public DownloadGrants(DownloadGrantOptions options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Keys.Count == 0)
        {
            throw new InvalidOperationException("Security:DownloadGrantKeys must hold at least one key.");
        }

        foreach (var (kid, key) in options.Keys)
        {
            if (key.Length < 32 || kid.Length == 0 || kid.Contains('.', StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Download grant key '{kid}' must be at least 32 bytes and its id must not contain a dot.");
            }
        }

        _options = options;
        _time = time;
    }

    /// <summary>A grant for <paramref name="level"/>'s lifetime from now.</summary>
    public string Issue(Guid documentId, Guid collectionId, Guid? assistantId, Guid clientId, Guid? keyId, string level)
    {
        var lifetime = level == Classification.Sensitive ? _options.SensitiveLifetime : _options.Lifetime;
        var payload = new Payload(documentId, collectionId, assistantId, clientId, keyId, level, _time.GetUtcNow().Add(lifetime).ToUnixTimeSeconds());
        var encoded = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(payload, Json));
        var kid = _options.SigningKeyId;
        return $"{Prefix}.{kid}.{encoded}.{Mac(_options.Keys[kid], kid, encoded)}";
    }

    /// <summary>
    /// Checks the token's shape, key, MAC (constant time) and expiry; <see cref="GrantStatus.Expired"/> only after a valid
    /// MAC, so an expiry is never reported for a forged token.
    /// </summary>
    public GrantStatus Read(string? token, out DownloadGrant? grant)
    {
        grant = null;
        var parts = token?.Split('.');
        if (parts is not [Prefix, var kid, var encoded, var mac] || !_options.Keys.TryGetValue(kid, out var key) ||
            !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Mac(key, kid, encoded)), Encoding.ASCII.GetBytes(mac)))
        {
            return GrantStatus.Invalid;
        }

        Payload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<Payload>(Base64Url.DecodeFromChars(encoded), Json);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return GrantStatus.Invalid;
        }

        if (payload is null || payload.DocumentId == Guid.Empty || payload.ClientId == Guid.Empty || !Classification.All.Contains(payload.Level))
        {
            return GrantStatus.Invalid;
        }

        var expires = DateTimeOffset.FromUnixTimeSeconds(payload.Expires);
        if (expires <= _time.GetUtcNow())
        {
            return GrantStatus.Expired;
        }

        grant = new DownloadGrant(payload.DocumentId, payload.CollectionId, payload.AssistantId, payload.ClientId, payload.KeyId, payload.Level, expires);
        return GrantStatus.Valid;
    }

    /// <summary>The link of a citation: <c>/v1/documents/{id}/content?grant=…</c>, absolute when <c>Knowledge:PublicBaseUrl</c> is set.</summary>
    public string Link(Guid documentId, string grant)
    {
        var path = $"/v1/documents/{documentId:D}/content?grant={grant}";
        return _options.PublicBaseUrl is { } origin ? new Uri(origin, path).ToString() : path;
    }

    private static string Mac(byte[] key, string kid, string payload)
    {
        Span<byte> hash = stackalloc byte[HMACSHA256.HashSizeInBytes];
        HMACSHA256.HashData(key, Encoding.ASCII.GetBytes($"{Prefix}.{kid}.{payload}"), hash);
        return Base64Url.EncodeToString(hash)[..MacChars];
    }

    private sealed record Payload(
        [property: JsonPropertyName("d")] Guid DocumentId,
        [property: JsonPropertyName("c")] Guid CollectionId,
        [property: JsonPropertyName("a")] Guid? AssistantId,
        [property: JsonPropertyName("cl")] Guid ClientId,
        [property: JsonPropertyName("k")] Guid? KeyId,
        [property: JsonPropertyName("l")] string Level,
        [property: JsonPropertyName("e")] long Expires);
}

/// <summary>Citation links signed as download grants for the caller's client and key (§4.6, §10.1).</summary>
public sealed class GrantDownloadLinks(DownloadGrants grants) : IDownloadLinks
{
    public string? For(RagPrincipal who, Guid documentId, Guid collectionId, Guid? assistantId, string level)
    {
        ArgumentNullException.ThrowIfNull(who);
        return grants.Link(documentId, grants.Issue(documentId, collectionId, assistantId, who.Client.Id, who.Key?.Id, level));
    }
}
