using System.Security.Cryptography;
using Ume.Rag.Infrastructure.Blobs;

namespace Ume.Rag.Worker.Tests.Blobs;

/// <summary>The file key provider (platform plan §8a, postponed decision K1): key files as init-deployment.sh writes them.</summary>
public sealed class FileKeyProviderTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly DirectoryInfo _keys = Directory.CreateTempSubdirectory("ume-rag-keys-unit-");

    public void Dispose() => _keys.Delete(recursive: true);

    [Fact]
    public async Task A_data_key_round_trips_and_verifies_only_under_its_name_and_version()
    {
        TestBlobKeys.WriteKey(_keys.FullName, "rag-blob-internal");
        TestBlobKeys.WriteKey(_keys.FullName, "rag-blob-other");
        var provider = TestBlobKeys.Provider(_keys.FullName);
        var dataKey = RandomNumberGenerator.GetBytes(32);

        var wrapped = await provider.WrapAsync("rag-blob-internal", dataKey, Ct);

        wrapped.KeyName.ShouldBe("rag-blob-internal");
        wrapped.KeyVersion.ShouldBe("1");
        wrapped.Ciphertext.Length.ShouldBe(60);
        (await provider.UnwrapAsync(wrapped, Ct)).ShouldBe(dataKey);
        await Should.ThrowAsync<KeyProviderException>(() => provider.UnwrapAsync(wrapped with { KeyName = "rag-blob-other" }, Ct));
        (await Should.ThrowAsync<KeyProviderException>(() => provider.UnwrapAsync(wrapped with { KeyVersion = "2" }, Ct)))
            .Message.ShouldContain("version 1 only");
        var damaged = wrapped.Ciphertext.ToArray();
        damaged[20] ^= 1;
        await Should.ThrowAsync<KeyProviderException>(() => provider.UnwrapAsync(wrapped with { Ciphertext = damaged }, Ct));
    }

    [Theory]
    [InlineData("not base64 !", "is not base64")]
    [InlineData("AAAA", "holds 3 bytes")]
    public async Task A_malformed_key_file_is_refused(string content, string message)
    {
        await File.WriteAllTextAsync(Path.Combine(_keys.FullName, "rag-blob-internal"), content, Ct);

        (await Should.ThrowAsync<KeyProviderException>(() => TestBlobKeys.Provider(_keys.FullName).WrapAsync("rag-blob-internal", new byte[32], Ct)))
            .Message.ShouldContain(message);
    }

    [Theory]
    [InlineData("../rag-blob-internal")]
    [InlineData("Rag-Blob")]
    [InlineData("")]
    public async Task Key_names_outside_the_alphabet_are_refused(string name)
    {
        await Should.ThrowAsync<KeyProviderException>(() => TestBlobKeys.Provider(_keys.FullName).WrapAsync(name, new byte[32], Ct));
    }

    [Fact]
    public async Task A_relative_directory_is_refused_when_a_key_is_first_used()
    {
        (await Should.ThrowAsync<KeyProviderException>(() => TestBlobKeys.Provider("relative/keys").WrapAsync("rag-blob-internal", new byte[32], Ct)))
            .Message.ShouldContain("must be an absolute path");
    }
}
