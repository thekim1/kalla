using System.Runtime.CompilerServices;
using Ume.Rag.Domain.Ports;

namespace Ume.Rag.Worker.Preview;

/// <summary>
/// The blob store of one preview job (engine design §11, <c>ExecutionMode.Preview</c>): every write lands under
/// <c>previews/{jobId}/</c> (a connector's <c>raw/{level}/{hash}</c> becomes <c>previews/{jobId}/raw/{level}/{hash}</c>),
/// reads see this job's blobs first and then the live store read-only (the raw blobs and cached outputs a preview reuses),
/// and deletes and listings outside the prefix are refused. Another job's preview prefix is never written.
/// </summary>
public sealed class PreviewBlobs(IBlobStore inner, Guid jobId) : IBlobStore
{
    public const string Area = "previews";

    /// <summary><c>previews/{jobId}</c>.</summary>
    public BlobKey Prefix { get; } = PrefixOf(jobId);

    public static BlobKey PrefixOf(Guid jobId) => new($"{Area}/{jobId:D}");

    /// <summary>The key a write of <paramref name="key"/> lands on.</summary>
    public BlobKey Own(BlobKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (IsOwn(key))
        {
            return key;
        }

        if (key.Area == Area)
        {
            throw new InvalidOperationException($"A preview writes only under {Prefix}/, not '{key}'.");
        }

        return new BlobKey($"{Prefix.Value}/{key.Value}");
    }

    public bool IsOwn(BlobKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key.Value.StartsWith(Prefix.Value + "/", StringComparison.Ordinal);
    }

    public Task<BlobWriteResult> PutAsync(BlobKey key, Stream content, long? length, CancellationToken cancellationToken) =>
        inner.PutAsync(Own(key), content, length, cancellationToken);

    public async Task<Stream?> OpenReadAsync(BlobKey key, BlobRange? range, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (IsOwn(key))
        {
            return await inner.OpenReadAsync(key, range, cancellationToken);
        }

        if (key.Area != Area && await inner.OpenReadAsync(Own(key), range, cancellationToken) is { } own)
        {
            return own;
        }

        return await inner.OpenReadAsync(key, range, cancellationToken);
    }

    public async Task<bool> ExistsAsync(BlobKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        return (!IsOwn(key) && key.Area != Area && await inner.ExistsAsync(Own(key), cancellationToken)) || await inner.ExistsAsync(key, cancellationToken);
    }

    public Task<BlobWriteResult> PromoteAsync(BlobKey from, BlobKey to, CancellationToken cancellationToken) =>
        inner.PromoteAsync(from, Own(to), cancellationToken);

    public Task DeleteAsync(BlobKey key, CancellationToken cancellationToken) => inner.DeleteAsync(OwnOnly(key), cancellationToken);

    public Task DeletePrefixAsync(BlobKey prefix, CancellationToken cancellationToken) =>
        inner.DeletePrefixAsync(prefix == Prefix ? prefix : OwnOnly(prefix), cancellationToken);

    public async IAsyncEnumerable<BlobInfo> ListAsync(BlobKey prefix, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var blob in inner.ListAsync(prefix == Prefix ? prefix : OwnOnly(prefix), cancellationToken))
        {
            yield return blob;
        }
    }

    private BlobKey OwnOnly(BlobKey key) =>
        IsOwn(key) ? key : throw new InvalidOperationException($"A preview deletes and lists only under {Prefix}/, not '{key}'.");
}
