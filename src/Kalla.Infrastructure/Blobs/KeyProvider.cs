using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Kalla.Infrastructure.Blobs;

/// <summary>
/// The key service behind envelope encryption (data-model §7, platform plan §8a): wraps and unwraps data keys with a named
/// master key, which never leaves the provider. The file key provider serves development and the pilot (postponed
/// decision K1); Azure Key Vault and OpenBao come with K1, switched with the <c>rewrap</c> command.
/// </summary>
public interface IKeyProvider
{
    /// <summary>Wraps <paramref name="dataKey"/> with the current version of master key <paramref name="keyName"/>.</summary>
    Task<WrappedKey> WrapAsync(string keyName, ReadOnlyMemory<byte> dataKey, CancellationToken cancellationToken);

    /// <summary>
    /// The data key inside <paramref name="wrapped"/>. Throws <see cref="KeyProviderException"/> when the master key or
    /// its version is not available, or when the wrapped key does not verify under it.
    /// </summary>
    Task<byte[]> UnwrapAsync(WrappedKey wrapped, CancellationToken cancellationToken);
}

/// <summary>A data key wrapped by master key <see cref="KeyName"/> at <see cref="KeyVersion"/>.</summary>
public sealed record WrappedKey(string KeyName, string KeyVersion, byte[] Ciphertext);

/// <summary>A master key is missing or unusable, or a wrapped key does not verify under it.</summary>
public sealed class KeyProviderException(string message, Exception? inner = null) : CryptographicException(message, inner);

/// <summary>Deployment settings of the file key provider (<c>KeyProvider:*</c>, repo-and-deployment §6.3).</summary>
public sealed class KeyProviderOptions
{
    public const string SectionName = "KeyProvider";

    /// <summary>
    /// The directory of the master key files, an absolute path: <c>/run/keys</c>, where the Compose files mount
    /// <c>secrets/keys/rag-blob-internal</c> read-only. Checked when a key is first used, so a service that never touches
    /// an encrypted level starts without it.
    /// </summary>
    [Required]
    public string Directory { get; set; } = "/run/keys";
}

/// <summary>
/// Master keys as files (platform plan §8a, postponed decision K1): <c>{KeyProvider:Directory}/{key name}</c> holds 32
/// random bytes in base64, as <c>init-deployment.sh</c> writes <c>secrets/keys/rag-blob-internal</c> and
/// <c>escrow-keys.sh</c> exports it. A file has no versions, so every key is version <see cref="Version"/>; a rotation
/// comes with K1's key service. Wrapping is AES-256-GCM with a random 96-bit nonce (<c>nonce ‖ ciphertext ‖ tag</c>, 60
/// bytes for a 32-byte data key) and the key name and version as associated data, so a wrapped key verifies only under
/// the key that the blob header names. Key files are read once per process.
/// </summary>
public sealed partial class FileKeyProvider(IOptions<KeyProviderOptions> options) : IKeyProvider
{
    public const string Version = "1";

    private const int MasterKeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly ConcurrentDictionary<string, byte[]> _keys = new(StringComparer.Ordinal);

    public Task<WrappedKey> WrapAsync(string keyName, ReadOnlyMemory<byte> dataKey, CancellationToken cancellationToken)
    {
        if (dataKey.IsEmpty)
        {
            throw new ArgumentException("The data key is empty.", nameof(dataKey));
        }

        var wrapped = new byte[NonceSize + dataKey.Length + TagSize];
        var nonce = wrapped.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(MasterKey(keyName), TagSize);
        aes.Encrypt(nonce, dataKey.Span, wrapped.AsSpan(NonceSize, dataKey.Length), wrapped.AsSpan(NonceSize + dataKey.Length), Context(keyName, Version));
        return Task.FromResult(new WrappedKey(keyName, Version, wrapped));
    }

    public Task<byte[]> UnwrapAsync(WrappedKey wrapped, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(wrapped);
        if (wrapped.KeyVersion != Version)
        {
            throw new KeyProviderException(
                $"Master key '{wrapped.KeyName}' version '{wrapped.KeyVersion}' is not available: the file key provider has version {Version} only.");
        }

        var length = wrapped.Ciphertext.Length - NonceSize - TagSize;
        if (length <= 0)
        {
            throw new KeyProviderException($"The data key wrapped by '{wrapped.KeyName}' is {wrapped.Ciphertext.Length} bytes, too short to unwrap.");
        }

        var dataKey = new byte[length];
        using var aes = new AesGcm(MasterKey(wrapped.KeyName), TagSize);
        try
        {
            var ciphertext = wrapped.Ciphertext.AsSpan();
            aes.Decrypt(ciphertext[..NonceSize], ciphertext.Slice(NonceSize, length), ciphertext[(NonceSize + length)..], dataKey, Context(wrapped.KeyName, wrapped.KeyVersion));
        }
        catch (CryptographicException e)
        {
            throw new KeyProviderException(
                $"The data key could not be unwrapped with master key '{wrapped.KeyName}' version {wrapped.KeyVersion}: the key file is not the key " +
                "that wrapped it, or the wrapped key is damaged.", e);
        }

        return Task.FromResult(dataKey);
    }

    private static byte[] Context(string keyName, string version) => Encoding.UTF8.GetBytes($"ume-rag-wrap:{keyName}:{version}");

    private byte[] MasterKey(string keyName)
    {
        if (!KeyName().IsMatch(keyName))
        {
            throw new KeyProviderException($"'{keyName}' is not a master key name (lower-case a-z, 0-9 and '-', at most 100 characters).");
        }

        // A failed load is not cached: a key file put in place later is found.
        return _keys.GetOrAdd(keyName, Load);
    }

    private byte[] Load(string keyName)
    {
        var directory = options.Value.Directory;
        if (!Path.IsPathFullyQualified(directory))
        {
            throw new KeyProviderException($"{KeyProviderOptions.SectionName}:Directory must be an absolute path, not '{directory}'.");
        }

        var path = Path.Join(directory, keyName);
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException)
        {
            throw new KeyProviderException($"Master key '{keyName}' is not available: {path} cannot be read ({e.GetType().Name}).", e);
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(text.Trim());
        }
        catch (FormatException e)
        {
            throw new KeyProviderException($"Master key file {path} is not base64.", e);
        }

        return key.Length == MasterKeySize ? key
            : throw new KeyProviderException($"Master key file {path} holds {key.Length} bytes; a master key is {MasterKeySize} random bytes in base64.");
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,99}$")]
    private static partial Regex KeyName();
}
