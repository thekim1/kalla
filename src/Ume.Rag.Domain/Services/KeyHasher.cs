using System.Security.Cryptography;
using System.Text;
using Ume.Rag.Domain.Entities;

namespace Ume.Rag.Domain.Services;

public sealed record GeneratedKey(string PlainText, string Prefix, string Hash, string Kind);

/// <summary>
/// Generates and verifies keys, adapted from the gateway's <c>VirtualKeyHasher</c> (identity design §6.1). Format:
/// <c>ume-rag-sk-</c> (secret) or <c>ume-rag-pk-</c> (public widget key) + 43 base62 chars (~256 bits). Stored as
/// HMAC-SHA256(pepper, key) with Ume.Rag's own pepper, so a database leak alone cannot authenticate and neither system
/// accepts the other's keys.
/// </summary>
public sealed class KeyHasher
{
    public const string SecretPrefix = "ume-rag-sk-";
    public const string PublicPrefix = "ume-rag-pk-";
    private const string Base62 = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    private const int RandomChars = 43;
    private const int DisplayPrefixLength = 15; // "ume-rag-sk-" + 4 chars
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
        return new GeneratedKey(key, key[..DisplayPrefixLength], Hash(key), kind);
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
        value is null || value.Length != SecretPrefix.Length + RandomChars || value.AsSpan(SecretPrefix.Length).IndexOfAnyExcept(Base62Values) >= 0
            ? null
            : value.StartsWith(SecretPrefix, StringComparison.Ordinal) ? KeyKinds.Secret
            : value.StartsWith(PublicPrefix, StringComparison.Ordinal) ? KeyKinds.Public
            : null;

    public static bool LooksLikeKey(string? value) => KindOf(value) is not null;
}
