using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Kalla.Domain.Entities;
using Kalla.Infrastructure.Persistence;
using Kalla.Infrastructure.Security;

namespace Kalla.AdminApi;

/// <summary>Where an effective sign-in value comes from (identity design §19, "Effective sign-in values").</summary>
public static class SignInValueSources
{
    /// <summary>The deployment's <c>Oidc:*</c> in the raw configuration.</summary>
    public const string Deployment = SignInSources.Deployment;

    /// <summary><c>Settings.Identity.Oidc.*</c>, set in the setup wizard.</summary>
    public const string Wizard = SignInSources.Wizard;

    /// <summary>The code's default (<see cref="OidcOptions"/>).</summary>
    public const string Default = SignInSources.Default;
}

/// <summary>
/// <c>Settings.Identity.Oidc</c> as stored (<c>config."Settings"."IdentityOidc"</c>, identity design §19): null is "not set in
/// the wizard". The client secret is stored apart, encrypted (<see cref="SignInSettings"/>).
/// </summary>
public sealed record OidcWizardValues
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public string? Authority { get; init; }
    public string? ClientId { get; init; }
    public string? RoleClaim { get; init; }
    public string? DepartmentClaim { get; init; }
    public string? TeamClaim { get; init; }
    public string? GroupClaim { get; init; }
    public IReadOnlyList<string>? ExtraScopes { get; init; }
    public string? TenantId { get; init; }
    public bool? RefuseGuests { get; init; }

    /// <summary>When the wizard's client secret expires, as the person entered it; a reminder, nothing enforces it.</summary>
    public DateOnly? ClientSecretExpiresOn { get; init; }

    public static OidcWizardValues Parse(string? json) =>
        string.IsNullOrWhiteSpace(json) ? new() : JsonSerializer.Deserialize<OidcWizardValues>(json, Json) ?? new();

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Trimmed, with empty strings and an empty scope list as not set, so "set in the wizard" means a value.</summary>
    public OidcWizardValues Normalized()
    {
        static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        List<string> scopes = [.. (ExtraScopes ?? []).Select(Clean).OfType<string>().Distinct(StringComparer.Ordinal)];
        return this with
        {
            Authority = Clean(Authority), ClientId = Clean(ClientId), RoleClaim = Clean(RoleClaim), DepartmentClaim = Clean(DepartmentClaim),
            TeamClaim = Clean(TeamClaim), GroupClaim = Clean(GroupClaim), ExtraScopes = scopes.Count == 0 ? null : scopes, TenantId = Clean(TenantId),
        };
    }
}

/// <summary>One effective sign-in value and its source (<see cref="SignInValueSources"/>).</summary>
public sealed record SignInValue<T>(T Value, string Source);

/// <summary>
/// The effective client secret, for server-side callers only: <see cref="ToString"/> leaves the value out, and the API answers
/// <see cref="SignInSecretStatus"/> instead. <see cref="Unreadable"/>: the wizard stored one that the key ring cannot unprotect.
/// </summary>
public sealed class SignInClientSecret(string source, string? value, DateTimeOffset? setAt, bool unreadable)
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
/// rag-admin's sign-in values as they apply now (identity design §19), with the settings <see cref="Version"/> they were read at
/// (<c>0</c> while no settings row exists) and <see cref="SetupCompletedAt"/> from the same row.
/// </summary>
public sealed record EffectiveSignIn(
    SignInValue<string?> Authority, SignInValue<string> ClientId, SignInClientSecret ClientSecret, SignInValue<string> RoleClaim,
    SignInValue<string> DepartmentClaim, SignInValue<string> TeamClaim, SignInValue<string> GroupClaim, SignInValue<IReadOnlyList<string>> ExtraScopes,
    SignInValue<string?> TenantId, SignInValue<bool> RefuseGuests, DateOnly? ClientSecretExpiresOn, uint Version, DateTimeOffset? SetupCompletedAt)
{
    /// <summary>When the current setup opening started, read with the values; null while no opening has a nonce (<see cref="SetupMode.IsOpen(EffectiveSignIn, DateTimeOffset)"/>).</summary>
    public DateTimeOffset? SetupOpenedAt { get; init; }

    /// <summary>Whether the deployment declares the sign-in (<see cref="SignInSettings.DeclaresSignIn"/>): its authority and client secret.</summary>
    public bool DeclaredByDeployment => Authority.Source == SignInValueSources.Deployment && ClientSecret.Source == SignInValueSources.Deployment;
}

/// <summary>
/// rag-admin's sign-in values (identity design §19): each from the deployment (<c>Oidc:*</c> in the raw configuration, where an
/// empty value is unset; the bound options cannot tell a default from a set value), else from <c>Settings.Identity.Oidc.*</c>,
/// else the code's default. Configuration is read at each call, so a reloaded secret file applies; the settings row too, so every
/// replica sees a save at once. <c>Oidc:RoleGroups</c> stays a deployment value outside this (identity design §2.5).
/// </summary>
public sealed class SignInSettings(RagDbContext db, IConfiguration configuration, SettingsSecretProtector secrets)
{
    /// <summary>The audit entry's entity id of a sign-in settings change, as a settings section's name.</summary>
    public const string AuditEntityId = "sign-in";

    private static readonly OidcOptions Defaults = new();

    public async Task<EffectiveSignIn> ReadAsync(CancellationToken ct)
    {
        var row = await db.Settings.AsNoTracking()
            .Select(s => new
            {
                s.IdentityOidc, s.IdentityOidcEncryptedClientSecret, s.IdentityOidcClientSecretSetAt, s.SetupCompletedAt, s.Version,
                SetupOpenedAt = s.SetupNonce == null ? null : s.SetupOpenedAt,
            })
            .SingleOrDefaultAsync(ct);
        var wizard = OidcWizardValues.Parse(row?.IdentityOidc);
        return Resolve(configuration, wizard, () => secrets.Unprotect(StoredSecret.IdentityOidcClientSecret, row?.IdentityOidcEncryptedClientSecret),
            row?.IdentityOidcClientSecretSetAt, row?.Version ?? 0, row?.SetupCompletedAt) with { SetupOpenedAt = row?.SetupOpenedAt };
    }

    /// <summary>
    /// Saves the wizard's values through the settings write path (version bump, <c>pg_notify('rag_config')</c>, an audit entry that
    /// holds whether a secret is set and when, never the secret) as <paramref name="actor"/>. <paramref name="clientSecret"/>: null
    /// keeps the stored one, an empty or blank value removes it. <paramref name="alsoApply"/> changes the same row in the same
    /// transaction (setup completion closes setup, identity design §19). Only setup mode calls it; no admin-session route does.
    /// </summary>
    public async Task<EffectiveSignIn> SaveWizardValuesAsync(AdminContext admin, AdminActor actor, OidcWizardValues values, string? clientSecret, string? reason,
        Func<PlatformSettings, CancellationToken, Task>? alsoApply, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(admin);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(values);
        var stored = values.Normalized().ToJson();
        // Protected once, outside the retried write: the ciphertext does not depend on the row.
        var encrypted = string.IsNullOrWhiteSpace(clientSecret) ? null : secrets.Protect(StoredSecret.IdentityOidcClientSecret, clientSecret);
        await admin.WriteAsAsync(actor, async attempt =>
        {
            var row = await admin.Db.Settings.SingleOrDefaultAsync(attempt);
            if (row is null)
            {
                row = new PlatformSettings();
                admin.Db.Settings.Add(row);
            }

            var before = row.Version == 0 ? null : SignInAudit.Of(row);
            row.IdentityOidc = stored;
            if (clientSecret is not null)
            {
                row.IdentityOidcEncryptedClientSecret = encrypted;
                row.IdentityOidcClientSecretSetAt = encrypted is null ? null : admin.Now;
            }

            row.UpdatedAt = admin.Now;
            row.UpdatedBy = actor.Subject;
            if (alsoApply is not null)
            {
                await alsoApply(row, attempt);
            }

            return new AdminChange<PlatformSettings>(row, "update", AuditEntityTypes.Settings, AuditEntityId, before, SignInAudit.Of(row)) { Reason = reason };
        }, ct);
        return await ReadAsync(ct);
    }

    /// <summary>
    /// Whether the deployment declares rag-admin's sign-in: an authority and a client secret in the raw configuration (the client id
    /// has a code default, so it alone cannot say). Such a deployment counts as setup completed (identity design §19, "Setup state").
    /// </summary>
    public static bool DeclaresSignIn(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var oidc = configuration.GetSection(OidcOptions.SectionName);
        return Deployed(oidc, nameof(OidcOptions.Authority)) is not null && Deployed(oidc, nameof(OidcOptions.ClientSecret)) is not null;
    }

    /// <summary>Whether the deployment's raw configuration sets the authority.</summary>
    public static bool DeploysAuthority(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return Deployed(configuration.GetSection(OidcOptions.SectionName), nameof(OidcOptions.Authority)) is not null;
    }

    /// <summary><paramref name="values"/> without what the deployment sets: a deployment value wins, so setup stores none of those.</summary>
    public static OidcWizardValues WithoutDeployed(IConfiguration configuration, OidcWizardValues values)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(values);
        var oidc = configuration.GetSection(OidcOptions.SectionName);
        string? Keep(string key, string? value) => Deployed(oidc, key) is null ? value : null;
        var deployedScopes = oidc.GetSection(nameof(OidcOptions.ExtraScopes)).GetChildren().Any(c => !string.IsNullOrWhiteSpace(c.Value));
        var v = values.Normalized();
        return v with
        {
            Authority = Keep(nameof(OidcOptions.Authority), v.Authority), ClientId = Keep(nameof(OidcOptions.ClientId), v.ClientId),
            RoleClaim = Keep(nameof(OidcOptions.RoleClaim), v.RoleClaim), DepartmentClaim = Keep(nameof(OidcOptions.DepartmentClaim), v.DepartmentClaim),
            TeamClaim = Keep(nameof(OidcOptions.TeamClaim), v.TeamClaim), GroupClaim = Keep(nameof(OidcOptions.GroupClaim), v.GroupClaim),
            ExtraScopes = deployedScopes ? null : v.ExtraScopes, TenantId = Keep(nameof(OidcOptions.TenantId), v.TenantId),
            RefuseGuests = Deployed(oidc, nameof(OidcOptions.RefuseGuests)) is null ? v.RefuseGuests : null,
            // The expiry date belongs to a secret typed in the wizard.
            ClientSecretExpiresOn = Deployed(oidc, nameof(OidcOptions.ClientSecret)) is null ? v.ClientSecretExpiresOn : null,
        };
    }

    /// <summary>
    /// The precedence rule on its own. <paramref name="storedSecret"/> is asked only when the deployment sets no client secret, so a
    /// deployment secret never needs the key ring.
    /// </summary>
    public static EffectiveSignIn Resolve(IConfiguration configuration, OidcWizardValues wizard, Func<StoredSecretValue> storedSecret,
        DateTimeOffset? secretSetAt, uint version, DateTimeOffset? setupCompletedAt)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(wizard);
        ArgumentNullException.ThrowIfNull(storedSecret);
        var oidc = configuration.GetSection(OidcOptions.SectionName);
        var stored = wizard.Normalized();

        SignInValue<string> Text(string key, string? fromWizard, string fallback) =>
            Deployed(oidc, key) is { } deployed ? new(deployed, SignInValueSources.Deployment)
            : fromWizard is not null ? new(fromWizard, SignInValueSources.Wizard)
            : new(fallback, SignInValueSources.Default);

        SignInValue<string?> Optional(string key, string? fromWizard, string? fallback) =>
            Deployed(oidc, key) is { } deployed ? new(deployed, SignInValueSources.Deployment)
            : fromWizard is not null ? new(fromWizard, SignInValueSources.Wizard)
            : new(fallback, SignInValueSources.Default);

        string[] deployedScopes = [.. oidc.GetSection(nameof(OidcOptions.ExtraScopes)).GetChildren().Select(c => c.Value).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!)];
        SignInValue<IReadOnlyList<string>> scopes = deployedScopes.Length > 0 ? new(deployedScopes, SignInValueSources.Deployment)
            : stored.ExtraScopes is { } wizardScopes ? new(wizardScopes, SignInValueSources.Wizard)
            : new(Defaults.ExtraScopes, SignInValueSources.Default);

        // As the options binder reads it: a value that is not a boolean fails here as it fails rag-admin's start.
        SignInValue<bool> refuseGuests = Deployed(oidc, nameof(OidcOptions.RefuseGuests)) is not null
            ? new(oidc.GetValue<bool>(nameof(OidcOptions.RefuseGuests)), SignInValueSources.Deployment)
            : stored.RefuseGuests is { } wizardRefuses ? new(wizardRefuses, SignInValueSources.Wizard)
            : new(Defaults.RefuseGuests, SignInValueSources.Default);

        return new EffectiveSignIn(
            Optional(nameof(OidcOptions.Authority), stored.Authority, Defaults.Authority),
            Text(nameof(OidcOptions.ClientId), stored.ClientId, Defaults.ClientId),
            Secret(oidc, storedSecret, secretSetAt),
            Text(nameof(OidcOptions.RoleClaim), stored.RoleClaim, Defaults.RoleClaim),
            Text(nameof(OidcOptions.DepartmentClaim), stored.DepartmentClaim, Defaults.DepartmentClaim),
            Text(nameof(OidcOptions.TeamClaim), stored.TeamClaim, Defaults.TeamClaim),
            Text(nameof(OidcOptions.GroupClaim), stored.GroupClaim, Defaults.GroupClaim),
            scopes,
            Optional(nameof(OidcOptions.TenantId), stored.TenantId, Defaults.TenantId),
            refuseGuests,
            stored.ClientSecretExpiresOn, version, setupCompletedAt);
    }

    private static SignInClientSecret Secret(IConfigurationSection oidc, Func<StoredSecretValue> storedSecret, DateTimeOffset? setAt)
    {
        if (Deployed(oidc, nameof(OidcOptions.ClientSecret)) is { } deployed)
        {
            return new(SignInValueSources.Deployment, deployed, null, unreadable: false);
        }

        var stored = storedSecret();
        return stored.State switch
        {
            StoredSecretState.Readable => new(SignInValueSources.Wizard, stored.Value, setAt, unreadable: false),
            StoredSecretState.Unreadable => new(SignInValueSources.Wizard, null, setAt, unreadable: true),
            _ => new(SignInValueSources.Default, Defaults.ClientSecret, null, unreadable: false),
        };
    }

    /// <summary>The deployment's value of <c>Oidc:<paramref name="key"/></c>; empty or blank is unset.</summary>
    private static string? Deployed(IConfigurationSection oidc, string key) => oidc[key] is { } value && !string.IsNullOrWhiteSpace(value) ? value : null;
}

/// <summary>
/// <c>GET /api/settings/sign-in</c>: each value with its source, and of the client secret only whether it is set and when;
/// <see cref="OpenWebUi"/> is Open WebUI's block there (in setup's state it is a member of its own, so it is left out here).
/// </summary>
public sealed record SignInSettingsDto(
    SignInValue<string?> Authority, SignInValue<string> ClientId, SignInSecretStatus ClientSecret, SignInValue<string> RoleClaim,
    SignInValue<string> DepartmentClaim, SignInValue<string> TeamClaim, SignInValue<string> GroupClaim, SignInValue<IReadOnlyList<string>> ExtraScopes,
    SignInValue<string?> TenantId, SignInValue<bool> RefuseGuests, string Version)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OpenWebUiSettingsDto? OpenWebUi { get; init; }

    public static SignInSettingsDto Of(EffectiveSignIn e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var wizardSecret = e.ClientSecret.Source == SignInValueSources.Wizard;
        return new(e.Authority, e.ClientId,
            // The expiry date belongs to the secret typed in the wizard, so it is shown only while that secret applies.
            new SignInSecretStatus(e.ClientSecret.IsSet, e.ClientSecret.Source, e.ClientSecret.SetAt, wizardSecret ? e.ClientSecretExpiresOn : null, e.ClientSecret.Unreadable),
            e.RoleClaim, e.DepartmentClaim, e.TeamClaim, e.GroupClaim, e.ExtraScopes, e.TenantId, e.RefuseGuests,
            e.Version.ToString(CultureInfo.InvariantCulture));
    }
}

/// <summary>The client secret as the API shows it: never the value, encrypted or not.</summary>
public sealed record SignInSecretStatus(bool Set, string Source, DateTimeOffset? SetAt, DateOnly? ExpiresOn, bool Unreadable);

/// <summary>A sign-in settings change in the audit log: the wizard's stored values and of the client secret only whether it is set and when.</summary>
public sealed record SignInAudit(
    string? Authority, string? ClientId, string? RoleClaim, string? DepartmentClaim, string? TeamClaim, string? GroupClaim, IReadOnlyList<string>? ExtraScopes,
    string? TenantId, bool? RefuseGuests, SignInCredentialAudit Credential)
{
    public static SignInAudit Of(PlatformSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var v = OidcWizardValues.Parse(s.IdentityOidc);
        return new(v.Authority, v.ClientId, v.RoleClaim, v.DepartmentClaim, v.TeamClaim, v.GroupClaim, v.ExtraScopes, v.TenantId, v.RefuseGuests,
            new SignInCredentialAudit(s.IdentityOidcEncryptedClientSecret is not null, s.IdentityOidcClientSecretSetAt, v.ClientSecretExpiresOn));
    }
}

/// <summary>The client secret in <see cref="SignInAudit"/>: set, when, and the expiry entered with it.</summary>
public sealed record SignInCredentialAudit(bool Set, DateTimeOffset? SetAt, DateOnly? ExpiresOn);
