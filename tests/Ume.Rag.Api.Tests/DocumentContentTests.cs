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

    [Fact]
    public async Task Stored_blobs_stream_as_stored_and_envelope_blobs_are_refused()
    {
        var store = new FileSystemBlobStore(Options.Create(new BlobStoreOptions { Root = _root }), TimeProvider.System);
        await store.PutAsync(new BlobKey("uploads/plain"), new MemoryStream("%PDF-1.7 hej"u8.ToArray()), null, Ct);
        await store.PutAsync(new BlobKey("uploads/sealed"), new MemoryStream("URB2\0\0ciphertext"u8.ToArray()), null, Ct);
        var reader = new StoredBlobContentReader(store, NullLogger<StoredBlobContentReader>.Instance);

        var plain = (await reader.OpenAsync(Content("application/pdf", null, "uploads/plain"), Ct)).ShouldNotBeNull();
        await using (plain.Content)
        {
            plain.Length.ShouldBe(12);
            (await new StreamReader(plain.Content).ReadToEndAsync(Ct)).ShouldBe("%PDF-1.7 hej");
        }

        (await Should.ThrowAsync<RetrievalDeniedException>(() => reader.OpenAsync(Content(null, null, "uploads/sealed"), Ct)))
            .Code.ShouldBe(KnowledgeCodes.ContentUnavailable);
        (await reader.OpenAsync(Content(null, null, "uploads/missing"), Ct)).ShouldBeNull();
    }
}
