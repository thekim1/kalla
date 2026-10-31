using System.Buffers.Text;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using Ume.Rag.Answering;
using Ume.Rag.Domain;

namespace Ume.Rag.Api.Tests;

/// <summary>Knowledge design §16.1 download grants (§4.6): format, MAC, expiry per level, key rotation, contents.</summary>
public sealed class DownloadGrantTests
{
    private static readonly byte[] OldKey = Encoding.UTF8.GetBytes(new string('o', 32));
    private static readonly byte[] NewKey = Encoding.UTF8.GetBytes(new string('n', 32));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
    private readonly Guid _document = Guid.CreateVersion7();
    private readonly Guid _collection = Guid.CreateVersion7();
    private readonly Guid _client = Guid.CreateVersion7();

    private DownloadGrants Grants(params (string Kid, byte[] Key)[] keys) =>
        new(new DownloadGrantOptions { Keys = keys.ToDictionary(k => k.Kid, k => k.Key) }, _time);

    [Fact]
    public void A_grant_round_trips_with_ids_and_expiry_only()
    {
        var grants = Grants(("2026-10", NewKey));
        var keyId = Guid.CreateVersion7();

        var token = grants.Issue(_document, _collection, null, _client, keyId, Classification.Internal);

        var parts = token.Split('.');
        parts.Length.ShouldBe(4);
        (parts[0], parts[1], parts[3].Length).ShouldBe(("g1", "2026-10", 32));
        Encoding.UTF8.GetString(Base64Url.DecodeFromChars(parts[2])).ShouldNotContain("\"a\""); // no assistant: omitted
        grants.Read(token, out var grant).ShouldBe(GrantStatus.Valid);
        grant.ShouldBe(new DownloadGrant(_document, _collection, null, _client, keyId, Classification.Internal, _time.GetUtcNow().AddDays(7)));
    }

    [Fact]
    public void Sensitive_grants_live_one_hour_and_expire()
    {
        var grants = Grants(("k1", NewKey));
        var sensitive = grants.Issue(_document, _collection, null, _client, null, Classification.Sensitive);
        var internalGrant = grants.Issue(_document, _collection, null, _client, null, Classification.Internal);

        _time.Advance(TimeSpan.FromHours(1));

        grants.Read(sensitive, out _).ShouldBe(GrantStatus.Expired);
        grants.Read(internalGrant, out _).ShouldBe(GrantStatus.Valid);
        _time.Advance(TimeSpan.FromDays(7));
        grants.Read(internalGrant, out _).ShouldBe(GrantStatus.Expired);
    }

    [Fact]
    public void A_tampered_payload_or_mac_or_unknown_key_is_invalid_never_expired()
    {
        var grants = Grants(("k1", NewKey));
        var token = grants.Issue(_document, _collection, null, _client, null, Classification.Public);
        var parts = token.Split('.');
        var other = Base64Url.EncodeToString(Encoding.UTF8.GetBytes($$"""{"d":"{{Guid.NewGuid()}}","c":"{{_collection}}","cl":"{{_client}}","l":"public","e":1}"""));

        grants.Read($"g1.k1.{other}.{parts[3]}", out _).ShouldBe(GrantStatus.Invalid);
        grants.Read($"g1.k1.{parts[2]}.{new string('A', 32)}", out _).ShouldBe(GrantStatus.Invalid);
        grants.Read($"g1.k2.{parts[2]}.{parts[3]}", out _).ShouldBe(GrantStatus.Invalid);
        grants.Read("g2." + token[3..], out _).ShouldBe(GrantStatus.Invalid);
        grants.Read("", out _).ShouldBe(GrantStatus.Invalid);
        grants.Read(null, out _).ShouldBe(GrantStatus.Invalid);

        _time.Advance(TimeSpan.FromDays(30));
        grants.Read($"g1.k1.{parts[2]}.{new string('A', 32)}", out _).ShouldBe(GrantStatus.Invalid);
    }

    [Fact]
    public void The_newest_key_signs_and_every_key_verifies()
    {
        var old = Grants(("2026-09", OldKey)).Issue(_document, _collection, null, _client, null, Classification.Public);
        var rotated = Grants(("2026-09", OldKey), ("2026-10", NewKey));

        rotated.Issue(_document, _collection, null, _client, null, Classification.Public).Split('.')[1].ShouldBe("2026-10");
        rotated.Read(old, out _).ShouldBe(GrantStatus.Valid);
        Grants(("2026-10", NewKey)).Read(old, out _).ShouldBe(GrantStatus.Invalid);
    }

    [Fact]
    public void Short_keys_and_no_keys_are_refused_at_start()
    {
        Should.Throw<InvalidOperationException>(() => Grants());
        Should.Throw<InvalidOperationException>(() => Grants(("k1", new byte[31])));
        Should.Throw<InvalidOperationException>(() => Grants(("k.1", NewKey)));
    }

    [Fact]
    public void Links_are_relative_unless_a_public_origin_is_set()
    {
        Grants(("k1", NewKey)).Link(_document, "g1.x").ShouldBe($"/v1/documents/{_document}/content?grant=g1.x");
        new DownloadGrants(new DownloadGrantOptions { Keys = new Dictionary<string, byte[]> { ["k1"] = NewKey }, PublicBaseUrl = new Uri("https://rag.umea.se") }, _time)
            .Link(_document, "g1.x").ShouldBe($"https://rag.umea.se/v1/documents/{_document}/content?grant=g1.x");
    }
}
