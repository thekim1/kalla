using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ume.Rag.Answering;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Domain.Retrieval;
using Ume.Rag.Infrastructure.Blobs;

namespace Ume.Rag.Api.Tests;

/// <summary>Knowledge design §3.5: the response headers and the stored-blob seam (envelope blobs refused until #145).</summary>
public sealed class DocumentContentTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _root = Directory.CreateTempSubdirectory("ume-rag-content-").FullName;
    private readonly Guid _document = Guid.Parse("0192aaaa-0000-7000-8000-000000000001");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private DocumentContent Content(string? mediaType, string? displayName, string key = "uploads/x") =>
        new(_document, new BlobKey(key), Classification.Internal, mediaType, displayName, [0xab, 0x01]);

    [Theory]
    [InlineData("application/pdf", "application/pdf", "inline")]
    [InlineData("image/png", "image/png", "inline")]
    [InlineData("application/vnd.openxmlformats-officedocument.wordprocessingml.document", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "attachment")]
    [InlineData("text/html", "text/html", "attachment")]
    [InlineData("application/x-msdownload", "application/octet-stream", "attachment")]
    [InlineData(null, "application/octet-stream", "attachment")]
    public void Type_and_disposition_follow_the_allow_list(string? sniffed, string type, string disposition)
    {
        ContentHeaders.ContentType(sniffed).ShouldBe(type);
        ContentHeaders.ContentDisposition(Content(sniffed, "a.bin")).ShouldStartWith(disposition + ";");
    }

    [Fact]
    public void The_file_name_comes_from_the_display_name_else_the_document_id()
    {
        ContentHeaders.ContentDisposition(Content("application/pdf", "Taxa för bygglov \"2026\".pdf"))
            .ShouldBe("inline; filename=\"Taxa f_r bygglov _2026_.pdf\"; filename*=UTF-8''Taxa%20f%C3%B6r%20bygglov%20%222026%22.pdf");
        ContentHeaders.ContentDisposition(Content("application/vnd.oasis.opendocument.text", null))
            .ShouldBe("attachment; filename=\"dokument-0192aaaa000070008000000000000001.odt\"; filename*=UTF-8''dokument-0192aaaa000070008000000000000001.odt");
    }

    [Fact]
    public void Fixed_headers_sandbox_everything_but_pdf()
    {
        var html = ContentHeaders.Fixed(Content("text/html", null)).ToDictionary();
        html["X-Content-Type-Options"].ShouldBe("nosniff");
        html["Cache-Control"].ShouldBe("private, no-store");
        html["Accept-Ranges"].ShouldBe("none");
        html["ETag"].ShouldBe("\"ab01\"");
        html["Content-Security-Policy"].ShouldBe("default-src 'none'; sandbox");
        ContentHeaders.Fixed(Content("application/pdf", null)).ToDictionary().ContainsKey("Content-Security-Policy").ShouldBeFalse();
    }

    /// <summary>No policy rows: rag-blob-internal for internal, nothing for public (data-model §7).</summary>
    private sealed class DefaultPolicy : IBlobKeyPolicy
    {
        public Task<string?> MasterKeyNameAsync(string level, CancellationToken cancellationToken) =>
            Task.FromResult(DataProtectionBlobKeyPolicy.DefaultKeyName(level));
    }

    private (FileSystemBlobStore Plain, BlobEnvelope Envelope, EnvelopeContentReader Reader) Envelope()
    {
        var keys = Directory.CreateDirectory(Path.Combine(_root, "keys")).FullName;
        File.WriteAllText(Path.Combine(keys, "rag-blob-internal"), Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)) + "\n");
        var plain = new FileSystemBlobStore(Options.Create(new BlobStoreOptions { Root = Path.Combine(_root, "blobs") }), TimeProvider.System);
        var envelope = new BlobEnvelope(plain, new FileKeyProvider(Options.Create(new KeyProviderOptions { Directory = keys })), new DefaultPolicy());
        return (plain, envelope, new EnvelopeContentReader(envelope, NullLogger<EnvelopeContentReader>.Instance));
    }

    private DocumentContent At(string level, string key) => new(_document, new BlobKey(key), level, "application/pdf", null, [0xab]);

    [Fact]
    public async Task Public_blobs_stream_as_stored_and_internal_blobs_are_decrypted()
    {
        // #158: citation downloads read through the envelope at the document's level (knowledge §3.5).
        var (plain, envelope, reader) = Envelope();
        var pdf = "%PDF-1.7 hej"u8.ToArray();
        var large = new byte[200_000]; // several 64 KiB segments
        System.Security.Cryptography.RandomNumberGenerator.Fill(large);
        await plain.PutAsync(new BlobKey("uploads/public"), new MemoryStream(pdf), null, Ct);
        await envelope.PutAsync(new BlobKey("uploads/internal"), Classification.Internal, new MemoryStream(large), large.Length, Ct);

        var open = (await reader.OpenAsync(At(Classification.Public, "uploads/public"), Ct)).ShouldNotBeNull();
        await using (open.Content)
        {
            open.Length.ShouldBe(pdf.Length);
            (await ReadAllAsync(open.Content)).ShouldBe(pdf);
        }

        var sealedBlob = (await reader.OpenAsync(At(Classification.Internal, "uploads/internal"), Ct)).ShouldNotBeNull();
        await using (sealedBlob.Content)
        {
            sealedBlob.Length.ShouldBe(large.Length);
            (await ReadAllAsync(sealedBlob.Content)).ShouldBe(large);
        }

        (await reader.OpenAsync(At(Classification.Internal, "uploads/missing"), Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task A_plaintext_internal_blob_is_refused_and_a_damaged_segment_aborts_the_read()
    {
        var (plain, envelope, reader) = Envelope();
        await plain.PutAsync(new BlobKey("uploads/plaintext"), new MemoryStream("%PDF-1.7 klartext"u8.ToArray()), null, Ct);
        (await Should.ThrowAsync<RetrievalDeniedException>(() => reader.OpenAsync(At(Classification.Internal, "uploads/plaintext"), Ct)))
            .Code.ShouldBe(KnowledgeCodes.ContentUnavailable);

        var data = new byte[200_000];
        System.Security.Cryptography.RandomNumberGenerator.Fill(data);
        await envelope.PutAsync(new BlobKey("uploads/damaged"), Classification.Internal, new MemoryStream(data), data.Length, Ct);
        var path = Path.Combine(_root, "blobs", "uploads", "damaged");
        File.SetAttributes(path, FileAttributes.Normal);
        var bytes = await File.ReadAllBytesAsync(path, Ct);
        bytes[^100] ^= 0xff; // the last segment
        File.Delete(path);
        await File.WriteAllBytesAsync(path, bytes, Ct);

        var damaged = (await reader.OpenAsync(At(Classification.Internal, "uploads/damaged"), Ct)).ShouldNotBeNull();
        await using (damaged.Content)
        {
            await Should.ThrowAsync<BlobIntegrityException>(() => ReadAllAsync(damaged.Content));
        }
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, Ct);
        return buffer.ToArray();
    }
}
