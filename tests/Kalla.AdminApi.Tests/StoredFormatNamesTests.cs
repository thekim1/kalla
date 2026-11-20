using Microsoft.AspNetCore.DataProtection;
using Kalla.Infrastructure;
using Kalla.Infrastructure.Security;

namespace Kalla.AdminApi.Tests;

/// <summary>
/// What an installation from before the rename to Källa has encrypted stays readable (thekim1/fors#116): a value protected
/// under 0.8.0's application name and purposes, written out here, unprotects with the same key ring under the names the
/// services use now.
/// </summary>
public sealed class StoredFormatNamesTests : IDisposable
{
    private readonly DirectoryInfo _ring = Directory.CreateTempSubdirectory("kalla-dp-ring-");

    public void Dispose() => _ring.Delete(recursive: true);

    [Theory]
    [InlineData("Ume.Rag.Settings.Identity.Oidc.ClientSecret", StoredFormatNames.IdentityOidcClientSecretPurpose)]
    [InlineData("Ume.Rag.Settings.OpenWebUi.Oidc.ClientSecret", StoredFormatNames.OpenWebUiOidcClientSecretPurpose)]
    [InlineData("Ume.Rag.Setup.Test", StoredFormatNames.SetupTestPurpose)]
    [InlineData("Ume.Rag.AdminApi.KeySecret.v1", StoredFormatNames.KeySecretPurpose)]
    [InlineData("Ume.Rag.AdminApi.Session.v1", StoredFormatNames.AdminSessionPurpose)]
    public void A_value_protected_before_the_rename_unprotects(string purposeBefore, string purposeNow)
    {
        var before = DataProtectionProvider.Create(_ring, b => b.SetApplicationName("Ume.Rag")).CreateProtector(purposeBefore);
        var stored = before.Protect("a stored secret");

        var now = DataProtectionProvider.Create(_ring, b => b.SetApplicationName(InfrastructureExtensions.DataProtectionApplicationName))
            .CreateProtector(purposeNow);

        now.Unprotect(stored).ShouldBe("a stored secret");
    }

    [Fact]
    public void The_stored_secrets_use_the_kept_purposes()
    {
        StoredSecret.IdentityOidcClientSecret.Purpose.ShouldBe("Ume.Rag.Settings.Identity.Oidc.ClientSecret");
        StoredSecret.OpenWebUiOidcClientSecret.Purpose.ShouldBe("Ume.Rag.Settings.OpenWebUi.Oidc.ClientSecret");
        StoredSecret.SetupTest.Purpose.ShouldBe("Ume.Rag.Setup.Test");
    }
}
