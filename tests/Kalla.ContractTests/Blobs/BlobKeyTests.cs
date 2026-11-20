using Kalla.Domain.Ports;

namespace Kalla.ContractTests.Blobs;

/// <summary>The key and range rules every <see cref="IBlobStore"/> relies on (data-model §9 layout).</summary>
public sealed class BlobKeyTests
{
    [Theory]
    [InlineData("uploads/0f8fad5bd9cb469fa16570867728950e/main/9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08")]
    [InlineData("uploads/0f8fad5bd9cb469fa16570867728950e/batches/1a2b3c4d5e6f40718293a4b5c6d7e8f9.json")]
    [InlineData("quarantine/0f8fad5bd9cb469fa16570867728950e/9f86d081884c7d659a2feaa0c55ad015")]
    [InlineData("raw/internal/9f86d081884c7d659a2feaa0c55ad015.links.json.gz")]
    [InlineData("parse/public/abc123/docling/1-20.json.gz")]
    [InlineData("parse/public/abc123/ranges/21-40.result.json")]
    [InlineData("stages/internal/chunk/abc123.json")]
    [InlineData("tmp/0f8fad5bd9cb469fa16570867728950e/scan_input")]
    [InlineData("chat-tmp/0f8fad5bd9cb469fa16570867728950e")]
    [InlineData("previews/0f8fad5bd9cb469fa16570867728950e/chunk.json")]
    [InlineData("crawl/www.umea.se/robots-20261030t120000z.txt")]
    [InlineData("raw")]
    public void Keys_of_the_layout_are_valid(string value)
    {
        BlobKey.TryCreate(value, out var key).ShouldBeTrue();
        key!.Value.ShouldBe(value);
        key.ToString().ShouldBe(value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/raw/a")]
    [InlineData("raw/a/")]
    [InlineData("raw//a")]
    [InlineData("raw/../etc/passwd")]
    [InlineData("raw/a/../../etc")]
    [InlineData("raw/./a")]
    [InlineData("raw/..")]
    [InlineData("raw/.hidden")]
    [InlineData("raw/trailing.")]
    [InlineData(@"raw\a")]
    [InlineData(@"raw/..\..\a")]
    [InlineData("raw/C:")]
    [InlineData("raw/Upper")]
    [InlineData("raw/with space")]
    [InlineData("raw/a\0b")]
    [InlineData("raw/a%2fb")]
    [InlineData("raw/å")]
    [InlineData("../raw/a")]
    [InlineData(".staging/x.part")]
    [InlineData("etc/passwd")]
    [InlineData("Raw/a")]
    public void Unsafe_keys_are_refused(string value)
    {
        BlobKey.TryCreate(value, out var key).ShouldBeFalse();
        key.ShouldBeNull();
        Should.Throw<ArgumentException>(() => new BlobKey(value));
    }

    [Fact]
    public void Length_limits_hold()
    {
        BlobKey.TryCreate(null, out _).ShouldBeFalse();
        BlobKey.TryCreate("raw/" + new string('a', 255), out _).ShouldBeTrue();
        BlobKey.TryCreate("raw/" + new string('a', 256), out _).ShouldBeFalse();
        var longest = "raw/" + string.Join('/', Enumerable.Repeat(new string('a', 99), 10)) + "/" + new string('b', 20);
        longest.Length.ShouldBe(BlobKey.MaxLength);
        BlobKey.TryCreate(longest, out _).ShouldBeTrue();
        BlobKey.TryCreate(longest + "b", out _).ShouldBeFalse();
    }

    [Fact]
    public void Areas_and_kinds()
    {
        var scratch = new BlobKey("tmp/job/x");
        scratch.Area.ShouldBe("tmp");
        scratch.IsBlob.ShouldBeTrue();
        scratch.IsScratch.ShouldBeTrue();
        scratch.IsSweepable.ShouldBeTrue();
        new BlobKey("previews/job").IsSweepable.ShouldBeTrue();
        new BlobKey("previews/job").IsScratch.ShouldBeFalse();
        new BlobKey("chat-tmp/a").IsScratch.ShouldBeFalse();
        new BlobKey("chat-tmp/a").IsSweepable.ShouldBeFalse();
        new BlobKey("raw").IsBlob.ShouldBeFalse();
        new BlobKey("raw").Area.ShouldBe("raw");
        new BlobKey("raw/a").ShouldBe(new BlobKey("raw/a"));
    }

    [Fact]
    public void Ranges_are_checked()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new BlobRange(-1));
        Should.Throw<ArgumentOutOfRangeException>(() => new BlobRange(0, 0));
        Should.Throw<ArgumentOutOfRangeException>(() => new BlobRange(0, -5));
        new BlobRange(7).Length.ShouldBeNull();
        new BlobRange(7, 3).ShouldBe(new BlobRange(7, 3));
    }
}
