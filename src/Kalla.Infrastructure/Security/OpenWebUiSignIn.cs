using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Kalla.Infrastructure.Persistence;

namespace Kalla.Infrastructure.Security;

/// <summary>Where an effective sign-in value comes from (identity design §19, "Effective sign-in values"), kalla-admin's and Open WebUI's alike.</summary>
public static class SignInSources
{
    /// <summary>The deployment's configuration (<c>Oidc:*</c>, <c>OpenWebUi:Oidc:*</c>); an empty value is unset.</summary>
    public const string Deployment = "deployment";

    /// <summary>The settings row, set in the setup wizard.</summary>
    public const string Wizard = "wizard";

    /// <summary>The code's default.</summary>
    public const string Default = "default";
}

/// <summary>
/// <c>Settings.OpenWebUi.Oidc</c> as stored (<c>config."Settings"."OpenWebUiOidc"</c>, identity design §19): null is "not set in the
/// wizard". The client secret is stored apart, encrypted (<see cref="StoredSecret.OpenWebUiOidcClientSecret"/>).
/// </summary>
public sealed record OpenWebUiOidcValues
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>The provider's discovery document (Open WebUI's <c>OPENID_PROVIDER_URL</c>), not its authority.</summary>
    public string? ProviderUrl { get; init; }

    public string? ClientId { get; init; }

    /// <summary>The sign-in button's label (<c>OAUTH_PROVIDER_NAME</c>); the one value a platform admin may change outside setup.</summary>
    public string? ProviderName { get; init; }

    /// <summary>When the wizard's client secret expires, as the person entered it; a reminder, nothing enforces it.</summary>
    public DateOnly? ClientSecretExpiresOn { get; init; }

    public static OpenWebUiOidcValues Parse(string? json) =>
        string.IsNullOrWhiteSpace(json) ? new() : JsonSerializer.Deserialize<OpenWebUiOidcValues>(json, Json) ?? new();

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Trimmed, with empty strings as not set, so "set in the wizard" means a value.</summary>
    public OpenWebUiOidcValues Normalized()
    {
        static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        return this with { ProviderUrl = Clean(ProviderUrl), ClientId = Clean(ClientId), ProviderName = Clean(ProviderName) };
    }
}

/// <summary>One effective Open WebUI sign-in value and its source (<see cref="SignInSources"/>).</summary>
public sealed record OpenWebUiSignInValue(string? Value, string Source);

/// <summary>
/// Open WebUI's effective client secret, for server-side callers only: <see cref="ToString"/> leaves the value out.
/// <see cref="Unreadable"/>: the wizard stored one that the key ring cannot unprotect.
/// </summary>
public sealed class OpenWebUiClientSecret(string source, string? value, DateTimeOffset? setAt, bool unreadable)
{
    public string Source { get; } = source;

    [JsonIgnore]
    public string? Value { get; } = value;

    /// <summary>When the wizard stored it; null for a deployment value.</summary>
    public DateTimeOffset? SetAt { get; } = setAt;

    public bool Unreadable { get; } = unreadable;

    public bool IsSet => Value is not null || Unreadable;

    public override string ToString() => $"{Source}, set: {IsSet}, unreadable: {Unreadable}";
}

/// <summary>
/// Open WebUI's sign-in values as they apply now (identity design §19, "Open WebUI"). <see cref="ProviderUrlFromDeployment"/>: the
/// provider address is the deployment's, or the default derived from the deployment's own <c>Oidc:Authority</c>; only then may a
/// deployment client secret be sent to it in a test.
/// </summary>
public sealed record EffectiveOpenWebUiSignIn(
    OpenWebUiSignInValue ProviderUrl, OpenWebUiSignInValue ClientId, OpenWebUiSignInValue ProviderName, OpenWebUiClientSecret ClientSecret,
    DateOnly? ClientSecretExpiresOn, bool ProviderUrlFromDeployment)
{
    /// <summary>
    /// Whether Open WebUI can offer OIDC sign-in with these values: a provider address is known (set, or kalla-admin's effective authority
    /// to derive it from) and no stored secret is unreadable. The client id always has a value (its default), and a public client has
    /// no secret, so neither decides.
    /// </summary>
    public bool Configured => ProviderUrl.Value is not null && !ClientSecret.Unreadable;
}

/// <summary>
/// Open WebUI's sign-in values (identity design §19, "Open WebUI"): each from the deployment (<c>OpenWebUi:Oidc:*</c> in the raw
/// configuration, an empty value is unset), else from <c>Settings.OpenWebUi.Oidc.*</c>, else the default. The provider address
/// defaults to kalla-admin's effective authority (the deployment's <c>Oidc:Authority</c>, else the wizard's) and its discovery
/// document. kalla-admin shows and saves them; kalla-api serves them to the deployment's own Open WebUI client.
/// </summary>
public static class OpenWebUiSignIn
{
    public const string SectionName = "OpenWebUi:Oidc";
    public const string DefaultClientId = "open-webui";
    public const string DefaultProviderName = "Umeå kommun";

    /// <summary>kalla-admin's deployment authority, which the default provider address derives from (kalla-api gets it too).</summary>
    public const string AdminAuthorityKey = "Oidc:Authority";

    /// <summary>kalla-admin: whether the deployment runs Open WebUI (the openwebui profile, or the Compose project's address); default false.</summary>
    public const string EnabledKey = "OpenWebUi:Enabled";

    /// <summary>kalla-admin: Open WebUI's public address (its <c>WEBUI_URL</c>), for the redirect URI the wizard names.</summary>
    public const string PublicUrlKey = "OpenWebUi:PublicUrl";

    /// <summary>Open WebUI's OIDC callback path (<c>OPENID_REDIRECT_URI</c>, identity design §5.3).</summary>
    public const string CallbackPath = "/oauth/oidc/login/callback";

    public const string ProviderUrlKey = nameof(OpenWebUiOidcValues.ProviderUrl);
    public const string ClientIdKey = nameof(OpenWebUiOidcValues.ClientId);
    public const string ClientSecretKey = "ClientSecret";
    public const string ProviderNameKey = nameof(OpenWebUiOidcValues.ProviderName);

    /// <summary>
    /// The precedence rule on its own. <paramref name="wizardAdminAuthority"/> is kalla-admin's authority from the wizard
    /// (<see cref="AuthorityOf"/>). <paramref name="storedSecret"/> is asked only when the deployment sets no client secret.
    /// <paramref name="sessionAuthority"/>, only inside a setup session: the authority of the session's latest passed kalla-admin test,
    /// which the default provider address derives from while kalla-admin's effective authority is unset (before Finish saves it).
    /// </summary>
    public static EffectiveOpenWebUiSignIn Resolve(IConfiguration configuration, OpenWebUiOidcValues wizard, string? wizardAdminAuthority,
        Func<StoredSecretValue> storedSecret, DateTimeOffset? secretSetAt, string? sessionAuthority = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(wizard);
        ArgumentNullException.ThrowIfNull(storedSecret);
        var section = configuration.GetSection(SectionName);
        var stored = wizard.Normalized();

        OpenWebUiSignInValue Text(string key, string? fromWizard, string? fallback) =>
            Deployed(section, key) is { } deployed ? new(deployed, SignInSources.Deployment)
            : fromWizard is not null ? new(fromWizard, SignInSources.Wizard)
            : new(fallback, SignInSources.Default);

        var deployedAuthority = Blank(configuration[AdminAuthorityKey]);
        var adminAuthority = deployedAuthority ?? Blank(wizardAdminAuthority) ?? Blank(sessionAuthority);
        var providerUrl = Text(ProviderUrlKey, stored.ProviderUrl, adminAuthority is null ? null : DiscoveryAddress(adminAuthority));
        var fromDeployment = providerUrl.Source == SignInSources.Deployment || (providerUrl.Source == SignInSources.Default && deployedAuthority is not null);
        return new EffectiveOpenWebUiSignIn(providerUrl, Text(ClientIdKey, stored.ClientId, DefaultClientId),
            Text(ProviderNameKey, stored.ProviderName, DefaultProviderName), Secret(section, storedSecret, secretSetAt), stored.ClientSecretExpiresOn, fromDeployment);
    }

    /// <summary>The discovery document's address of <paramref name="authority"/>, as the OpenID Connect handler derives it.</summary>
    public static string DiscoveryAddress(string authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        return authority.EndsWith('/') ? authority + ".well-known/openid-configuration" : authority + "/.well-known/openid-configuration";
    }

    /// <summary>Whether the deployment's raw configuration sets <c>OpenWebUi:Oidc:<paramref name="key"/></c>.</summary>
    public static bool Deploys(IConfiguration configuration, string key)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return Deployed(configuration.GetSection(SectionName), key) is not null;
    }

    /// <summary><paramref name="values"/> without what the deployment sets: a deployment value wins, so setup stores none of those.</summary>
    public static OpenWebUiOidcValues WithoutDeployed(IConfiguration configuration, OpenWebUiOidcValues values)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(values);
        var section = configuration.GetSection(SectionName);
        string? Keep(string key, string? value) => Deployed(section, key) is null ? value : null;
        var v = values.Normalized();
        return v with
        {
            ProviderUrl = Keep(ProviderUrlKey, v.ProviderUrl), ClientId = Keep(ClientIdKey, v.ClientId), ProviderName = Keep(ProviderNameKey, v.ProviderName),
            // The expiry date belongs to a secret typed in the wizard.
            ClientSecretExpiresOn = Deployed(section, ClientSecretKey) is null ? v.ClientSecretExpiresOn : null,
        };
    }

    /// <summary>kalla-admin's authority as the wizard stored it (<c>Settings.Identity.Oidc</c>'s <c>authority</c>), or null.</summary>
    public static string? AuthorityOf(string? identityOidcJson)
    {
        if (string.IsNullOrWhiteSpace(identityOidcJson))
        {
            return null;
        }

        using var json = JsonDocument.Parse(identityOidcJson);
        return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("authority", out var authority) &&
               authority.ValueKind == JsonValueKind.String ? Blank(authority.GetString()) : null;
    }

    /// <summary>kalla-admin: whether the deployment runs Open WebUI (<see cref="EnabledKey"/>); anything but <c>true</c> is false.</summary>
    public static bool Enabled(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return bool.TryParse(configuration[EnabledKey], out var enabled) && enabled;
    }

    /// <summary>The redirect URI to register on Open WebUI's client: its public address and <see cref="CallbackPath"/>; null without an absolute http(s) address.</summary>
    public static string? RedirectUri(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return Uri.TryCreate(configuration[PublicUrlKey]?.Trim(), UriKind.Absolute, out var url) && (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp)
            ? url.GetLeftPart(UriPartial.Path).TrimEnd('/') + CallbackPath
            : null;
    }

    /// <summary>
    /// An opaque version of what kalla-api answers Open WebUI (<paramref name="answered"/>, null while not configured): HMAC-SHA-256 under
    /// <paramref name="key"/> (kalla-api's <c>Security:CacheKeyPepper</c>) over every value, the secret included, so a change of any
    /// one changes it and it cannot be used to test guesses of the secret without the key. 128 bits, hex.
    /// </summary>
    public static string Version(EffectiveOpenWebUiSignIn? answered, ReadOnlySpan<byte> key)
    {
        var text = new StringBuilder("kalla/openwebui-sign-in/v1\n");
        string?[] parts = answered is null
            ? ["0"]
            : ["1", answered.ProviderUrl.Value, answered.ClientId.Value, answered.ProviderName.Value, answered.ClientSecret.Value];
        foreach (var part in parts)
        {
            // Length-prefixed, so no two different value lists give the same text.
            text.Append(part is null ? "-" : part.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(part).Append('\n');
        }

        return Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(text.ToString())).AsSpan(0, 16));
    }

    private static OpenWebUiClientSecret Secret(IConfigurationSection section, Func<StoredSecretValue> storedSecret, DateTimeOffset? setAt)
    {
        if (Deployed(section, ClientSecretKey) is { } deployed)
        {
            return new(SignInSources.Deployment, deployed, null, unreadable: false);
        }

        var stored = storedSecret();
        return stored.State switch
        {
            StoredSecretState.Readable => new(SignInSources.Wizard, stored.Value, setAt, unreadable: false),
            StoredSecretState.Unreadable => new(SignInSources.Wizard, null, setAt, unreadable: true),
            _ => new(SignInSources.Default, null, null, unreadable: false),
        };
    }

    private static string? Deployed(IConfigurationSection section, string key) => Blank(section[key]);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Reads Open WebUI's effective sign-in values: one read of the settings row, configuration at each call (a reloaded secret file applies).</summary>
public sealed class OpenWebUiSignInReader(RagDbContext db, IConfiguration configuration, SettingsSecretProtector secrets)
{
    public Task<EffectiveOpenWebUiSignIn> ReadAsync(CancellationToken ct) => ReadAsync(null, ct);

    /// <summary>As <see cref="ReadAsync(CancellationToken)"/>, inside a setup session with its <paramref name="sessionAuthority"/> (<see cref="OpenWebUiSignIn.Resolve"/>).</summary>
    public async Task<EffectiveOpenWebUiSignIn> ReadAsync(string? sessionAuthority, CancellationToken ct)
    {
        var row = await db.Settings.AsNoTracking()
            .Select(s => new { s.OpenWebUiOidc, s.OpenWebUiOidcEncryptedClientSecret, s.OpenWebUiOidcClientSecretSetAt, s.IdentityOidc })
            .SingleOrDefaultAsync(ct);
        return OpenWebUiSignIn.Resolve(configuration, OpenWebUiOidcValues.Parse(row?.OpenWebUiOidc), OpenWebUiSignIn.AuthorityOf(row?.IdentityOidc),
            () => secrets.Unprotect(StoredSecret.OpenWebUiOidcClientSecret, row?.OpenWebUiOidcEncryptedClientSecret), row?.OpenWebUiOidcClientSecretSetAt,
            sessionAuthority);
    }
}
