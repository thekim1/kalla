using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Ume.Rag.AdminApi;

/// <summary>
/// Why a setup test refused or failed (identity design §19, "Test sign-in"). Stable codes for the setup wizard; no response
/// content, header or timing detail ever goes back with them.
/// </summary>
public static class SetupTestReasons
{
    public const string Passed = "passed";

    /// <summary>The authority is not an absolute https URL (http in Development only), or holds user info, a query or a fragment.</summary>
    public const string AuthorityInvalid = "authority_invalid";

    /// <summary>The provider did not answer: no address, no connection, or a timeout.</summary>
    public const string AuthorityUnreachable = "authority_unreachable";

    /// <summary>The provider's certificate is not trusted (the service's CA bundle, as the sign-in's back channel uses).</summary>
    public const string Tls = "tls";

    /// <summary>The provider answered with a redirect; none is followed.</summary>
    public const string Redirected = "redirected";

    /// <summary>The answer is not a discovery document: an error status, not JSON, too large, or endpoints missing.</summary>
    public const string NotDiscovery = "not_discovery";

    /// <summary>The document's issuer is not the authority.</summary>
    public const string IssuerMismatch = "issuer_mismatch";

    /// <summary>An endpoint of the document is not https outside Development.</summary>
    public const string InsecureEndpoint = "insecure_endpoint";

    /// <summary>The provider requires pushed authorization requests, which the test does not make.</summary>
    public const string ParRequired = "par_required";

    /// <summary>The provider's signing keys could not be fetched, or there are none.</summary>
    public const string KeysUnavailable = "keys_unavailable";

    /// <summary>The wizard's stored client secret cannot be read by the key ring; type it again.</summary>
    public const string StoredSecretUnreadable = "stored_secret_unreadable";

    /// <summary>The provider sent the person back with an error instead of a code.</summary>
    public const string ProviderError = "provider_error";

    /// <summary>The token endpoint refused the code exchange: a wrong client secret or client id, most often.</summary>
    public const string TokenRefused = "token_refused";

    public const string TokenEndpointUnreachable = "token_endpoint_unreachable";

    public const string IdTokenMissing = "id_token_missing";

    /// <summary>The id token failed validation: signature, issuer, audience (the client id) or lifetime.</summary>
    public const string IdTokenInvalid = "id_token_invalid";

    public const string NonceMismatch = "nonce_mismatch";

    /// <summary>
    /// The wizard's stored client secret may be used only for the provider and client it was stored for: the candidate's authority
    /// or client id differs from the ones in force, so the secret must be typed again.
    /// </summary>
    public const string SecretRequired = "secret_required";

    /// <summary>The deployment sets the client secret but not the authority: a deployment secret goes only to the deployment's provider.</summary>
    public const string DeploymentSecretWithoutAuthority = "deployment_secret_without_authority";

    /// <summary>The callback did not come from the browser that started the test (its test cookie is missing or another).</summary>
    public const string BrowserMismatch = "browser_mismatch";

    /// <summary>The setup session of the test ended before its callback.</summary>
    public const string SessionExpired = "session_expired";

    /// <summary>A callback whose test is unknown, used already or expired (a replay or a forged marker).</summary>
    public const string UnknownTest = "unknown_test";
}

/// <summary>What a back-channel call of a setup test gave: the body, or why there is none.</summary>
public enum ProviderOutcome
{
    Ok = 0,
    Unreachable = 1,
    Tls = 2,
    Redirected = 3,

    /// <summary>An answer with an error status.</summary>
    Status = 4,
    TooLarge = 5,
}

/// <summary>A back-channel answer: <see cref="Body"/> only when <see cref="Outcome"/> is <see cref="ProviderOutcome.Ok"/>.</summary>
public sealed record ProviderResponse(ProviderOutcome Outcome, string? Body = null);

/// <summary>
/// The setup test's back channel to a candidate provider (identity design §19): a code holder chooses the address, so it is an SSRF
/// surface. No redirect is followed, a call lasts at most 10 seconds, an answer at most 256 KiB, and only the outcome goes back to
/// the caller, never content. TLS trusts what the sign-in's own back channel trusts: the system store, which the deployment points
/// at the service's CA bundle (<c>SSL_CERT_FILE</c>). Nothing is logged, so no address reaches the logs. The https rule is the
/// caller's (<see cref="SetupTestSignIn"/>): it knows the environment.
/// </summary>
public sealed class SetupProviderClient(HttpMessageHandler handler, TimeSpan? timeout = null) : IDisposable
{
    public const int MaxResponseBytes = 256 * 1024;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The whole call's limit, headers and body; tests may shorten it.</summary>
    public TimeSpan Timeout { get; } = timeout ?? DefaultTimeout;

    // The timeout is SendAsync's own, over the headers and the body; the client's would end at the headers (ResponseHeadersRead).
    private readonly HttpClient _http = new(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    /// <summary>The production handler: no redirects, no cookies, no decompression, short connect timeout.</summary>
    public static SetupProviderClient Create() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.None, ConnectTimeout = TimeSpan.FromSeconds(5),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2), UseProxy = true,
    });

    public Task<ProviderResponse> GetAsync(Uri uri, CancellationToken ct) => SendAsync(new HttpRequestMessage(HttpMethod.Get, uri), ct);

    public Task<ProviderResponse> PostFormAsync(Uri uri, IEnumerable<KeyValuePair<string, string>> form, CancellationToken ct) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Post, uri) { Content = new FormUrlEncodedContent(form) }, ct);

    public void Dispose() => _http.Dispose();

    private async Task<ProviderResponse> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using (request)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(Timeout);
            var call = timeout.Token;
            request.Headers.Accept.ParseAdd("application/json");
            try
            {
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, call);
                var status = (int)response.StatusCode;
                if (status is >= 300 and < 400)
                {
                    return new ProviderResponse(ProviderOutcome.Redirected);
                }

                if (!response.IsSuccessStatusCode)
                {
                    return new ProviderResponse(ProviderOutcome.Status);
                }

                if (response.Content.Headers.ContentLength > MaxResponseBytes)
                {
                    return new ProviderResponse(ProviderOutcome.TooLarge);
                }

                await using var stream = await response.Content.ReadAsStreamAsync(call);
                var buffer = new byte[MaxResponseBytes + 1];
                var read = 0;
                int n;
                while (read < buffer.Length && (n = await stream.ReadAsync(buffer.AsMemory(read), call)) > 0)
                {
                    read += n;
                }

                return read > MaxResponseBytes
                    ? new ProviderResponse(ProviderOutcome.TooLarge)
                    : new ProviderResponse(ProviderOutcome.Ok, Encoding.UTF8.GetString(buffer, 0, read));
            }
            catch (HttpRequestException ex)
            {
                return new ProviderResponse(ex.HttpRequestError == HttpRequestError.SecureConnectionError ? ProviderOutcome.Tls : ProviderOutcome.Unreachable);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new ProviderResponse(ProviderOutcome.Unreachable); // the timeout, during the headers or the body
            }
        }
    }
}

/// <summary>A candidate provider's metadata as the test uses it, from its discovery document and key set.</summary>
public sealed record ProviderMetadata(string Issuer, string AuthorizationEndpoint, string TokenEndpoint, string JwksUri);

/// <summary>Reads and checks a candidate's discovery document and keys (identity design §19): each failure is a <see cref="SetupTestReasons"/> code.</summary>
public static class SetupDiscovery
{
    /// <summary>The discovery document's address, as the OpenID Connect handler derives it from the authority.</summary>
    public static Uri MetadataAddress(string authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        return new Uri(authority.EndsWith('/') ? authority + ".well-known/openid-configuration" : authority + "/.well-known/openid-configuration");
    }

    /// <summary>Whether <paramref name="uri"/> may be called: https, or http in Development (as <c>RequireHttpsMetadata</c>); no user info.</summary>
    public static bool Allowed(Uri uri, bool requireHttps)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return uri.IsAbsoluteUri && uri.UserInfo.Length == 0 && (uri.Scheme == Uri.UriSchemeHttps || (!requireHttps && uri.Scheme == Uri.UriSchemeHttp));
    }

    /// <summary>The metadata, or the refusal's reason.</summary>
    public static async Task<(ProviderMetadata? Metadata, string? Refusal)> FetchAsync(SetupProviderClient provider, string authority, bool requireHttps, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var response = await provider.GetAsync(MetadataAddress(authority), ct);
        if (Refusal(response.Outcome, SetupTestReasons.AuthorityUnreachable, SetupTestReasons.NotDiscovery) is { } refused)
        {
            return (null, refused);
        }

        OpenIdConnectConfiguration document;
        try
        {
            using (var json = JsonDocument.Parse(response.Body!))
            {
                if (json.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return (null, SetupTestReasons.NotDiscovery);
                }
            }

            document = OpenIdConnectConfiguration.Create(response.Body);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or FormatException)
        {
            return (null, SetupTestReasons.NotDiscovery);
        }

        string[] endpoints = [document.AuthorizationEndpoint, document.TokenEndpoint, document.JwksUri];
        if (string.IsNullOrEmpty(document.Issuer) || endpoints.Any(e => !Uri.TryCreate(e, UriKind.Absolute, out _)))
        {
            return (null, SetupTestReasons.NotDiscovery);
        }

        // OpenID Connect Discovery 1.0 §4.3: the issuer is the authority the document was fetched for.
        if (!string.Equals(document.Issuer.TrimEnd('/'), authority.TrimEnd('/'), StringComparison.Ordinal))
        {
            return (null, SetupTestReasons.IssuerMismatch);
        }

        if (endpoints.Any(e => !Allowed(new Uri(e), requireHttps)))
        {
            return (null, SetupTestReasons.InsecureEndpoint);
        }

        if (document.RequirePushedAuthorizationRequests)
        {
            return (null, SetupTestReasons.ParRequired);
        }

        return (new ProviderMetadata(document.Issuer, document.AuthorizationEndpoint, document.TokenEndpoint, document.JwksUri), null);
    }

    /// <summary>The provider's signing keys, or null when they cannot be fetched or there are none.</summary>
    public static async Task<IList<SecurityKey>?> KeysAsync(SetupProviderClient provider, ProviderMetadata metadata, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(metadata);
        var response = await provider.GetAsync(new Uri(metadata.JwksUri), ct);
        if (response.Outcome != ProviderOutcome.Ok)
        {
            return null;
        }

        try
        {
            var keys = new JsonWebKeySet(response.Body).GetSigningKeys();
            return keys.Count > 0 ? keys : null;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>The reason for a failed call: TLS and redirects have their own; an unreachable host <paramref name="unreachable"/>; else <paramref name="refused"/>.</summary>
    public static string? Refusal(ProviderOutcome outcome, string unreachable, string refused) => outcome switch
    {
        ProviderOutcome.Ok => null,
        ProviderOutcome.Tls => SetupTestReasons.Tls,
        ProviderOutcome.Redirected => SetupTestReasons.Redirected,
        ProviderOutcome.Unreachable => unreachable,
        _ => refused,
    };
}
