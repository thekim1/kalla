using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Ume.Rag.Domain.Ports;

namespace Ume.Rag.Infrastructure.Blobs;

public static class BlobStoreExtensions
{
    /// <summary>
    /// The <see cref="IBlobStore"/>: the <see cref="FileSystemBlobStore"/> rooted at <c>BlobStore:Root</c>. The S3 store
    /// and <c>BlobStore:Kind</c> come in P1 (performance pass §3.13, to-verify #114).
    /// </summary>
    public static IHostApplicationBuilder AddRagBlobStore(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddOptions<BlobStoreOptions>().BindConfiguration(BlobStoreOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton<IBlobStore, FileSystemBlobStore>();
        return builder;
    }
}
