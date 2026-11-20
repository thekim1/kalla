using System.IO.Compression;
using System.Text;
using System.Xml;
using Kalla.Worker.Connectors.Http;

namespace Kalla.Worker.Connectors.Uploads;

/// <summary>What a file is by its bytes (connectors design §9.2).</summary>
public enum FileClass
{
    /// <summary>An allowed P1 type.</summary>
    Allowed,

    /// <summary>E-mail (<c>.eml</c>, Outlook <c>.msg</c>): <c>upload.format_deferred</c> (Z1).</summary>
    Deferred,

    /// <summary><c>.docm</c>, <c>.xlsm</c>, <c>.pptm</c> and other macro-enabled OOXML: <c>upload.macro_enabled</c>.</summary>
    MacroEnabled,

    /// <summary>zip, 7z, rar, tar, gzip that are not OOXML or ODF: <c>upload.archive</c>.</summary>
    Archive,

    /// <summary>Executables and anything unrecognised: <c>upload.type_unknown</c>.</summary>
    Unknown,
}

/// <param name="MediaType">The sniffed media type, or null when unknown.</param>
/// <param name="Class">Whether P1 accepts it.</param>
public sealed record SniffedType(string? MediaType, FileClass Class);

/// <summary>
/// Type sniffing by magic bytes (connectors design §9.2): PDF, PNG, JPEG, TIFF; ZIP containers by their
/// <c>[Content_Types].xml</c> main part (OOXML, incl. macro-enabled types) or <c>mimetype</c> entry (ODF), else an
/// archive; OLE compound files by the root entry's CLSID (Word, Excel, PowerPoint, Outlook <c>.msg</c>: deferred);
/// RTF and e-mail headers deferred; other archives and executables refused; text by a UTF-8 heuristic, HTML by its
/// opening tag, Markdown and CSV by extension. Reads at most the container's index and two small entries.
/// </summary>
public static class FileTypeSniffer
{
    public const string Docx = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    public const string Pptx = "application/vnd.openxmlformats-officedocument.presentationml.presentation";
    public const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private const int HeadLength = 8192;
    private const long MaxEntryBytes = 1024 * 1024;

    private static readonly Dictionary<string, string> OoxmlMainTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"] = Docx,
        ["application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml"] = Pptx,
        ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"] = Xlsx,
    };

    private static readonly HashSet<string> OdfTypes = new(StringComparer.Ordinal)
    {
        "application/vnd.oasis.opendocument.text", "application/vnd.oasis.opendocument.spreadsheet", "application/vnd.oasis.opendocument.presentation",
    };

    private static readonly Dictionary<Guid, string> OleRoots = new()
    {
        [new Guid("00020906-0000-0000-C000-000000000046")] = "application/msword",
        [new Guid("00020820-0000-0000-C000-000000000046")] = "application/vnd.ms-excel",
        [new Guid("64818D10-4F9B-11CF-86EA-00AA00B929E8")] = "application/vnd.ms-powerpoint",
        [new Guid("00020D0B-0000-0000-C000-000000000046")] = "application/vnd.ms-outlook",
    };

    /// <summary>Legacy types by extension, for OLE files whose root CLSID is not set.</summary>
    private static readonly Dictionary<string, string> LegacyExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".doc"] = "application/msword", [".xls"] = "application/vnd.ms-excel", [".ppt"] = "application/vnd.ms-powerpoint",
        [".msg"] = "application/vnd.ms-outlook",
    };

    private static readonly string[] MailHeaders = ["from:", "received:", "return-path:", "mime-version:", "message-id:", "delivered-to:"];

    /// <param name="file">A seekable stream of the whole file (the local temp copy).</param>
    /// <param name="fileName">The uploaded name: decides between plain text, Markdown and CSV.</param>
    public static SniffedType Sniff(Stream file, string fileName)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(fileName);
        var head = new byte[HeadLength];
        file.Position = 0;
        var length = file.ReadAtLeast(head, HeadLength, throwOnEndOfStream: false);
        var bytes = head.AsSpan(0, length);
        file.Position = 0;

        if (bytes.StartsWith("%PDF-"u8))
        {
            return new("application/pdf", FileClass.Allowed);
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return new("image/png", FileClass.Allowed);
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
        {
            return new("image/jpeg", FileClass.Allowed);
        }

        if (bytes.StartsWith("II*\0"u8) || bytes.StartsWith("MM\0*"u8))
        {
            return new("image/tiff", FileClass.Allowed);
        }

        if (bytes.StartsWith("PK\x03\x04"u8))
        {
            return Zip(file);
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]))
        {
            return Ole(file, bytes, Path.GetExtension(fileName));
        }

        if (bytes.StartsWith("{\\rtf"u8))
        {
            return new("application/rtf", FileClass.Allowed);
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0x1F, 0x8B]) || bytes.StartsWith((ReadOnlySpan<byte>)[0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C]) || bytes.StartsWith("Rar!\x1A\x07"u8)
            || (length > 262 && bytes[257..262].SequenceEqual("ustar"u8)))
        {
            return new(null, FileClass.Archive);
        }

        if (bytes.StartsWith("MZ"u8) || bytes.StartsWith((ReadOnlySpan<byte>)[0x7F, 0x45, 0x4C, 0x46]) || bytes.StartsWith((ReadOnlySpan<byte>)[0xCF, 0xFA, 0xED, 0xFE]))
        {
            return new(null, FileClass.Unknown);
        }

        return Text(bytes, Path.GetExtension(fileName));
    }

    private static SniffedType Zip(Stream file)
    {
        try
        {
            using var zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
            if (zip.GetEntry("[Content_Types].xml") is { } types)
            {
                foreach (var contentType in OverrideTypes(types))
                {
                    if (contentType.Contains("macroEnabled", StringComparison.OrdinalIgnoreCase))
                    {
                        return new(contentType, FileClass.MacroEnabled);
                    }

                    if (OoxmlMainTypes.TryGetValue(contentType, out var main))
                    {
                        return new(main, FileClass.Allowed);
                    }
                }

                return new(null, FileClass.Unknown);
            }

            if (zip.GetEntry("mimetype") is { Length: < 200 } mimetype)
            {
                using var reader = new StreamReader(mimetype.Open(), Encoding.ASCII);
                var odf = reader.ReadToEnd().Trim();
                return OdfTypes.Contains(odf) ? new(odf, FileClass.Allowed) : new(odf, FileClass.Unknown);
            }

            return new("application/zip", FileClass.Archive);
        }
        catch (InvalidDataException)
        {
            return new(null, FileClass.Unknown);
        }
        finally
        {
            file.Position = 0;
        }
    }

    /// <summary>The <c>ContentType</c> of every <c>Override</c> in <c>[Content_Types].xml</c> (DTDs refused, at most 1 MB).</summary>
    private static List<string> OverrideTypes(ZipArchiveEntry entry)
    {
        var found = new List<string>();
        if (entry.Length > MaxEntryBytes)
        {
            return found;
        }

        try
        {
            using var stream = entry.Open();
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            while (reader.Read())
            {
                if (reader is { NodeType: XmlNodeType.Element, LocalName: "Override" } && reader.GetAttribute("ContentType") is { } type)
                {
                    found.Add(type);
                }
            }
        }
        catch (XmlException)
        {
            // An unreadable index: no known main part, so the file is refused as unknown.
        }

        return found;
    }

    /// <summary>The root directory entry's CLSID (compound file header: sector shift at 30, first directory sector at 48).</summary>
    private static SniffedType Ole(Stream file, ReadOnlySpan<byte> head, string extension)
    {
        var shift = BitConverter.ToUInt16(head[30..32]);
        var directory = BitConverter.ToUInt32(head[48..52]);
        if (shift is 9 or 12 && directory < 0xFFFFFFFA)
        {
            var offset = ((long)directory + 1) << shift;
            var clsid = new byte[16];
            if (offset + 96 <= file.Length)
            {
                file.Position = offset + 80;
                file.ReadExactly(clsid);
                file.Position = 0;
                if (OleRoots.TryGetValue(new Guid(clsid), out var type))
                {
                    return new(type, LegacyClass(type));
                }
            }
        }

        file.Position = 0;
        return LegacyExtensions.TryGetValue(extension, out var byName) ? new(byName, LegacyClass(byName)) : new(null, FileClass.Unknown);
    }

    /// <summary>Legacy Word, Excel and PowerPoint go to the parser (LibreOffice); Outlook <c>.msg</c> stays deferred.</summary>
    private static FileClass LegacyClass(string mediaType) => mediaType == "application/vnd.ms-outlook" ? FileClass.Deferred : FileClass.Allowed;

    private static SniffedType Text(ReadOnlySpan<byte> head, string extension)
    {
        if (head.Contains((byte)0))
        {
            return new(null, FileClass.Unknown);
        }

        // A multi-byte character cut at the end of the sample is not a decoding error.
        var sample = head.Length == HeadLength ? head[..^4] : head;
        string text;
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(sample);
        }
        catch (DecoderFallbackException)
        {
            return new(null, FileClass.Unknown);
        }

        var start = text.TrimStart('﻿', ' ', '\t', '\r', '\n');
        if (start.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase) || start.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
        {
            return new("text/html", FileClass.Allowed);
        }

        if (MailHeaders.Any(h => start.StartsWith(h, StringComparison.OrdinalIgnoreCase)) && (start.Contains("\n\n", StringComparison.Ordinal) || start.Contains("\r\n\r\n", StringComparison.Ordinal)))
        {
            return new("message/rfc822", FileClass.Deferred);
        }

        var type = extension.ToLowerInvariant() switch
        {
            ".md" or ".markdown" => "text/markdown",
            ".csv" => "text/csv",
            _ => "text/plain",
        };
        return MediaTypes.UploadFormats.Contains(type) ? new(type, FileClass.Allowed) : new(type, FileClass.Unknown);
    }
}

/// <param name="Allowed">The file may be parsed as <paramref name="MediaType"/>.</param>
/// <param name="MediaType">The sniffed type (it wins over the declared one).</param>
/// <param name="Reason">Why not: <c>upload.format_deferred</c>, <c>upload.macro_enabled</c>, <c>upload.archive</c>, <c>upload.type_unknown</c>, <c>upload.type_mismatch</c>.</param>
/// <param name="DeclaredDiffers">The declared type or the extension names another type (a fact on the run item).</param>
public sealed record TypeVerdict(bool Allowed, string? MediaType, string? Reason, bool DeclaredDiffers);

/// <summary>
/// The type check of an upload (connectors design §9.2): the sniffed type wins; a declared allowed type (by the browser's
/// type or the extension) whose sniffed type is not allowed is <c>upload.type_mismatch</c>; otherwise a refused file gets
/// its class's reason.
/// </summary>
public static class UploadTypeCheck
{
    public const string FormatDeferred = "upload.format_deferred";
    public const string MacroEnabled = "upload.macro_enabled";
    public const string Archive = "upload.archive";
    public const string TypeUnknown = "upload.type_unknown";
    public const string TypeMismatch = "upload.type_mismatch";

    private static readonly Dictionary<string, string> AllowedByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf", [".docx"] = FileTypeSniffer.Docx, [".pptx"] = FileTypeSniffer.Pptx, [".xlsx"] = FileTypeSniffer.Xlsx,
        [".odt"] = "application/vnd.oasis.opendocument.text", [".ods"] = "application/vnd.oasis.opendocument.spreadsheet",
        [".odp"] = "application/vnd.oasis.opendocument.presentation", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg",
        [".png"] = "image/png", [".tif"] = "image/tiff", [".tiff"] = "image/tiff", [".txt"] = "text/plain",
        [".md"] = "text/markdown", [".csv"] = "text/csv", [".html"] = "text/html", [".htm"] = "text/html",
        [".doc"] = "application/msword", [".xls"] = "application/vnd.ms-excel", [".ppt"] = "application/vnd.ms-powerpoint",
        [".rtf"] = "application/rtf",
    };

    public static TypeVerdict Check(SniffedType sniffed, string fileName, string? declaredMediaType)
    {
        ArgumentNullException.ThrowIfNull(sniffed);
        ArgumentNullException.ThrowIfNull(fileName);
        var byExtension = AllowedByExtension.GetValueOrDefault(Path.GetExtension(fileName));
        var declared = declaredMediaType?.Split(';')[0].Trim().ToLowerInvariant();
        var differs = (declared is not null && declared != sniffed.MediaType) || (byExtension is not null && byExtension != sniffed.MediaType);
        if (sniffed.Class == FileClass.Allowed)
        {
            return new TypeVerdict(true, sniffed.MediaType, null, differs);
        }

        var declaredAllowed = byExtension is not null || (declared is not null && MediaTypes.UploadFormats.Contains(declared));
        var reason = declaredAllowed ? TypeMismatch : sniffed.Class switch
        {
            FileClass.Deferred => FormatDeferred,
            FileClass.MacroEnabled => MacroEnabled,
            FileClass.Archive => Archive,
            _ => TypeUnknown,
        };
        return new TypeVerdict(false, sniffed.MediaType, reason, differs);
    }
}
