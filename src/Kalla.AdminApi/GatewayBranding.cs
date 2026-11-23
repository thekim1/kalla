using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Kalla.Infrastructure.Gateway;

namespace Kalla.AdminApi;

/// <summary>
/// The organisation's logo as the gateway's admin console brands it, shown beside the organisation name (the context's
/// <c>organisation_name</c>) in the admin UI's menu and on its sign-in page. The gateway serves it on its Integration API
/// (<c>GET /integration/v1/branding/logo</c>, feature <see cref="GatewayFeatures.OrganisationLogoFeature"/>), read with
/// kalla-admin's credential key at the address the context names (<see cref="GatewayFeatures.IntegrationApiUrl"/>), as
/// <see cref="IntegrationApiClient"/> does. Held in memory and checked again with its <c>ETag</c> after
/// <see cref="RefreshAfter"/>. kalla-admin serves it from its own origin (<see cref="LogoPath"/>), so the admin UI's
/// Content-Security-Policy (<c>img-src 'self' data:</c>) stays as it is and the browser never needs the gateway's host.
/// Without the feature, an address or a logo there is none, and the admin UI shows its built-in mark; a failed read keeps
/// the logo it had.
/// </summary>
public sealed partial class GatewayBranding(IHttpClientFactory clients, IOptionsMonitor<GatewayOptions> options, GatewayFeatures features, TimeProvider time,
    ILogger<GatewayBranding> logger) : IDisposable
{
    public const string LogoPath = "/bff/branding/logo";

    /// <summary>Its own client (one try, no resilience handler, as <see cref="IntegrationApiClient"/>'s), so its reads stay apart.</summary>
    public const string HttpClientName = "gateway-branding";

    /// <summary>How long a read holds; a change in the gateway shows after this at the latest.</summary>
    public static readonly TimeSpan RefreshAfter = TimeSpan.FromMinutes(5);

    /// <summary>One read's limit: <c>/bff/user</c> waits for it, so a slow gateway must not hold up the page.</summary>
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(3);

    /// <summary>The gateway's own limit for an uploaded logo (its <c>Branding.MaxLogoBytes</c>).</summary>
    public const int MaxLogoBytes = 256 * 1024;

    private readonly SemaphoreSlim _reading = new(1, 1);
    private Logo? _logo;
    private DateTimeOffset _readAt = DateTimeOffset.MinValue;

    /// <summary>A logo read from the gateway. <c>Version</c>: a short hash of the image, which makes its URL change with it.</summary>
    public sealed record Logo(byte[] Data, string ContentType, string Version, EntityTagHeaderValue? ETag)
    {
        public string Url => $"{LogoPath}?v={Version}";
    }

    /// <summary>The logo, read again when the last read is older than <see cref="RefreshAfter"/>; null when there is none.</summary>
    public async Task<Logo?> GetAsync(CancellationToken ct)
    {
        if (time.GetUtcNow() - _readAt < RefreshAfter)
        {
            return Volatile.Read(ref _logo);
        }

        await _reading.WaitAsync(ct);
        try
        {
            if (time.GetUtcNow() - _readAt >= RefreshAfter)
            {
                await ReadAsync(ct);
            }

            return Volatile.Read(ref _logo);
        }
        finally
        {
            _reading.Release();
        }
    }

    private async Task ReadAsync(CancellationToken ct)
    {
        // Read once: a context read may change them meanwhile. Before the first context answer nothing is known yet, so
        // nothing is held either and the next request asks again.
        var address = features.IntegrationApiUrl;
        if (!features.OrganisationLogo || !IntegrationApiClient.Usable(address, options.CurrentValue.BaseUrl))
        {
            Volatile.Write(ref _logo, null);
            return;
        }

        var previous = Volatile.Read(ref _logo);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(address.TrimEnd('/') + "/"), "branding/logo"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.CurrentValue.Key);
        if (previous?.ETag is { } etag)
        {
            request.Headers.IfNoneMatch.Add(etag);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ReadTimeout);
        try
        {
            using var response = await clients.CreateClient(HttpClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            switch (response.StatusCode)
            {
                case HttpStatusCode.NotModified when previous is not null:
                    break;
                case HttpStatusCode.NotFound:
                    Volatile.Write(ref _logo, null); // the gateway shows its built-in icon
                    break;
                case HttpStatusCode.OK:
                    Volatile.Write(ref _logo, await ParseAsync(response, timeout.Token) ?? previous);
                    break;
                default:
                    LogFailed(logger, (int)response.StatusCode);
                    break;
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // The logo is decoration: no failure on this path (the body read's own timeout among them) may fail /bff/user.
            LogFailed(logger, null);
        }

        // A failure is held as long as an answer, so a gateway that is down is asked every few minutes, not on every page.
        _readAt = time.GetUtcNow();
    }

    /// <summary>The image when its type is one the gateway accepts for a logo and its content is of that type; else null.</summary>
    private async Task<Logo?> ParseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (response.Content.Headers.ContentLength > MaxLogoBytes)
        {
            LogRefused(logger, contentType, "too large");
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxLogoBytes)
            {
                LogRefused(logger, contentType, "too large");
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        var data = buffer.ToArray();
        if (contentType is null || Detect(data) != contentType)
        {
            LogRefused(logger, contentType, "not a PNG, JPEG, WebP or SVG image of the type it names");
            return null;
        }

        return new Logo(data, contentType, Convert.ToHexStringLower(SHA256.HashData(data))[..16], response.Headers.ETag);
    }

    /// <summary>The logo's type from its first bytes, as the gateway detects it on upload (an SVG: an <c>&lt;svg</c> element).</summary>
    public static string? Detect(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return "image/png";
        }

        if (data.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
        {
            return "image/jpeg";
        }

        if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }

        return data.IndexOf("<svg"u8) >= 0 ? "image/svg+xml" : null;
    }

    public void Dispose() => _reading.Dispose();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reading the gateway's logo failed (HTTP {Status}); the admin UI keeps the logo it had")]
    private static partial void LogFailed(ILogger logger, int? status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The gateway's logo was refused ({ContentType}: {Reason}); the admin UI keeps the logo it had")]
    private static partial void LogRefused(ILogger logger, string? contentType, string reason);
}

/// <summary>The logo for every visitor, signed in or not (the sign-in page shows it), from kalla-admin's own origin.</summary>
public static class GatewayBrandingEndpoints
{
    public static void MapGatewayBranding(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet(GatewayBranding.LogoPath, async (string? v, GatewayBranding branding, HttpContext http, CancellationToken ct) =>
        {
            if (await branding.GetAsync(ct) is not { } logo)
            {
                return Results.NotFound();
            }

            // The URL names the logo's hash, so a matching request can be cached for good; any other gets the current logo uncached.
            http.Response.Headers.CacheControl = v == logo.Version ? "public, max-age=31536000, immutable" : "no-cache";
            // Opened directly, an SVG would be a document on this origin: no scripts, no requests, sandboxed (as the gateway serves it).
            http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; img-src data:; style-src 'unsafe-inline'; sandbox";
            return Results.Bytes(logo.Data, logo.ContentType);
        }).AllowAnonymous().WithTags("Branding").WithName("GetBrandingLogo").WithSummary("The organisation's logo from the gateway's admin console");
    }
}
