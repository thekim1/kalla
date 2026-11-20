using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace Kalla.Infrastructure.Security;

/// <summary>
/// A secret a person types in the setup wizard and the settings row keeps encrypted (identity design §19, "Stored secrets"):
/// each has a Data Protection purpose of its own, so one secret's ciphertext never unprotects as another's.
/// </summary>
public sealed class StoredSecret
{
    /// <summary>kalla-admin's client secret, <c>Settings.Identity.Oidc.ClientSecret</c>.</summary>
    public static readonly StoredSecret IdentityOidcClientSecret = new("Kalla.Settings.Identity.Oidc.ClientSecret");

    /// <summary>Open WebUI's client secret, <c>Settings.OpenWebUi.Oidc.ClientSecret</c>: the only stored secret kalla-api unprotects.</summary>
    public static readonly StoredSecret OpenWebUiOidcClientSecret = new("Kalla.Settings.OpenWebUi.Oidc.ClientSecret");

    /// <summary>A setup test sign-in's candidate values, PKCE verifier and nonce (<c>config."SetupTests"."Protected"</c>, identity design §19).</summary>
    public static readonly StoredSecret SetupTest = new("Kalla.Setup.Test");

    private StoredSecret(string purpose) => Purpose = purpose;

    public string Purpose { get; }

    public override string ToString() => Purpose;
}

/// <summary>What unprotecting a stored secret gave.</summary>
public enum StoredSecretState
{
    /// <summary>Nothing is stored.</summary>
    NotSet = 0,
    Readable = 1,

    /// <summary>Stored, but the key ring cannot unprotect it (a missing key, a replaced certificate, a damaged value).</summary>
    Unreadable = 2,
}

/// <summary>A stored secret after unprotecting: its state and, when readable, its value (never in <see cref="ToString"/>).</summary>
public sealed class StoredSecretValue
{
    private StoredSecretValue(StoredSecretState state, string? value)
    {
        State = state;
        Value = value;
    }

    public static StoredSecretValue NotSet { get; } = new(StoredSecretState.NotSet, null);
    public static StoredSecretValue Unreadable { get; } = new(StoredSecretState.Unreadable, null);

    public StoredSecretState State { get; }

    /// <summary>The plaintext when <see cref="State"/> is <see cref="StoredSecretState.Readable"/>.</summary>
    public string? Value { get; }

    public static StoredSecretValue Readable(string value) => new(StoredSecretState.Readable, value);

    public override string ToString() => State.ToString();
}

/// <summary>
/// Protects and unprotects <see cref="StoredSecret"/>s with the shared key ring (<c>config."DataProtectionKeys"</c>, protected
/// by <c>data-protection.pfx</c>), as revealable key secrets are. An unprotect failure is <see cref="StoredSecretState.Unreadable"/>
/// and a warning without the value, never an exception: a replaced certificate must not take the caller down.
/// </summary>
public sealed partial class SettingsSecretProtector(IDataProtectionProvider protection, ILogger<SettingsSecretProtector> logger)
{
    public string Protect(StoredSecret secret, string plaintext)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentException.ThrowIfNullOrEmpty(plaintext);
        return protection.CreateProtector(secret.Purpose).Protect(plaintext);
    }

    public StoredSecretValue Unprotect(StoredSecret secret, string? ciphertext)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (string.IsNullOrEmpty(ciphertext))
        {
            return StoredSecretValue.NotSet;
        }

        try
        {
            return StoredSecretValue.Readable(protection.CreateProtector(secret.Purpose).Unprotect(ciphertext));
        }
        catch (CryptographicException ex)
        {
            // Data Protection reports every unprotect failure (bad payload, missing key, undecryptable ring) as this type.
            LogUnreadable(logger, secret.Purpose, ex.Message);
            return StoredSecretValue.Unreadable;
        }
    }

    [LoggerMessage(LogLevel.Warning, "The stored secret {Purpose} cannot be unprotected and is treated as unreadable: {Reason}")]
    private static partial void LogUnreadable(ILogger logger, string purpose, string reason);
}
