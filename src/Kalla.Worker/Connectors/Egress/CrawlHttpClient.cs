using System.Net;
using System.Net.Security;
using System.Net.Sockets;

namespace Kalla.Worker.Connectors.Egress;

/// <summary>
/// The one HTTP client for all crawling (connectors design §7): a <see cref="SocketsHttpHandler"/> whose
/// <c>ConnectCallback</c> resolves through <see cref="IEgressGuard"/> and connects only to the checked addresses, so DNS
/// rebinding has no window. TLS still uses the URL host for SNI and the certificate check. No redirects (the fetcher
/// follows them and re-checks each hop), no proxy, no cookies. Not built through <c>IHttpClientFactory</c>, so no
/// resilience handler retries or follows anything behind the fetcher's back.
/// </summary>
public sealed class CrawlHttpClient : IDisposable
{
    /// <summary>The request option that carries the purpose to the connect callback (event 5101's <c>Purpose</c>).</summary>
    public static readonly HttpRequestOptionsKey<EgressPurpose> PurposeKey = new("Kalla.EgressPurpose");

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    public CrawlHttpClient(IEgressGuard guard)
        : this(guard, null)
    {
    }

    /// <summary>Tests only: <paramref name="certificateValidation"/> trusts a test site's self-signed certificate.</summary>
    internal CrawlHttpClient(IEgressGuard guard, RemoteCertificateValidationCallback? certificateValidation)
    {
        ArgumentNullException.ThrowIfNull(guard);
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            // Counted after decompression by the fetcher, so a gzip bomb hits the body cap (§4).
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = ConnectTimeout,
            ConnectCallback = (context, cancellationToken) => ConnectAsync(guard, context, cancellationToken),
        };
        if (certificateValidation is not null)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = certificateValidation;
        }

        Http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public HttpClient Http { get; }

    public void Dispose() => Http.Dispose();

    private static async ValueTask<Stream> ConnectAsync(IEgressGuard guard, SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var purpose = context.InitialRequestMessage.Options.TryGetValue(PurposeKey, out var value) ? value : EgressPurpose.Fetch;
        var addresses = await guard.ResolveAsync(context.DnsEndPoint.Host, purpose, cancellationToken);
        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException e)
            {
                socket.Dispose();
                last = e;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw last ?? new SocketException((int)SocketError.HostNotFound);
    }
}
