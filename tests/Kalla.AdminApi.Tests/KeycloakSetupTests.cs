using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Kalla.Domain.Admin;
using Kalla.TestKit;

namespace Kalla.AdminApi.Tests;

/// <summary>
/// M-20 S3 (identity design §19): the setup test sign-in's round trip at the bundled realm, completion, and a real sign-in with the
/// saved values afterwards. In <see cref="KeycloakSignInTests"/>'s class so it shares the realm container and its login helpers.
/// </summary>
public sealed partial class KeycloakSignInTests
{
    [Fact]
    public async Task Setup_tests_at_keycloak_saves_only_a_platform_admins_test_and_a_real_sign_in_then_uses_the_saved_values()
    {
        var connection = keycloak.Database.Connection(RagDatabase.Admin);
        await SetupHosts.ResetAsync(connection, completed: false);
        using var logs = new CapturingLoggerProvider();
        try
        {
            await using var factory = SetupHosts.Host(keycloak.Factory(), logs: logs);
            using var session = await SetupHosts.SessionAsync(factory, connection, "https://localhost:5174");
            var code = await SetupHosts.CodeAsync(connection);
            var values = new { authority = keycloak.Authority, clientSecret = keycloak.OidcClientSecret };

            // A person who is not a platform admin passes the test but cannot complete.
            var (viewerTest, viewerCallback) = await SetupTestAtKeycloakAsync(factory, session, values, "rag-viewer");
            var viewer = await LatestSetupTestAsync(session);
            viewer.GetProperty("status").GetString().ShouldBe("passed");
            viewer.GetProperty("result").GetProperty("isPlatformAdmin").GetBoolean().ShouldBeFalse();
            Strings(viewer.GetProperty("result").GetProperty("roles")).ShouldContain(RagRoles.Viewer);
            (await SetupRefusalAsync(await CompleteSetupAsync(session, keycloak.Authority, viewerTest))).ShouldBe("not_platform_admin");

            // The same callback again is answered alike, without a second result.
            using (var replay = await CallbackAsync(session, viewerCallback))
            {
                replay.Headers.Location!.OriginalString.ShouldBe(SetupTestSignIn.DonePath);
            }

            var (adminTest, _) = await SetupTestAtKeycloakAsync(factory, session, values, "rag-platform-admin");
            var passed = await LatestSetupTestAsync(session);
            passed.GetProperty("status").GetString().ShouldBe("passed");
            var result = passed.GetProperty("result");
            result.GetProperty("isPlatformAdmin").GetBoolean().ShouldBeTrue();
            Strings(result.GetProperty("roles")).ShouldContain(RagRoles.PlatformAdmin);
            Strings(result.GetProperty("departmentCodes")).ShouldBe(["1000", "3000", "5000"], ignoreOrder: true);

            (await SetupRefusalAsync(await CompleteSetupAsync(session, keycloak.Authority, adminTest, roleClaim: "realm_roles"))).ShouldBe("values_changed");
            using (var completed = await CompleteSetupAsync(session, keycloak.Authority, adminTest))
            {
                completed.StatusCode.ShouldBe(HttpStatusCode.OK, await completed.Content.ReadAsStringAsync(Ct));
            }

            var row = (await SetupHosts.RowAsync(connection))!;
            row.SetupCompletedAt.ShouldNotBeNull();
            row.IdentityOidcEncryptedClientSecret.ShouldNotBeNullOrEmpty();
            row.IdentityOidcEncryptedClientSecret.ShouldNotContain(keycloak.OidcClientSecret);
            (await session.GetAsync("/bff/setup/state", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

            // A real sign-in at /bff/login now takes the saved values: no deployment authority or secret is set.
            using (var signedIn = AdminClient(factory))
            {
                await SignInAsync(signedIn, "rag-platform-admin");
                using var user = JsonDocument.Parse(await signedIn.GetStringAsync("/bff/user", Ct));
                Strings(user.RootElement.GetProperty("roles")).ShouldBe([RagRoles.PlatformAdmin]);
                user.RootElement.GetProperty("setup").GetProperty("open").GetBoolean().ShouldBeFalse();
            }

            logs.Text.ShouldNotContain(keycloak.OidcClientSecret);
            foreach (var form in SetupHosts.Forms(code))
            {
                logs.Text.ShouldNotContain(form);
            }
        }
        finally
        {
            await SetupHosts.ResetAsync(connection, completed: true);
        }
    }

    /// <summary>
    /// M-20 S5: the Open WebUI test's verdict on the client at a real Keycloak (the realm's <c>open-webui-dev</c>). A token request with
    /// a made-up code answers 400 <c>invalid_grant</c> for the right secret, 401 <c>unauthorized_client</c> for a wrong secret (Keycloak
    /// 26.8's code, not RFC 6749's <c>invalid_client</c>) and 401 <c>invalid_client</c> for an unknown client: the test refuses on a 401.
    /// As on a first run, kalla-admin's authority is only tested, not saved: the provider address is left empty and follows the session's
    /// passed kalla-admin test.
    /// </summary>
    [Fact]
    public async Task Open_WebUIs_setup_test_at_keycloak_accepts_the_right_secret_and_refuses_a_wrong_one()
    {
        var connection = keycloak.Database.Connection(RagDatabase.Admin);
        await SetupHosts.ResetAsync(connection, completed: false);
        try
        {
            await using var factory = SetupHosts.Host(keycloak.Factory(), settings: (Kalla.Infrastructure.Security.OpenWebUiSignIn.PublicUrlKey, "http://localhost:8080"));
            using var session = await SetupHosts.SessionAsync(factory, connection, "https://localhost:5174");
            await SetupTestAtKeycloakAsync(factory, session, new { authority = keycloak.Authority, clientSecret = keycloak.OidcClientSecret }, "rag-platform-admin");
            (await LatestSetupTestAsync(session)).GetProperty("status").GetString().ShouldBe("passed");
            async Task<JsonElement> TestAsync(string clientId, string secret)
            {
                using var response = await SetupHosts.PostAsync(session, "/bff/setup/test",
                    new { target = "open-webui", providerUrl = (string?)null, clientId, clientSecret = secret });
                var text = await response.Content.ReadAsStringAsync(Ct);
                response.StatusCode.ShouldBe(HttpStatusCode.OK, text);
                text.ShouldNotContain(secret);
                return JsonDocument.Parse(text).RootElement.Clone();
            }

            var right = await TestAsync("open-webui-dev", keycloak.OwuiClientSecret);
            right.GetProperty("status").GetString().ShouldBe("passed");
            right.GetProperty("result").GetProperty("clientAccepted").GetBoolean().ShouldBeTrue();
            right.GetProperty("values").GetProperty("providerUrl").ValueKind.ShouldBe(JsonValueKind.Null);
            foreach (var (clientId, secret) in new[] { ("open-webui-dev", "wrong-" + keycloak.OwuiClientSecret), ("no-such-client", keycloak.OwuiClientSecret) })
            {
                var wrong = await TestAsync(clientId, secret);
                (wrong.GetProperty("status").GetString(), wrong.GetProperty("reason").GetString()).ShouldBe(("failed", SetupTestReasons.ClientRefused), clientId);
                wrong.GetProperty("result").GetProperty("clientAccepted").GetBoolean().ShouldBeFalse();
            }
        }
        finally
        {
            await SetupHosts.ResetAsync(connection, completed: true);
        }
    }

    /// <summary>
    /// Starts a setup test, signs in at the realm as <paramref name="user"/> and sends the answer back from the same client, which holds
    /// the test cookie as the browser that started the test does; returns the test and the callback.
    /// </summary>
    private async Task<(Guid TestId, (string Action, Dictionary<string, string> Fields) Callback)> SetupTestAtKeycloakAsync(WebApplicationFactory<Program> factory,
        HttpClient session, object values, string user)
    {
        using var started = await SetupHosts.PostAsync(session, "/bff/setup/test", values);
        var text = await started.Content.ReadAsStringAsync(Ct);
        started.StatusCode.ShouldBe(HttpStatusCode.OK, text);
        using var json = JsonDocument.Parse(text);
        var callback = await LoginAtKeycloakAsync(json.RootElement.GetProperty("authorizeUrl").GetString()!, user);
        using var done = await CallbackAsync(session, callback);
        done.StatusCode.ShouldBe(HttpStatusCode.Redirect, await done.Content.ReadAsStringAsync(Ct));
        done.Headers.Location!.OriginalString.ShouldBe(SetupTestSignIn.DonePath);
        return (json.RootElement.GetProperty("testId").GetGuid(), callback);
    }

    private static async Task<JsonElement> LatestSetupTestAsync(HttpClient session)
    {
        using var json = JsonDocument.Parse(await session.GetStringAsync("/bff/setup/state", Ct));
        return json.RootElement.GetProperty("latestTest").Clone();
    }

    private static Task<HttpResponseMessage> CompleteSetupAsync(HttpClient session, string authority, Guid testId, string? roleClaim = null) =>
        SetupHosts.PostAsync(session, "/bff/setup/complete", new { authority, roleClaim, testId });

    private static async Task<string?> SetupRefusalAsync(HttpResponseMessage response)
    {
        using (response)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            return json.RootElement.GetProperty("params").GetProperty("reason").GetString();
        }
    }
}
