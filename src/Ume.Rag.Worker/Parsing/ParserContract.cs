using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ume.Rag.Worker.Parsing;

// The parser contract parser/v1 (plans/parser/design.md §7; schemas in contracts/parser/v1/). Property names map to the
// parser's snake_case JSON through ParserJson. Request options use enums (the worker writes them); result values that
// /v1 may extend with new enum values (§7.7) stay strings, so a newer parser does not break an older worker.

public enum ParserPipeline
{
    Docling,
    TextLayer,
}

public enum ParserOcrMode
{
    Auto,
    Off,
    Force,
}

public enum ParserTableMode
{
    Accurate,
    Fast,
    Off,
}

/// <summary>1-based, inclusive page numbers of the original file.</summary>
public sealed record ParserPageRange(int From, int To);

public sealed record ParserOcrOptions
{
    public ParserOcrMode Mode { get; init; } = ParserOcrMode.Auto;

    /// <summary>Tesseract <c>swe</c> only (§9).</summary>
    public IReadOnlyList<string> Languages { get; init; } = ["swe"];
}

public sealed record ParserTableOptions
{
    public ParserTableMode Mode { get; init; } = ParserTableMode.Accurate;
}

public sealed record ParserOutputOptions
{
    public bool DoclingJson { get; init; } = true;
    public bool Markdown { get; init; }
}

/// <summary>The <c>options</c> part of <c>POST /v1/convert</c> (convert-request.schema.json).</summary>
public sealed record ParserConvertOptions(string RequestId, string FileExt)
{
    public string? MediaType { get; init; }
    public ParserPageRange? Pages { get; init; }
    public int DeadlineMs { get; init; } = 110_000;
    public ParserPipeline Pipeline { get; init; } = ParserPipeline.Docling;
    public ParserOcrOptions Ocr { get; init; } = new();
    public ParserTableOptions Tables { get; init; } = new();
    public double ImageScale { get; init; } = 2.0;
    public ParserOutputOptions Outputs { get; init; } = new();
}

public sealed record ParserPageSpan(int From, int To);

public sealed record ParserInput(
    string Format,
    string MediaTypeSniffed,
    string Sha256,
    long Bytes,
    int? PageCount,
    ParserPageSpan? PagesRequested,
    ParserPageSpan? PagesCompleted,
    string Pipeline);

public sealed record ParserOcrConfidence(double Mean, double P10);

public sealed record ParserPageSignals(
    int Chars,
    int FurnitureChars,
    double GarbledScore,
    int? TextLayerChars,
    double? ImageCoverage,
    string OcrApplied,
    ParserOcrConfidence? OcrConfidence,
    int TableCount,
    double TableAreaRatio,
    double PictureAreaRatio,
    bool Empty,
    int? Ms);

public sealed record ParserPage(int? Page, string? Label, double? Width, double? Height, ParserPageSignals Signals);

public sealed record ParserBlock(
    string Id,
    string Kind,
    int? Level,
    string? LevelSource,
    int? Page,
    int? PageEnd,
    IReadOnlyList<double>? Bbox,
    string Layer,
    string Md,
    string? Ref,
    string? Table);

public sealed record ParserTableCell(int R, int C, int Rs, int Cs, bool Header, string Text);

public sealed record ParserTable(
    string Id,
    int Rows,
    int Cols,
    int HeaderRows,
    string? Caption,
    IReadOnlyList<ParserTableCell> Cells);

public sealed record ParserPicture(
    string Id,
    string Block,
    int? Page,
    IReadOnlyList<double>? Bbox,
    string? Caption,
    int OcrChars);

public sealed record ParserWarning(string Code, int? Page);

public sealed record ParserTimings(int TotalMs, int WaitMs, int DoclingMs, int TextLayerMs, int SignalsMs);

public sealed record ParserBuild(string Parser, string Docling, string BuildHash);

/// <summary><c>result.json</c>, the first part of a <c>/v1/convert</c> response (convert-result.schema.json).</summary>
public sealed record ParserConvertResult(
    string Contract,
    string RequestId,
    string Status,
    ParserInput Input,
    IReadOnlyList<ParserPage> Pages,
    IReadOnlyList<ParserBlock> Blocks,
    IReadOnlyList<ParserTable> Tables,
    IReadOnlyList<ParserPicture> Pictures,
    IReadOnlyList<ParserWarning> Warnings,
    ParserTimings Timings,
    ParserBuild Build,
    string? Markdown);

public sealed record ParserErrorBody(
    string Code,
    string Detail,
    string Retry,
    string? Limit,
    string? Reason,
    ParserPageSpan? PagesCompleted);

/// <summary>Every non-200 answer of <c>/v1/convert</c> and <c>/v1/inspect</c> (error.schema.json).</summary>
public sealed record ParserErrorResponse(ParserErrorBody Error, string? RequestId, ParserBuild Build);

public sealed record ParserVersion(string Version, string Revision);

public sealed record ParserPdfium(int Build, string Origin, IReadOnlyList<string> Flags);

public sealed record ParserTesseract(string Version, IReadOnlyList<string> Languages);

public sealed record ParserModel(
    string Name,
    string Repository,
    string Revision,
    string Licence,
    IReadOnlyDictionary<string, string> Files);

public sealed record ParserLimits(
    long MaxBodyBytes,
    int MaxPagesPerRequest,
    int MaxPagesPerDocument,
    int MaxDeadlineMs,
    int InspectDeadlineMs);

public sealed record ParserFormats(IReadOnlyList<string> Supported, IReadOnlyList<string> Deferred);

public sealed record ParserCapacity(int ConvertSlots, int SlotsReady, int InspectSlots, bool NerReady);

/// <summary><c>GET /v1/info</c> (info.schema.json). The worker records <see cref="BuildHash"/> on every parse.</summary>
public sealed record ParserInfo(
    ParserVersion Parser,
    string Python,
    IReadOnlyDictionary<string, string> Packages,
    ParserPdfium Pdfium,
    ParserTesseract? Tesseract,
    IReadOnlyList<ParserModel> Models,
    ParserLimits Limits,
    ParserFormats Formats,
    string BuildHash,
    ParserCapacity Capacity);

/// <summary>Serializer options for the parser contract.</summary>
public static class ParserJson
{
    /// <summary>
    /// snake_case names, enums as snake_case strings, required members and nullability enforced. <paramref name="strict"/>
    /// also refuses unknown members (contract tests: drift in either direction fails); production tolerates them, because
    /// /v1 grows additively (§7.7).
    /// </summary>
    public static JsonSerializerOptions Create(bool strict = false) => new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) },
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = strict ? JsonUnmappedMemberHandling.Disallow : JsonUnmappedMemberHandling.Skip,
    };
}
