using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kalla.Pipelines.Json;

namespace Kalla.Worker.Processing;

// The stage values of processing (engine design §2.2; processing design §2–§9). They are internal C# values checked by the
// compiler; the engine (item 9) serializes them into the output cache with ProcessingJson, and their OutputHash is the next
// stage's input hash (engine design §3.3, §8).

/// <summary>Parser block kinds (parser design §7.3); the worker-side parsers emit the same kinds.</summary>
public static class BlockKinds
{
    public const string Title = "title";
    public const string Heading = "heading";
    public const string Paragraph = "paragraph";
    public const string ListItem = "list_item";
    public const string Table = "table";
    public const string Picture = "picture";
    public const string Caption = "caption";
    public const string Code = "code";
    public const string Formula = "formula";
    public const string Footnote = "footnote";
    public const string PageHeader = "page_header";
    public const string PageFooter = "page_footer";
}

/// <summary>Where a heading level comes from (parser design §7.3): <c>layout</c> and <c>font_size</c> are re-levelled (processing design §5.2).</summary>
public static class LevelSources
{
    public const string Style = "style";
    public const string Layout = "layout";
    public const string FontSize = "font_size";
    public const string Html = "html";
    public const string Markdown = "markdown";

    public static bool IsTrusted(string? source) => source is Style or Html or Markdown;
}

/// <summary>Block layers (parser design §7.3).</summary>
public static class Layers
{
    public const string Body = "body";
    public const string Furniture = "furniture";
}

/// <summary>
/// One block of a <see cref="ParsedDocument"/>: the parser's block shape (<c>md</c> as <see cref="Text"/>) with the
/// list nesting depth the HTML and Markdown parsers know. <see cref="Table"/> indexes <see cref="ParsedDocument.Tables"/>.
/// </summary>
public sealed record DocBlock(string Kind, string Text)
{
    public int? Level { get; init; }

    public string? LevelSource { get; init; }

    public int? Page { get; init; }

    public int? PageEnd { get; init; }

    public string Layer { get; init; } = Layers.Body;

    /// <summary>0 for a top-level list item.</summary>
    public int ListDepth { get; init; }

    public int? Table { get; init; }

    /// <summary>Text-layer pages: the block is in the top or bottom 8 % of its page (processing design §4.2 furniture rule).</summary>
    public bool PageEdge { get; init; }
}

/// <summary>A table: <see cref="Cells"/> is the source of truth (parser design §7.3).</summary>
public sealed record DocTable(int Rows, int Cols, int HeaderRows, IReadOnlyList<DocCell> Cells)
{
    public string? Caption { get; init; }

    /// <summary>The sheet name of a spreadsheet table; joins the heading path (processing design §6.3).</summary>
    public string? Sheet { get; init; }
}

/// <summary>A cell with its spans (0-based row and column).</summary>
public sealed record DocCell(int R, int C, int Rs, int Cs, bool Header, string Text);

/// <summary>A page and the post-conversion signals the quality facts use (parser design §6.4).</summary>
public sealed record DocPage(int Page)
{
    public int Chars { get; init; }

    public double GarbledScore { get; init; }

    public double? OcrP10 { get; init; }

    public bool Blank { get; init; }

    public string? Strategy { get; init; }
}

/// <summary>
/// The <c>ParsedDocument</c> stage value (engine design §2.2): blocks, tables and pages in the parser's shape plus the
/// extraction facts (breadcrumb, description, keywords, visible date, site-profile version). It holds content only, no
/// volatile facts (fetch time, timings, parser build), so its <see cref="ProcessingJson.OutputHash{T}"/> is stable across a
/// noise-only change (processing design §12).
/// </summary>
public sealed record ParsedDocument
{
    public string? Title { get; init; }

    public string? Language { get; init; }

    public IReadOnlyList<DocBlock> Blocks { get; init; } = [];

    public IReadOnlyList<DocTable> Tables { get; init; } = [];

    public IReadOnlyList<DocPage> Pages { get; init; } = [];

    public IReadOnlyList<string> Breadcrumb { get; init; } = [];

    public string? Description { get; init; }

    public IReadOnlyList<string> Keywords { get; init; } = [];

    /// <summary>The visible date by the site profile's date pattern (processing design §3.7, §7).</summary>
    public DateOnly? VisibleDate { get; init; }

    /// <summary>The document's own date (PDF/Office properties), when the parser reports one.</summary>
    public DateOnly? DocumentDate { get; init; }

    /// <summary><c>Strategy</c> of the run item (processing design §3.4).</summary>
    public string? Strategy { get; init; }

    /// <summary>Pages that failed alone after halving (processing design §3.3).</summary>
    public IReadOnlyList<int> MissingPages { get; init; } = [];

    /// <summary>The parse rule that matched, <c>name@version</c> (processing design §3.5).</summary>
    public string? ParseRule { get; init; }

    /// <summary>The matched rule's structure hints when it is admin-made (<see cref="Parse.ParseRules.Of"/>); null for built-in rules.</summary>
    public Parse.ParseRuleHints? RuleHints { get; init; }

    /// <summary>Facts of the stages so far (<c>clean.*</c>, <c>quality.*</c>, <c>html.*</c>), numbers and short codes only.</summary>
    public IReadOnlyDictionary<string, double> Facts { get; init; } = new SortedDictionary<string, double>(StringComparer.Ordinal);
}

/// <summary>One chunk of the <c>Chunks</c> stage value (processing design §6.5): what the index writer stores per row.</summary>
public sealed record ChunkDraft
{
    public required int Ordinal { get; init; }

    /// <summary>The embedded prefix (breadcrumb › heading path, ≤ 60 tokens).</summary>
    public required string Prefix { get; init; }

    /// <summary>The full heading path joined with <c> › </c> (breadcrumb not included).</summary>
    public required string HeadingPath { get; init; }

    /// <summary>The body without the prefix.</summary>
    public required string Text { get; init; }

    public required string SearchText { get; init; }

    public required int OverlapChars { get; init; }

    /// <summary>SHA-256 of the embedded string, lower-case hex.</summary>
    public required string TextHash { get; init; }

    public required int TokenCount { get; init; }

    public int? Page { get; init; }

    public int? PageEnd { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    public required string DocumentType { get; init; }

    public DateOnly? SourceModifiedAt { get; init; }

    /// <summary>Table row groups and code: no overlap, rows counted for personal data as they are.</summary>
    public bool IsTable { get; init; }

    /// <summary><c>Chunks."ContentKind"</c> (#129): <c>table</c> for a table row group, <c>list</c> when list items are at least half of the body tokens, else <c>text</c>.</summary>
    public string ContentKind { get; init; } = Domain.Entities.ChunkContentKinds.Text;

    /// <summary><c>Chunks."LargeTable"</c> (#129): the second or a later row group of a table over <see cref="Chunking.ChunkOptions.LargeTableRows"/> rows.</summary>
    public bool LargeTable { get; init; }

    /// <summary>The string sent to the embedding model: <c>prefix + "\n\n" + Text</c>, NFC.</summary>
    [JsonIgnore]
    public string EmbeddedText => Chunking.ChunkText.Embedded(Prefix, Text);
}

/// <summary>The <c>Chunks</c> stage value: the chunk set's chunks with the facts the chunk stage computed.</summary>
public sealed record ChunkSetDraft
{
    public IReadOnlyList<ChunkDraft> Chunks { get; init; } = [];

    public IReadOnlyDictionary<string, double> Facts { get; init; } = new SortedDictionary<string, double>(StringComparer.Ordinal);

    /// <summary>Set by <c>personal-data</c> (processing design §9.4); null before it ran.</summary>
    public Pipelines.Processing.PiiCounts? Pii { get; init; }
}

/// <summary>Serialization of stage values for the output cache, and their <c>OutputHash</c> (engine design §8).</summary>
public static class ProcessingJson
{
    /// <summary>
    /// The contract options, except that a constructor parameter left out reads as its default. Outputs are written without
    /// their null members (<see cref="ContractJson"/>'s <c>WhenWritingNull</c>), so a nullable parameter that was null is
    /// absent when the output is read back: a Word document's page count, a section without pages. Required parameters made
    /// that a "required property missing" failure, and a .docx's parse job went dead. Only reading changes: what is written,
    /// and so every <see cref="OutputHash"/>, stays the same.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = ReadTolerant();

    private static JsonSerializerOptions ReadTolerant()
    {
        var options = new JsonSerializerOptions(ContractJson.Options) { RespectRequiredConstructorParameters = false };
        options.MakeReadOnly();
        return options;
    }

    /// <summary>SHA-256 of the RFC 8785 form of <paramref name="value"/>: equal values hash equal whatever the member order.</summary>
    public static byte[] OutputHash<T>(T value) => CanonicalJson.Sha256(JsonSerializer.SerializeToElement(value, Options));

    /// <summary>Lower-case hex of a SHA-256 over UTF-8 text.</summary>
    public static string Sha256Hex(string text) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
}
