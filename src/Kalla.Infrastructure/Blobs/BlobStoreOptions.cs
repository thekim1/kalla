using System.ComponentModel.DataAnnotations;

namespace Kalla.Infrastructure.Blobs;

/// <summary>Deployment settings of the blob store (<c>BlobStore:*</c>, data-model §9, repo-and-deployment §3).</summary>
public sealed class BlobStoreOptions
{
    public const string SectionName = "BlobStore";

    /// <summary>
    /// The container of the file system store, an absolute path: <c>/data/blobs/rag</c>, the <c>rag/</c> container on the
    /// <c>kalla-blobs</c> volume (repo-and-deployment §6.3). It must be one file system, because writes are published by a
    /// hard link from its <c>.staging/</c> directory.
    /// </summary>
    [Required]
    public string Root { get; set; } = "/data/blobs/rag";
}
