namespace Ume.Rag.Worker.Connectors.Http;

/// <summary>
/// Media types the fetch path decides on (connectors design §4, §9.2, §11; parser design §3). The accepted set of a source
/// type is its pipeline's formats; <see cref="DocumentFormats"/> is the P1 set of the <c>url-list</c> and <c>website</c>
/// templates until the pipeline's formats are read from the definition (engine §13.1).
/// </summary>
public static class MediaTypes
{
    public static IReadOnlySet<string> Html { get; } = new HashSet<string>(StringComparer.Ordinal) { "text/html", "application/xhtml+xml" };

    /// <summary>Parsed in-process from text: not malware-scanned (§11).</summary>
    public static IReadOnlySet<string> Text { get; } = new HashSet<string>(StringComparer.Ordinal) { "text/plain", "text/markdown", "text/csv" };

    /// <summary>Legacy Office, RTF and e-mail: <c>format_deferred</c> in P1 (Z1, §9.2).</summary>
    public static IReadOnlySet<string> Deferred { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "application/msword", "application/vnd.ms-excel", "application/vnd.ms-powerpoint", "application/rtf", "text/rtf",
        "message/rfc822", "application/vnd.ms-outlook",
    };

    /// <summary>HTML, text and the P1 document formats (PDF, OOXML, ODF); images are excluded from fetches by default (§5.2).</summary>
    public static IReadOnlySet<string> DocumentFormats { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "text/html", "application/xhtml+xml", "text/plain", "text/markdown", "text/csv", "application/pdf",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "application/vnd.oasis.opendocument.text", "application/vnd.oasis.opendocument.spreadsheet",
        "application/vnd.oasis.opendocument.presentation",
    };

    /// <summary>
    /// The upload types (§9.2): the document formats, JPG, PNG and TIFF (OCR), and legacy Word, Excel, PowerPoint and RTF
    /// (converted by the parser's LibreOffice; fetched documents of these types stay <see cref="Deferred"/>).
    /// </summary>
    public static IReadOnlySet<string> UploadFormats { get; } = new HashSet<string>(
        DocumentFormats.Concat(["image/jpeg", "image/png", "image/tiff", "application/msword", "application/vnd.ms-excel", "application/vnd.ms-powerpoint", "application/rtf"]),
        StringComparer.Ordinal);

    public static bool IsHtml(string mediaType) => Html.Contains(mediaType);

    /// <summary>Files are scanned before they are stored or parsed; HTML and text are not (§11).</summary>
    public static bool NeedsScan(string mediaType) => !Html.Contains(mediaType) && !Text.Contains(mediaType);

    /// <summary>HTML and XHTML ≤ 10 MB, every other type ≤ 50 MB (the upload limit), after decompression (§4).</summary>
    public static long BodyLimit(string mediaType) => IsHtml(mediaType) ? 10L * 1024 * 1024 : 50L * 1024 * 1024;
}
