using System.Text;
using Microsoft.Extensions.Logging;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Domain.Retrieval;

namespace Ume.Rag.Answering;

/// <summary>A document's plaintext bytes; <see cref="Length"/> when known (sets <c>Content-Length</c>).</summary>
public sealed record DocumentBytes(Stream Content, long? Length);

/// <summary>Opens the stored original of a <see cref="DocumentContent"/> as plaintext (knowledge design §3.5).</summary>
public interface IDocumentContentReader
{
    /// <summary>
    /// The plaintext, or null when the blob is absent. Throws <see cref="RetrievalDeniedException"/> 503
    /// <c>content_unavailable</c> when the blob cannot be served.
    /// </summary>
    Task<DocumentBytes?> OpenAsync(DocumentContent content, CancellationToken ct);
}

/// <summary>
/// The seam until stream A's blob envelope reader exists (issue #145): blobs are streamed as stored, and a blob that
/// starts with the <c>URB2</c> envelope header is refused with 503 <c>content_unavailable</c> (logged), so no ciphertext
/// is ever served as a document. Replaced by the envelope reader, which decrypts and verifies each segment.
/// </summary>
public sealed partial class StoredBlobContentReader(IBlobStore blobs, ILogger<StoredBlobContentReader> logger) : IDocumentContentReader
{
    private static readonly byte[] EnvelopeHeader = "URB2"u8.ToArray();

    public async Task<DocumentBytes?> OpenAsync(DocumentContent content, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        var stream = await blobs.OpenReadAsync(content.Key, null, ct);
        if (stream is null)
        {
            return null;
        }

        var head = new byte[EnvelopeHeader.Length];
        var read = await stream.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, ct);
        if (read == head.Length && head.AsSpan().SequenceEqual(EnvelopeHeader))
        {
            await stream.DisposeAsync();
            LogEnvelopeUnsupported(logger, content.DocumentId);
            throw new RetrievalDeniedException(503, KnowledgeCodes.ContentUnavailable);
        }

        var length = stream.CanSeek ? stream.Length : (long?)null;
        if (stream.CanSeek)
        {
            stream.Position = 0;
            return new DocumentBytes(stream, length);
        }

        return new DocumentBytes(new PrefixedStream(head.AsMemory(0, read), stream), null);
    }

    [LoggerMessage(LogLevel.Error, "Document {DocumentId}'s blob is envelope-encrypted and no envelope reader is registered (issue #145)")]
    private static partial void LogEnvelopeUnsupported(ILogger logger, Guid documentId);

    /// <summary>The bytes already read, then the rest of the stream (read-only, forward-only).</summary>
    private sealed class PrefixedStream(ReadOnlyMemory<byte> prefix, Stream rest) : Stream
    {
        private ReadOnlyMemory<byte> _prefix = prefix;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_prefix.IsEmpty)
            {
                return rest.Read(buffer);
            }

            var n = Math.Min(buffer.Length, _prefix.Length);
            _prefix.Span[..n].CopyTo(buffer);
            _prefix = _prefix[n..];
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_prefix.IsEmpty)
            {
                return await rest.ReadAsync(buffer, cancellationToken);
            }

            var n = Math.Min(buffer.Length, _prefix.Length);
            _prefix[..n].CopyTo(buffer);
            _prefix = _prefix[n..];
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                rest.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await rest.DisposeAsync();
            await base.DisposeAsync();
        }
    }
}

/// <summary>The response headers of <c>/content</c> (knowledge design §3.5), pure so the rules are unit-tested.</summary>
public static class ContentHeaders
{
    /// <summary>The P1 upload types (connectors §9.2); anything else is served as <c>application/octet-stream</c>.</summary>
    public static IReadOnlySet<string> AllowedTypes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "application/pdf", "text/html", "application/xhtml+xml", "text/plain", "text/markdown", "text/csv",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "application/vnd.oasis.opendocument.text", "application/vnd.oasis.opendocument.spreadsheet",
        "application/vnd.oasis.opendocument.presentation", "image/jpeg", "image/png", "image/tiff",
    };

    private static readonly HashSet<string> Inline = new(StringComparer.Ordinal) { "application/pdf", "image/png", "image/jpeg" };

    private static readonly Dictionary<string, string> Extensions = new(StringComparer.Ordinal)
    {
        ["application/pdf"] = "pdf", ["text/html"] = "html", ["application/xhtml+xml"] = "xhtml", ["text/plain"] = "txt", ["text/markdown"] = "md",
        ["text/csv"] = "csv", ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"] = "docx",
        ["application/vnd.openxmlformats-officedocument.presentationml.presentation"] = "pptx",
        ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"] = "xlsx", ["application/vnd.oasis.opendocument.text"] = "odt",
        ["application/vnd.oasis.opendocument.spreadsheet"] = "ods", ["application/vnd.oasis.opendocument.presentation"] = "odp",
        ["image/jpeg"] = "jpg", ["image/png"] = "png", ["image/tiff"] = "tiff",
    };

    /// <summary>The sniffed type if allowed, else <c>application/octet-stream</c>.</summary>
    public static string ContentType(string? mediaType) => mediaType is not null && AllowedTypes.Contains(mediaType) ? mediaType : "application/octet-stream";

    /// <summary>
    /// <c>inline</c> for PDF, PNG and JPEG, else <c>attachment</c>; <c>filename*=UTF-8''…</c> from the display name, else
    /// <c>dokument-{id:N}.{ext}</c>.
    /// </summary>
    public static string ContentDisposition(DocumentContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var type = ContentType(content.MediaType);
        var name = content.DisplayName is { Length: > 0 } display
            ? display
            : $"dokument-{content.DocumentId:N}{(Extensions.TryGetValue(type, out var ext) ? "." + ext : "")}";
        var ascii = new string([.. name.Select(c => c is >= ' ' and < (char)127 and not '"' and not '\\' and not ';' ? c : '_')]);
        return $"{(Inline.Contains(type) ? "inline" : "attachment")}; filename=\"{ascii}\"; filename*=UTF-8''{Uri.EscapeDataString(name)}";
    }

    /// <summary>The fixed headers: nosniff, no caching, no ranges; a sandboxing CSP for everything but PDF; ETag = hex content hash.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Fixed(DocumentContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var headers = new List<KeyValuePair<string, string>>
        {
            new("X-Content-Type-Options", "nosniff"),
            new("Cache-Control", "private, no-store"),
            new("Accept-Ranges", "none"),
            new("ETag", $"\"{Convert.ToHexStringLower(content.ContentHash)}\""),
        };
        if (ContentType(content.MediaType) != "application/pdf")
        {
            headers.Add(new("Content-Security-Policy", "default-src 'none'; sandbox"));
        }

        return headers;
    }

    /// <summary>The static Swedish page a browser gets for an expired or invalid grant (§4.6); served with CSP <c>default-src 'none'</c>.</summary>
    public static string ExpiredPage { get; } = new StringBuilder()
        .Append("<!doctype html><html lang=\"sv\"><head><meta charset=\"utf-8\"><title>Länken fungerar inte</title></head>")
        .Append("<body><p>Länken har gått ut. Ställ frågan igen för att få en ny länk.</p></body></html>")
        .ToString();
}
