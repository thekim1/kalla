using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Kalla.Domain.Ports;

namespace Kalla.Infrastructure.Blobs;

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

    /// <summary>
    /// Envelope encryption of blobs (data-model §7) over the registered <see cref="IBlobStore"/>; call after
    /// <see cref="AddRagBlobStore"/> and <c>AddRagDatabase</c> (the level policies are read from
    /// <c>config."DataProtectionPolicies"</c>). Registers the <see cref="FileKeyProvider"/> as <see cref="IKeyProvider"/>
    /// (<c>KeyProvider:Directory</c>, default <c>/run/keys</c>, where the Compose files mount
    /// <c>secrets/keys/rag-blob-internal</c>), the <see cref="DataProtectionBlobKeyPolicy"/>, the
    /// <see cref="IBlobEnvelope"/> over the plain store, and replaces <see cref="IBlobStore"/> by the
    /// <see cref="EnvelopeBlobStore"/> around it. The worker calls it; kalla-api calls it for citation downloads (knowledge
    /// design §3.5) and reads with the document's level:
    /// <code>
    /// builder.AddRagBlobStore();
    /// builder.AddRagBlobEnvelope();
    /// // GET /v1/documents/{id}/content, with IBlobEnvelope envelope:
    /// await using var blob = await envelope.OpenReadAsync(rawKey, document.Classification, ct); // null: the blob is absent
    /// </code>
    /// </summary>
    public static IHostApplicationBuilder AddRagBlobEnvelope(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        var plain = services.LastOrDefault(d => d.ServiceType == typeof(IBlobStore) && !d.IsKeyedService)
            ?? throw new InvalidOperationException($"Register the blob store ({nameof(AddRagBlobStore)}) before the envelope.");
        services.AddOptions<KeyProviderOptions>().BindConfiguration(KeyProviderOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IKeyProvider, FileKeyProvider>();
        services.TryAddSingleton<IBlobKeyPolicy, DataProtectionBlobKeyPolicy>();
        services.TryAddSingleton(sp => new PlainBlobStore(Build(sp, plain)));
        services.TryAddSingleton(sp => new BlobEnvelope(sp.GetRequiredService<PlainBlobStore>().Store, sp.GetRequiredService<IKeyProvider>(),
            sp.GetRequiredService<IBlobKeyPolicy>()));
        services.TryAddSingleton<IBlobEnvelope>(sp => sp.GetRequiredService<BlobEnvelope>());
        services.Replace(ServiceDescriptor.Singleton<IBlobStore>(sp =>
            new EnvelopeBlobStore(sp.GetRequiredService<BlobEnvelope>(), sp.GetRequiredService<PlainBlobStore>().Store)));
        return builder;
    }

    private static IBlobStore Build(IServiceProvider services, ServiceDescriptor descriptor) => (IBlobStore)(
        descriptor.ImplementationInstance
        ?? descriptor.ImplementationFactory?.Invoke(services)
        ?? ActivatorUtilities.CreateInstance(services, descriptor.ImplementationType!));

    /// <summary>The store the envelope wraps, built once from the registration it replaced.</summary>
    private sealed record PlainBlobStore(IBlobStore Store);
}
