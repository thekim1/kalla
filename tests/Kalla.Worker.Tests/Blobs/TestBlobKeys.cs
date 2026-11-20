using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Kalla.Infrastructure.Blobs;

namespace Kalla.Worker.Tests.Blobs;

/// <summary>
/// The test run's master keys for the file key provider: one directory with <c>rag-blob-internal</c> (32 random bytes in
/// base64, as <c>init-deployment.sh</c> writes it), set as <c>KeyProvider:Directory</c> on every test worker.
/// </summary>
internal static class TestBlobKeys
{
    public const string Internal = "rag-blob-internal";

    private static readonly Lazy<string> Root = new(() =>
    {
        var directory = System.IO.Directory.CreateTempSubdirectory("kalla-keys-").FullName;
        WriteKey(directory, Internal);
        return directory;
    });

    public static string Directory => Root.Value;

    public static void WriteKey(string directory, string name) =>
        File.WriteAllText(Path.Combine(directory, name), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) + "\n");

    public static FileKeyProvider Provider(string? directory = null) =>
        new(Options.Create(new KeyProviderOptions { Directory = directory ?? Directory }));

    /// <summary>The worker's envelope store over the file system store at <paramref name="root"/>, with the default level policies.</summary>
    public static EnvelopeBlobStore Store(DirectoryInfo root, IKeyProvider? keys = null)
    {
        var plain = Plain(root);
        return new EnvelopeBlobStore(new BlobEnvelope(plain, keys ?? Provider(), new DefaultPolicy()), plain);
    }

    /// <summary>The envelope over the file system store at <paramref name="root"/>, as the admin writes uploads with it.</summary>
    public static BlobEnvelope Envelope(DirectoryInfo root) => new(Plain(root), Provider(), new DefaultPolicy());

    private static FileSystemBlobStore Plain(DirectoryInfo root) =>
        new(Options.Create(new BlobStoreOptions { Root = root.FullName }), TimeProvider.System);

    /// <summary>No policy rows: <see cref="DataProtectionBlobKeyPolicy.DefaultKeyName"/>.</summary>
    internal sealed class DefaultPolicy : IBlobKeyPolicy
    {
        public Task<string?> MasterKeyNameAsync(string level, CancellationToken cancellationToken) =>
            Task.FromResult(DataProtectionBlobKeyPolicy.DefaultKeyName(level));
    }
}
