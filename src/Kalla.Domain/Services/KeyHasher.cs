using System.Security.Cryptography;
using System.Text;
using Kalla.Domain.Entities;

namespace Kalla.Domain.Services;

public sealed record GeneratedKey(string PlainText, string Prefix, string Hash, string Kind);

/// <summary>
/// Generates and verifies keys, adapted from the gateway's <c>VirtualKeyHasher</c> (identity design §6.1). Format:
/// <c>kalla-sk-</c> (secret) or <c>kalla-pk-</c> (public widget key) + 43 base62 chars (~256 bits). Stored as
/// HMAC-SHA256(pepper, key) with Källa's own pepper, so a database leak alone cannot authenticate and neither system
/// accepts the other's keys. Keys issued before the rename to Källa (<c>kalla-sk-</c>, <c>kalla-pk-</c>) stay valid: the
/// prefix only labels the kind, and the hash is the whole key's (Decisions "Product names").
/// </summary>
public sealed class KeyHasher
{
    public const string SecretPrefix = "kalla-sk-";
    public const string PublicPrefix = "kalla-pk-";

    /// <summary>The prefixes of keys issued before the rename, still accepted (never issued).</summary>
    public const string LegacySecretPrefix = "ume-rag-sk-";
    public const string LegacyPublicPrefix = "ume-rag-pk-";

    private const string Base62 = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    private const int RandomChars = 43;
    private const int DisplayChars = 4; // the prefix + 4 chars are shown (Prefix column)
    private static readonly System.Buffers.SearchValues<char> Base62Values = System.Buffers.SearchValues.Create(Base62);

    private readonly byte[] _pepper;

    public KeyHasher(byte[] pepper)
    {
        ArgumentNullException.ThrowIfNull(pepper);
        if (pepper.Length < 32)
        {
            throw new ArgumentException("The key pepper must be at least 32 bytes.", nameof(pepper));
        }

        _pepper = pepper;
    }

    public GeneratedKey Generate(string kind = KeyKinds.Secret)
    {
        var prefix = kind switch
        {
            KeyKinds.Secret => SecretPrefix,
            KeyKinds.Public => PublicPrefix,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Key kind must be sk or pk."),
        };
        var chars = new char[RandomChars];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = Base62[RandomNumberGenerator.GetInt32(Base62.Length)];
        }

        var key = prefix + new string(chars);
        return new GeneratedKey(key, key[..(prefix.Length + DisplayChars)], Hash(key), kind);
    }

    public string Hash(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var mac = HMACSHA256.HashData(_pepper, Encoding.UTF8.GetBytes(key));
        return Convert.ToHexStringLower(mac);
    }

    public bool Verify(string presentedKey, string storedHash)
    {
        if (!LooksLikeKey(presentedKey))
        {
            return false;
        }

        var computed = Encoding.ASCII.GetBytes(Hash(presentedKey));
        var stored = Encoding.ASCII.GetBytes(storedHash);
        return CryptographicOperations.FixedTimeEquals(computed, stored);
    }

    /// <summary>The kind its prefix claims (<c>sk</c> or <c>pk</c>), or null when <paramref name="value"/> is not shaped like a key.</summary>
    public static string? KindOf(string? value) =>
        value is null ? null
        : Shaped(value, SecretPrefix) || Shaped(value, LegacySecretPrefix) ? KeyKinds.Secret
        : Shaped(value, PublicPrefix) || Shaped(value, LegacyPublicPrefix) ? KeyKinds.Public
        : null;

    private static bool Shaped(string value, string prefix) =>
        value.Length == prefix.Length + RandomChars && value.StartsWith(prefix, StringComparison.Ordinal) &&
        value.AsSpan(prefix.Length).IndexOfAnyExcept(Base62Values) < 0;

    public static bool LooksLikeKey(string? value) => KindOf(value) is not null;
}
