using System.Net;
using Kalla.Domain.Entities;
using Kalla.Domain.Services;

namespace Kalla.Domain.Tests;

// Copied from the gateway's tests/Fors.Domain.Tests/KeyTests.cs, adapted to the kalla-sk-/kalla-pk- format,
// the 13-character display prefix (the prefix and 4 characters), the ume-rag-* keys issued before the rename, and a client instead of the gateway's per-key model policies (identity design §6.1).
public class KeyHasherTests
{
    private static readonly KeyHasher Hasher = new(new byte[32]);

    [Fact]
    public void Generate_produces_prefixed_key_with_matching_hash()
    {
        var key = Hasher.Generate();

        key.PlainText.ShouldStartWith("kalla-sk-");
        key.PlainText.Length.ShouldBe(52);
        key.Prefix.ShouldBe(key.PlainText[..13]);
        key.Kind.ShouldBe(KeyKinds.Secret);
        key.Hash.ShouldBe(Hasher.Hash(key.PlainText));
        key.Hash.ShouldNotContain(key.PlainText);
        KeyHasher.LooksLikeKey(key.PlainText).ShouldBeTrue();
    }

    [Fact]
    public void Public_keys_have_their_own_prefix()
    {
        var key = Hasher.Generate(KeyKinds.Public);

        key.PlainText.ShouldStartWith("kalla-pk-");
        key.Kind.ShouldBe(KeyKinds.Public);
        KeyHasher.KindOf(key.PlainText).ShouldBe(KeyKinds.Public);
        KeyHasher.KindOf(Hasher.Generate().PlainText).ShouldBe(KeyKinds.Secret);
        Should.Throw<ArgumentOutOfRangeException>(() => Hasher.Generate("xx"));
    }

    [Fact]
    public void Keys_issued_before_the_rename_stay_valid()
    {
        // Decisions "Product names": ume-rag-sk-/ume-rag-pk- keys keep working; the hash is the whole key's, so only the
        // shape check needs to know the old prefix. No key is issued with it.
        var random = new string('A', 43);
        var legacy = "ume-rag-sk-" + random;
        KeyHasher.KindOf(legacy).ShouldBe(KeyKinds.Secret);
        KeyHasher.KindOf("ume-rag-pk-" + random).ShouldBe(KeyKinds.Public);
        Hasher.Verify(legacy, Hasher.Hash(legacy)).ShouldBeTrue();
        Hasher.Verify(legacy, Hasher.Hash("kalla-sk-" + random)).ShouldBeFalse();
        KeyHasher.LooksLikeKey("ume-rag-sk-" + random[..42]).ShouldBeFalse();
        KeyHasher.LooksLikeKey("ume-sk-" + random).ShouldBeFalse(); // a gateway key of the old format, never ours
    }

    [Fact]
    public void Generated_keys_are_unique()
    {
        var keys = Enumerable.Range(0, 1000).Select(_ => Hasher.Generate().PlainText).ToHashSet();
        keys.Count.ShouldBe(1000);
    }

    [Fact]
    public void Verify_accepts_correct_key_and_rejects_others()
    {
        var key = Hasher.Generate();
        var other = Hasher.Generate();

        Hasher.Verify(key.PlainText, key.Hash).ShouldBeTrue();
        Hasher.Verify(other.PlainText, key.Hash).ShouldBeFalse();
        Hasher.Verify("not-a-key", key.Hash).ShouldBeFalse();
    }

    [Fact]
    public void Hash_depends_on_pepper()
    {
        var pepper2 = Enumerable.Repeat((byte)7, 32).ToArray();
        var key = Hasher.Generate();
        new KeyHasher(pepper2).Hash(key.PlainText).ShouldNotBe(key.Hash);
    }

    [Fact]
    public void Short_pepper_is_rejected() =>
        Should.Throw<ArgumentException>(() => new KeyHasher(new byte[16]));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sk-openai-123")]
    [InlineData("kalla-sk-short")]
    [InlineData("kalla-sk-!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!")]
    // A gateway key (fors-sk-, 50 chars) is never a Källa key.
    [InlineData("fors-sk-0123456789abcdefghijABCDEFGHIJ0123456789abc")]
    [InlineData("ume-rag-xk-0123456789abcdefghijABCDEFGHIJ0123456789abc")]
    public void LooksLikeKey_rejects_malformed(string? value) =>
        KeyHasher.LooksLikeKey(value).ShouldBeFalse();
}

public class KeyStatusAndRotationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly KeyHasher Hasher = new(new byte[32]);

    private static ApiKey NewKey() => new()
    {
        Name = "Bygglov-app",
        Prefix = "kalla-sk-abcd",
        KeyHash = "hash",
        ClientId = Guid.NewGuid(),
        TeamId = Guid.NewGuid(),
        Scopes = new KeyScopes([Guid.NewGuid()], [Guid.NewGuid()]),
        AllowedOrigins = ["https://www.umea.se"],
        AllowedIpRanges = ["10.0.0.0/8"],
        RequestsPerMinute = 60,
        Client = new Client { Name = "Integration", Kind = ClientKinds.Integration, ClassificationCeiling = Classification.Internal },
    };

    [Fact]
    public void Status_reflects_flags_and_dates()
    {
        var key = NewKey();
        key.GetStatus(Now).ShouldBe(KeyStatus.Active);

        key.ExpiresAt = Now.AddMinutes(-1);
        key.GetStatus(Now).ShouldBe(KeyStatus.Expired);

        key.ExpiresAt = null;
        key.IsEnabled = false;
        key.GetStatus(Now).ShouldBe(KeyStatus.Disabled);
        key.IsUsable(Now).ShouldBeFalse();

        key.IsEnabled = true;
        key.RevokedAt = Now;
        key.GetStatus(Now).ShouldBe(KeyStatus.Revoked);
    }

    [Fact]
    public void Rotate_immediately_revokes_old_key_and_copies_policy()
    {
        var old = NewKey();
        var generated = Hasher.Generate();

        var replacement = KeyRotation.Rotate(old, generated, KeyRotationMode.RevokeImmediately, Now, "admin@umea.se");

        old.GetStatus(Now).ShouldBe(KeyStatus.Revoked);
        old.RotatedToKeyId.ShouldBe(replacement.Id);
        replacement.KeyHash.ShouldBe(generated.Hash);
        replacement.Prefix.ShouldBe(generated.Prefix);
        replacement.ClientId.ShouldBe(old.ClientId);
        replacement.TeamId.ShouldBe(old.TeamId);
        replacement.Scopes.ShouldBe(old.Scopes);
        replacement.Scopes.AssistantIds.ShouldNotBeSameAs(old.Scopes.AssistantIds);
        replacement.AllowedOrigins.ShouldBe(old.AllowedOrigins);
        replacement.AllowedOrigins.ShouldNotBeSameAs(old.AllowedOrigins);
        replacement.AllowedIpRanges.ShouldBe(old.AllowedIpRanges);
        replacement.AllowedIpRanges.ShouldNotBeSameAs(old.AllowedIpRanges);
        replacement.RequestsPerMinute.ShouldBe(60);
        replacement.CreatedBy.ShouldBe("admin@umea.se");
        replacement.GetStatus(Now).ShouldBe(KeyStatus.Active);
    }

    [Fact]
    public void Rotate_with_grace_keeps_old_key_working_for_24_hours()
    {
        var old = NewKey();
        KeyRotation.Rotate(old, Hasher.Generate(), KeyRotationMode.Grace24Hours, Now, null);

        old.GetStatus(Now).ShouldBe(KeyStatus.InGracePeriod);
        old.IsUsable(Now.AddHours(23).AddMinutes(59)).ShouldBeTrue();
        old.GetStatus(Now.AddHours(24)).ShouldBe(KeyStatus.Revoked);
        old.IsUsable(Now.AddHours(24)).ShouldBeFalse();
    }

    [Fact]
    public void Revoked_key_cannot_be_rotated()
    {
        var old = NewKey();
        KeyRotation.Revoke(old, Now);
        Should.Throw<InvalidOperationException>(() => KeyRotation.Rotate(old, Hasher.Generate(), KeyRotationMode.RevokeImmediately, Now, null));
    }

    [Fact]
    public void A_key_is_rotated_to_a_key_of_the_same_kind() =>
        Should.Throw<InvalidOperationException>(() => KeyRotation.Rotate(NewKey(), Hasher.Generate(KeyKinds.Public), KeyRotationMode.RevokeImmediately, Now, null));

    [Fact]
    public void Revoking_a_key_in_grace_period_stops_it_immediately()
    {
        var old = NewKey();
        KeyRotation.Rotate(old, Hasher.Generate(), KeyRotationMode.Grace24Hours, Now, null);
        KeyRotation.Revoke(old, Now.AddHours(1));
        old.IsUsable(Now.AddHours(1)).ShouldBeFalse();
    }
}

/// <summary>The status step of key authentication (identity design §6.3 step 3).</summary>
public class KeyChecksTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly KeyHasher Hasher = new(new byte[32]);

    private static (ApiKey Key, string Plain) NewKey(string kind = KeyKinds.Secret)
    {
        var generated = Hasher.Generate(kind);
        var key = new ApiKey
        {
            Name = "k",
            Kind = kind,
            Prefix = generated.Prefix,
            KeyHash = generated.Hash,
            Client = new Client { Name = "c", Kind = ClientKinds.Integration, ClassificationCeiling = Classification.Public },
        };
        return (key, generated.PlainText);
    }

    [Fact]
    public void An_active_key_of_an_enabled_client_is_accepted()
    {
        var (key, plain) = NewKey();
        KeyChecks.Refusal(key, plain, Now, null).ShouldBeNull();
    }

    [Fact]
    public void The_stored_kind_must_match_the_prefix()
    {
        var (key, plain) = NewKey(KeyKinds.Public);
        key.Kind = KeyKinds.Secret;
        KeyChecks.Refusal(key, plain, Now, null).ShouldBe(KeyChecks.InvalidKey);
    }

    [Fact]
    public void Revoked_disabled_and_expired_keys_are_refused_in_that_order()
    {
        var (key, plain) = NewKey();
        key.ExpiresAt = Now.AddDays(-1);
        KeyChecks.Refusal(key, plain, Now, null).ShouldBe(KeyChecks.KeyExpired);
        key.IsEnabled = false;
        KeyChecks.Refusal(key, plain, Now, null).ShouldBe(KeyChecks.KeyDisabled);
        key.RevokedAt = Now;
        KeyChecks.Refusal(key, plain, Now, null).ShouldBe(KeyChecks.KeyRevoked);
    }

    [Fact]
    public void A_key_of_a_disabled_or_missing_client_is_refused_as_owner_inactive()
    {
        var (key, plain) = NewKey();
        key.Client!.IsEnabled = false;
        KeyChecks.Refusal(key, plain, Now, null).ShouldBe(KeyChecks.OwnerInactive);
        key.Client = null;
        KeyChecks.Refusal(key, plain, Now, null).ShouldBe(KeyChecks.OwnerInactive);
    }

    [Fact]
    public void An_address_outside_the_allow_list_is_refused_after_the_status_checks()
    {
        var (key, plain) = NewKey();
        key.AllowedIpRanges = ["203.0.113.0/24"];
        KeyChecks.Refusal(key, plain, Now, IPAddress.Parse("203.0.113.7")).ShouldBeNull();
        KeyChecks.Refusal(key, plain, Now, IPAddress.Parse("198.51.100.1")).ShouldBe(KeyChecks.AddressNotAllowed);
        key.RevokedAt = Now;
        KeyChecks.Refusal(key, plain, Now, IPAddress.Parse("198.51.100.1")).ShouldBe(KeyChecks.KeyRevoked);
    }
}
