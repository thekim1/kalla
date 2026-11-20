using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Kalla.Worker.Connectors.Sitemaps;

/// <summary>One <c>&lt;url&gt;</c> or <c>&lt;sitemap&gt;</c> entry: its <c>loc</c> as written and its <c>lastmod</c>, if valid.</summary>
public sealed record SitemapEntry(string Location, DateTimeOffset? LastModified);

/// <param name="IsIndex">A <c>sitemapindex</c>: the entries are child sitemaps.</param>
/// <param name="Entries">At most <see cref="SitemapParser.MaxEntries"/>.</param>
/// <param name="Truncated">More entries than the limit; the rest were dropped (and counted in the crawl report).</param>
/// <param name="Error">Why the file was refused (<c>sitemap.*</c>); entries read before the error are kept.</param>
public sealed record SitemapDocument(bool IsIndex, IReadOnlyList<SitemapEntry> Entries, bool Truncated, string? Error);

/// <summary>
/// The sitemaps.org protocol (connectors design §5.1): <see cref="XmlReader"/> with DTDs prohibited and no resolver, so
/// XXE and entity expansion are refused; gzip detected by its magic bytes; at most 50,000 entries and 50 MB uncompressed
/// per file. Any namespace is accepted, since real sitemaps get it wrong. A file whose first character (after a byte-order
/// mark and white space) is not <c>&lt;</c> is a plain-text sitemap: one URL per line, lines that are not absolute
/// http(s) URLs skipped, and none at all <c>sitemap.invalid</c>.
/// </summary>
public static class SitemapParser
{
    public const int MaxEntries = 50_000;
    public const long MaxUncompressedBytes = 50L * 1024 * 1024;

    public const string RefusedDtd = "sitemap.dtd";
    public const string RefusedSize = "sitemap.too_large";
    public const string RefusedSyntax = "sitemap.invalid";

    /// <summary>Enough of the start of a file to tell XML from plain text.</summary>
    private const int SniffBytes = 64;

    public static async Task<SitemapDocument> ParseAsync(Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var buffered = new BufferedStream(content);
        var magic = new byte[2];
        var peeked = await buffered.ReadAtLeastAsync(magic, 2, throwOnEndOfStream: false, cancellationToken);
        Stream source = new PrefixedStream(magic.AsMemory(0, peeked), buffered);
        if (peeked == 2 && magic[0] == 0x1F && magic[1] == 0x8B)
        {
            source = new GZipStream(source, CompressionMode.Decompress);
        }

        await using var limited = new LimitedStream(source, MaxUncompressedBytes);
        // Filled as the file is read, so a refusal keeps what was read before it.
        var read = new Reading();
        try
        {
            var head = new byte[SniffBytes];
            var peekedHead = await limited.ReadAtLeastAsync(head, SniffBytes, throwOnEndOfStream: false, cancellationToken);
            var file = new PrefixedStream(head.AsMemory(0, peekedHead), limited);
            if (IsPlainText(head.AsSpan(0, peekedHead)))
            {
                await ReadTextAsync(file, read, cancellationToken);
                return read.Document(read.Entries.Count == 0 ? RefusedSyntax : null);
            }

            await ReadXmlAsync(file, read, cancellationToken);
        }
        catch (XmlException e) when (e.Message.Contains("DTD", StringComparison.Ordinal))
        {
            return read.Document(RefusedDtd);
        }
        catch (XmlException)
        {
            return read.Document(RefusedSyntax);
        }
        catch (InvalidDataException)
        {
            // A gzip stream that does not decompress.
            return read.Document(RefusedSyntax);
        }
        catch (LimitExceededException)
        {
            return read.Document(RefusedSize);
        }

        return read.Document(null);
    }

    /// <summary>Plain text when the first character after a UTF-8 byte-order mark and white space is not <c>&lt;</c>; an empty or blank start is XML (and fails as such).</summary>
    private static bool IsPlainText(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            head = head[3..];
        }

        var first = head.IndexOfAnyExcept(" \t\r\n"u8);
        return first >= 0 && head[first] != (byte)'<';
    }

    /// <summary>The <c>url</c> or <c>sitemap</c> entries of an XML sitemap into <paramref name="read"/>.</summary>
    private static async Task ReadXmlAsync(Stream content, Reading read, CancellationToken cancellationToken)
    {
        var settings = new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            IgnoreProcessingInstructions = true,
        };

        string? location = null;
        DateTimeOffset? lastModified = null;
        using var reader = XmlReader.Create(content, settings);
        // ReadElementContentAsStringAsync leaves the reader on the next node, so only other nodes advance it.
        var more = await reader.ReadAsync();
        while (more)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "loc")
            {
                location = (await reader.ReadElementContentAsStringAsync()).Trim();
                more = !reader.EOF;
                continue;
            }

            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "lastmod")
            {
                lastModified = ParseLastModified((await reader.ReadElementContentAsStringAsync()).Trim());
                more = !reader.EOF;
                continue;
            }

            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.LocalName)
                {
                    case "sitemapindex":
                        read.IsIndex = true;
                        break;
                    case "url" or "sitemap":
                        location = null;
                        lastModified = null;
                        break;
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName is "url" or "sitemap" && !string.IsNullOrEmpty(location))
            {
                if (read.Entries.Count == MaxEntries)
                {
                    read.Truncated = true;
                    return;
                }

                read.Entries.Add(new SitemapEntry(location, lastModified));
                location = null;
            }

            more = await reader.ReadAsync();
        }
    }

    /// <summary>A plain-text sitemap's absolute http(s) URLs into <paramref name="read"/>, one per line.</summary>
    private static async Task ReadTextAsync(Stream content, Reading read, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            var url = line.Trim();
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (read.Entries.Count == MaxEntries)
            {
                read.Truncated = true;
                return;
            }

            read.Entries.Add(new SitemapEntry(url, null));
        }
    }

    /// <summary>W3C datetime as sitemaps use it: <c>YYYY</c>, <c>YYYY-MM</c>, <c>YYYY-MM-DD</c>, or a full date-time; UTC when no zone.</summary>
    public static DateTimeOffset? ParseLastModified(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string[] partial = ["yyyy", "yyyy-MM", "yyyy-MM-dd"];
        if (DateTimeOffset.TryParseExact(value, partial, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
        {
            return date;
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var full) ? full : null;
    }

    private sealed class LimitExceededException : IOException;

    private sealed class Reading
    {
        public List<SitemapEntry> Entries { get; } = [];

        public bool IsIndex { get; set; }

        public bool Truncated { get; set; }

        public SitemapDocument Document(string? error) => new(IsIndex, Entries, Truncated, error);
    }

    /// <summary>Reads the bytes already peeked, then the rest of the stream; the caller keeps owning the stream.</summary>
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
            if (_prefix.Length > 0)
            {
                var n = Math.Min(buffer.Length, _prefix.Length);
                _prefix.Span[..n].CopyTo(buffer);
                _prefix = _prefix[n..];
                return n;
            }

            return rest.Read(buffer);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_prefix.Length > 0)
            {
                return Read(buffer.Span);
            }

            return await rest.ReadAsync(buffer, cancellationToken);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Throws once more than <c>limit</c> bytes were read: the uncompressed cap.</summary>
    private sealed class LimitedStream(Stream inner, long limit) : Stream
    {
        private long _read;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken));

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
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        private int Count(int read)
        {
            _read += read;
            return _read > limit ? throw new LimitExceededException() : read;
        }
    }
}
