using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Infrastructure.Persistence;
using Ume.Rag.Infrastructure.Security;

namespace Ume.Rag.AdminApi;

/// <summary>What an Open WebUI test keeps protected (<see cref="StoredSecret.SetupTest"/>): never answered, logged or audited.</summary>
/// <see cref="ProviderUrl"/> and <see cref="ClientId"/> are the effective ones the test ran with.
public sealed record OpenWebUiTestPayload(OpenWebUiOidcValues Values, string ProviderUrl, string ClientId, string SecretSource, string? Secret);

/// <summary>
/// What an Open WebUI test found: the discovery document (<c>ok</c>; a failed one refuses the test instead) and whether the token
/// endpoint accepted the client (<c>true</c>), refused it (<c>false</c>) or could not tell (<c>null</c>, with <see cref="ClientReason"/>).
/// </summary>
public sealed record OpenWebUiTestResult(string Discovery, bool? ClientAccepted, string? ClientReason);

/// <summary>An Open WebUI test as the wizard shows it: <c>passed</c> or <c>failed</c>, the tested values without the secret, and where the secret came from.</summary>
public sealed record OpenWebUiTestDto(
    Guid TestId, string Target, string Status, string? Reason, DateTimeOffset StartedAt, OpenWebUiOidcValues? Values, string? ClientSecret, OpenWebUiTestResult? Result)
{
    public static OpenWebUiTestDto Of(SetupTest test, OpenWebUiTestPayload? payload)
    {
        ArgumentNullException.ThrowIfNull(test);
        return new OpenWebUiTestDto(test.Id, test.Target, test.Passed == true ? "passed" : "failed", test.Reason, test.CreatedAt, payload?.Values, payload?.SecretSource,
            test.Result is { } json ? JsonSerializer.Deserialize<OpenWebUiTestResult>(json, SetupTestSignIn.Json) : null);
    }
}

/// <summary>
/// The setup test of Open WebUI's sign-in values (identity design §19, "Open WebUI"). Its provider sends the browser back to Open WebUI,
/// never to rag-admin, so a round trip with Open WebUI's redirect URI cannot be finished here, and one with rag-admin's would need
/// rag-admin's callback registered on Open WebUI's client. The test is therefore the back channel only: the discovery document at the
/// provider address, fetched and checked as for rag-admin's test (https outside Development, no redirects, size and time caps; its
/// issuer must give that address), its signing keys, and one token request with a made-up authorization code. RFC 6749 §5.2:
/// a client that fails authentication gets <c>invalid_client</c> (401 with HTTP authentication); an authenticated client with an
/// invalid code gets <c>invalid_grant</c>. <c>invalid_client</c> or any 401 fails the test
/// (<see cref="SetupTestReasons.ClientRefused"/>; Keycloak 26.8 answers a wrong secret with 401 <c>unauthorized_client</c>, read in
/// <c>KeycloakSignInTests</c>), only <c>invalid_grant</c> confirms the client, and any other answer passes it unverified
/// (<see cref="SetupTestReasons.ClientUnverified"/>, or the transport's reason). The secret rules are rag-admin's: the stored secret
/// goes only to the provider address and client id in force, a deployment secret only to the deployment's provider. No browser leg,
/// so no test cookie; the test row is claimed at once and no callback can take it.
/// </summary>
public sealed class SetupOpenWebUiTest(RagDbContext db, IConfiguration configuration, IHostEnvironment environment, TimeProvider time, SetupProviderClient provider,
    SettingsSecretProtector secrets, OpenWebUiSignInReader live, ILoggerFactory loggers)
{
    private bool RequireHttps => !environment.IsDevelopment();

    /// <summary>
    /// Runs a test for <paramref name="session"/> and stores it. Refusals before the token request (the values, the secret rules, the
    /// discovery document, the keys) throw <c>setup_test_refused</c> with a <see cref="SetupTestReasons"/> code and store nothing.
    /// </summary>
    public async Task<OpenWebUiTestDto> RunAsync(HttpContext http, SetupSession session, SetupValuesRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        var values = request.ToOpenWebUiValues().Normalized();
        var row = await db.Settings.AsNoTracking().Select(s => new { s.OpenWebUiOidcEncryptedClientSecret, s.IdentityOidc }).SingleOrDefaultAsync(ct);
        var typed = request.ClientSecret;
        var effective = OpenWebUiSignIn.Resolve(configuration, values, OpenWebUiSignIn.AuthorityOf(row?.IdentityOidc),
            () => typed is null ? secrets.Unprotect(StoredSecret.OpenWebUiOidcClientSecret, row?.OpenWebUiOidcEncryptedClientSecret)
                : string.IsNullOrWhiteSpace(typed) ? StoredSecretValue.NotSet : StoredSecretValue.Readable(typed),
            null);
        if (effective.ClientSecret.Unreadable)
        {
            throw SetupTestSignIn.Refused(SetupTestReasons.StoredSecretUnreadable);
        }

        if (effective.ProviderUrl.Value is not { } providerUrl || !Uri.TryCreate(providerUrl, UriKind.Absolute, out var address) ||
            !SetupDiscovery.Allowed(address, RequireHttps) || address.Query.Length > 0 || address.Fragment.Length > 0)
        {
            throw SetupTestSignIn.Refused(SetupTestReasons.ProviderUrlInvalid);
        }

        var secretSource = effective.ClientSecret.Source == SignInSources.Deployment ? SetupSecretSources.Deployment
            : typed is null ? effective.ClientSecret.Value is null ? SetupSecretSources.None : SetupSecretSources.Stored
            : string.IsNullOrWhiteSpace(typed) ? SetupSecretSources.Removed : SetupSecretSources.Typed;
        var clientId = effective.ClientId.Value!;
        if (await SecretRefusalAsync(secretSource, effective, providerUrl, clientId, ct) is { } secretRefusal)
        {
            throw SetupTestSignIn.Refused(secretRefusal);
        }

        var (metadata, refusal) = await SetupDiscovery.FetchDocumentAsync(provider, address, RequireHttps, ct);
        if (refusal is not null)
        {
            throw SetupTestSignIn.Refused(refusal);
        }

        if (await SetupDiscovery.KeysAsync(provider, metadata!, ct) is null)
        {
            throw SetupTestSignIn.Refused(SetupTestReasons.KeysUnavailable);
        }

        var (accepted, clientReason) = await CheckClientAsync(metadata!, clientId, effective.ClientSecret.Value, ct);
        var result = new OpenWebUiTestResult("ok", accepted, clientReason);
        // A deployment secret is never copied into the row: complete stores nothing of it.
        var payload = new OpenWebUiTestPayload(values, providerUrl, clientId, secretSource,
            secretSource is SetupSecretSources.Typed or SetupSecretSources.Stored ? effective.ClientSecret.Value : null);
        var now = time.GetUtcNow();
        var test = new SetupTest
        {
            Id = Guid.NewGuid(), SessionId = session.Id, Target = SetupTargets.OpenWebUi,
            // No browser leg: random hashes nobody holds the preimage of, and claimed at once, so no callback can take the row.
            MarkerHash = SetupSessions.Hash(SetupMode.Base64Url(RandomNumberGenerator.GetBytes(32))),
            BrowserHash = SetupSessions.Hash(SetupMode.Base64Url(RandomNumberGenerator.GetBytes(32))),
            Protected = secrets.Protect(StoredSecret.SetupTest, JsonSerializer.Serialize(payload, SetupTestSignIn.Json)),
            ValuesFingerprint = Fingerprint(values), CreatedAt = now, ExpiresAt = now + SetupTestSignIn.Lifetime, CallbackAt = now,
            Passed = accepted != false, Reason = accepted == false ? SetupTestReasons.ClientRefused : null,
            Result = JsonSerializer.Serialize(result, SetupTestSignIn.Json),
        };
        db.SetupTests.Add(test);
        await db.SaveChangesAsync(ct);
        SecurityEvents.SetupTestSignIn(SecurityEvents.CreateLogger(loggers), SetupTargets.OpenWebUi, test.Passed == true ? "passed" : "failed",
            test.Reason ?? clientReason ?? SetupTestReasons.Passed, null, false, SetupSessions.EventId(session), AdminActor.AddressOf(http), 1);
        return OpenWebUiTestDto.Of(test, payload);
    }

    /// <summary>The protected part of <paramref name="test"/>, or null when the key ring cannot read it.</summary>
    public OpenWebUiTestPayload? PayloadOf(SetupTest test)
    {
        ArgumentNullException.ThrowIfNull(test);
        var value = secrets.Unprotect(StoredSecret.SetupTest, test.Protected);
        return value.State == StoredSecretState.Readable ? JsonSerializer.Deserialize<OpenWebUiTestPayload>(value.Value!, SetupTestSignIn.Json) : null;
    }

    /// <summary>SHA-256 of the target and the tested values without the secret; <c>complete</c> saves only values with the same fingerprint.</summary>
    public static byte[] Fingerprint(OpenWebUiOidcValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return SetupTestSignIn.Fingerprint(SetupTargets.OpenWebUi, values.Normalized().ToJson());
    }

    /// <summary>
    /// What the token endpoint says of the client (RFC 6749 §5.2): <c>invalid_client</c>, or any 401 (the status §5.2 gives a failed
    /// client authentication; Keycloak 26.8 answers a wrong secret with 401 <c>unauthorized_client</c>), refuses it;
    /// <c>invalid_grant</c> for the made-up code accepts it; anything else (another error, a success, no answer) cannot tell, with
    /// its reason.
    /// </summary>
    public static (bool? Accepted, string? Reason) Classify(ProviderResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        switch (response.Outcome)
        {
            case ProviderOutcome.Status when response.StatusCode == StatusCodes.Status401Unauthorized:
                return (false, SetupTestReasons.ClientRefused);
            case ProviderOutcome.Status when response.Body is { } body:
                try
                {
                    using var json = JsonDocument.Parse(body);
                    var error = json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("error", out var e) &&
                                e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                    return error switch
                    {
                        "invalid_client" => (false, SetupTestReasons.ClientRefused),
                        "invalid_grant" => (true, null),
                        _ => (null, SetupTestReasons.ClientUnverified),
                    };
                }
                catch (JsonException)
                {
                    return (null, SetupTestReasons.ClientUnverified);
                }

            case ProviderOutcome.Tls:
                return (null, SetupTestReasons.Tls);
            case ProviderOutcome.Redirected:
                return (null, SetupTestReasons.Redirected);
            case ProviderOutcome.Unreachable:
                return (null, SetupTestReasons.TokenEndpointUnreachable);
            default:
                return (null, SetupTestReasons.ClientUnverified);
        }
    }

    /// <summary>
    /// The secret rules (identity design §19, "Whose secret goes where"): the wizard's stored secret goes only to the provider address and
    /// client id in force, a deployment secret only to the deployment's provider (its <c>OpenWebUi:Oidc:ProviderUrl</c>, or the default
    /// derived from its own <c>Oidc:Authority</c>). Checked before anything is fetched.
    /// </summary>
    private async Task<string?> SecretRefusalAsync(string secretSource, EffectiveOpenWebUiSignIn candidate, string providerUrl, string clientId, CancellationToken ct)
    {
        if (secretSource == SetupSecretSources.Deployment)
        {
            return candidate.ProviderUrlFromDeployment ? null : SetupTestReasons.DeploymentSecretWithoutAuthority;
        }

        if (secretSource == SetupSecretSources.Stored)
        {
            var current = await live.ReadAsync(ct);
            return SetupTestSignIn.Same(current.ProviderUrl.Value, providerUrl) && SetupTestSignIn.Same(current.ClientId.Value, clientId)
                ? null : SetupTestReasons.SecretRequired;
        }

        return null;
    }

    /// <summary>
    /// One token request with a random code, as Open WebUI's own exchange would send the client (its id, the secret in the form when it
    /// has one, Open WebUI's redirect URI when rag-admin knows it). The code is unknown to the provider, so no token can come back.
    /// </summary>
    private async Task<(bool? Accepted, string? Reason)> CheckClientAsync(ProviderMetadata metadata, string clientId, string? secret, CancellationToken ct)
    {
        var tokenEndpoint = new Uri(metadata.TokenEndpoint);
        if (!SetupDiscovery.Allowed(tokenEndpoint, RequireHttps))
        {
            return (null, SetupTestReasons.InsecureEndpoint);
        }

        List<KeyValuePair<string, string>> form =
        [
            new("grant_type", "authorization_code"), new("code", SetupMode.Base64Url(RandomNumberGenerator.GetBytes(32))), new("client_id", clientId),
        ];
        if (OpenWebUiSignIn.RedirectUri(configuration) is { } redirectUri)
        {
            form.Add(new("redirect_uri", redirectUri));
        }

        if (!string.IsNullOrEmpty(secret))
        {
            form.Add(new("client_secret", secret));
        }

        return Classify(await provider.PostFormAsync(tokenEndpoint, form, ct, errorBody: true));
    }
}
