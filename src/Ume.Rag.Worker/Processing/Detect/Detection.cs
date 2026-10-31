using System.IO.Compression;
using System.Text;

namespace Ume.Rag.Worker.Processing.Detect;

/// <summary>
/// The <c>RawContent</c> stage value (engine design §2.2) as processing reads it: the bytes, what the source declared,
/// and the document facts later stages need (URL or upload name, dates from the fetch).
/// </summary>
public sealed record RawContent(ReadOnlyMemory<byte> Bytes, string? DeclaredMediaType)
{
    public Uri? Url { get; init; }

    /// <summary>Upload file name (path rules match it, processing design §7); never the title.</summary>
    public string? DisplayName { get; init; }

    /// <summary>HTTP <c>Last-Modified</c> (the last date candidate, processing design §7).</summary>
    public DateTimeOffset? HttpLastModified { get; init; }

    /// <summary>The sitemap <c>lastmod</c>, set only when the site profile marks it reliable for the host.</summary>
    public DateTimeOffset? ReliableLastmod { get; init; }
}

/// <summary>Where <c>parse</c> sends a document (processing design §3.2).</summary>
public enum ParseRoute
{
    Html,
    Text,
    Csv,
    Parser,

    /// <summary>Legacy Office, RTF, e-mail: outcome <c>unsupported</c>, reason <c>format_deferred</c> (P2).</summary>
    Deferred,

    /// <summary>Archives, macro-enabled Office, executables, unknown: outcome <c>unsupported</c>.</summary>
    Unsupported,
}

/// <summary>Inspect page signals (<c>contracts/parser/v1/inspect-result.schema.json</c> <c>InspectPage</c>).</summary>
public sealed record InspectPageSignals(int Page)
{
    public int? TextChars { get; init; }

    public int? BadChars { get; init; }

    public double? GarbledScore { get; init; }

    public double? ImageCoverage { get; init; }

    public int? PathObjects { get; init; }
}

/// <summary>The parser's <c>/v1/inspect</c> answer as the parse plan reads it (inspect-result.schema.json).</summary>
public sealed record InspectFacts(string Format, int? PageCount, bool Rangeable, IReadOnlyList<InspectPageSignals> Pages)
{
    /// <summary><c>none</c>, <c>owner_only</c> or <c>user_password</c>.</summary>
    public string? Encrypted { get; init; }

    /// <summary>The refusal code (<c>encrypted</c>, <c>too_many_pages</c>, <c>unsupported_format</c>, <c>format_deferred</c>, …).</summary>
    public string? Refusal { get; init; }
}

/// <summary>
/// The parser's <c>/v1/inspect</c> (parser design §6.2, own lane, 10 s). Implemented over <c>ParserClient</c> once its
/// inspect call is merged (P1a item 5); a <c>busy</c> answer is thrown as <see cref="Parsing.ParserException"/> with 503
/// and requeues the job without an attempt (engine design §7.3).
/// </summary>
public interface IDocumentInspector
{
    Task<InspectFacts> InspectAsync(ReadOnlyMemory<byte> file, string mediaType, string fileExtension, CancellationToken cancellationToken);
}

/// <summary>What <c>detect</c> adds to <c>RawContent</c> (processing design §2); it never changes the bytes.</summary>
public sealed record DetectResult(string MediaType, ParseRoute Route)
{
    public string? MediaTypeDeclared { get; init; }

    public bool MediaTypeMismatch { get; init; }

    /// <summary><c>sv</c>, <c>en</c>, <c>und</c>; null for parser-bound types (set by <c>clean</c> after parsing).</summary>
    public string? Language { get; init; }

    public InspectFacts? Inspect { get; init; }

    /// <summary>Share of inspected pages that are not clean (§3.1 rule 5) ≥ 0.5.</summary>
    public bool Scanned { get; init; }

    public int? PageCount { get; init; }

    public long Bytes { get; init; }

    /// <summary>The parser's <c>file_ext</c> for the media type.</summary>
    public string? FileExtension { get; init; }
}

/// <summary>Media types and their routes (processing design §3.2; parser design §3).</summary>
public static class ProcessingMediaTypes
{
    public const string Html = "text/html";
    public const string Xhtml = "application/xhtml+xml";
    public const string PlainText = "text/plain";
    public const string Markdown = "text/markdown";
    public const string Csv = "text/csv";
    public const string Pdf = "application/pdf";
    public const string Docx = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    public const string Pptx = "application/vnd.openxmlformats-officedocument.presentationml.presentation";
    public const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    public const string Odt = "application/vnd.oasis.opendocument.text";
    public const string Ods = "application/vnd.oasis.opendocument.spreadsheet";
    public const string Odp = "application/vnd.oasis.opendocument.presentation";
    public const string Png = "image/png";
    public const string Jpeg = "image/jpeg";
    public const string Tiff = "image/tiff";
    public const string Ole = "application/x-ole-storage";
    public const string Rtf = "application/rtf";
    public const string Eml = "message/rfc822";
    public const string Zip = "application/zip";
    public const string OctetStream = "application/octet-stream";

    private static readonly Dictionary<string, string> Extensions = new(StringComparer.Ordinal)
    {
        [Pdf] = ".pdf", [Docx] = ".docx", [Pptx] = ".pptx", [Xlsx] = ".xlsx", [Odt] = ".odt", [Ods] = ".ods",
        [Odp] = ".odp", [Png] = ".png", [Jpeg] = ".jpg", [Tiff] = ".tif",
    };

    private static readonly HashSet<string> Deferred = new(StringComparer.Ordinal)
    {
        "application/msword", "application/vnd.ms-excel", "application/vnd.ms-powerpoint", Ole, Rtf, "text/rtf", Eml,
        "application/vnd.ms-outlook",
    };

    /// <summary>The parser-bound types: those <see cref="Route"/> sends to <see cref="ParseRoute.Parser"/>.</summary>
    public static IReadOnlyCollection<string> ParserTypes => Extensions.Keys;

    /// <summary>The parser's <c>file_ext</c> for a parser-bound type, else null.</summary>
    public static string? FileExtension(string mediaType) => Extensions.GetValueOrDefault(mediaType);

    /// <summary>Types the parser inspects (PDF, TIFF, Office, ODF; processing design §2).</summary>
    public static bool IsInspected(string mediaType) => mediaType is Pdf or Tiff or Docx or Pptx or Xlsx or Odt or Ods or Odp;

    public static ParseRoute Route(string mediaType) => mediaType switch
    {
        Html or Xhtml => ParseRoute.Html,
        PlainText or Markdown => ParseRoute.Text,
        Csv => ParseRoute.Csv,
        _ when Extensions.ContainsKey(mediaType) => ParseRoute.Parser,
        _ when Deferred.Contains(mediaType) => ParseRoute.Deferred,
        _ => ParseRoute.Unsupported,
    };

    /// <summary>The media type without parameters, lower case (<c>text/html; charset=utf-8</c> → <c>text/html</c>).</summary>
    public static string? Essence(string? declared)
    {
        if (string.IsNullOrWhiteSpace(declared))
        {
            return null;
        }

        var semicolon = declared.IndexOf(';', StringComparison.Ordinal);
        return (semicolon < 0 ? declared : declared[..semicolon]).Trim().ToLowerInvariant();
    }
}

/// <summary>
/// Magic-byte sniffing (processing design §2): PDF, ZIP with <c>[Content_Types].xml</c> (OOXML by its part folders), ODF
/// by its <c>mimetype</c> entry, PNG, JPEG, TIFF, OLE (legacy Office), RTF; then the declared type for text. The parser
/// re-sniffs anyway (parser design §3).
/// </summary>
public static class MediaTypeSniffer
{
    private const int MaxZipEntries = 10_000;

    public static string Sniff(ReadOnlySpan<byte> bytes, string? declared)
    {
        var essence = ProcessingMediaTypes.Essence(declared);
        if (bytes.StartsWith("%PDF-"u8))
        {
            return ProcessingMediaTypes.Pdf;
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return ProcessingMediaTypes.Png;
        }

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return ProcessingMediaTypes.Jpeg;
        }

        if (bytes.StartsWith("II*\0"u8) || bytes.StartsWith("MM\0*"u8))
        {
            return ProcessingMediaTypes.Tiff;
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]))
        {
            // Legacy .doc/.xls/.ppt/.msg: keep the declared legacy type when there is one (format_deferred either way).
            return essence is not null && ProcessingMediaTypes.Route(essence) == ParseRoute.Deferred ? essence : ProcessingMediaTypes.Ole;
        }

        if (bytes.StartsWith(@"{\rtf"u8))
        {
            return ProcessingMediaTypes.Rtf;
        }

        if (bytes.StartsWith("PK\x03\x04"u8))
        {
            return SniffZip(bytes);
        }

        // Text: no magic bytes. Trust a declared text type; sniff HTML when nothing useful was declared.
        if (essence is ProcessingMediaTypes.Html or ProcessingMediaTypes.Xhtml or ProcessingMediaTypes.PlainText
            or ProcessingMediaTypes.Markdown or ProcessingMediaTypes.Csv)
        {
            return essence;
        }

        if (essence is "text/x-markdown")
        {
            return ProcessingMediaTypes.Markdown;
        }

        if (LooksLikeHtml(bytes))
        {
            return ProcessingMediaTypes.Html;
        }

        return essence is null or ProcessingMediaTypes.OctetStream ? ProcessingMediaTypes.OctetStream : essence;
    }

    private static string SniffZip(ReadOnlySpan<byte> bytes)
    {
        try
        {
            using var archive = new ZipArchive(new MemoryStream(bytes.ToArray(), writable: false), ZipArchiveMode.Read);
            var names = archive.Entries.Take(MaxZipEntries).Select(e => e.FullName).ToList();
            if (names.Contains("[Content_Types].xml", StringComparer.Ordinal))
            {
                if (names.Any(n => n.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase)))
                {
                    return "application/vnd.ms-office.macroenabled"; // unsupported (macro_enabled)
                }

                if (names.Any(n => n.StartsWith("word/", StringComparison.Ordinal)))
                {
                    return ProcessingMediaTypes.Docx;
                }

                if (names.Any(n => n.StartsWith("xl/", StringComparison.Ordinal)))
                {
                    return ProcessingMediaTypes.Xlsx;
                }

                if (names.Any(n => n.StartsWith("ppt/", StringComparison.Ordinal)))
                {
                    return ProcessingMediaTypes.Pptx;
                }
            }

            if (archive.Entries.FirstOrDefault() is { FullName: "mimetype", Length: < 200 } first)
            {
                using var reader = new StreamReader(first.Open(), Encoding.ASCII);
                var type = reader.ReadToEnd().Trim();
                if (type is ProcessingMediaTypes.Odt or ProcessingMediaTypes.Ods or ProcessingMediaTypes.Odp)
                {
                    return type;
                }
            }
        }
        catch (InvalidDataException)
        {
            // A broken archive is not one of ours.
        }

        return ProcessingMediaTypes.Zip;
    }

    private static bool LooksLikeHtml(ReadOnlySpan<byte> bytes)
    {
        var head = Encoding.UTF8.GetString(bytes[..Math.Min(bytes.Length, 1024)]).TrimStart('﻿', ' ', '\t', '\r', '\n');
        return head.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith("<html", StringComparison.OrdinalIgnoreCase);
    }
}
