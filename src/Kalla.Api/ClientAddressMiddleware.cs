using System.Collections.Immutable;
using System.Net;
using Kalla.Infrastructure.Access;
using Kalla.Infrastructure.Security;

namespace Kalla.Api;

/// <summary>
/// Copied from the gateway (<c>Fors.Gateway/Authentication/ClientAddressMiddleware.cs</c>), adapted to read the
/// trusted proxies from the access snapshot (<c>Settings.TrustedProxies</c>, the admin's network settings) instead of the
/// gateway's catalogue. Replaces the connection address with the client address from <c>X-Forwarded-For</c> when, and only
/// when, the request came through a trusted proxy. Everything downstream (key address allow-lists, the auth-failure
/// throttle, refused-credential events, the downloads limiter) then sees the real client. With no trusted proxies the header
/// is ignored and nothing is parsed. Before the first snapshot load the list is empty, so the connection address is used.
/// </summary>
public sealed class ClientAddressMiddleware(RequestDelegate next, IPolicySnapshotSource snapshots)
{
    public const string ForwardedForHeader = ClientAddresses.ForwardedForHeader;

    public Task InvokeAsync(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        Apply(http, snapshots.Current.TrustedProxies);
        return next(http);
    }

    private static void Apply(HttpContext http, ImmutableArray<IPNetwork> trusted)
    {
        if (trusted.IsEmpty)
        {
            return;
        }

        var remote = http.Connection.RemoteIpAddress;
        var client = ClientAddresses.Resolve(remote, http.Request.Headers[ForwardedForHeader], trusted.AsSpan());
        if (!ReferenceEquals(client, remote))
        {
            http.Connection.RemoteIpAddress = client;
        }
    }
}
