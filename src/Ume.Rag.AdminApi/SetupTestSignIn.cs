using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Ume.Rag.Domain.Admin;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Infrastructure.Persistence;
using Ume.Rag.Infrastructure.Security;

namespace Ume.Rag.AdminApi;

/// <summary>Where a test's client secret came from; <c>complete</c> stores it only when it was typed.</summary>
public static class SetupSecretSources
{
    /// <summary>The deployment sets it; the typed one is ignored and never stored.</summary>
    public const string Deployment = "deployment";

    public const string Typed = "typed";

    /// <summary>None typed: the wizard's stored one was used, and stays.</summary>
    public const string Stored = "stored";

    /// <summary>An empty one typed: the test ran without a secret, and <c>complete</c> removes the stored one.</summary>
    public const string Removed = "removed";

    /// <summary>No secret anywhere: a public client.</summary>
    public const string None = "none";
}

/// <summary>What the test row keeps protected (<see cref="StoredSecret.SetupTest"/>): never answered, logged or audited.</summary>
/// <see cref="Authority"/> and <see cref="ClientId"/> are the effective ones the test started with.
public sealed record SetupTestPayload(
    OidcWizardValues Values, string Authority, string ClientId, string SecretSource, string? Secret, ProviderMetadata Provider, string RedirectUri,
    string CodeVerifier, string Nonce);

/// <summary>The claim names and checks a test read (identity design §19): what the wizard names when it explains a result.</summary>
public sealed record SetupClaimsRead(string Role, string Group, string Department, string Team, string? TenantId, bool RefuseGuests);

/// <summary>The tested person as the sign-in checks saw them; lists are capped at 100 entries, <see cref="GroupCount"/> is the whole count.</summary>
public sealed record SetupTestResult(
    string Subject, string? Name, IReadOnlyList<string> Roles, IReadOnlyList<string> Groups, int GroupCount, IReadOnlyList<string> DepartmentCodes,
    IReadOnlyList<string> TeamCodes, bool IsPlatformAdmin, SetupClaimsRead ClaimsRead);

/// <summary>A started test: the provider address the wizard opens.</summary>
public sealed record SetupTestStart(Guid TestId, string AuthorizeUrl, DateTimeOffset ExpiresAt);

/// <summary>
/// The test sign-in (identity design §19, "How the test sign-in runs"): a hand-written authorization-code sign-in with candidate
/// values and rag-admin's own redirect URI. <c>state</c> is <c>rag-setup-test.</c> and a 256-bit random marker (the handler's own
/// state is base64url and never holds a dot); the test row, single use and 10 minutes, keeps the PKCE verifier, nonce and candidate
/// values protected, so the callback may land on another replica. The callback exchanges the code as the handler does (secret in
/// the form, PKCE S256), validates the id token with <c>Microsoft.IdentityModel</c> against the candidate's metadata (issuer,
/// audience = client id, signature, lifetime, nonce), and runs the same <see cref="AdminAuthentication.ValidateSignInAsync"/> as a
/// real sign-in on the candidate values with the deployment's role groups. No admin session is created and no token is kept; the
/// only cookie is the test cookie, which binds the callback to the browser that started the test.
/// </summary>
public sealed partial class SetupTestSignIn(RagDbContext db, IConfiguration configuration, IHostEnvironment environment, TimeProvider time, SetupMode setup,
    SetupProviderClient provider, SettingsSecretProtector secrets, SignInSettings signIn, SetupEventSampler sampler, SetupCleanup cleanup,
    IOptions<OidcOptions> deployment, IOptionsMonitor<OpenIdConnectOptions> oidc, ILoggerFactory loggers)
{
    public const string StatePrefix = "rag-setup-test.";

    /// <summary>The test cookie, set by <c>POST test</c> on the callback path and required at the callback.</summary>
    public const string TestCookieName = "__Secure-ume-rag-setup-test";

    private const int UnknownTestEvent = 6011;

    /// <summary>Where every callback ends, whatever happened: the result is never in the address.</summary>
    public const string DonePath = "/setup?test=done";

    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const int MarkerLength = 43; // 32 random bytes, base64url

    /// <summary>The handler's scopes: its defaults openid and profile, and email (<see cref="AdminAuthentication.AddAdminAuthentication"/>).</summary>
    private static readonly string[] BaseScopes = ["openid", "profile", "email"];
    private const int MaxListed = 100;
    private const int MaxValue = 200;

    private bool RequireHttps => !environment.IsDevelopment();

    /// <summary>
    /// Starts a test for <paramref name="session"/>: resolves the candidate through the same precedence as the live values (a
    /// deployment value wins), applies the secret rules (<see cref="SecretRefusalAsync"/>), fetches and checks the discovery document
    /// and keys, stores the test and sets the test cookie that binds the callback to this browser. Throws <c>setup_test_refused</c>
    /// with a <see cref="SetupTestReasons"/> code.
    /// </summary>
    public async Task<SetupTestStart> StartAsync(HttpContext http, SetupSession session, SetupValuesRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        var values = request.ToWizardValues().Normalized();
        var stored = await db.Settings.AsNoTracking().Select(s => s.IdentityOidcEncryptedClientSecret).SingleOrDefaultAsync(ct);
        var typed = request.ClientSecret;
        var effective = SignInSettings.Resolve(configuration, values,
            () => typed is null ? secrets.Unprotect(StoredSecret.IdentityOidcClientSecret, stored)
                : string.IsNullOrWhiteSpace(typed) ? StoredSecretValue.NotSet : StoredSecretValue.Readable(typed),
            null, 0, null);
        var snapshot = SignInSnapshot.Of(effective, RequireHttps);
        if (snapshot.NotConfiguredReason == SignInNotConfiguredReasons.UnreadableClientSecret)
        {
            throw Refused(SetupTestReasons.StoredSecretUnreadable);
        }

        if (snapshot.NotConfiguredReason is not null || !Uri.TryCreate(effective.Authority.Value, UriKind.Absolute, out var authorityUri) ||
            !SetupDiscovery.Allowed(authorityUri, RequireHttps) || authorityUri.Query.Length > 0 || authorityUri.Fragment.Length > 0)
        {
            throw Refused(SetupTestReasons.AuthorityInvalid);
        }

        var secretSource = effective.ClientSecret.Source == SignInValueSources.Deployment ? SetupSecretSources.Deployment
            : typed is null ? effective.ClientSecret.Value is null ? SetupSecretSources.None : SetupSecretSources.Stored
            : string.IsNullOrWhiteSpace(typed) ? SetupSecretSources.Removed : SetupSecretSources.Typed;
        var authority = effective.Authority.Value!;
        if (await SecretRefusalAsync(secretSource, effective, authority, effective.ClientId.Value, ct) is { } secretRefusal)
        {
            throw Refused(secretRefusal);
        }

        var (metadata, refusal) = await SetupDiscovery.FetchAsync(provider, authority, RequireHttps, ct);
        if (refusal is not null)
        {
            throw Refused(refusal);
        }

        if (await SetupDiscovery.KeysAsync(provider, metadata!, ct) is null)
        {
            throw Refused(SetupTestReasons.KeysUnavailable);
        }

        var marker = SetupMode.Base64Url(RandomNumberGenerator.GetBytes(32));
        var browser = SetupMode.Base64Url(RandomNumberGenerator.GetBytes(32));
        var verifier = SetupMode.Base64Url(RandomNumberGenerator.GetBytes(32));
        var nonce = SetupMode.Base64Url(RandomNumberGenerator.GetBytes(32));
        var options = oidc.Get(OpenIdConnectDefaults.AuthenticationScheme);
        var redirectUri = RedirectUri(http, options.CallbackPath);
        // A deployment secret is never copied into the row: the callback reads it from the deployment again.
        var payload = new SetupTestPayload(values, authority, effective.ClientId.Value, secretSource,
            secretSource is SetupSecretSources.Typed or SetupSecretSources.Stored ? effective.ClientSecret.Value : null, metadata!, redirectUri, verifier, nonce);

        var now = time.GetUtcNow();
        var test = new SetupTest
        {
            Id = Guid.NewGuid(), SessionId = session.Id, Target = SetupTargets.RagAdmin, MarkerHash = SetupSessions.Hash(marker), BrowserHash = SetupSessions.Hash(browser),
            Protected = secrets.Protect(StoredSecret.SetupTest, JsonSerializer.Serialize(payload, Json)), ValuesFingerprint = Fingerprint(SetupTargets.RagAdmin, values),
            CreatedAt = now, ExpiresAt = now + Lifetime,
        };
        db.SetupTests.Add(test);
        await db.SaveChangesAsync(ct);
        http.Response.Cookies.Append(TestCookieName, browser, TestCookie(options.CallbackPath, test.ExpiresAt));

        // As the handler's challenge (scopes openid, profile, email and the extra ones; code with PKCE S256), with form_post as the
        // handler's default response mode.
        var message = new OpenIdConnectMessage
        {
            IssuerAddress = metadata!.AuthorizationEndpoint, ClientId = effective.ClientId.Value, RedirectUri = redirectUri,
            ResponseType = OpenIdConnectResponseType.Code, ResponseMode = OpenIdConnectResponseMode.FormPost,
            Scope = string.Join(' ', BaseScopes.Concat(effective.ExtraScopes.Value).Distinct(StringComparer.Ordinal)),
            State = StatePrefix + marker, Nonce = nonce,
        };
        message.Parameters.Add("code_challenge", SetupMode.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
        message.Parameters.Add("code_challenge_method", "S256");
        return new SetupTestStart(test.Id, message.CreateAuthenticationRequestUrl(), test.ExpiresAt);
    }

    /// <summary>
    /// The callback of a test (<see cref="SetupCallbackMiddleware"/>): claims the test once, requires the test cookie of the browser
    /// that started it, applies the secret rules again, runs the exchange and the checks, stores the result and sends the browser to
    /// <see cref="DonePath"/>. A replayed, forged or expired marker gets the same redirect and a sampled event 6011
    /// <c>unknown_test</c>. While setup is closed it answers 404.
    /// </summary>
    public async Task HandleCallbackAsync(HttpContext http, string state, string? code, bool providerError)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(state);
        var ct = http.RequestAborted;
        var options = oidc.Get(OpenIdConnectDefaults.AuthenticationScheme);
        var browser = http.Request.Cookies[TestCookieName];
        var status = await setup.ReadAsync(ct);
        if (!status.Open)
        {
            await cleanup.AfterClosedAsync(status, setup, ct);
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var security = SecurityEvents.CreateLogger(loggers);
        var address = AdminActor.AddressOf(http);
        var marker = state[StatePrefix.Length..];
        var now = time.GetUtcNow();
        SetupTest? test = null;
        if (marker.Length == MarkerLength && marker.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            var hash = SetupSessions.Hash(marker);
            // Single use across replicas: only one callback moves CallbackAt from null.
            if (await db.SetupTests.Where(t => t.MarkerHash == hash && t.CallbackAt == null && t.ExpiresAt > now)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.CallbackAt, now), ct) == 1)
            {
                test = await db.SetupTests.SingleAsync(t => t.MarkerHash == hash, ct);
            }
        }

        if (test is null)
        {
            // The test cookie stays: a request with a made-up marker (a page that makes the deployer's browser load this address)
            // must not end the test that browser is running.
            if (sampler.ShouldLog(UnknownTestEvent, out var count))
            {
                SecurityEvents.SetupTestSignIn(security, SetupTargets.RagAdmin, "failed", SetupTestReasons.UnknownTest, null, false, null, address, count);
            }

            http.Response.Redirect(DonePath);
            return;
        }

        // The test is claimed: its cookie has done its work, whatever the outcome.
        http.Response.Cookies.Delete(TestCookieName, TestCookie(options.CallbackPath, null));
        var session = await db.SetupSessions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == test.SessionId && s.OpeningNonce == status.Nonce && s.ExpiresAt > now, ct);
        var sameBrowser = browser is { Length: > 0 and <= 100 } && CryptographicOperations.FixedTimeEquals(SetupSessions.Hash(browser), test.BrowserHash);
        (string Reason, SetupTestResult? Result) outcome = session is null ? (SetupTestReasons.SessionExpired, null)
            : !sameBrowser ? (SetupTestReasons.BrowserMismatch, null)
            : providerError || string.IsNullOrEmpty(code) ? (SetupTestReasons.ProviderError, null)
            : await RunAsync(test, code, ct);
        var (reason, result) = outcome;
        test.Passed = reason == SetupTestReasons.Passed;
        test.Reason = test.Passed == true ? null : reason;
        test.Result = result is null ? null : JsonSerializer.Serialize(result, Json);
        await db.SaveChangesAsync(CancellationToken.None);
        SecurityEvents.SetupTestSignIn(security, test.Target, test.Passed == true ? "passed" : "failed", reason, result?.Subject, result?.IsPlatformAdmin == true,
            session is null ? null : SetupSessions.EventId(session), address, 1);
        http.Response.Redirect(DonePath);
    }

    /// <summary>
    /// The secret rules (identity design §19): the wizard's stored secret goes only to the provider and client it was stored for (the
    /// candidate's effective authority and client id equal the ones in force), and a deployment secret only to the deployment's own
    /// authority. Checked when a test starts and again at its callback, before any secret is sent.
    /// </summary>
    private async Task<string?> SecretRefusalAsync(string secretSource, EffectiveSignIn candidate, string authority, string clientId, CancellationToken ct)
    {
        if (secretSource == SetupSecretSources.Deployment)
        {
            return candidate.ClientSecret.Source == SignInValueSources.Deployment && candidate.Authority.Source == SignInValueSources.Deployment &&
                   Same(candidate.Authority.Value, authority) ? null : SetupTestReasons.DeploymentSecretWithoutAuthority;
        }

        if (secretSource == SetupSecretSources.Stored)
        {
            var live = await signIn.ReadAsync(ct);
            return Same(live.Authority.Value, authority) && Same(live.ClientId.Value, clientId) ? null : SetupTestReasons.SecretRequired;
        }

        return null;
    }

    internal static bool Same(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.Ordinal);

    /// <summary>
    /// The test cookie: <c>SameSite=None</c>, since the provider's form_post back is a cross-site POST, scoped to the callback path and
    /// expiring with the test.
    /// </summary>
    private static CookieOptions TestCookie(PathString callbackPath, DateTimeOffset? expiresAt) => new()
    {
        HttpOnly = true, Secure = true, SameSite = SameSiteMode.None, Path = callbackPath.Value, Expires = expiresAt, IsEssential = true,
    };

    /// <summary>The protected part of <paramref name="test"/>, or null when the key ring cannot read it.</summary>
    public SetupTestPayload? PayloadOf(SetupTest test)
    {
        ArgumentNullException.ThrowIfNull(test);
        var value = secrets.Unprotect(StoredSecret.SetupTest, test.Protected);
        return value.State == StoredSecretState.Readable ? JsonSerializer.Deserialize<SetupTestPayload>(value.Value!, Json) : null;
    }

    /// <summary>
    /// SHA-256 of a target and the tested values without the secret (the secret is taken from the test row, never sent again).
    /// <c>complete</c> saves only values with the same fingerprint.
    /// </summary>
    public static byte[] Fingerprint(string target, OidcWizardValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return Fingerprint(target, values.Normalized().ToJson());
    }

    /// <summary>The same over a target's normalized values as JSON (Open WebUI's, <see cref="SetupOpenWebUiTest"/>).</summary>
    public static byte[] Fingerprint(string target, string normalizedJson) => SHA256.HashData(Encoding.UTF8.GetBytes(target + "\n" + normalizedJson));

    /// <summary>The redirect URI as the OpenID Connect handler builds it: the request's scheme and host (behind the proxy, its Host header), path base and <paramref name="path"/>.</summary>
    public static string RedirectUri(HttpContext http, PathString path)
    {
        ArgumentNullException.ThrowIfNull(http);
        return http.Request.Scheme + Uri.SchemeDelimiter + http.Request.Host.ToUriComponent() + http.Request.PathBase.ToUriComponent() + path.ToUriComponent();
    }

    internal static Ume.Rag.ServiceDefaults.ApiFaultException Refused(string reason) =>
        AdminFaults.Of(AdminErrorCodes.SetupTestRefused, new Dictionary<string, string> { ["reason"] = reason });

    /// <summary>The candidate's effective values at the callback, through the same precedence (the deployment may have changed since).</summary>
    private EffectiveSignIn Effective(SetupTestPayload payload) => SignInSettings.Resolve(configuration, payload.Values,
        () => payload.Secret is null ? StoredSecretValue.NotSet : StoredSecretValue.Readable(payload.Secret), null, 0, null);

    private async Task<(string Reason, SetupTestResult? Result)> RunAsync(SetupTest test, string code, CancellationToken ct)
    {
        if (PayloadOf(test) is not { } payload)
        {
            return (SetupTestReasons.UnknownTest, null);
        }

        var effective = Effective(payload);
        // Again before anything is sent: the deployment or the stored values may have changed since the test started.
        if (await SecretRefusalAsync(payload.SecretSource, effective, payload.Authority, payload.ClientId, ct) is { } secretRefusal)
        {
            return (secretRefusal, null);
        }

        var secret = payload.SecretSource == SetupSecretSources.Deployment ? effective.ClientSecret.Value : payload.Secret;
        List<KeyValuePair<string, string>> form =
        [
            new("grant_type", "authorization_code"), new("code", code), new("redirect_uri", payload.RedirectUri), new("client_id", payload.ClientId),
            new("code_verifier", payload.CodeVerifier),
        ];
        if (!string.IsNullOrEmpty(secret))
        {
            form.Add(new("client_secret", secret));
        }

        var tokenEndpoint = new Uri(payload.Provider.TokenEndpoint);
        if (!SetupDiscovery.Allowed(tokenEndpoint, RequireHttps))
        {
            return (SetupTestReasons.InsecureEndpoint, null);
        }

        var token = await provider.PostFormAsync(tokenEndpoint, form, ct);
        if (SetupDiscovery.Refusal(token.Outcome, SetupTestReasons.TokenEndpointUnreachable, SetupTestReasons.TokenRefused) is { } failed)
        {
            return (failed, null);
        }

        string? idToken;
        try
        {
            using var json = JsonDocument.Parse(token.Body!);
            idToken = json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("id_token", out var value) &&
                      value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
        catch (JsonException)
        {
            return (SetupTestReasons.TokenRefused, null);
        }

        if (string.IsNullOrEmpty(idToken))
        {
            return (SetupTestReasons.IdTokenMissing, null);
        }

        if (!SetupDiscovery.Allowed(new Uri(payload.Provider.JwksUri), RequireHttps) || await SetupDiscovery.KeysAsync(provider, payload.Provider, ct) is not { } keys)
        {
            return (SetupTestReasons.KeysUnavailable, null);
        }

        // As the handler's TokenValidationParameters: issuer from the metadata, audience the client id, the metadata's keys, the
        // default lifetime check and clock skew; MapInboundClaims off; name and role claim types as AddAdminAuthentication sets them.
        var validated = await new JsonWebTokenHandler { MapInboundClaims = false }.ValidateTokenAsync(idToken, new TokenValidationParameters
        {
            ValidIssuer = payload.Provider.Issuer, ValidAudience = payload.ClientId, IssuerSigningKeys = keys, RequireSignedTokens = true,
            RequireExpirationTime = true, NameClaimType = "name", RoleClaimType = AdminClaims.Roles, AuthenticationType = OpenIdConnectDefaults.AuthenticationScheme,
        });
        if (!validated.IsValid || validated.ClaimsIdentity.FindFirst(AdminClaims.Subject) is null)
        {
            return (SetupTestReasons.IdTokenInvalid, null);
        }

        var identity = validated.ClaimsIdentity;
        if (identity.FindFirst(OpenIdConnectParameterNames.Nonce)?.Value is not { } nonce ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(nonce), Encoding.UTF8.GetBytes(payload.Nonce)))
        {
            return (SetupTestReasons.NonceMismatch, null);
        }

        // As OnTokenValidated: the candidate's claim names, tenant and guest rule, with the deployment's role groups (identity design §2.5).
        var principal = new ClaimsPrincipal(identity);
        var options = SignInSnapshot.Of(effective, RequireHttps).ToOidcOptions(deployment.Value.RoleGroups);
        var refusal = await AdminAuthentication.ValidateSignInAsync(principal, options, db, ct);
        return (refusal ?? SetupTestReasons.Passed, ResultOf(principal, options, refusal is null));
    }

    private static SetupTestResult ResultOf(ClaimsPrincipal principal, OidcOptions options, bool signedIn)
    {
        static string Shown(string value) => AdminActor.Printable(value.Length > MaxValue ? value[..MaxValue] : value)!;
        static IReadOnlyList<string> Listed(IEnumerable<string> values) => [.. values.Distinct(StringComparer.Ordinal).Take(MaxListed).Select(Shown)];
        var groups = principal.FindAll(options.GroupClaim).Select(c => c.Value).Distinct(StringComparer.Ordinal).ToList();
        return new SetupTestResult(
            Shown(AdminActor.SubjectOf(principal)), principal.Identity?.Name is { } name ? Shown(name) : null,
            Listed(principal.FindAll(AdminClaims.Roles).Select(c => c.Value)), Listed(groups), groups.Count,
            Listed(principal.FindAll(AdminClaims.DepartmentCodes).Select(c => c.Value)), Listed(principal.FindAll(AdminClaims.TeamCodes).Select(c => c.Value)),
            signedIn && principal.HasClaim(AdminClaims.Roles, RagRoles.PlatformAdmin),
            new SetupClaimsRead(options.RoleClaim, options.GroupClaim, options.DepartmentClaim, options.TeamClaim, options.TenantId, options.RefuseGuests));
    }
}

/// <summary>
/// Takes the callbacks of setup tests before authentication (identity design §19): only requests to the OpenID Connect callback path
/// whose <c>state</c> starts with <c>rag-setup-test.</c>, from the query (GET) or the form (POST, form_post). Every other request,
/// a real sign-in's callback included, passes on untouched; reading the form leaves it cached for the handler.
/// </summary>
public sealed class SetupCallbackMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http, IOptionsMonitor<OpenIdConnectOptions> monitor)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(monitor);
        if (http.Request.Path == monitor.Get(OpenIdConnectDefaults.AuthenticationScheme).CallbackPath)
        {
            IEnumerable<KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues>>? parameters = null;
            if (HttpMethods.IsGet(http.Request.Method))
            {
                parameters = http.Request.Query;
            }
            else if (HttpMethods.IsPost(http.Request.Method) && http.Request.ContentType?.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) == true)
            {
                parameters = await http.Request.ReadFormAsync(http.RequestAborted);
            }

            var values = parameters?.ToDictionary(p => p.Key, p => p.Value.ToString(), StringComparer.Ordinal);
            if (values is not null && values.TryGetValue(OpenIdConnectParameterNames.State, out var state) && state.StartsWith(SetupTestSignIn.StatePrefix, StringComparison.Ordinal))
            {
                await http.RequestServices.GetRequiredService<SetupTestSignIn>().HandleCallbackAsync(http, state,
                    values.GetValueOrDefault(OpenIdConnectParameterNames.Code), values.ContainsKey(OpenIdConnectParameterNames.Error));
                return;
            }
        }

        await next(http);
    }
}
