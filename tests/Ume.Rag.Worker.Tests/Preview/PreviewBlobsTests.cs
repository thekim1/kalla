using Microsoft.Extensions.Options;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Infrastructure.Blobs;
using Ume.Rag.Worker.Preview;

namespace Ume.Rag.Worker.Tests.Preview;

/// <summary>Engine design §19 unit row: the preview context refuses non-preview blob paths (writes land in its own prefix).</summary>
public sealed class PreviewBlobsTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("ume-rag-preview-unit-");
    private readonly FileSystemBlobStore _live;

    public PreviewBlobsTests() => _live = new FileSystemBlobStore(Options.Create(new BlobStoreOptions { Root = _root.FullName }), TimeProvider.System);

    public void Dispose() => _root.Delete(recursive: true);

    [Fact]
    public async Task Writes_land_under_the_jobs_prefix_and_reads_fall_back_to_the_live_store()
    {
        var job = Guid.CreateVersion7();
        var preview = new PreviewBlobs(_live, job);
        var raw = new BlobKey("raw/internal/" + new string('a', 64));
        await PutAsync(_live, raw, [1]);

        (await ReadAsync(preview, raw)).ShouldBe([1], "the live blob is read");
        await PutAsync(preview, raw, [2]);

        (await ReadAsync(_live, raw)).ShouldBe([1], "the live blob is unchanged");
        (await ReadAsync(_live, new BlobKey($"previews/{job:D}/{raw.Value}"))).ShouldBe([2]);
        (await ReadAsync(preview, raw)).ShouldBe([2], "the preview sees its own copy first");
    }

    [Fact]
    public async Task Another_jobs_prefix_and_live_deletes_are_refused()
    {
        var preview = new PreviewBlobs(_live, Guid.CreateVersion7());
        var other = new BlobKey($"previews/{Guid.CreateVersion7():D}/0/parse.json");
        var raw = new BlobKey("raw/internal/" + new string('b', 64));
        await PutAsync(_live, raw, [1]);

        await Should.ThrowAsync<InvalidOperationException>(() => PutAsync(preview, other, [1]));
        await Should.ThrowAsync<InvalidOperationException>(() => preview.DeleteAsync(raw, Ct));
        await Should.ThrowAsync<InvalidOperationException>(() => preview.DeletePrefixAsync(new BlobKey("previews"), Ct));
        (await ReadAsync(_live, raw)).ShouldBe([1]);
    }

    private static async Task PutAsync(IBlobStore store, BlobKey key, byte[] bytes)
    {
        using var content = new MemoryStream(bytes);
        await store.PutAsync(key, content, bytes.Length, Ct);
    }

    private static async Task<byte[]> ReadAsync(IBlobStore store, BlobKey key)
    {
        await using var stream = await store.OpenReadAsync(key, null, Ct);
        stream.ShouldNotBeNull();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, Ct);
        return buffer.ToArray();
    }
}
