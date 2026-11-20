using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Kalla.Domain;
using Kalla.Domain.Ports;
using Kalla.Infrastructure.Blobs;

namespace Kalla.Worker.Tests.Blobs;

/// <summary>
/// The <c>URB2</c> blob envelope (data-model §7 "Blobs"): round trips at the segment edges, the header, tamper, cut and
/// reordered segments, wrong and missing master keys, absent blobs, ranges, and the level routing of the worker's store.
/// </summary>
public sealed class BlobEnvelopeTests : IDisposable
{
    private const int Segment = 64 * 1024;
    private const int Tag = 16;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("ume-rag-envelope-");
    private readonly FileSystemBlobStore _plain;
    private readonly BlobEnvelope _envelope;

    public BlobEnvelopeTests()
    {
        _plain = new FileSystemBlobStore(Options.Create(new BlobStoreOptions { Root = _root.FullName }), TimeProvider.System);
        _envelope = new BlobEnvelope(_plain, TestBlobKeys.Provider(), new TestBlobKeys.DefaultPolicy());
    }

    public void Dispose() => _root.Delete(recursive: true);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(Segment - 1)]
    [InlineData(Segment)]
    [InlineData(Segment + 1)]
    [InlineData((3 * Segment) + 12345)]
    [InlineData(4 * Segment)]
    public async Task Internal_blobs_round_trip_at_every_segment_edge(int size)
    {
        var plaintext = RandomNumberGenerator.GetBytes(size);
        var key = Key();

        (await PutAsync(key, plaintext)).ShouldBe(BlobWriteResult.Created);

        var stored = await RawAsync(key);
        var segments = Math.Max(1, (size + Segment - 1) / Segment);
        stored.Length.ShouldBe(HeaderLength(stored) + size + (segments * Tag));
        var blob = (await _envelope.OpenReadAsync(key, Classification.Internal, Ct)).ShouldNotBeNull();
        await using (blob.Content)
        {
            blob.Length.ShouldBe(size);
            (await ReadAllAsync(blob.Content)).ShouldBe(plaintext);
        }
    }

    [Fact]
    public async Task Several_megabytes_round_trip_through_small_synchronous_and_asynchronous_reads()
    {
        var plaintext = RandomNumberGenerator.GetBytes((5 * 1024 * 1024) + 777);
        var key = Key();
        await PutAsync(key, plaintext, declareLength: false);

        await using var blob = (await _envelope.OpenReadAsync(key, Classification.Internal, Ct)).ShouldNotBeNull().Content;
        using var copy = new MemoryStream();
        var buffer = new byte[1000];
        int read;
        var sync = false;
        while ((read = sync ? blob.Read(buffer, 0, buffer.Length) : await blob.ReadAsync(buffer, Ct)) > 0)
        {
            copy.Write(buffer, 0, read);
            sync = !sync;
        }

        copy.ToArray().ShouldBe(plaintext);
    }

    [Fact]
    public async Task The_header_names_the_master_key_and_version_and_holds_the_wrapped_key_and_two_encryptions_differ()
    {
        var plaintext = Encoding.UTF8.GetBytes($"marker-{Guid.NewGuid():N} " + new string('x', 1000));
        var first = Key();
        var second = Key();
        await PutAsync(first, plaintext);
        await PutAsync(second, plaintext);

        var stored = await RawAsync(first);
        var (name, version, wrapped, prefix) = ParseHeader(stored);
        Encoding.ASCII.GetString(stored, 0, 4).ShouldBe("URB2");
        name.ShouldBe(TestBlobKeys.Internal);
        version.ShouldBe(FileKeyProvider.Version);
        wrapped.Length.ShouldBe(12 + 32 + 16, "nonce, data key and tag");
        prefix.Length.ShouldBe(7);
        stored.AsSpan().IndexOf(plaintext.AsSpan(0, 20)).ShouldBe(-1, "no plaintext in the stored blob");

        var other = await RawAsync(second);
        other.ShouldNotBe(stored);
        ParseHeader(other).Wrapped.ShouldNotBe(wrapped, "a fresh data key per blob");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task A_flipped_byte_in_any_segment_throws_before_that_segment_is_returned(int segment)
    {
        var plaintext = RandomNumberGenerator.GetBytes((2 * Segment) + 100);
        var key = Key();
        await PutAsync(key, plaintext);
        var stored = await RawAsync(key);
        var header = HeaderLength(stored);
        stored[header + (segment * (Segment + Tag)) + 50] ^= 0x01;
        var damaged = await RewriteAsync(stored);

        await using var blob = (await _envelope.OpenReadAsync(damaged, Classification.Internal, Ct)).ShouldNotBeNull().Content;
        var returned = new MemoryStream();
        var failure = await Should.ThrowAsync<BlobIntegrityException>(() => CopyAsync(blob, returned));

        failure.Message.ShouldContain($"segment {segment} failed verification");
        returned.Length.ShouldBe(segment * Segment, "only the verified segments before the damaged one were returned");
        returned.ToArray().ShouldBe(plaintext[..(segment * Segment)]);
    }

    [Fact]
    public async Task A_changed_header_fails_the_first_segment()
    {
        var key = Key();
        await PutAsync(key, RandomNumberGenerator.GetBytes(100));
        var stored = await RawAsync(key);
        stored[HeaderLength(stored) - 1] ^= 0x01; // the nonce prefix
        var damaged = await RewriteAsync(stored);

        await Should.ThrowAsync<BlobIntegrityException>(() => ReadAsync(damaged));
    }

    [Theory]
    [InlineData((2 * Segment) + 100)]
    [InlineData(3 * Segment)]
    public async Task A_blob_cut_at_a_segment_boundary_throws(int size)
    {
        var key = Key();
        await PutAsync(key, RandomNumberGenerator.GetBytes(size));
        var stored = await RawAsync(key);
        var header = HeaderLength(stored);
        var cut = await RewriteAsync(stored[..(header + (2 * (Segment + Tag)))]);
        var headerOnly = await RewriteAsync(stored[..header]);

        (await Should.ThrowAsync<BlobIntegrityException>(() => ReadAsync(cut))).Message.ShouldContain("segment 1 failed verification");
        var store = TestBlobKeys.Store(_root);
        var missing = await Should.ThrowAsync<BlobIntegrityException>(() => ReadFromAsync(store, headerOnly));
        missing.Message.ShouldContain("no final segment");
        await Should.ThrowAsync<BlobIntegrityException>(() => _envelope.OpenReadAsync(headerOnly, Classification.Internal, Ct), "no segment at all");
    }

    [Fact]
    public async Task A_blob_cut_inside_a_segment_throws()
    {
        var key = Key();
        await PutAsync(key, RandomNumberGenerator.GetBytes((2 * Segment) + 100));
        var stored = await RawAsync(key);

        var cut = await RewriteAsync(stored[..^60]);
        var shorterThanATag = await RewriteAsync(stored[..^110]);

        (await Should.ThrowAsync<BlobIntegrityException>(() => ReadAsync(cut))).Message.ShouldContain("segment 2 failed verification");
        (await Should.ThrowAsync<BlobIntegrityException>(() => _envelope.OpenReadAsync(shorterThanATag, Classification.Internal, Ct)))
            .Message.ShouldContain("no whole segments");
        (await Should.ThrowAsync<BlobIntegrityException>(() => ReadFromAsync(TestBlobKeys.Store(_root), shorterThanATag)))
            .Message.ShouldContain("segment 2 is cut");
    }

    [Fact]
    public async Task Swapped_segments_throw()
    {
        var key = Key();
        await PutAsync(key, RandomNumberGenerator.GetBytes(3 * Segment));
        var stored = await RawAsync(key);
        var header = HeaderLength(stored);
        const int stride = Segment + Tag;
        var swapped = stored.ToArray();
        stored.AsSpan(header, stride).CopyTo(swapped.AsSpan(header + stride));
        stored.AsSpan(header + stride, stride).CopyTo(swapped.AsSpan(header));

        var reordered = await RewriteAsync(swapped);

        (await Should.ThrowAsync<BlobIntegrityException>(() => ReadAsync(reordered))).Message.ShouldContain("segment 0 failed verification");
    }

    [Fact]
    public async Task The_wrong_master_key_or_a_missing_key_file_is_a_clear_key_error_and_an_absent_blob_is_null()
    {
        var key = Key();
        await PutAsync(key, RandomNumberGenerator.GetBytes(10));
        var otherKeys = Directory.CreateTempSubdirectory("ume-rag-keys-other-");
        var empty = Directory.CreateTempSubdirectory("ume-rag-keys-none-");
        try
        {
            TestBlobKeys.WriteKey(otherKeys.FullName, TestBlobKeys.Internal);
            var wrong = new BlobEnvelope(_plain, TestBlobKeys.Provider(otherKeys.FullName), new TestBlobKeys.DefaultPolicy());
            var none = new BlobEnvelope(_plain, TestBlobKeys.Provider(empty.FullName), new TestBlobKeys.DefaultPolicy());

            (await Should.ThrowAsync<KeyProviderException>(() => wrong.OpenReadAsync(key, Classification.Internal, Ct)))
                .Message.ShouldContain("could not be unwrapped with master key 'rag-blob-internal'");
            (await Should.ThrowAsync<KeyProviderException>(() => none.OpenReadAsync(key, Classification.Internal, Ct)))
                .Message.ShouldContain("Master key 'rag-blob-internal' is not available");
            using var content = new MemoryStream([1, 2, 3]);
            await Should.ThrowAsync<KeyProviderException>(() => none.PutAsync(Key(), Classification.Internal, content, 3, Ct));
            (await none.OpenReadAsync(Key(), Classification.Internal, Ct)).ShouldBeNull("absent is null, not a key error");
        }
        finally
        {
            otherKeys.Delete(recursive: true);
            empty.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Public_blobs_are_stored_and_read_as_they_are_and_an_internal_blob_without_the_header_is_refused()
    {
        var plaintext = Encoding.UTF8.GetBytes($"public-{Guid.NewGuid():N}");
        var key = new BlobKey($"raw/public/{Convert.ToHexStringLower(SHA256.HashData(plaintext))}");
        using (var content = new MemoryStream(plaintext))
        {
            await _envelope.PutAsync(key, Classification.Public, content, plaintext.Length, Ct);
        }

        (await RawAsync(key)).ShouldBe(plaintext);
        var blob = (await _envelope.OpenReadAsync(key, Classification.Public, Ct)).ShouldNotBeNull();
        await using (blob.Content)
        {
            blob.Length.ShouldBe(plaintext.Length);
            (await ReadAllAsync(blob.Content)).ShouldBe(plaintext);
        }

        var plain = await RewriteAsync(plaintext);
        (await Should.ThrowAsync<BlobIntegrityException>(() => _envelope.OpenReadAsync(plain, Classification.Internal, Ct)))
            .Message.ShouldContain("no URB2 header");
    }

    [Fact]
    public async Task A_declared_length_that_does_not_match_leaves_no_blob()
    {
        var key = Key();
        using var content = new MemoryStream(new byte[100]);

        await Should.ThrowAsync<InvalidDataException>(() => _envelope.PutAsync(key, Classification.Internal, content, 99, Ct));
        (await _plain.ExistsAsync(key, Ct)).ShouldBeFalse();
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(10, 20)]
    [InlineData(Segment - 5, 10)]
    [InlineData(Segment, null)]
    [InlineData((2 * Segment) + 7, 100_000)]
    [InlineData((3 * Segment) + 99, 50)]
    [InlineData(10 * Segment, 5)]
    public async Task Range_reads_of_an_internal_blob_return_the_right_plaintext(int offset, int? length)
    {
        var plaintext = RandomNumberGenerator.GetBytes((3 * Segment) + 100);
        var store = TestBlobKeys.Store(_root);
        var key = Key();
        using (var content = new MemoryStream(plaintext))
        {
            await store.PutAsync(key, content, plaintext.Length, Ct);
        }

        await using var stream = (await store.OpenReadAsync(key, new BlobRange(offset, length), Ct)).ShouldNotBeNull();

        var start = Math.Min(offset, plaintext.Length);
        var end = length is { } count ? Math.Min(plaintext.Length, start + count) : plaintext.Length;
        (await ReadAllAsync(stream)).ShouldBe(plaintext[start..end]);
    }

    [Fact]
    public async Task The_worker_store_encrypts_by_the_level_in_the_key_or_of_its_view()
    {
        var store = TestBlobKeys.Store(_root);
        var marker = Encoding.UTF8.GetBytes($"marker-{Guid.NewGuid():N}");
        var job = Guid.CreateVersion7();
        var hash = Convert.ToHexStringLower(SHA256.HashData(marker));
        BlobKey[] encrypted =
        [
            new($"raw/internal/{hash}"), new($"parse/internal/{hash}/result.json"), new($"stages/internal/chunk/{hash}.json.gz"),
            new($"previews/{job:D}/raw/internal/{hash}"),
        ];
        BlobKey[] plain = [new($"raw/public/{hash}"), new($"previews/{job:D}/stages/public/chunk/{hash}.json.gz"), new($"uploads/{job:D}/batches/{job:D}.json")];
        BlobKey[] scoped =
        [
            new($"quarantine/{job:D}/{hash}"), new($"previews/{job:D}/0/parse.json"), new($"previews/{job:D}/keys/parse/{hash}.json.gz"),
            new($"uploads/{job:D}/{job:D}/{hash}"),
        ];

        foreach (var key in encrypted.Concat(plain))
        {
            await StoreAsync(store, key, marker);
        }

        foreach (var key in scoped)
        {
            await Should.ThrowAsync<InvalidOperationException>(() => StoreAsync(store, key, marker), $"{key} needs a level");
            await Should.ThrowAsync<InvalidOperationException>(() => store.OpenReadAsync(key, null, Ct));
            await StoreAsync(store.ForLevel(Classification.Internal), key, marker);
        }

        foreach (var key in encrypted.Concat(scoped))
        {
            Encoding.ASCII.GetString((await RawAsync(key))[..4]).ShouldBe("URB2", key.Value);
        }

        foreach (var key in plain)
        {
            (await RawAsync(key)).ShouldBe(marker, key.Value);
        }

        foreach (var key in encrypted.Concat(plain))
        {
            (await ReadFromAsync(store, key)).ShouldBe(marker, key.Value);
        }

        foreach (var key in scoped)
        {
            (await ReadFromAsync(store.ForLevel(Classification.Internal), key)).ShouldBe(marker, key.Value);
        }

        (await store.ForLevel(Classification.Internal).OpenReadAsync(new BlobKey($"quarantine/{job:D}/absent"), null, Ct)).ShouldBeNull();
        await Should.ThrowAsync<InvalidOperationException>(() => store.PromoteAsync(new BlobKey($"tmp/{job:D}/a"), encrypted[0], Ct));

        // The admin stages an upload through the envelope and promotes it unchanged: it reads back at the source's level.
        var staged = new BlobKey($"tmp/upload-batches/{job:D}/data/{hash}");
        await PutAsync(staged, marker);
        var upload = new BlobKey($"uploads/{job:D}/{Guid.CreateVersion7():D}/{hash}");
        (await store.PromoteAsync(staged, upload, Ct)).ShouldBe(BlobWriteResult.Created);
        Encoding.ASCII.GetString((await RawAsync(upload))[..4]).ShouldBe("URB2");
        (await ReadFromAsync(store.ForLevel(Classification.Internal), upload)).ShouldBe(marker);
    }

    private static BlobKey Key() => new($"raw/internal/{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32))}");

    private async Task<BlobWriteResult> PutAsync(BlobKey key, byte[] plaintext, bool declareLength = true)
    {
        using var content = new MemoryStream(plaintext);
        return await _envelope.PutAsync(key, Classification.Internal, content, declareLength ? plaintext.Length : null, Ct);
    }

    private static async Task StoreAsync(IBlobStore store, BlobKey key, byte[] bytes)
    {
        using var content = new MemoryStream(bytes);
        await store.PutAsync(key, content, bytes.Length, Ct);
    }

    private async Task<byte[]> RawAsync(BlobKey key)
    {
        await using var stream = (await _plain.OpenReadAsync(key, null, Ct)).ShouldNotBeNull();
        return await ReadAllAsync(stream);
    }

    /// <summary>Stores <paramref name="bytes"/> under a fresh internal key as they are (a damaged copy).</summary>
    private async Task<BlobKey> RewriteAsync(byte[] bytes)
    {
        var key = Key();
        using var content = new MemoryStream(bytes);
        await _plain.PutAsync(key, content, bytes.Length, Ct);
        return key;
    }

    private async Task<byte[]> ReadAsync(BlobKey key)
    {
        var blob = (await _envelope.OpenReadAsync(key, Classification.Internal, Ct)).ShouldNotBeNull();
        await using (blob.Content)
        {
            return await ReadAllAsync(blob.Content);
        }
    }

    private static async Task<byte[]> ReadFromAsync(IBlobStore store, BlobKey key)
    {
        await using var stream = (await store.OpenReadAsync(key, null, Ct)).ShouldNotBeNull();
        return await ReadAllAsync(stream);
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        await CopyAsync(stream, buffer);
        return buffer.ToArray();
    }

    private static Task CopyAsync(Stream from, Stream to) => from.CopyToAsync(to, Ct);

    private static int HeaderLength(byte[] stored)
    {
        var (name, version, wrapped, prefix) = ParseHeader(stored);
        return 4 + 1 + name.Length + 1 + version.Length + 2 + wrapped.Length + prefix.Length;
    }

    private static (string Name, string Version, byte[] Wrapped, byte[] Prefix) ParseHeader(byte[] stored)
    {
        var at = 4;
        var name = Encoding.ASCII.GetString(stored, at + 1, stored[at]);
        at += 1 + stored[at];
        var version = Encoding.ASCII.GetString(stored, at + 1, stored[at]);
        at += 1 + stored[at];
        var wrappedLength = BinaryPrimitives.ReadUInt16BigEndian(stored.AsSpan(at, 2));
        at += 2;
        var wrapped = stored[at..(at + wrappedLength)];
        at += wrappedLength;
        return (name, version, wrapped, stored[at..(at + 7)]);
    }
}
