namespace Kalla.Infrastructure.Security;

/// <summary>
/// The names that are part of what Källa has encrypted and stored: the Data Protection application name and purposes,
/// and the associated data of a wrapped blob key. A value decrypts only under the names it was encrypted with, so they
/// keep the product's name from before the rename to Källa (Decisions "Product names"). They are format constants,
/// never shown to anyone. Renaming one makes every value stored under it unreadable: the sign-in client secrets saved
/// in the setup wizard, the stored key secrets and every encrypted upload (thekim1/fors#116).
/// </summary>
public static class StoredFormatNames
{
    /// <summary>The Data Protection application name, part of every protector's purpose chain.</summary>
    public const string DataProtectionApplication = "Ume.Rag";

    /// <summary>kalla-admin's client secret, <c>Settings.Identity.Oidc.ClientSecret</c>.</summary>
    public const string IdentityOidcClientSecretPurpose = "Ume.Rag.Settings.Identity.Oidc.ClientSecret";

    /// <summary>Open WebUI's client secret, <c>Settings.OpenWebUi.Oidc.ClientSecret</c>.</summary>
    public const string OpenWebUiOidcClientSecretPurpose = "Ume.Rag.Settings.OpenWebUi.Oidc.ClientSecret";

    /// <summary>A setup test sign-in's candidate values (<c>config."SetupTests"."Protected"</c>).</summary>
    public const string SetupTestPurpose = "Ume.Rag.Setup.Test";

    /// <summary>A stored key secret, revealed or copied in the admin.</summary>
    public const string KeySecretPurpose = "Ume.Rag.AdminApi.KeySecret.v1";

    /// <summary>An admin session's ticket.</summary>
    public const string AdminSessionPurpose = "Ume.Rag.AdminApi.Session.v1";

    /// <summary>The prefix of a wrapped data key's associated data: <c>{prefix}:{key name}:{key version}</c>.</summary>
    public const string BlobKeyWrapContext = "ume-rag-wrap";
}
