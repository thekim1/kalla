using Microsoft.EntityFrameworkCore;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Infrastructure.Security;

namespace Ume.Rag.AdminApi;

/// <summary>
/// Open WebUI's sign-in values in rag-admin (identity design §19, "Open WebUI"): read through the shared precedence
/// (<see cref="OpenWebUiSignInReader"/>), shown with their sources, and saved only by setup mode (<see cref="SaveWizardValuesAsync"/>).
/// Whether the deployment runs Open WebUI and its public address are deployment values of rag-admin (<c>OpenWebUi:Enabled</c>,
/// <c>OpenWebUi:PublicUrl</c>).
/// </summary>
public sealed class OpenWebUiSignInSettings(OpenWebUiSignInReader reader, IConfiguration configuration, SettingsSecretProtector secrets)
{
    /// <summary>The audit entry's entity id of a change of Open WebUI's sign-in values.</summary>
    public const string AuditEntityId = "openwebui-sign-in";

    public Task<EffectiveOpenWebUiSignIn> ReadAsync(CancellationToken ct) => reader.ReadAsync(ct);

    /// <summary>The block <c>GET /api/settings/sign-in</c> shows: no secret, only whether one is set and when.</summary>
    public Task<OpenWebUiSettingsDto> ShowAsync(CancellationToken ct) => ShowAsync(null, ct);

    /// <summary>
    /// The block as <c>GET /bff/setup/state</c> shows it: <paramref name="sessionAuthority"/> is the setup session's latest passed rag-admin
    /// test's authority (<see cref="SetupOpenWebUiTest.SessionAuthorityAsync"/>), the default provider address's source while rag-admin's
    /// effective authority is unset.
    /// </summary>
    public async Task<OpenWebUiSettingsDto> ShowAsync(string? sessionAuthority, CancellationToken ct) =>
        new(OpenWebUiSignIn.Enabled(configuration), OpenWebUiSignInDto.Of(await reader.ReadAsync(sessionAuthority, ct)), OpenWebUiSignIn.RedirectUri(configuration));

    /// <summary>
    /// Saves the wizard's values through the settings write path (version bump, <c>pg_notify('rag_config')</c>, an audit entry with
    /// whether a secret is set and when, never the secret) as <paramref name="actor"/>. <paramref name="clientSecret"/>: null keeps the
    /// stored one, an empty value removes it. <paramref name="alsoApply"/> runs on the same row in the same transaction. Only setup
    /// mode calls it; no admin-session route does.
    /// </summary>
    public async Task SaveWizardValuesAsync(AdminContext admin, AdminActor actor, OpenWebUiOidcValues values, string? clientSecret, string? reason,
        Func<PlatformSettings, CancellationToken, Task>? alsoApply, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(admin);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(values);
        var stored = values.Normalized().ToJson();
        // Protected once, outside the retried write: the ciphertext does not depend on the row.
        var encrypted = string.IsNullOrWhiteSpace(clientSecret) ? null : secrets.Protect(StoredSecret.OpenWebUiOidcClientSecret, clientSecret);
        await admin.WriteAsAsync(actor, async attempt =>
        {
            var row = await admin.Db.Settings.SingleOrDefaultAsync(attempt);
            if (row is null)
            {
                row = new PlatformSettings();
                admin.Db.Settings.Add(row);
            }

            var before = row.Version == 0 ? null : OpenWebUiSignInAudit.Of(row);
            row.OpenWebUiOidc = stored;
            if (clientSecret is not null)
            {
                row.OpenWebUiOidcEncryptedClientSecret = encrypted;
                row.OpenWebUiOidcClientSecretSetAt = encrypted is null ? null : admin.Now;
            }

            row.UpdatedAt = admin.Now;
            row.UpdatedBy = actor.Subject;
            if (alsoApply is not null)
            {
                await alsoApply(row, attempt);
            }

            return new AdminChange<PlatformSettings>(row, "update", AuditEntityTypes.Settings, AuditEntityId, before, OpenWebUiSignInAudit.Of(row)) { Reason = reason };
        }, ct);
    }
}

/// <summary>Open WebUI's sign-in values as the API shows them: each with its source, and of the client secret only whether it is set and when.</summary>
public sealed record OpenWebUiSignInDto(
    OpenWebUiSignInValue ProviderUrl, OpenWebUiSignInValue ClientId, OpenWebUiSignInValue ProviderName, SignInSecretStatus ClientSecret, bool Configured)
{
    public static OpenWebUiSignInDto Of(EffectiveOpenWebUiSignIn e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var wizardSecret = e.ClientSecret.Source == SignInValueSources.Wizard;
        return new(e.ProviderUrl, e.ClientId, e.ProviderName,
            // The expiry date belongs to the secret typed in the wizard, so it is shown only while that secret applies.
            new SignInSecretStatus(e.ClientSecret.IsSet, e.ClientSecret.Source, e.ClientSecret.SetAt, wizardSecret ? e.ClientSecretExpiresOn : null, e.ClientSecret.Unreadable),
            e.Configured);
    }
}

/// <summary>
/// The Open WebUI block: <see cref="Enabled"/> whether the deployment runs it (<c>OpenWebUi:Enabled</c>), its sign-in values, and the
/// redirect URI to register on its client (null while rag-admin has no <c>OpenWebUi:PublicUrl</c>).
/// </summary>
public sealed record OpenWebUiSettingsDto(bool Enabled, OpenWebUiSignInDto SignIn, string? RedirectUri);

/// <summary>A change of Open WebUI's sign-in values in the audit log: the wizard's stored values and of the client secret only whether it is set and when.</summary>
public sealed record OpenWebUiSignInAudit(string? ProviderUrl, string? ClientId, string? ProviderName, SignInCredentialAudit Credential)
{
    public static OpenWebUiSignInAudit Of(PlatformSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var v = OpenWebUiOidcValues.Parse(s.OpenWebUiOidc);
        return new(v.ProviderUrl, v.ClientId, v.ProviderName,
            new SignInCredentialAudit(s.OpenWebUiOidcEncryptedClientSecret is not null, s.OpenWebUiOidcClientSecretSetAt, v.ClientSecretExpiresOn));
    }
}
