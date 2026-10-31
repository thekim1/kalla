using System.Net;
using Microsoft.Extensions.Primitives;
using Ume.Rag.Infrastructure.Security;

namespace Ume.Rag.AdminApi.Tests;

/// <summary>
/// The copied <see cref="ClientAddresses"/> (identity design §1.2, item 14): <c>X-Forwarded-For</c> is believed only from
/// trusted proxies, walked from the right; entries left of the first untrusted hop are never believed.
/// </summary>
public sealed class ClientAddressesTests
{
    private static readonly IPNetwork[] Proxies = [IPNetwork.Parse("10.0.0.0/24"), IPNetwork.Parse("10.0.1.5/32")];
    private static readonly IPAddress Proxy = IPAddress.Parse("10.0.0.7");

    [Fact]
    public void An_untrusted_connection_is_the_client_whatever_the_header_says() =>
        ClientAddresses.Resolve(IPAddress.Parse("198.51.100.9"), new StringValues("203.0.113.1"), Proxies)!.ToString().ShouldBe("198.51.100.9");

    [Fact]
    public void Behind_trusted_proxies_the_nearest_untrusted_hop_is_the_client()
    {
        ClientAddresses.Resolve(Proxy, new StringValues("203.0.113.1, 10.0.1.5"), Proxies)!.ToString().ShouldBe("203.0.113.1");
        // A client prepending a forged address does not move the result.
        ClientAddresses.Resolve(Proxy, new StringValues("1.2.3.4, 203.0.113.1"), Proxies)!.ToString().ShouldBe("203.0.113.1");
        // Several header lines are one list; a port is dropped.
        ClientAddresses.Resolve(Proxy, new StringValues(["203.0.113.1:5555", "10.0.0.8"]), Proxies)!.ToString().ShouldBe("203.0.113.1");
    }

    [Fact]
    public void No_trusted_proxies_or_no_header_keeps_the_connection_address()
    {
        ClientAddresses.Resolve(Proxy, new StringValues("203.0.113.1"), []).ShouldBe(Proxy);
        ClientAddresses.Resolve(Proxy, StringValues.Empty, Proxies)!.ToString().ShouldBe(Proxy.ToString());
    }

    [Theory]
    [InlineData("203.0.113.77", SourceAddressMode.Full, "203.0.113.77")]
    [InlineData("203.0.113.77", SourceAddressMode.Truncated, "203.0.113.0")]
    [InlineData("2001:db8:1:2:3::9", SourceAddressMode.Truncated, "2001:db8:1::")]
    [InlineData("203.0.113.77", SourceAddressMode.None, null)]
    public void Format_keeps_as_much_as_the_mode_says(string address, SourceAddressMode mode, string? expected) =>
        ClientAddresses.Format(IPAddress.Parse(address), mode).ShouldBe(expected);
}
