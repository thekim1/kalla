using System.Net;
using Kalla.Worker.Connectors.Egress;
using Kalla.Worker.Connectors.Urls;

namespace Kalla.Worker.Tests.Connectors;

/// <summary>SSRF fixtures of connectors design §18 against a fake resolver: every blocked range, embedded IPv4, mixed answers.</summary>
public sealed class EgressGuardTests
{
    [Theory]
    [InlineData("0.0.0.1", "reserved")]
    [InlineData("10.1.2.3", "rfc1918")]
    [InlineData("100.64.0.1", "cgnat")]
    [InlineData("100.127.255.254", "cgnat")]
    [InlineData("127.0.0.1", "loopback")]
    [InlineData("127.255.255.254", "loopback")]
    [InlineData("169.254.169.254", "link_local")]
    [InlineData("172.16.0.1", "rfc1918")]
    [InlineData("172.30.4.10", "rfc1918")]
    [InlineData("172.31.255.255", "rfc1918")]
    [InlineData("192.0.0.8", "reserved")]
    [InlineData("192.0.2.1", "reserved")]
    [InlineData("192.88.99.1", "reserved")]
    [InlineData("192.168.1.1", "rfc1918")]
    [InlineData("198.18.0.1", "reserved")]
    [InlineData("198.19.255.255", "reserved")]
    [InlineData("198.51.100.7", "reserved")]
    [InlineData("203.0.113.9", "reserved")]
    [InlineData("224.0.0.251", "multicast")]
    [InlineData("239.255.255.250", "multicast")]
    [InlineData("240.0.0.1", "reserved")]
    [InlineData("255.255.255.255", "reserved")]
    [InlineData("::", "reserved")]
    [InlineData("::1", "loopback")]
    [InlineData("fc00::1", "ula")]
    [InlineData("fd12:3456::1", "ula")]
    [InlineData("fe80::1", "link_local")]
    [InlineData("ff02::1", "multicast")]
    [InlineData("2001:db8::1", "reserved")]
    [InlineData("100::1", "reserved")]
    [InlineData("64:ff9b:1::a00:1", "reserved")]
    [InlineData("::7f00:1", "reserved")]
    [InlineData("4000::1", "reserved")]
    public void Blocks_every_private_and_reserved_range(string address, string addressClass)
    {
        var decision = ConnectorTestKit.Guard(new FakeDnsResolver()).CheckAddress(IPAddress.Parse(address));

        decision.Reason.ShouldBe(EgressGuard.PrivateAddress);
        decision.AddressClass.ShouldBe(addressClass);
    }

    [Theory]
    // IPv4-mapped is judged as the IPv4 address it is.
    [InlineData("::ffff:127.0.0.1", EgressGuard.PrivateAddress, "loopback")]
    [InlineData("::ffff:169.254.169.254", EgressGuard.PrivateAddress, "link_local")]
    // NAT64, 6to4 and Teredo carry IPv4 inside.
    [InlineData("64:ff9b::a9fe:a9fe", EgressGuard.EmbeddedAddress, "link_local")]
    [InlineData("64:ff9b::7f00:1", EgressGuard.EmbeddedAddress, "loopback")]
    [InlineData("2002:a00:1::1", EgressGuard.EmbeddedAddress, "rfc1918")]
    [InlineData("2002:c0a8:101::1", EgressGuard.EmbeddedAddress, "rfc1918")]
    // Teredo: server 10.0.0.1 at bits 32-63.
    [InlineData("2001:0:a00:1::1", EgressGuard.EmbeddedAddress, "rfc1918")]
    // Teredo: client 127.0.0.1 in the last 32 bits, inverted (0x80ff:fffe).
    [InlineData("2001:0:5db8:d3b::80ff:fffe", EgressGuard.EmbeddedAddress, "loopback")]
    public void Judges_addresses_that_embed_ipv4_by_the_embedded_address(string address, string reason, string addressClass)
    {
        var decision = ConnectorTestKit.Guard(new FakeDnsResolver()).CheckAddress(IPAddress.Parse(address));

        decision.Reason.ShouldBe(reason);
        decision.AddressClass.ShouldBe(addressClass);
    }

    [Theory]
    [InlineData("93.184.215.14")]
    [InlineData("8.8.8.8")]
    [InlineData("2a00:1450:4001::1")]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("64:ff9b::808:808")]
    [InlineData("2002:808:808::1")]
    public void Allows_global_addresses(string address) =>
        ConnectorTestKit.Guard(new FakeDnsResolver()).CheckAddress(IPAddress.Parse(address)).IsAllowed.ShouldBeTrue();

    [Fact]
    public void Allowed_private_ranges_lift_the_block_for_their_addresses_only()
    {
        var guard = ConnectorTestKit.Guard(new FakeDnsResolver(), allowedPrivateRanges: "10.20.0.0/16, 192.168.5.7/32");

        guard.CheckAddress(IPAddress.Parse("10.20.3.4")).IsAllowed.ShouldBeTrue();
        guard.CheckAddress(IPAddress.Parse("::ffff:10.20.3.4")).IsAllowed.ShouldBeTrue();
        guard.CheckAddress(IPAddress.Parse("192.168.5.7")).IsAllowed.ShouldBeTrue();
        guard.CheckAddress(IPAddress.Parse("10.21.0.1")).IsAllowed.ShouldBeFalse();
        guard.CheckAddress(IPAddress.Parse("192.168.5.8")).IsAllowed.ShouldBeFalse();
        guard.CheckAddress(IPAddress.Parse("169.254.169.254")).IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public void An_invalid_allowed_range_fails_validation()
    {
        var result = new CrawlOptionsValidator().Validate(null, new CrawlOptions { AllowedPrivateRanges = "10.0.0.0/8,10.0.0.300/8" });

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("10.0.0.300/8");
    }

    [Theory]
    [InlineData("")]
    [InlineData("/1.0")]
    [InlineData("Umeå Bot/1.0")]
    [InlineData("Example.Bot/1.0")]
    public void An_invalid_user_agent_fails_validation(string userAgent) =>
        new CrawlOptionsValidator().Validate(null, new CrawlOptions { UserAgent = userAgent }).FailureMessage!.ShouldContain("Crawl:UserAgent");

    [Theory]
    [InlineData("UmeaKommunBot/1.0")]
    [InlineData("Example-Bot/2.0 (+https://www.example.org/bot)")]
    [InlineData("ExampleBot")]
    public void A_user_agent_with_a_product_token_passes_validation(string userAgent) =>
        new CrawlOptionsValidator().Validate(null, new CrawlOptions { UserAgent = userAgent }).Succeeded.ShouldBeTrue();

    [Fact]
    public async Task Refuses_a_host_when_any_resolved_address_is_blocked()
    {
        var dns = new FakeDnsResolver().Add("mixed.example", "93.184.215.14", "10.0.0.5").Add("public.example", "93.184.215.14", "2a00:1450:4001::1");
        var guard = ConnectorTestKit.Guard(dns);

        var refused = await Should.ThrowAsync<EgressBlockedException>(
            async () => await guard.ResolveAsync("mixed.example", EgressPurpose.Fetch, TestContext.Current.CancellationToken));
        refused.Decision.ShouldBe(new EgressDecision(EgressGuard.PrivateAddress, "rfc1918"));

        var allowed = await guard.ResolveAsync("public.example", EgressPurpose.Fetch, TestContext.Current.CancellationToken);
        allowed.Select(a => a.ToString()).ShouldBe(["93.184.215.14", "2a00:1450:4001::1"]);
    }

    [Fact]
    public async Task Ipv4_is_tried_first()
    {
        var dns = new FakeDnsResolver().Add("dual.example", "2a00:1450:4001::1", "93.184.215.14");

        var addresses = await ConnectorTestKit.Guard(dns).ResolveAsync("dual.example", EgressPurpose.Fetch, TestContext.Current.CancellationToken);

        addresses[0].ToString().ShouldBe("93.184.215.14");
    }

    [Fact]
    public async Task Literal_addresses_are_checked_without_a_lookup()
    {
        var dns = new FakeDnsResolver();
        var guard = ConnectorTestKit.Guard(dns);

        await Should.ThrowAsync<EgressBlockedException>(async () => await guard.ResolveAsync("127.0.0.1", EgressPurpose.Fetch, TestContext.Current.CancellationToken));
        await Should.ThrowAsync<EgressBlockedException>(async () => await guard.ResolveAsync("[::1]", EgressPurpose.Fetch, TestContext.Current.CancellationToken));
        dns.Calls("127.0.0.1").ShouldBe(0);
    }

    [Theory]
    [InlineData("ftp://example.org/", EgressGuard.Scheme)]
    [InlineData("file:///etc/passwd", EgressGuard.Scheme)]
    [InlineData("https://user:secret@example.org/", EgressGuard.UserInfo)]
    [InlineData("https://example.org:8443/", EgressGuard.Port)]
    [InlineData("http://example.org:22/", EgressGuard.Port)]
    [InlineData("http://169.254.169.254/latest/meta-data/", EgressGuard.PrivateAddress)]
    [InlineData("http://[::1]/", EgressGuard.PrivateAddress)]
    [InlineData("http://[::ffff:169.254.169.254]/", EgressGuard.PrivateAddress)]
    [InlineData("https://blocked.example/", EgressGuard.BlockedHost)]
    [InlineData("https://www.Blocked.Example/", EgressGuard.BlockedHost)]
    public void Checks_urls_before_any_lookup(string url, string reason)
    {
        var policy = new FixedEgressPolicy();
        policy.Blocked.Add("blocked.example");

        ConnectorTestKit.Guard(new FakeDnsResolver(), policy).CheckUrl(new Uri(url), EgressPurpose.Fetch).Reason.ShouldBe(reason);
    }

    [Theory]
    // Decimal, octal and hex spellings of loopback and the metadata address: canonicalised to dotted quads, then blocked.
    [InlineData("http://2130706433/")]
    [InlineData("http://0x7f000001/")]
    [InlineData("http://017700000001/")]
    [InlineData("http://0x7f.0.0.1/")]
    [InlineData("http://0251.0376.0251.0376/")]
    [InlineData("http://2852039166/")]
    public void Integer_ip_spellings_are_blocked(string url)
    {
        var canonical = UrlCanonicalizer.Canonicalize(url).ToUri();

        ConnectorTestKit.Guard(new FakeDnsResolver()).CheckUrl(canonical, EgressPurpose.Fetch).Reason.ShouldBe(EgressGuard.PrivateAddress);
    }

    [Fact]
    public void An_extra_port_from_the_policy_is_allowed() =>
        ConnectorTestKit.Guard(new FakeDnsResolver(), new FixedEgressPolicy(8443))
            .CheckUrl(new Uri("https://example.org:8443/"), EgressPurpose.Fetch).IsAllowed.ShouldBeTrue();

    [Fact]
    public void Idn_homographs_are_judged_in_their_own_a_label()
    {
        // "umeа.se" with a Cyrillic а is another host; a block on umea.se does not cover it, nor does the homograph unblock it.
        var policy = new FixedEgressPolicy();
        policy.Blocked.Add("xn--ume-8cd.se");
        var guard = ConnectorTestKit.Guard(new FakeDnsResolver(), policy);
        var homograph = UrlCanonicalizer.Canonicalize("https://umeа.se/").ToUri();

        homograph.Host.ShouldBe("xn--ume-8cd.se");
        guard.CheckUrl(homograph, EgressPurpose.Fetch).Reason.ShouldBe(EgressGuard.BlockedHost);
        guard.CheckUrl(new Uri("https://umea.se/"), EgressPurpose.Fetch).IsAllowed.ShouldBeTrue();
    }
}
