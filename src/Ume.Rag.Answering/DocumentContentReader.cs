using System.Text;
using Microsoft.Extensions.Logging;
using Ume.Rag.Domain.Retrieval;
using Ume.Rag.Infrastructure.Blobs;

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
/// Reads the stored original through stream A's blob envelope (data-model §7, knowledge design §3.5, #158) at the
/// document's level: <c>public</c> blobs as stored, <c>internal</c> blobs decrypted with each 64 KiB segment verified before
/// its bytes are returned. A blob that is not what its level requires, or a missing or wrong master key, is refused on
/// open with 503 <c>content_unavailable</c> (logged); damage found later surfaces as <see cref="BlobIntegrityException"/>
/// from the stream, which aborts the response.
/// </summary>
public sealed partial class EnvelopeContentReader(IBlobEnvelope envelope, ILogger<EnvelopeContentReader> logger) : IDocumentContentReader
{
    public async Task<DocumentBytes?> OpenAsync(DocumentContent content, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        try
        {
            return await envelope.OpenReadAsync(content.Key, content.Level, ct) is { } blob ? new DocumentBytes(blob.Content, blob.Length) : null;
        }
        catch (Exception ex) when (ex is BlobIntegrityException or KeyProviderException)
        {
            LogUnreadable(logger, content.DocumentId, ex.GetType().Name);
            throw new RetrievalDeniedException(503, KnowledgeCodes.ContentUnavailable);
        }
    }

    [LoggerMessage(LogLevel.Error, "Document {DocumentId}'s blob cannot be read ({Failure}); the download is refused")]
    private static partial void LogUnreadable(ILogger logger, Guid documentId, string failure);
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
