using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Ume.Rag.Domain.Admin;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Infrastructure.Security;
using Ume.Rag.TestKit;
using static Ume.Rag.AdminApi.Tests.SetupHosts;

namespace Ume.Rag.AdminApi.Tests;

/// <summary>
/// M-20 S5 (identity design §19, "Open WebUI"): Open WebUI's sign-in values, their precedence and sources, the setup wizard's
/// back-channel test of them against a provider in the test process (the discovery document at the provider address, the token
/// endpoint's verdict on the client), the secret rules for this target, <c>complete</c> for it (the session's passed
/// platform-admin rag-admin test required, setup left open), and that no admin-session route writes them but the provider name.
/// </summary>
[Collection(AdminDatabaseGroup3.Name)]
public sealed class SetupOpenWebUiTests(AdminDatabaseFixture database) : IAsyncLifetime
{
    private const string TypedSecret = "owui-typed-secret-8e2d41";
    private const string AdminSecret = "admin-typed-secret-0123456789abcdef";
    private const string ProviderUrl = FakeProvider.Authority + "/.well-known/openid-configuration";
    private const string OtherProviderUrl = "https://idp2.test/realms/ume/.well-known/openid-configuration";
    private const string TokenPath = "/realms/ume/token";
    private const string PublicUrl = "https://chat.test";
    private const string RedirectUri = PublicUrl + "/oauth/oidc/login/callback";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Admin => database.Connection(RagDatabase.Admin);

    public async ValueTask InitializeAsync() => await ResetAsync(Admin, completed: false);

    public async ValueTask DisposeAsync() => await ResetAsync(Admin, completed: true);

    private WebApplicationFactory<Program> Host(FakeProvider provider, CapturingLoggerProvider? logs = null, string environment = "Development",
        params (string Key, string Value)[] settings) =>
        SetupHosts.Host(database.Factory().WithWebHostBuilder(web =>
        {
            web.UseEnvironment(environment);
            web.UseSetting(OpenWebUiSignIn.EnabledKey, "true");
            web.UseSetting(OpenWebUiSignIn.PublicUrlKey, PublicUrl);
            web.ConfigureTestServices(services => services.AddSingleton(new SetupProviderClient(provider)));
        }), logs: logs, settings: settings);

    private static object Values(string? clientSecret = TypedSecret, string? providerUrl = ProviderUrl, string? clientId = null, string? providerName = null) =>
        new { target = SetupTargets.OpenWebUi, providerUrl, clientId, clientSecret, providerName };

    private static object CompleteValues(Guid testId, string? providerUrl = ProviderUrl, string? providerName = null) =>
        new { target = SetupTargets.OpenWebUi, providerUrl, providerName, testId };

    private static HttpResponseMessage TokenError(string error, HttpStatusCode status = HttpStatusCode.BadRequest) =>
        FakeProvider.Json(new { error, error_description = FakeProvider.LeakMarker }, status);

    private static IConfiguration Configuration(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    /// <summary>Runs an Open WebUI test; returns its answer (200).</summary>
    private static async Task<JsonElement> TestAsync(HttpClient session, object values)
    {
        using var response = await PostAsync(session, "/bff/setup/test", values);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, text);
        text.ShouldNotContain(TypedSecret);
        text.ShouldNotContain(FakeProvider.LeakMarker);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string?> ReasonOf(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return json.RootElement.TryGetProperty("params", out var p) && p.TryGetProperty("reason", out var reason) ? reason.GetString() : null;
    }

    /// <summary>A refused test: 400 <c>setup_test_refused</c> and its reason.</summary>
    private static async Task<string?> RefusalAsync(HttpClient session, object values)
    {
        using var response = await PostAsync(session, "/bff/setup/test", values);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync(Ct));
        (await response.Content.ReadAsStringAsync(Ct)).ShouldNotContain(FakeProvider.LeakMarker);
        return await ReasonOf(response);
    }

    /// <summary>A rag-admin test sign-in of <paramref name="role"/> in <paramref name="session"/>, through to its callback.</summary>
    private static async Task AdminTestAsync(HttpClient session, FakeProvider provider, string role)
    {
        provider.Claims = FakeProvider.Person("person-1", role);
        using var started = await PostAsync(session, "/bff/setup/test", new { authority = FakeProvider.Authority, clientSecret = AdminSecret, refuseGuests = true });
        var text = await started.Content.ReadAsStringAsync(Ct);
        started.StatusCode.ShouldBe(HttpStatusCode.OK, text);
        var query = HttpUtility.ParseQueryString(new Uri(JsonDocument.Parse(text).RootElement.GetProperty("authorizeUrl").GetString()!).Query);
        provider.Nonce = query["nonce"];
        using var done = await session.PostAsync("/signin-oidc", new FormUrlEncodedContent(new Dictionary<string, string> { ["state"] = query["state"]!, ["code"] = "c1" }), Ct);
        done.StatusCode.ShouldBe(HttpStatusCode.Redirect);
    }

    private static async Task<JsonElement> StateAsync(HttpClient session)
    {
        using var json = JsonDocument.Parse(await session.GetStringAsync("/bff/setup/state", Ct));
        return json.RootElement.GetProperty("openWebUi").Clone();
    }

    /// <summary>The store's write as setup mode calls it, in a request of a platform admin.</summary>
    private static async Task SaveAsync(WebApplicationFactory<Program> factory, OpenWebUiOidcValues values, string? clientSecret)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(AdminClaims.Subject, "setup-person"), new Claim(AdminClaims.Roles, RagRoles.PlatformAdmin)], "test")),
        };
        try
        {
            await scope.ServiceProvider.GetRequiredService<OpenWebUiSignInSettings>()
                .SaveWizardValuesAsync(scope.ServiceProvider.GetRequiredService<AdminContext>(), AdminActor.Of(accessor.HttpContext), values, clientSecret, null, null, Ct);
        }
        finally
        {
            accessor.HttpContext = null;
        }
    }

    [Fact]
    public void The_values_come_from_the_deployment_else_the_wizard_else_the_default_from_rag_admins_authority()
    {
        var wizard = new OpenWebUiOidcValues { ProviderUrl = OtherProviderUrl, ClientId = "wizard-client", ProviderName = "Wizard name" };
        var deployed = OpenWebUiSignIn.Resolve(Configuration(
                ("OpenWebUi:Oidc:ProviderUrl", ProviderUrl), ("OpenWebUi:Oidc:ClientId", " "), ("OpenWebUi:Oidc:ProviderName", "Deployed name"),
                ("OpenWebUi:Oidc:ClientSecret", "deployed-secret")),
            wizard, null, () => throw new InvalidOperationException("The stored secret is not read when the deployment sets one."), null);
        deployed.ProviderUrl.ShouldBe(new OpenWebUiSignInValue(ProviderUrl, SignInSources.Deployment));
        deployed.ClientId.ShouldBe(new OpenWebUiSignInValue("wizard-client", SignInSources.Wizard)); // blank is unset
        deployed.ProviderName.ShouldBe(new OpenWebUiSignInValue("Deployed name", SignInSources.Deployment));
        (deployed.ClientSecret.Source, deployed.ClientSecret.Value).ShouldBe((SignInSources.Deployment, "deployed-secret"));
        deployed.ClientSecret.ToString().ShouldNotContain("deployed-secret");
        (deployed.Configured, deployed.ProviderUrlFromDeployment).ShouldBe((true, true));

        var fromWizard = OpenWebUiSignIn.Resolve(Configuration(), wizard, null, () => StoredSecretValue.Readable(TypedSecret), DateTimeOffset.UnixEpoch);
        fromWizard.ProviderUrl.ShouldBe(new OpenWebUiSignInValue(OtherProviderUrl, SignInSources.Wizard));
        (fromWizard.ClientSecret.Source, fromWizard.ClientSecret.Value, fromWizard.ClientSecret.SetAt).ShouldBe((SignInSources.Wizard, TypedSecret, DateTimeOffset.UnixEpoch));
        fromWizard.ProviderUrlFromDeployment.ShouldBeFalse();

        // The defaults: the provider from rag-admin's effective authority, the deployment's before the wizard's.
        var byDeployedAuthority = OpenWebUiSignIn.Resolve(Configuration(("Oidc:Authority", FakeProvider.Authority + "/")), new OpenWebUiOidcValues(),
            "https://wizard.test/realms/ume", () => StoredSecretValue.NotSet, null);
        byDeployedAuthority.ProviderUrl.ShouldBe(new OpenWebUiSignInValue(ProviderUrl, SignInSources.Default));
        byDeployedAuthority.ClientId.ShouldBe(new OpenWebUiSignInValue(OpenWebUiSignIn.DefaultClientId, SignInSources.Default));
        byDeployedAuthority.ProviderName.ShouldBe(new OpenWebUiSignInValue(OpenWebUiSignIn.DefaultProviderName, SignInSources.Default));
        (byDeployedAuthority.ClientSecret.Source, byDeployedAuthority.ClientSecret.IsSet).ShouldBe((SignInSources.Default, false));
        (byDeployedAuthority.Configured, byDeployedAuthority.ProviderUrlFromDeployment).ShouldBe((true, true)); // a public client

        var byWizardAuthority = OpenWebUiSignIn.Resolve(Configuration(), new OpenWebUiOidcValues(),
            OpenWebUiSignIn.AuthorityOf("""{"authority":"https://wizard.test/realms/ume"}"""), () => StoredSecretValue.NotSet, null);
        byWizardAuthority.ProviderUrl.ShouldBe(new OpenWebUiSignInValue("https://wizard.test/realms/ume/.well-known/openid-configuration", SignInSources.Default));
        byWizardAuthority.ProviderUrlFromDeployment.ShouldBeFalse();

        // No provider address at all, or a stored secret the key ring cannot read: not configured.
        OpenWebUiSignIn.Resolve(Configuration(), new OpenWebUiOidcValues(), null, () => StoredSecretValue.NotSet, null).Configured.ShouldBeFalse();
        var unreadable = OpenWebUiSignIn.Resolve(Configuration(), wizard, null, () => StoredSecretValue.Unreadable, null);
        (unreadable.Configured, unreadable.ClientSecret.Unreadable, unreadable.ClientSecret.Value).ShouldBe((false, true, null));

        // What setup stores: nothing the deployment sets, and an expiry only for a secret typed in the wizard.
        var stored = OpenWebUiSignIn.WithoutDeployed(Configuration(("OpenWebUi:Oidc:ProviderUrl", ProviderUrl), ("OpenWebUi:Oidc:ClientSecret", "s")),
            wizard with { ClientSecretExpiresOn = new DateOnly(2027, 1, 31) });
        (stored.ProviderUrl, stored.ClientId, stored.ProviderName, stored.ClientSecretExpiresOn).ShouldBe((null, "wizard-client", "Wizard name", null));
    }

    [Fact]
    public void Only_invalid_client_refuses_and_only_invalid_grant_confirms_the_client()
    {
        static ProviderResponse Error(string body, int status = 400) => new(ProviderOutcome.Status, body, status);
        SetupOpenWebUiTest.Classify(Error("""{"error":"invalid_client"}""")).ShouldBe((false, SetupTestReasons.ClientRefused));
        // Any 401 is a failed client authentication (RFC 6749 §5.2), whatever the code: Keycloak's wrong secret is unauthorized_client.
        SetupOpenWebUiTest.Classify(Error("""{"error":"unauthorized_client"}""", 401)).ShouldBe((false, SetupTestReasons.ClientRefused));
        SetupOpenWebUiTest.Classify(Error("""{"error":"invalid_grant"}""", 401)).ShouldBe((false, SetupTestReasons.ClientRefused));
        SetupOpenWebUiTest.Classify(new ProviderResponse(ProviderOutcome.Status, null, 401)).ShouldBe((false, SetupTestReasons.ClientRefused));
        SetupOpenWebUiTest.Classify(Error("""{"error":"invalid_grant","error_description":"Code not valid"}""")).ShouldBe((true, null));
        foreach (var body in new[] { """{"error":"unauthorized_client"}""", """{"error":"invalid_request"}""", "<html>bad gateway</html>", "[]", """{"error":7}""" })
        {
            SetupOpenWebUiTest.Classify(Error(body)).ShouldBe((null, SetupTestReasons.ClientUnverified), body);
        }

        SetupOpenWebUiTest.Classify(new ProviderResponse(ProviderOutcome.Status)).ShouldBe((null, SetupTestReasons.ClientUnverified));
        SetupOpenWebUiTest.Classify(new ProviderResponse(ProviderOutcome.Ok, """{"access_token":"x"}""")).ShouldBe((null, SetupTestReasons.ClientUnverified));
        SetupOpenWebUiTest.Classify(new ProviderResponse(ProviderOutcome.TooLarge)).ShouldBe((null, SetupTestReasons.ClientUnverified));
        SetupOpenWebUiTest.Classify(new ProviderResponse(ProviderOutcome.Tls)).ShouldBe((null, SetupTestReasons.Tls));
        SetupOpenWebUiTest.Classify(new ProviderResponse(ProviderOutcome.Redirected)).ShouldBe((null, SetupTestReasons.Redirected));
        SetupOpenWebUiTest.Classify(new ProviderResponse(ProviderOutcome.Unreachable)).ShouldBe((null, SetupTestReasons.TokenEndpointUnreachable));
    }

    [Fact]
    public async Task A_test_reads_the_discovery_document_and_asks_the_token_endpoint_about_the_client_as_Open_WebUI_would()
    {
        using var provider = new FakeProvider();
        provider.Overrides[TokenPath] = () => TokenError("invalid_grant");
        using var logs = new CapturingLoggerProvider();
        await using var host = Host(provider, logs);
        using var session = await SessionAsync(host, Admin);

        var test = await TestAsync(session, Values(providerName: "Chatten"));
        test.GetProperty("target").GetString().ShouldBe(SetupTargets.OpenWebUi);
        test.GetProperty("status").GetString().ShouldBe("passed");
        test.GetProperty("clientSecret").GetString().ShouldBe(SetupSecretSources.Typed);
        test.GetProperty("values").GetProperty("providerName").GetString().ShouldBe("Chatten");
        var result = test.GetProperty("result");
        (result.GetProperty("discovery").GetString(), result.GetProperty("clientAccepted").GetBoolean()).ShouldBe(("ok", true));
        result.GetProperty("clientReason").ValueKind.ShouldBe(JsonValueKind.Null);

        // One token request with a made-up code, Open WebUI's redirect URI, the default client id and the typed secret.
        provider.TokenRequests.ShouldBe(1);
        var form = provider.LastTokenForm!;
        (form["grant_type"], form["client_id"], form["client_secret"], form["redirect_uri"]).ShouldBe(("authorization_code", "open-webui", TypedSecret, RedirectUri));
        form["code"].Length.ShouldBe(43);
        provider.Requested.Select(u => u.AbsolutePath).ShouldBe(["/realms/ume/.well-known/openid-configuration", "/realms/ume/certs", TokenPath]);

        // The wizard's view: Open WebUI runs here, the redirect URI to register, and this test as the latest.
        var state = await StateAsync(session);
        state.GetProperty("enabled").GetBoolean().ShouldBeTrue();
        state.GetProperty("redirectUri").GetString().ShouldBe(RedirectUri);
        state.GetProperty("latestTest").GetProperty("testId").GetGuid().ShouldBe(test.GetProperty("testId").GetGuid());
        state.GetProperty("signIn").GetProperty("clientId").GetProperty("source").GetString().ShouldBe(SignInSources.Default);
        state.GetProperty("signIn").GetProperty("configured").GetBoolean().ShouldBeFalse(); // nothing saved, no authority to derive from

        await using var db = TestPostgres.Context(Admin);
        var row = await db.SetupTests.AsNoTracking().SingleAsync(t => t.Target == SetupTargets.OpenWebUi, Ct);
        row.Protected.ShouldNotContain(TypedSecret);
        row.CallbackAt.ShouldNotBeNull(); // claimed at once: no callback can take it
        Events(logs, 6011).Single().Message.ShouldContain(SetupTargets.OpenWebUi);
        logs.Text.ShouldNotContain(TypedSecret);
        logs.Text.ShouldNotContain(FakeProvider.LeakMarker);
    }

    [Fact]
    public async Task The_token_endpoints_answer_decides_refused_accepted_or_unverified_with_its_reason()
    {
        var cases = new (Func<HttpResponseMessage> Answer, string Status, bool? Accepted, string? Reason)[]
        {
            (() => TokenError("invalid_client", HttpStatusCode.Unauthorized), "failed", false, SetupTestReasons.ClientRefused),
            (() => TokenError("invalid_client"), "failed", false, SetupTestReasons.ClientRefused),
            (() => TokenError("unauthorized_client", HttpStatusCode.Unauthorized), "failed", false, SetupTestReasons.ClientRefused),
            (() => TokenError("invalid_grant"), "passed", true, null),
            (() => TokenError("unauthorized_client"), "passed", null, SetupTestReasons.ClientUnverified),
            (() => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("<html>" + FakeProvider.LeakMarker + "</html>") }, "passed", null,
                SetupTestReasons.ClientUnverified),
            (() => FakeProvider.Json(new { access_token = FakeProvider.LeakMarker }), "passed", null, SetupTestReasons.ClientUnverified),
            (() => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://internal.test/" + FakeProvider.LeakMarker) } }, "passed", null,
                SetupTestReasons.Redirected),
            (() => throw new HttpRequestException(HttpRequestError.SecureConnectionError, FakeProvider.LeakMarker), "passed", null, SetupTestReasons.Tls),
            (() => throw new HttpRequestException(HttpRequestError.ConnectionError, FakeProvider.LeakMarker), "passed", null, SetupTestReasons.TokenEndpointUnreachable),
        };
        using var provider = new FakeProvider();
        await using var host = Host(provider);
        using var session = await SessionAsync(host, Admin);
        foreach (var (answer, status, accepted, reason) in cases)
        {
            provider.Overrides[TokenPath] = answer;
            var test = await TestAsync(session, Values());
            test.GetProperty("status").GetString().ShouldBe(status, reason);
            var result = test.GetProperty("result");
            (result.GetProperty("clientAccepted").ValueKind == JsonValueKind.Null ? null : (bool?)result.GetProperty("clientAccepted").GetBoolean()).ShouldBe(accepted, reason);
            (accepted == false ? test.GetProperty("reason").GetString() : result.GetProperty("clientReason").GetString()).ShouldBe(reason);
        }

        provider.Requested.ShouldNotContain(u => u.Host == "internal.test"); // no redirect followed
    }

    [Fact]
    public async Task The_provider_address_must_be_the_discovery_document_of_its_issuer_and_https_outside_development()
    {
        using var provider = new FakeProvider();
        provider.Overrides[TokenPath] = () => TokenError("invalid_grant");
        await using (var host = Host(provider))
        {
            using var session = await SessionAsync(host, Admin);
            // The document of another realm path names the issuer of /realms/ume: not this address's issuer.
            (await RefusalAsync(session, Values(providerUrl: "https://idp.test/realms/other/.well-known/openid-configuration"))).ShouldBe(SetupTestReasons.IssuerMismatch);
            provider.Overrides["/realms/ume/.well-known/openid-configuration"] = () => FakeProvider.Json(FakeProvider.Discovery(issuer: "https://other.test/" + FakeProvider.LeakMarker));
            (await RefusalAsync(session, Values())).ShouldBe(SetupTestReasons.IssuerMismatch);
            // An issuer with a trailing slash names the same document.
            provider.Overrides["/realms/ume/.well-known/openid-configuration"] = () => FakeProvider.Json(FakeProvider.Discovery(issuer: FakeProvider.Authority + "/"));
            (await TestAsync(session, Values())).GetProperty("status").GetString().ShouldBe("passed");
            provider.Overrides["/realms/ume/.well-known/openid-configuration"] = () =>
                new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://internal.test/x") } };
            (await RefusalAsync(session, Values())).ShouldBe(SetupTestReasons.Redirected);
            provider.Overrides.Remove("/realms/ume/.well-known/openid-configuration");

            var before = provider.Requested.Count;
            (await RefusalAsync(session, Values(providerUrl: ProviderUrl + "?appid=x"))).ShouldBe(SetupTestReasons.ProviderUrlInvalid);
            (await RefusalAsync(session, Values(providerUrl: null))).ShouldBe(SetupTestReasons.ProviderUrlInvalid); // no authority to derive one from
            provider.Requested.Count.ShouldBe(before);
        }

        await using var staging = Host(provider, environment: "Staging");
        using var stagingSession = await SessionAsync(staging, Admin);
        var count = provider.Requested.Count;
        (await RefusalAsync(stagingSession, Values(providerUrl: "http://idp.test/realms/ume/.well-known/openid-configuration"))).ShouldBe(SetupTestReasons.ProviderUrlInvalid);
        provider.Requested.Count.ShouldBe(count);
    }

    [Fact]
    public async Task The_stored_secret_goes_only_to_the_provider_and_client_it_was_stored_for()
    {
        using var provider = new FakeProvider();
        provider.Overrides[TokenPath] = () => TokenError("invalid_grant");
        await using var host = Host(provider);
        using var session = await SessionAsync(host, Admin);
        await SaveAsync(host, new OpenWebUiOidcValues { ProviderUrl = ProviderUrl }, "stored-owui-secret-61c2");

        // No secret typed, and another provider or client: refused before anything is fetched.
        (await RefusalAsync(session, Values(clientSecret: null, providerUrl: OtherProviderUrl))).ShouldBe(SetupTestReasons.SecretRequired);
        (await RefusalAsync(session, Values(clientSecret: null, clientId: "another-client"))).ShouldBe(SetupTestReasons.SecretRequired);
        provider.Requested.ShouldBeEmpty();

        // The same provider and client: the stored secret is used, and never comes back.
        var test = await TestAsync(session, Values(clientSecret: null));
        test.GetProperty("clientSecret").GetString().ShouldBe(SetupSecretSources.Stored);
        provider.LastTokenForm!["client_secret"].ShouldBe("stored-owui-secret-61c2");
        test.GetRawText().ShouldNotContain("stored-owui-secret-61c2");
    }

    [Fact]
    public async Task A_deployment_secret_goes_only_to_the_deployments_provider()
    {
        using var provider = new FakeProvider();
        provider.Overrides[TokenPath] = () => TokenError("invalid_grant");
        await using (var secretOnly = Host(provider, settings: ("OpenWebUi:Oidc:ClientSecret", "deployed-owui-secret")))
        {
            using var session = await SessionAsync(secretOnly, Admin);
            (await RefusalAsync(session, Values())).ShouldBe(SetupTestReasons.DeploymentSecretWithoutAuthority);
            provider.Requested.ShouldBeEmpty();
        }

        // The deployment's provider address, or the default derived from its own authority: the deployment's secret is used.
        foreach (var deployedProvider in new[] { ("OpenWebUi:Oidc:ProviderUrl", ProviderUrl), ("Oidc:Authority", FakeProvider.Authority) })
        {
            await using var declared = Host(provider, settings: [("OpenWebUi:Oidc:ClientSecret", "deployed-owui-secret"), deployedProvider]);
            using var session = await SessionAsync(declared, Admin);
            var test = await TestAsync(session, Values(providerUrl: null));
            test.GetProperty("clientSecret").GetString().ShouldBe(SetupSecretSources.Deployment);
            provider.LastTokenForm!["client_secret"].ShouldBe("deployed-owui-secret");
            test.GetRawText().ShouldNotContain("deployed-owui-secret");
        }
    }

    [Fact]
    public async Task Complete_needs_the_sessions_passed_platform_admin_test_saves_the_values_and_leaves_setup_open()
    {
        using var provider = new FakeProvider();
        using var logs = new CapturingLoggerProvider();
        await using var host = Host(provider, logs);
        using var session = await SessionAsync(host, Admin);
        async Task<HttpResponseMessage> CompleteAsync(Guid testId, string? providerName = "Chatten") =>
            await PostAsync(session, "/bff/setup/complete", CompleteValues(testId, providerName: providerName));

        provider.Overrides[TokenPath] = () => TokenError("invalid_grant");
        var test = (await TestAsync(session, Values(providerName: "Chatten"))).GetProperty("testId").GetGuid();
        // No rag-admin test in this session, then one by a person who is no platform admin: refused.
        using (var refused = await CompleteAsync(test))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await ReasonOf(refused)).ShouldBe("admin_test_required");
        }

        provider.Overrides.Clear();
        await AdminTestAsync(session, provider, RagRoles.Viewer);
        (await ReasonOf(await CompleteAsync(test))).ShouldBe("admin_test_required");
        await AdminTestAsync(session, provider, RagRoles.PlatformAdmin);

        // A client the provider refuses fails its test, so it cannot be saved; the values must be the tested ones.
        provider.Overrides[TokenPath] = () => TokenError("invalid_client", HttpStatusCode.Unauthorized);
        var refusedClient = (await TestAsync(session, Values(providerName: "Chatten"))).GetProperty("testId").GetGuid();
        (await ReasonOf(await CompleteAsync(refusedClient))).ShouldBe("not_passed");
        (await ReasonOf(await CompleteAsync(test))).ShouldBe("not_latest");
        provider.Overrides[TokenPath] = () => TokenError("invalid_grant");
        test = (await TestAsync(session, Values(providerName: "Chatten"))).GetProperty("testId").GetGuid();
        (await ReasonOf(await CompleteAsync(test, providerName: "Other"))).ShouldBe("values_changed");

        var before = (await RowAsync(Admin))!;
        using (var saved = await CompleteAsync(test))
        {
            var text = await saved.Content.ReadAsStringAsync(Ct);
            saved.StatusCode.ShouldBe(HttpStatusCode.OK, text);
            text.ShouldNotContain(TypedSecret);
            var json = JsonDocument.Parse(text).RootElement;
            json.GetProperty("clientVerified").GetBoolean().ShouldBeTrue();
            var signIn = json.GetProperty("openWebUi").GetProperty("signIn");
            signIn.GetProperty("providerUrl").GetProperty("value").GetString().ShouldBe(ProviderUrl);
            signIn.GetProperty("providerUrl").GetProperty("source").GetString().ShouldBe(SignInSources.Wizard);
            signIn.GetProperty("providerName").GetProperty("value").GetString().ShouldBe("Chatten");
            (signIn.GetProperty("clientSecret").GetProperty("set").GetBoolean(), signIn.GetProperty("clientSecret").GetProperty("source").GetString())
                .ShouldBe((true, SignInSources.Wizard));
            signIn.GetProperty("configured").GetBoolean().ShouldBeTrue();
        }

        // Saved, setup still open in the same opening, and the session still works.
        var row = (await RowAsync(Admin))!;
        OpenWebUiOidcValues.Parse(row.OpenWebUiOidc).ShouldBe(new OpenWebUiOidcValues { ProviderUrl = ProviderUrl, ProviderName = "Chatten" });
        row.OpenWebUiOidcEncryptedClientSecret.ShouldNotBeNullOrEmpty();
        row.OpenWebUiOidcEncryptedClientSecret.ShouldNotContain(TypedSecret);
        row.SetupCompletedAt.ShouldBeNull();
        row.SetupNonce.ShouldBe(before.SetupNonce);
        row.IdentityOidc.ShouldBe("{}"); // rag-admin's values are not touched
        (await session.GetAsync("/bff/setup/state", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<OpenWebUiSignInReader>().ReadAsync(Ct)).ClientSecret.Value.ShouldBe(TypedSecret);
        }

        await using (var db = TestPostgres.Context(Admin))
        {
            var audit = await db.AuditLog.AsNoTracking().Where(a => a.EntityId == OpenWebUiSignInSettings.AuditEntityId).OrderByDescending(a => a.Id).FirstAsync(Ct);
            audit.Actor.ShouldBe("person-1");
            audit.SessionId.ShouldStartWith("setup-");
            audit.Details.ShouldNotBeNull().ShouldNotContain(TypedSecret);
            audit.Details.ShouldNotContain(row.OpenWebUiOidcEncryptedClientSecret!);
        }

        Events(logs, 6013).Single().Message.ShouldContain("person-1");
        Events(logs, 6012).ShouldBeEmpty();
        logs.Text.ShouldNotContain(TypedSecret);
    }

    [Fact]
    public async Task In_the_session_the_default_provider_address_follows_the_latest_passed_rag_admin_test_while_rag_admin_has_no_authority()
    {
        using var provider = new FakeProvider();
        await using var host = Host(provider);
        using var session = await SessionAsync(host, Admin);

        // Before a passed rag-admin test: no default, and a check without a provider address is refused.
        var before = (await StateAsync(session)).GetProperty("signIn");
        before.GetProperty("providerUrl").GetProperty("value").ValueKind.ShouldBe(JsonValueKind.Null);
        before.GetProperty("configured").GetBoolean().ShouldBeFalse();
        (await RefusalAsync(session, Values(providerUrl: null))).ShouldBe(SetupTestReasons.ProviderUrlInvalid);

        await AdminTestAsync(session, provider, RagRoles.PlatformAdmin);
        // A later rag-admin test that fails (the provider sends no code) leaves the default at the passed one.
        using (var started = await PostAsync(session, "/bff/setup/test", new { authority = FakeProvider.Authority, clientSecret = AdminSecret }))
        {
            var query = HttpUtility.ParseQueryString(new Uri(JsonDocument.Parse(await started.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("authorizeUrl").GetString()!).Query);
            using var failed = await session.PostAsync("/signin-oidc", new FormUrlEncodedContent(new Dictionary<string, string> { ["state"] = query["state"]! }), Ct);
            failed.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        }

        var after = (await StateAsync(session)).GetProperty("signIn");
        (after.GetProperty("providerUrl").GetProperty("value").GetString(), after.GetProperty("providerUrl").GetProperty("source").GetString())
            .ShouldBe((ProviderUrl, SignInSources.Default));
        after.GetProperty("configured").GetBoolean().ShouldBeTrue();

        // A check without a provider address uses the tested authority's discovery document.
        provider.Overrides[TokenPath] = () => TokenError("invalid_grant");
        var count = provider.Requested.Count;
        var test = await TestAsync(session, Values(providerUrl: null));
        (test.GetProperty("status").GetString(), test.GetProperty("result").GetProperty("clientAccepted").GetBoolean()).ShouldBe(("passed", true));
        test.GetProperty("values").GetProperty("providerUrl").ValueKind.ShouldBe(JsonValueKind.Null);
        provider.Requested.Skip(count).Select(u => u.AbsolutePath).ShouldBe(["/realms/ume/.well-known/openid-configuration", "/realms/ume/certs", TokenPath]);

        // Saved as null, so it keeps following the sign-in provider once Finish saves the authority. Saving needs the session's latest
        // rag-admin test to have passed, so it is passed again first.
        provider.Overrides.Clear();
        await AdminTestAsync(session, provider, RagRoles.PlatformAdmin);
        provider.Overrides[TokenPath] = () => TokenError("invalid_grant");
        test = await TestAsync(session, Values(providerUrl: null));
        using (var saved = await PostAsync(session, "/bff/setup/complete", CompleteValues(test.GetProperty("testId").GetGuid(), providerUrl: null)))
        {
            saved.StatusCode.ShouldBe(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync(Ct));
            var signIn = JsonDocument.Parse(await saved.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("openWebUi").GetProperty("signIn");
            (signIn.GetProperty("providerUrl").GetProperty("value").GetString(), signIn.GetProperty("providerUrl").GetProperty("source").GetString())
                .ShouldBe((ProviderUrl, SignInSources.Default));
        }

        OpenWebUiOidcValues.Parse((await RowAsync(Admin))!.OpenWebUiOidc).ProviderUrl.ShouldBeNull();
        // The stored secret meets the same resolved address in the session: it is used, not refused as secret_required.
        (await TestAsync(session, Values(clientSecret: null, providerUrl: null))).GetProperty("clientSecret").GetString().ShouldBe(SetupSecretSources.Stored);
        provider.LastTokenForm!["client_secret"].ShouldBe(TypedSecret);

        // Outside a setup session nothing changes: rag-admin's authority is still unset there.
        await using var scope = host.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<OpenWebUiSignInReader>().ReadAsync(Ct)).ProviderUrl.Value.ShouldBeNull();
    }

    [Fact]
    public async Task An_unverified_client_may_be_saved_and_says_so()
    {
        using var provider = new FakeProvider();
        await using var host = Host(provider);
        using var session = await SessionAsync(host, Admin);
        await AdminTestAsync(session, provider, RagRoles.PlatformAdmin);
        provider.Overrides[TokenPath] = () => TokenError("unauthorized_client");
        var test = (await TestAsync(session, Values())).GetProperty("testId").GetGuid();
        using var saved = await PostAsync(session, "/bff/setup/complete", CompleteValues(test));
        saved.StatusCode.ShouldBe(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync(Ct));
        JsonDocument.Parse(await saved.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("clientVerified").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Each_target_takes_its_own_values_only()
    {
        using var provider = new FakeProvider();
        await using var host = Host(provider);
        using var session = await SessionAsync(host, Admin);
        foreach (var body in new object[]
        {
            new { target = SetupTargets.OpenWebUi, providerUrl = ProviderUrl, authority = FakeProvider.Authority },
            new { target = SetupTargets.OpenWebUi, providerUrl = ProviderUrl, refuseGuests = true },
            new { authority = FakeProvider.Authority, providerUrl = ProviderUrl },
            new { target = "gateway", providerUrl = ProviderUrl },
        })
        {
            using var response = await PostAsync(session, "/bff/setup/test", body);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, JsonSerializer.Serialize(body));
        }

        provider.Requested.ShouldBeEmpty();
    }

    [Fact]
    public async Task No_admin_session_route_writes_Open_WebUIs_values_but_the_provider_name()
    {
        await ResetAsync(Admin, completed: true);
        await using var factory = database.HeaderFactory().WithWebHostBuilder(web =>
        {
            web.UseSetting(OpenWebUiSignIn.EnabledKey, "true");
            web.UseSetting(OpenWebUiSignIn.PublicUrlKey, PublicUrl + "/");
        });
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);
        using var current = JsonDocument.Parse(await admin.GetStringAsync("/api/settings/openwebui", Ct));
        var body = new Dictionary<string, object?>
        {
            ["providerName"] = "Chatten", ["version"] = current.RootElement.GetProperty("version").GetString(),
            ["providerUrl"] = "https://evil.invalid/.well-known/openid-configuration", ["clientId"] = "evil", ["clientSecret"] = "evil-secret",
        };
        using (var put = await admin.PutAsync("/api/settings/openwebui", JsonContent(body), Ct))
        {
            put.StatusCode.ShouldBe(HttpStatusCode.OK, await put.Content.ReadAsStringAsync(Ct));
        }

        var row = (await RowAsync(Admin))!;
        OpenWebUiOidcValues.Parse(row.OpenWebUiOidc).ShouldBe(new OpenWebUiOidcValues { ProviderName = "Chatten" });
        row.OpenWebUiOidcEncryptedClientSecret.ShouldBeNull();

        // The platform admin's read: Open WebUI's block with each value's source, the provider derived from the deployment's authority.
        var text = await admin.GetStringAsync("/api/settings/sign-in", Ct);
        text.ShouldNotContain("evil");
        var openWebUi = JsonDocument.Parse(text).RootElement.GetProperty("openWebUi");
        (openWebUi.GetProperty("enabled").GetBoolean(), openWebUi.GetProperty("redirectUri").GetString()).ShouldBe((true, RedirectUri));
        var signIn = openWebUi.GetProperty("signIn");
        signIn.GetProperty("providerName").GetProperty("source").GetString().ShouldBe(SignInSources.Wizard);
        signIn.GetProperty("providerUrl").GetProperty("value").GetString().ShouldBe("https://idp.invalid/realms/ume/.well-known/openid-configuration");
        signIn.GetProperty("providerUrl").GetProperty("source").GetString().ShouldBe(SignInSources.Default);
        signIn.GetProperty("clientSecret").GetProperty("set").GetBoolean().ShouldBeFalse();

        // A name the deployment sets wins, so the admin's is refused.
        await using var deployed = factory.WithWebHostBuilder(web => web.UseSetting("OpenWebUi:Oidc:ProviderName", "Deployed"));
        using var deployedAdmin = await AdminDatabaseFixture.UserAsync(deployed, RagRoles.PlatformAdmin);
        using var version = JsonDocument.Parse(await deployedAdmin.GetStringAsync("/api/settings/openwebui", Ct));
        using var locked = await deployedAdmin.PutAsync("/api/settings/openwebui",
            JsonContent(new { providerName = "Mine", version = version.RootElement.GetProperty("version").GetString() }), Ct);
        locked.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await locked.Content.ReadAsStringAsync(Ct));
    }

    private static StringContent JsonContent(object body) => new(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
}
