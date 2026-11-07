using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace Ume.Rag.Worker.Connectors.Egress;

/// <summary>Why a request leaves the worker (event 5101's <c>Purpose</c>).</summary>
public enum EgressPurpose
{
    Fetch,
    Robots,
    Sitemap,
    Preflight,
    Preview,

    /// <summary>A request the browser made while rendering a page, fetched by the worker (connectors design §8.3).</summary>
    Render,
}

/// <summary>The guard's verdict on a URL or an address; <see cref="Reason"/> and <see cref="AddressClass"/> are 5101's attributes.</summary>
public sealed record EgressDecision(string? Reason, string? AddressClass)
{
    public static EgressDecision Allowed { get; } = new(null, null);

    public bool IsAllowed => Reason is null;
}

/// <summary>A refused destination: the fetch ends <c>blocked</c> with reason <c>egress.&lt;Reason&gt;</c>.</summary>
public sealed class EgressBlockedException : Exception
{
    public EgressBlockedException(EgressDecision decision)
        : base($"Egress refused: {decision.Reason} ({decision.AddressClass ?? "-"}).")
    {
        Decision = decision;
    }

    public EgressDecision Decision { get; }
}

/// <summary>
/// The SSRF guard (connectors design §7). <see cref="CheckUrl"/> judges scheme, port, userinfo, blocked hosts and literal
/// addresses; <see cref="ResolveAsync"/> resolves a host once and refuses it if <b>any</b> address is blocked. The crawl
/// handler connects only to the returned addresses (<see cref="CrawlHttpClient"/>), so there is no second lookup and no
/// rebinding window.
/// </summary>
public interface IEgressGuard
{
    EgressDecision CheckUrl(Uri url, EgressPurpose purpose);

    /// <summary>Allowed addresses of <paramref name="asciiHost"/>, IPv4 first; throws <see cref="EgressBlockedException"/>.</summary>
    ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string asciiHost, EgressPurpose purpose, CancellationToken cancellationToken);
}

/// <summary>Name resolution behind the guard; a fake in tests (rebinding, mixed answers).</summary>
public interface IDnsResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken);
}

public sealed class SystemDnsResolver : IDnsResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
        Dns.GetHostAddressesAsync(host, cancellationToken);
}

/// <summary>
/// The run-time part of the guard's rules (<c>Settings.Crawl</c>: <c>AllowedPorts</c> beyond 80 and 443, hosts blocked by a
/// platform admin). Read through <see cref="IEgressPolicySource"/>.
/// </summary>
public sealed record EgressPolicy(IReadOnlySet<int> AllowedPorts, IReadOnlySet<string> BlockedHosts)
{
    public static EgressPolicy Default { get; } = new(new HashSet<int>(), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
}

/// <summary>Where the guard reads <see cref="EgressPolicy"/> (<c>Settings.Crawl</c>, <c>PlatformCrawlSettingsSource</c>).</summary>
public interface IEgressPolicySource
{
    EgressPolicy Current { get; }
}

public sealed class EgressGuard : IEgressGuard
{
    public const string PrivateAddress = "private_address";
    public const string EmbeddedAddress = "embedded_address";
    public const string Scheme = "scheme";
    public const string Port = "port";
    public const string UserInfo = "userinfo";
    public const string BlockedHost = "blocked_host";
    public const string RedirectDowngrade = "redirect_downgrade";

    private static readonly (IPNetwork Network, string Class)[] BlockedV4 =
    [
        (IPNetwork.Parse("0.0.0.0/8"), "reserved"),
        (IPNetwork.Parse("10.0.0.0/8"), "rfc1918"),
        (IPNetwork.Parse("100.64.0.0/10"), "cgnat"),
        (IPNetwork.Parse("127.0.0.0/8"), "loopback"),
        (IPNetwork.Parse("169.254.0.0/16"), "link_local"),
        (IPNetwork.Parse("172.16.0.0/12"), "rfc1918"),
        (IPNetwork.Parse("192.0.0.0/24"), "reserved"),
        (IPNetwork.Parse("192.0.2.0/24"), "reserved"),
        (IPNetwork.Parse("192.88.99.0/24"), "reserved"),
        (IPNetwork.Parse("192.168.0.0/16"), "rfc1918"),
        (IPNetwork.Parse("198.18.0.0/15"), "reserved"),
        (IPNetwork.Parse("198.51.100.0/24"), "reserved"),
        (IPNetwork.Parse("203.0.113.0/24"), "reserved"),
        (IPNetwork.Parse("224.0.0.0/4"), "multicast"),
        (IPNetwork.Parse("240.0.0.0/4"), "reserved"),
    ];

    private static readonly (IPNetwork Network, string Class)[] BlockedV6 =
    [
        (IPNetwork.Parse("::/128"), "reserved"),
        (IPNetwork.Parse("::1/128"), "loopback"),
        (IPNetwork.Parse("fc00::/7"), "ula"),
        (IPNetwork.Parse("fe80::/10"), "link_local"),
        (IPNetwork.Parse("ff00::/8"), "multicast"),
        (IPNetwork.Parse("2001:db8::/32"), "reserved"),
        (IPNetwork.Parse("100::/64"), "reserved"),
        // Local-use NAT64 (RFC 8215): where the IPv4 sits depends on the operator's prefix length, so the range is refused.
        (IPNetwork.Parse("64:ff9b:1::/48"), "reserved"),
        // IPv4-compatible addresses (deprecated, RFC 4291 §2.5.5.1).
        (IPNetwork.Parse("::/96"), "reserved"),
    ];

    private static readonly IPNetwork GlobalUnicastV6 = IPNetwork.Parse("2000::/3");
    private static readonly IPNetwork Nat64 = IPNetwork.Parse("64:ff9b::/96");
    private static readonly IPNetwork SixToFour = IPNetwork.Parse("2002::/16");
    private static readonly IPNetwork Teredo = IPNetwork.Parse("2001::/32");

    private readonly IReadOnlyList<IPNetwork> _allowed;
    private readonly IDnsResolver _dns;
    private readonly IEgressPolicySource _policy;
    private readonly ConnectorMetrics _metrics;

    public EgressGuard(IOptions<CrawlOptions> options, IDnsResolver dns, IEgressPolicySource policy, ConnectorMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(options);
        _allowed = options.Value.AllowedNetworks();
        _dns = dns;
        _policy = policy;
        _metrics = metrics;
    }

    public EgressDecision CheckUrl(Uri url, EgressPurpose purpose)
    {
        ArgumentNullException.ThrowIfNull(url);
        var decision = Check(url);
        if (!decision.IsAllowed)
        {
            _metrics.EgressBlocked(decision.Reason!, purpose);
        }

        return decision;
    }

    public async ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string asciiHost, EgressPurpose purpose, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(asciiHost);
        var literal = asciiHost.Trim('[', ']');
        var addresses = IPAddress.TryParse(literal, out var address) ? [address] : await _dns.ResolveAsync(asciiHost, cancellationToken);
        if (addresses.Length == 0)
        {
            throw new SocketException((int)SocketError.HostNotFound);
        }

        // Any blocked address refuses the host, so a round-robin answer cannot win by luck.
        foreach (var candidate in addresses)
        {
            var decision = CheckAddress(candidate);
            if (!decision.IsAllowed)
            {
                _metrics.EgressBlocked(decision.Reason!, purpose);
                throw new EgressBlockedException(decision);
            }
        }

        return [.. addresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)];
    }

    /// <summary>
    /// The verdict on one address: blocked ranges (§7) unless inside <c>Crawl:AllowedPrivateRanges</c>; addresses that embed
    /// an IPv4 address are judged by it too.
    /// </summary>
    public EgressDecision CheckAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (_allowed.Any(n => n.Contains(address)))
        {
            return EgressDecision.Allowed;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return Classify(address, BlockedV4) is { } v4Class ? new(PrivateAddress, v4Class) : EgressDecision.Allowed;
        }

        if (Classify(address, BlockedV6) is { } v6Class)
        {
            return new(PrivateAddress, v6Class);
        }

        foreach (var embedded in EmbeddedV4(address))
        {
            if (!_allowed.Any(n => n.Contains(embedded)) && Classify(embedded, BlockedV4) is { } embeddedClass)
            {
                return new(EmbeddedAddress, embeddedClass);
            }
        }

        return Nat64.Contains(address) || GlobalUnicastV6.Contains(address) ? EgressDecision.Allowed : new(PrivateAddress, "reserved");
    }

    private EgressDecision Check(Uri url)
    {
        if (!url.IsAbsoluteUri || url.Scheme is not ("http" or "https"))
        {
            return new(Scheme, null);
        }

        if (url.UserInfo.Length > 0)
        {
            return new(UserInfo, null);
        }

        var policy = _policy.Current;
        if (url.Port is not (80 or 443) && !policy.AllowedPorts.Contains(url.Port))
        {
            return new(Port, null);
        }

        var host = url.IdnHost.TrimEnd('.');
        if (policy.BlockedHosts.Any(b => string.Equals(host, b, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("." + b, StringComparison.OrdinalIgnoreCase)))
        {
            return new(BlockedHost, null);
        }

        return url.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6 && IPAddress.TryParse(host.Trim('[', ']'), out var literal)
            ? CheckAddress(literal)
            : EgressDecision.Allowed;
    }

    private static string? Classify(IPAddress address, (IPNetwork Network, string Class)[] ranges)
    {
        foreach (var (network, addressClass) in ranges)
        {
            if (network.Contains(address))
            {
                return addressClass;
            }
        }

        return null;
    }

    /// <summary>The IPv4 addresses inside NAT64 (<c>64:ff9b::/96</c>), 6to4 (<c>2002::/16</c>) and Teredo (<c>2001::/32</c>).</summary>
    private static IEnumerable<IPAddress> EmbeddedV4(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (Nat64.Contains(address))
        {
            yield return new IPAddress(bytes.AsSpan(12, 4));
        }
        else if (SixToFour.Contains(address))
        {
            yield return new IPAddress(bytes.AsSpan(2, 4));
        }
        else if (Teredo.Contains(address))
        {
            // Server at bits 32–63; client at bits 96–127, inverted (RFC 4380 §4).
            yield return new IPAddress(bytes.AsSpan(4, 4));
            var client = bytes[12..16];
            for (var i = 0; i < client.Length; i++)
            {
                client[i] = (byte)~client[i];
            }

            yield return new IPAddress(client);
        }
    }
}
