using System.Net;
using Ume.Rag.Domain.Services;

namespace Ume.Rag.Domain.Tests;

/// <summary>Copied from the gateway (<c>tests/Ume.LlmGateway.Domain.Tests/NetworkRangesTests.cs</c> at e25b491).</summary>
public class NetworkRangesTests
{
    [Theory]
    [InlineData("10.20.0.0/16", "10.20.0.0/16")]
    [InlineData(" 192.0.2.10 ", "192.0.2.10")]
    [InlineData("192.0.2.10/32", "192.0.2.10")]
    [InlineData("2001:DB8:0:0::1", "2001:db8::1")]
    [InlineData("2001:db8::/32", "2001:db8::/32")]
    [InlineData("::ffff:192.0.2.10", "192.0.2.10")]
    [InlineData("0.0.0.0/0", "0.0.0.0/0")]
    public void Accepts_addresses_and_networks_in_canonical_form(string input, string canonical)
    {
        NetworkRanges.Normalize([input], out var result).ShouldBeNull();
        result.ShouldBe([canonical]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("10")]
    [InlineData("10.1")]
    [InlineData("example.se")]
    [InlineData("10.0.0.0/33")]
    [InlineData("10.0.0.0/-1")]
    [InlineData("10.0.0.0/ 8")]
    [InlineData("fe80::1%eth0")]
    [InlineData("::ffff:10.0.0.0/104")]
    public void Rejects_anything_else(string input)
    {
        NetworkRanges.TryParse(input, out _, out var error).ShouldBeFalse();
        error.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void Network_with_host_bits_suggests_the_network_address()
    {
        NetworkRanges.TryParse("10.20.1.5/16", out _, out var error).ShouldBeFalse();
        error!.ShouldContain("10.20.0.0/16");
    }

    [Fact]
    public void Duplicates_are_removed_and_the_size_is_capped()
    {
        NetworkRanges.Normalize(["192.0.2.1", "192.0.2.1/32", "::ffff:192.0.2.1"], out var result).ShouldBeNull();
        result.ShouldBe(["192.0.2.1"]);

        NetworkRanges.Normalize(Enumerable.Range(0, NetworkRanges.MaxEntries + 1).Select(i => $"10.0.{i / 256}.{i % 256}"), out _).ShouldNotBeNull();
    }

    [Theory]
    [InlineData("10.20.30.40", true)]
    [InlineData("::ffff:10.20.30.40", true)]
    [InlineData("10.21.0.1", false)]
    [InlineData("2001:db8::5", true)]
    [InlineData("2001:db9::5", false)]
    public void Contains_matches_either_family(string address, bool expected)
    {
        IPNetwork[] networks = [IPNetwork.Parse("10.20.0.0/16"), IPNetwork.Parse("2001:db8::/32")];
        NetworkRanges.Contains(networks, IPAddress.Parse(address)).ShouldBe(expected);
    }
}
