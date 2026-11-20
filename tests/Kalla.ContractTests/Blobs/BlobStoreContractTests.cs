using Kalla.Domain.Ports;

namespace Kalla.ContractTests.Blobs;

/// <summary>
/// The <see cref="IBlobStore"/> contract (data-model §9, performance pass §3.13; P0 checklist #21). Every implementation
/// runs these through a subclass that supplies <see cref="Store"/>: the file system store now, the S3 store against the
/// chosen server in P1 (to-verify #114). Each test works below its own <see cref="Id"/>, so the tests can share one
/// container or bucket.
/// </summary>
public abstract class BlobStoreContractTests
{
    protected abstract IBlobStore Store { get; }

    /// <summary>Unique per test: every key a test writes contains it.</summary>
    protected string Id { get; } = Guid.NewGuid().ToString("N");

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected BlobKey Key(string area, string name = "blob") => new($"{area}/{Id}/{name}");

    [Fact]
    public async Task A_put_is_readable_and_exists()
    {
        var key = Key("raw");
        byte[] bytes = [1, 2, 3, 4, 5];

        (await Store.PutAsync(key, new MemoryStream(bytes), bytes.Length, Ct)).ShouldBe(BlobWriteResult.Created);

        (await Store.ExistsAsync(key, Ct)).ShouldBeTrue();
        (await ReadAsync(key)).ShouldBe(bytes);
    }

    [Fact]
    public async Task A_put_without_a_length_and_an_empty_blob_are_stored()
    {
        var key = Key("raw", "unknown-length");
        var empty = Key("raw", "empty");

        (await Store.PutAsync(key, new MemoryStream(Bytes(100_000, 3)), null, Ct)).ShouldBe(BlobWriteResult.Created);
        (await Store.PutAsync(empty, new MemoryStream(), 0, Ct)).ShouldBe(BlobWriteResult.Created);

        (await ReadAsync(key)).ShouldBe(Bytes(100_000, 3));
        (await ReadAsync(empty)).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_overwrite_is_refused()
    {
        var key = Key("uploads");
        var original = Bytes(1000, 1);

        (await Store.PutAsync(key, new MemoryStream(original), original.Length, Ct)).ShouldBe(BlobWriteResult.Created);
        (await Store.PutAsync(key, new MemoryStream(Bytes(2000, 2)), 2000, Ct)).ShouldBe(BlobWriteResult.AlreadyExisted);

        (await ReadAsync(key)).ShouldBe(original);
    }

    [Fact]
    public async Task Concurrent_puts_of_one_key_have_exactly_one_winner()
    {
        // Small blobs, so the writers reach the publish step together; many rounds, so a check-then-rename race shows.
        const int Writers = 16;
        const int Rounds = 25;
        for (var round = 0; round < Rounds; round++)
        {
            var key = Key("raw", $"round-{round}");
            var results = await PutConcurrentlyAsync(key, Writers, 4096);

            results.Count(r => r == BlobWriteResult.Created).ShouldBe(1, $"round {round}");
            var winner = (byte)(Array.IndexOf(results, BlobWriteResult.Created) + 1);
            (await ReadAsync(key)).ShouldBe(Bytes(4096, winner), $"round {round}: the winner's bytes stay");
        }
    }

    [Fact]
    public async Task Readers_never_see_a_partial_blob()
    {
        const int Writers = 16;
        const int Size = 512 * 1024;
        var key = Key("raw");
        using var writersDone = new CancellationTokenSource();

        // A reader polls throughout: whatever it sees must be one writer's complete content.
        var reader = Task.Run(async () =>
        {
            while (!writersDone.IsCancellationRequested)
            {
                if (await ReadOrNullAsync(key) is { } read)
                {
                    read.Length.ShouldBe(Size);
                    read.ShouldAllBe(b => b == read[0]);
                }
            }
        }, Ct);

        var results = await PutConcurrentlyAsync(key, Writers, Size);
        await writersDone.CancelAsync();
        await reader;

        results.Count(r => r == BlobWriteResult.Created).ShouldBe(1);
        var winner = (byte)(Array.IndexOf(results, BlobWriteResult.Created) + 1);
        (await ReadAsync(key)).ShouldBe(Bytes(Size, winner));
    }

    [Fact]
    public async Task A_cancelled_put_leaves_no_key_and_a_later_put_succeeds()
    {
        var key = Key("raw");
        var content = new StallingStream(64 * 1024);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var put = Store.PutAsync(key, content, null, cancel.Token);
        await content.Stalled.Task.WaitAsync(Ct);
        await AssertAbsentAsync(key);
        (await ListAsync(new BlobKey($"raw/{Id}"))).ShouldBeEmpty("a write in progress is not listed");
        await cancel.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(put);
        await AssertAbsentAsync(key);
        (await Store.PutAsync(key, new MemoryStream([9]), 1, Ct)).ShouldBe(BlobWriteResult.Created);
    }

    [Fact]
    public async Task A_failed_put_leaves_no_key()
    {
        var key = Key("raw");

        await Should.ThrowAsync<IOException>(() => Store.PutAsync(key, new StallingStream(64 * 1024, new IOException("source broke")), null, Ct));

        await AssertAbsentAsync(key);
        (await ListAsync(new BlobKey($"raw/{Id}"))).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_put_of_another_length_than_declared_leaves_no_key()
    {
        var key = Key("raw");

        await Should.ThrowAsync<InvalidDataException>(() => Store.PutAsync(key, new MemoryStream(Bytes(10, 1)), 11, Ct));
        await Should.ThrowAsync<InvalidDataException>(() => Store.PutAsync(key, new MemoryStream(Bytes(12, 1)), 11, Ct));

        await AssertAbsentAsync(key);
    }

    [Theory]
    [InlineData(0L, null, 0, 1000)]
    [InlineData(0L, 1L, 0, 1)]
    [InlineData(10L, 20L, 10, 20)]
    [InlineData(999L, null, 999, 1)]
    [InlineData(999L, 1L, 999, 1)]
    [InlineData(990L, 100L, 990, 10)]
    [InlineData(1000L, null, 0, 0)]
    [InlineData(5000L, 10L, 0, 0)]
    public async Task Range_reads_are_clamped_to_the_blob(long offset, long? length, int expectedStart, int expectedCount)
    {
        var key = Key("parse", "result.json");
        var bytes = Enumerable.Range(0, 1000).Select(i => (byte)(i % 251)).ToArray();
        await Store.PutAsync(key, new MemoryStream(bytes), bytes.Length, Ct);

        await using var stream = await Store.OpenReadAsync(key, new BlobRange(offset, length), Ct);

        stream.ShouldNotBeNull();
        (await ReadToEndAsync(stream)).ShouldBe(bytes.AsSpan(expectedStart, expectedCount).ToArray());
    }

    [Fact]
    public async Task An_absent_key_reads_as_null_and_deletes_quietly()
    {
        var key = Key("stages");

        await AssertAbsentAsync(key);
        (await Store.OpenReadAsync(key, new BlobRange(10, 5), Ct)).ShouldBeNull();
        await Store.DeleteAsync(key, Ct);
        await Store.DeletePrefixAsync(new BlobKey($"tmp/{Id}"), Ct);
        (await ListAsync(new BlobKey($"stages/{Id}"))).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_one_segment_key_is_a_prefix_and_names_no_blob()
    {
        var prefix = new BlobKey("raw");

        await Should.ThrowAsync<ArgumentException>(() => Store.PutAsync(prefix, new MemoryStream([1]), 1, Ct));
        await Should.ThrowAsync<ArgumentException>(() => Store.ExistsAsync(prefix, Ct));
        await Should.ThrowAsync<ArgumentException>(() => Store.OpenReadAsync(prefix, null, Ct));
        await Should.ThrowAsync<ArgumentException>(() => Store.DeleteAsync(prefix, Ct));
    }

    [Fact]
    public async Task Delete_removes_the_blob_and_is_idempotent()
    {
        var key = Key("quarantine");
        await Store.PutAsync(key, new MemoryStream([1]), 1, Ct);

        await Store.DeleteAsync(key, Ct);
        await Store.DeleteAsync(key, Ct);

        await AssertAbsentAsync(key);
    }

    [Fact]
    public async Task Promote_moves_a_scratch_blob_without_overwriting()
    {
        var scratch = Key("tmp", "out");
        var target = Key("stages", "out.json");
        await Store.PutAsync(scratch, new MemoryStream(Bytes(100, 1)), 100, Ct);

        (await Store.PromoteAsync(scratch, target, Ct)).ShouldBe(BlobWriteResult.Created);
        await AssertAbsentAsync(scratch);
        (await ReadAsync(target)).ShouldBe(Bytes(100, 1));

        // A retry after the promote completed is success.
        (await Store.PromoteAsync(scratch, target, Ct)).ShouldBe(BlobWriteResult.AlreadyExisted);

        // A second scratch blob for the same target leaves the target as it is and is removed.
        await Store.PutAsync(scratch, new MemoryStream(Bytes(200, 2)), 200, Ct);
        (await Store.PromoteAsync(scratch, target, Ct)).ShouldBe(BlobWriteResult.AlreadyExisted);
        await AssertAbsentAsync(scratch);
        (await ReadAsync(target)).ShouldBe(Bytes(100, 1));
    }

    [Fact]
    public async Task Promote_takes_only_tmp_sources_and_non_tmp_targets()
    {
        var raw = Key("raw");
        var scratch = Key("tmp");
        await Store.PutAsync(raw, new MemoryStream([1]), 1, Ct);
        await Store.PutAsync(scratch, new MemoryStream([2]), 1, Ct);

        await Should.ThrowAsync<ArgumentException>(() => Store.PromoteAsync(raw, Key("stages"), Ct));
        await Should.ThrowAsync<ArgumentException>(() => Store.PromoteAsync(scratch, Key("tmp", "other"), Ct));
        await Should.ThrowAsync<KeyNotFoundException>(() => Store.PromoteAsync(Key("tmp", "missing"), Key("stages", "missing"), Ct));

        (await ReadAsync(raw)).ShouldBe([(byte)1]);
        (await ReadAsync(scratch)).ShouldBe([(byte)2]);
        await AssertAbsentAsync(Key("stages", "missing"));
    }

    [Fact]
    public async Task Prefix_delete_removes_only_what_is_under_the_prefix()
    {
        var inside = new[] { new BlobKey($"tmp/{Id}/a"), new BlobKey($"tmp/{Id}/b/c") };
        var sibling = new BlobKey($"tmp/{Id}x/a");
        var preview = new BlobKey($"previews/{Id}/chunk.json");
        foreach (var key in inside.Append(sibling).Append(preview))
        {
            await Store.PutAsync(key, new MemoryStream([1]), 1, Ct);
        }

        await Store.DeletePrefixAsync(new BlobKey($"tmp/{Id}"), Ct);
        await Store.DeletePrefixAsync(new BlobKey($"previews/{Id}"), Ct);

        foreach (var key in inside.Append(preview))
        {
            await AssertAbsentAsync(key);
        }

        (await Store.ExistsAsync(sibling, Ct)).ShouldBeTrue("a key that only shares the prefix string is not under it");
        await Store.DeleteAsync(sibling, Ct);
    }

    [Theory]
    [InlineData("uploads")]
    [InlineData("raw")]
    [InlineData("parse")]
    [InlineData("stages")]
    [InlineData("quarantine")]
    [InlineData("crawl")]
    [InlineData("chat-tmp")]
    public async Task Prefix_delete_is_refused_outside_tmp_and_previews(string area)
    {
        var key = Key(area);
        await Store.PutAsync(key, new MemoryStream([1]), 1, Ct);

        await Should.ThrowAsync<ArgumentException>(() => Store.DeletePrefixAsync(new BlobKey($"{area}/{Id}"), Ct));
        await Should.ThrowAsync<ArgumentException>(() => Store.DeletePrefixAsync(new BlobKey(area), Ct));

        (await Store.ExistsAsync(key, Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task Listing_returns_the_blobs_under_the_prefix()
    {
        var keys = new[] { Key("raw", "a"), Key("raw", "b.links.json.gz"), new BlobKey($"raw/{Id}/deeper/c") };
        for (var i = 0; i < keys.Length; i++)
        {
            await Store.PutAsync(keys[i], new MemoryStream(Bytes(i + 1, 1)), i + 1, Ct);
        }

        await Store.PutAsync(new BlobKey($"raw/{Id}x/other"), new MemoryStream([1]), 1, Ct);
        var before = DateTimeOffset.UtcNow.AddMinutes(-5);

        var listed = await ListAsync(new BlobKey($"raw/{Id}"));

        listed.Select(b => b.Key).OrderBy(k => k.Value, StringComparer.Ordinal).ShouldBe(keys.OrderBy(k => k.Value, StringComparer.Ordinal));
        listed.Single(b => b.Key == keys[2]).Length.ShouldBe(3);
        listed.ShouldAllBe(b => b.LastModified > before);
    }

    /// <summary>Writer i (1-based) puts <paramref name="size"/> bytes of value i; all start together.</summary>
    private async Task<BlobWriteResult[]> PutConcurrentlyAsync(BlobKey key, int writers, int size)
    {
        using var start = new Barrier(writers);
        return await Task.WhenAll(Enumerable.Range(1, writers).Select(i => Task.Run(async () =>
        {
            var content = new MemoryStream(Bytes(size, (byte)i));
            start.SignalAndWait(Ct);
            return await Store.PutAsync(key, content, size, Ct);
        }, Ct)));
    }

    protected async Task<byte[]> ReadAsync(BlobKey key) =>
        await ReadOrNullAsync(key) ?? throw new ShouldAssertException($"'{key}' is absent.");

    protected async Task<List<BlobInfo>> ListAsync(BlobKey prefix)
    {
        var listed = new List<BlobInfo>();
        await foreach (var blob in Store.ListAsync(prefix, Ct))
        {
            listed.Add(blob);
        }

        return listed;
    }

    protected async Task AssertAbsentAsync(BlobKey key)
    {
        (await Store.ExistsAsync(key, Ct)).ShouldBeFalse(key.Value);
        (await Store.OpenReadAsync(key, null, Ct)).ShouldBeNull(key.Value);
    }

    protected static byte[] Bytes(int count, byte fill) => Enumerable.Repeat(fill, count).ToArray();

    private async Task<byte[]?> ReadOrNullAsync(BlobKey key)
    {
        await using var stream = await Store.OpenReadAsync(key, null, Ct);
        return stream is null ? null : await ReadToEndAsync(stream);
    }

    private static async Task<byte[]> ReadToEndAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, Ct);
        return buffer.ToArray();
    }

    /// <summary>
    /// Content that delivers <c>bytesBeforeStall</c> bytes, then fails with <c>fault</c> or waits until the read is
    /// cancelled; <see cref="Stalled"/> completes when it gets there.
    /// </summary>
    protected sealed class StallingStream(int bytesBeforeStall, Exception? fault = null) : Stream
    {
        private int _sent;

        public TaskCompletionSource Stalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_sent < bytesBeforeStall)
            {
                var count = Math.Min(buffer.Length, bytesBeforeStall - _sent);
                buffer.Span[..count].Fill(7);
                _sent += count;
                return count;
            }

            Stalled.TrySetResult();
            if (fault is not null)
            {
                throw fault;
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
