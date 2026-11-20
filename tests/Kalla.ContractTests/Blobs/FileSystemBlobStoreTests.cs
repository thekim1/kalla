using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Kalla.Domain.Ports;
using Kalla.Infrastructure.Blobs;

namespace Kalla.ContractTests.Blobs;

/// <summary>
/// The <see cref="IBlobStore"/> contract on <see cref="FileSystemBlobStore"/>, rooted in a fresh directory per test, plus
/// what only the file system has: staging files, stray files and symbolic links below the root.
/// </summary>
public sealed class FileSystemBlobStoreTests : BlobStoreContractTests, IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("kalla-blobs-").FullName;
    private readonly FileSystemBlobStore _store;

    public FileSystemBlobStoreTests() => _store = Create(_root);

    protected override IBlobStore Store => _store;

    private string Staging => Path.Join(_root, FileSystemBlobStore.StagingDirectoryName);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Blobs_are_files_below_the_root_and_no_staging_file_remains()
    {
        var key = Key("raw");
        await _store.PutAsync(key, new MemoryStream([1, 2]), 2, Ct);
        await Should.ThrowAsync<IOException>(() => _store.PutAsync(Key("raw", "failed"), new StallingStream(10, new IOException("broke")), null, Ct));

        File.ReadAllBytes(Path.Join(_root, "raw", Id, "blob")).ShouldBe([(byte)1, (byte)2]);
        Directory.EnumerateFiles(Staging).ShouldBeEmpty();
    }

    [Fact]
    public async Task Staging_and_stray_files_are_never_listed()
    {
        var key = Key("raw");
        await _store.PutAsync(key, new MemoryStream([1]), 1, Ct);
        await File.WriteAllBytesAsync(Path.Join(Staging, "leftover.part"), [1], Ct);
        foreach (var stray in new[] { ".hidden", "UPPER", "with space", "trailing." })
        {
            await File.WriteAllBytesAsync(Path.Join(_root, "raw", Id, stray), [1], Ct);
        }

        var listed = await ListAsync(new BlobKey($"raw/{Id}"));

        listed.Select(b => b.Key).ShouldBe([key]);
    }

    [Fact]
    public async Task Staging_files_older_than_a_day_are_removed_on_the_first_put()
    {
        Directory.CreateDirectory(Staging);
        var stale = Path.Join(Staging, "stale.part");
        var fresh = Path.Join(Staging, "fresh.part");
        await File.WriteAllBytesAsync(stale, [1], Ct);
        await File.WriteAllBytesAsync(fresh, [1], Ct);
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow - FileSystemBlobStore.StagingMaxAge - TimeSpan.FromMinutes(1));
        var store = Create(_root);

        await store.PutAsync(Key("tmp"), new MemoryStream([1]), 1, Ct);

        File.Exists(stale).ShouldBeFalse();
        File.Exists(fresh).ShouldBeTrue("a write in progress elsewhere is younger than a day");
    }

    [Fact]
    public async Task A_symbolic_link_below_the_root_is_refused()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Symbolic links need privileges on Windows.");
        var outside = Directory.CreateTempSubdirectory("kalla-outside-").FullName;
        try
        {
            await File.WriteAllBytesAsync(Path.Join(outside, "secret"), [42], Ct);
            Directory.CreateDirectory(Path.Join(_root, "raw"));
            Directory.CreateSymbolicLink(Path.Join(_root, "raw", Id), outside);
            var key = Key("raw", "secret");

            await Should.ThrowAsync<IOException>(() => _store.OpenReadAsync(key, null, Ct));
            await Should.ThrowAsync<IOException>(() => _store.ExistsAsync(key, Ct));
            await Should.ThrowAsync<IOException>(() => _store.PutAsync(Key("raw", "planted"), new MemoryStream([1]), 1, Ct));
            await Should.ThrowAsync<IOException>(() => _store.DeleteAsync(key, Ct));
            File.Exists(Path.Join(outside, "secret")).ShouldBeTrue();
            File.Exists(Path.Join(outside, "planted")).ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void The_root_must_be_absolute()
    {
        Should.Throw<InvalidOperationException>(() => Create("relative/blobs"));
    }

    [Fact]
    public void AddRagBlobStore_registers_the_file_system_store_on_BlobStore_Root()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["BlobStore:Root"] = _root });

        builder.AddRagBlobStore();
        using var services = builder.Services.BuildServiceProvider();

        services.GetRequiredService<IBlobStore>().ShouldBeOfType<FileSystemBlobStore>();
        services.GetRequiredService<IOptions<BlobStoreOptions>>().Value.Root.ShouldBe(_root);
        new BlobStoreOptions().Root.ShouldBe("/data/blobs/rag");
    }

    private static FileSystemBlobStore Create(string root) =>
        new(Options.Create(new BlobStoreOptions { Root = root }), TimeProvider.System);
}
