using System.Globalization;
using System.Text;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Retrieval.Text;
using Ume.Rag.Worker.Processing.Clean;

namespace Ume.Rag.Worker.Processing.Chunking;

/// <summary>The <c>chunk</c> parameters (template <c>chunkTokens</c>, <c>overlapPercent</c>; processing design §6.2).</summary>
public sealed record ChunkOptions(int ChunkTokens = 400, int OverlapPercent = 15)
{
    public const int PrefixTokens = 60;
    public const int ShortSectionTokens = 50;
    public const int MaxTableRows = 20;
    public const int LargeTableRows = 2_000;

    public int OverlapTokens => ChunkTokens * OverlapPercent / 100;
}

/// <summary>The embedded string (processing design §6.5): <c>prefix + "\n\n" + Text</c>, NFC; the body alone without a prefix.</summary>
public static class ChunkText
{
    public const string PathSeparator = " › ";

    public static string Embedded(string prefix, string text) =>
        (prefix.Length == 0 ? text : prefix + "\n\n" + text).Normalize(NormalizationForm.FormC);
}

/// <summary>
/// <c>chunk</c> (processing design §5–§7): split by headings, then pack each section into chunks of
/// <see cref="ChunkOptions.ChunkTokens"/> tokens counted on the embedded string (prefix included). Units are sentences
/// (Swedish splitter), list items with their nested items, table row groups and code blocks. A new chunk of the same
/// section starts with whole trailing sentences up to the overlap (a list continues with its lead-in sentence); never
/// across sections, tables or code. A short tail is merged (≤ 115 %) or the last two chunks re-split evenly; short sibling
/// sections are packed together under their parent. Tables become <c>Kolumn: värde; …</c> row groups. Each chunk gets its
/// <c>SearchText</c> from <see cref="SwedishDecompounder"/>, its <c>TextHash</c> and token count, the document's metadata,
/// and its content kind (#129): <c>table</c> for a row group (<c>LargeTable</c> on the second and later groups of a table
/// over <see cref="ChunkOptions.LargeTableRows"/> rows), <c>list</c> when list items are at least half of the body tokens,
/// else <c>text</c>.
/// </summary>
public sealed class Chunker(ITokenCounter tokens, SwedishDecompounder decompounder)
{
    public const int Version = 2;

    public ChunkSetDraft Chunk(ParsedDocument document, ChunkOptions options, DocumentMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(metadata);
        var sections = MergeShortSections(SectionSplitter.Split(document));
        var drafts = new List<Pending>();
        var largeTable = false;
        foreach (var section in sections)
        {
            var prefix = Prefix(document.Breadcrumb, section.HeadingPath);
            var packer = new Packer(this, options, prefix, section);
            foreach (var block in section.Blocks)
            {
                if (block.Kind == BlockKinds.Table && block.Table is { } index && index < document.Tables.Count)
                {
                    var table = document.Tables[index];
                    largeTable |= table.Rows > ChunkOptions.LargeTableRows;
                    drafts.AddRange(packer.Flush());
                    drafts.AddRange(TableChunks(table, section, document.Breadcrumb, options, block));
                    continue;
                }

                packer.Add(block);
            }

            drafts.AddRange(packer.Finish());
        }

        var chunks = drafts.Select((d, i) => Build(i, d, metadata)).ToList();
        var facts = new SortedDictionary<string, double>(QualityFacts.ForChunks(chunks, t => tokens.Count(t)).ToDictionary(), StringComparer.Ordinal);
        if (largeTable)
        {
            facts["quality.largeTable"] = 1;
        }

        return new ChunkSetDraft { Chunks = chunks, Facts = facts };
    }

    /// <summary>Breadcrumb ++ heading path joined with <c> › </c>, ≤ 60 tokens: the first element, <c>…</c>, the last two heading levels.</summary>
    internal string Prefix(IReadOnlyList<string> breadcrumb, IReadOnlyList<string> headingPath)
    {
        var elements = new List<string>();
        foreach (var element in breadcrumb.Concat(headingPath))
        {
            if (elements.Count > 0 && string.Equals(elements[^1], element, StringComparison.OrdinalIgnoreCase))
            {
                continue; // an element that repeats the next one (breadcrumb's last level = the title)
            }

            elements.Add(element);
        }

        var prefix = string.Join(ChunkText.PathSeparator, elements);
        if (tokens.Count(prefix) <= ChunkOptions.PrefixTokens || elements.Count <= 3)
        {
            return Fit(prefix);
        }

        var tail = headingPath.Count >= 2 ? headingPath.Skip(headingPath.Count - 2).ToList() : elements.Skip(elements.Count - 2).ToList();
        return Fit(string.Join(ChunkText.PathSeparator, new[] { elements[0], "…" }.Concat(tail)));

        string Fit(string value) => tokens.Count(value) <= ChunkOptions.PrefixTokens
            ? value
            : value[..TokenCuts.PrefixLength(tokens, value, ChunkOptions.PrefixTokens - 1)].TrimEnd() + "…";
    }

    private ITokenCounter Tokens => tokens;

    private int Count(string prefix, string body) => tokens.Count(ChunkText.Embedded(prefix, body));

    private ChunkDraft Build(int ordinal, Pending draft, DocumentMetadata metadata)
    {
        var text = draft.Body.Normalize(NormalizationForm.FormC);
        var embedded = ChunkText.Embedded(draft.Prefix, text);
        var parts = decompounder.PartsOf(draft.Prefix + "\n" + text);
        return new ChunkDraft
        {
            Ordinal = ordinal,
            Prefix = draft.Prefix,
            HeadingPath = string.Join(ChunkText.PathSeparator, draft.HeadingPath),
            Text = text,
            SearchText = parts.Count == 0 ? text : text + "\n" + string.Join(' ', parts),
            OverlapChars = draft.OverlapChars,
            TextHash = ProcessingJson.Sha256Hex(embedded),
            TokenCount = tokens.Count(embedded),
            Page = draft.Page,
            PageEnd = draft.PageEnd,
            Tags = metadata.Tags,
            DocumentType = metadata.DocumentType,
            SourceModifiedAt = metadata.SourceModifiedAt,
            IsTable = draft.ContentKind == ChunkContentKinds.Table,
            ContentKind = draft.ContentKind,
            LargeTable = draft.LargeTable,
        };
    }

    /// <summary>Rule 5: consecutive sibling sections each under 50 body tokens, packed under their parent; never across level 1.</summary>
    private List<Section> MergeShortSections(IReadOnlyList<Section> sections)
    {
        var result = new List<Section>();
        var nonEmpty = sections.Where(s => s.Blocks.Count > 0).ToList();
        var i = 0;
        while (i < nonEmpty.Count)
        {
            var section = nonEmpty[i];
            var j = i;
            if (section.Level >= 2 && IsShort(section))
            {
                var parent = section.HeadingPath.Take(section.HeadingPath.Count - 1).ToList();
                while (j + 1 < nonEmpty.Count && nonEmpty[j + 1].Level == section.Level && IsShort(nonEmpty[j + 1])
                    && nonEmpty[j + 1].HeadingPath.Take(nonEmpty[j + 1].HeadingPath.Count - 1).SequenceEqual(parent, StringComparer.Ordinal))
                {
                    j++;
                }

                if (j > i)
                {
                    var group = nonEmpty.Skip(i).Take(j - i + 1).ToList();
                    var blocks = group.SelectMany(s => s.Heading is null
                        ? s.Blocks
                        : s.Blocks.Prepend(new DocBlock(BlockKinds.Paragraph, "**" + s.Heading + "**") { Page = s.PageStart })).ToList();
                    result.Add(new Section(section.Ordinal, section.Level - 1, parent, null, blocks,
                        group.Min(s => s.PageStart), group.Max(s => s.PageEnd)));
                    i = j + 1;
                    continue;
                }
            }

            result.Add(section);
            i++;
        }

        return result;
    }

    private bool IsShort(Section section) =>
        section.Blocks.All(b => b.Kind != BlockKinds.Table)
        && tokens.Count(string.Join("\n\n", section.Blocks.Select(b => b.Text))) < ChunkOptions.ShortSectionTokens;

    /// <summary>Tables as row groups (processing design §6.3): header names repeated in every row, no overlap, ≤ 20 rows per chunk.</summary>
    private IEnumerable<Pending> TableChunks(DocTable table, Section section, IReadOnlyList<string> breadcrumb, ChunkOptions options, DocBlock block)
    {
        var path = table.Sheet is { } sheet ? [.. section.HeadingPath, sheet] : section.HeadingPath;
        var prefix = Prefix(breadcrumb, path);
        var grid = Grid(table);
        var headerRows = Math.Min(table.HeaderRows, grid.Count);
        var cols = grid.Count == 0 ? 0 : grid.Max(r => r.Length);
        var names = new string[cols];
        for (var c = 0; c < cols; c++)
        {
            var parts = new List<string>();
            for (var r = 0; r < headerRows; r++)
            {
                var value = c < grid[r].Length ? grid[r][c] : string.Empty;
                if (value.Length > 0 && (parts.Count == 0 || parts[^1] != value))
                {
                    parts.Add(value);
                }
            }

            names[c] = parts.Count > 0 ? string.Join(" / ", parts) : string.Create(CultureInfo.InvariantCulture, $"Kolumn {c + 1}");
        }

        var keyValue = headerRows == 0 && cols == 2;
        var lead = table.Caption is { Length: > 0 } caption ? "Tabell: " + caption : null;
        var rows = new List<string>();
        for (var r = headerRows; r < grid.Count; r++)
        {
            var cells = Enumerable.Range(0, cols).Select(c => (Name: names[c], Value: c < grid[r].Length ? grid[r][c] : string.Empty)).ToList();
            if (keyValue)
            {
                if (cells[0].Value.Length > 0 || cells[1].Value.Length > 0)
                {
                    rows.Add(cells[0].Value.Length == 0 ? cells[1].Value : cells[0].Value + ": " + cells[1].Value);
                }

                continue;
            }

            var filled = cells.Where(c => c.Value.Length > 0).ToList();
            if (filled.Count == 0)
            {
                continue;
            }

            var line = string.Join("; ", filled.Select(c => c.Name + ": " + c.Value));
            if (Count(prefix, Join(lead, [line])) <= options.ChunkTokens)
            {
                rows.Add(line);
                continue;
            }

            // A row longer than the budget: column groups, each repeating the first column.
            var first = filled[0].Name + ": " + filled[0].Value;
            var group = new List<string> { first };
            foreach (var cell in filled.Skip(1))
            {
                var part = cell.Name + ": " + cell.Value;
                if (group.Count > 1 && Count(prefix, Join(lead, [string.Join("; ", group.Append(part))])) > options.ChunkTokens)
                {
                    rows.Add(string.Join("; ", group));
                    group = [first];
                }

                group.Add(part);
            }

            rows.Add(string.Join("; ", group));
        }

        var large = table.Rows > ChunkOptions.LargeTableRows;
        var groups = 0;
        var current = new List<string>();
        foreach (var row in rows)
        {
            if (current.Count > 0 && (current.Count >= ChunkOptions.MaxTableRows || Count(prefix, Join(lead, [.. current, row])) > options.ChunkTokens))
            {
                yield return Table(current);
                current = [];
            }

            current.Add(row);
        }

        if (current.Count > 0)
        {
            yield return Table(current);
        }

        Pending Table(List<string> group)
        {
            var body = Join(lead, group);
            if (Count(prefix, body) > options.ChunkTokens)
            {
                body = body[..TokenCuts.PrefixLength(tokens, body, options.ChunkTokens - tokens.Count(prefix) - 2)];
            }

            return new Pending(prefix, path, body, 0, block.Page, block.PageEnd ?? block.Page, ChunkContentKinds.Table, LargeTable: large && groups++ > 0);
        }
    }

    private static string Join(string? lead, IEnumerable<string> rows) => string.Join('\n', lead is null ? rows : rows.Prepend(lead));

    /// <summary>The table's cells as a grid with spans expanded (a spanning value repeated in each row and column it covers).</summary>
    private static List<string[]> Grid(DocTable table)
    {
        var rows = Math.Max(table.Rows, table.Cells.Count == 0 ? 0 : table.Cells.Max(c => c.R + c.Rs));
        var cols = Math.Max(table.Cols, table.Cells.Count == 0 ? 0 : table.Cells.Max(c => c.C + c.Cs));
        var grid = Enumerable.Range(0, rows).Select(_ => Enumerable.Repeat(string.Empty, cols).ToArray()).ToList();
        foreach (var cell in table.Cells)
        {
            for (var r = cell.R; r < cell.R + Math.Max(1, cell.Rs) && r < rows; r++)
            {
                for (var c = cell.C; c < cell.C + Math.Max(1, cell.Cs) && c < cols; c++)
                {
                    grid[r][c] = cell.Text;
                }
            }
        }

        return grid;
    }

    private enum UnitKind
    {
        Sentence,
        ListItem,
        Code,
    }

    /// <summary>A packing unit with the separator that goes before it when it is not first in a chunk.</summary>
    private sealed record Unit(UnitKind Kind, string Text, string Separator, int? Page, int? PageEnd)
    {
        /// <summary>For list items: the lead-in sentence before the list (the overlap when a list continues).</summary>
        public string? LeadIn { get; init; }
    }

    /// <summary>A chunk before its hashes: prefix, path, body, the overlap length and its content kind.</summary>
    private sealed record Pending(string Prefix, IReadOnlyList<string> HeadingPath, string Body, int OverlapChars, int? Page, int? PageEnd,
        string ContentKind = ChunkContentKinds.Text, bool LargeTable = false);

    /// <summary>Greedy packing of one section's prose units (rules 2–4).</summary>
    private sealed class Packer(Chunker owner, ChunkOptions options, string prefix, Section section)
    {
        private readonly List<List<Unit>> _closed = [];
        private readonly List<string?> _overlaps = [];
        private readonly List<Pending> _done = [];
        private List<Unit> _current = [];
        private string? _overlap;
        private int? _tokens;
        private int _estimatedJoins;
        private string? _lastSentence;
        private bool _inList;

        private int Budget => options.ChunkTokens;

        public void Add(DocBlock block)
        {
            foreach (var unit in Units(block))
            {
                Place(unit);
            }
        }

        /// <summary>Ends the prose before a table (no overlap across tables) and hands over its chunks, so the order is kept.</summary>
        public List<Pending> Flush()
        {
            _lastSentence = null;
            return Finish();
        }

        public List<Pending> Finish()
        {
            Close();
            Drain();
            var done = _done.ToList();
            _done.Clear();
            return done;
        }

        private IEnumerable<Unit> Units(DocBlock block)
        {
            switch (block.Kind)
            {
                case BlockKinds.ListItem:
                    var lead = _inList ? null : _lastSentence;
                    _inList = true;
                    yield return new Unit(UnitKind.ListItem, new string(' ', 2 * block.ListDepth) + "- " + block.Text, "\n", block.Page, block.PageEnd ?? block.Page)
                    {
                        LeadIn = lead,
                    };
                    yield break;
                case BlockKinds.Code or BlockKinds.Formula:
                    _inList = false;
                    foreach (var piece in block.Text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
                    {
                        yield return new Unit(UnitKind.Code, "```\n" + piece.Trim('\n') + "\n```", "\n\n", block.Page, block.PageEnd ?? block.Page);
                    }

                    yield break;
                default:
                    _inList = false;
                    var first = true;
                    foreach (var sentence in SentenceSplitter.Split(block.Text))
                    {
                        _lastSentence = sentence;
                        yield return new Unit(UnitKind.Sentence, sentence, first ? "\n\n" : " ", block.Page, block.PageEnd ?? block.Page);
                        first = false;
                    }

                    yield break;
            }
        }

        private static string Body(IReadOnlyList<Unit> units, string? overlap)
        {
            var body = new StringBuilder(overlap ?? string.Empty);
            foreach (var unit in units)
            {
                if (body.Length > 0)
                {
                    body.Append(unit.Separator);
                }

                body.Append(unit.Text);
            }

            return body.ToString();
        }

        private bool Fits(IReadOnlyList<Unit> units, string? overlap, double factor = 1) => owner.Count(prefix, Body(units, overlap)) <= Budget * factor;

        /// <summary>
        /// Adds <paramref name="unit"/> to the current chunk when the chunk still fits the budget with it. The chunk's count
        /// is carried as a running sum of unit counts; the exact count of the embedded string decides whenever the sum
        /// plus <see cref="TokenCuts.JoinSlack"/> per estimated join comes near the budget.
        /// </summary>
        private bool TryAppend(Unit unit)
        {
            if (_tokens is { } tokens)
            {
                var estimate = tokens + owner.Tokens.Count(unit.Text.Normalize(NormalizationForm.FormC));
                if (estimate + (_estimatedJoins + 1) * TokenCuts.JoinSlack <= Budget)
                {
                    _current.Add(unit);
                    (_tokens, _estimatedJoins) = (estimate, _estimatedJoins + 1);
                    return true;
                }
            }

            var exact = owner.Count(prefix, Body([.. _current, unit], _overlap));
            if (exact > Budget)
            {
                return false;
            }

            _current.Add(unit);
            (_tokens, _estimatedJoins) = (exact, 0);
            return true;
        }

        private void Place(Unit unit)
        {
            if (TryAppend(unit))
            {
                return;
            }

            if (_current.Count > 0)
            {
                var previous = _current;
                Close();
                _overlap = OverlapFor(previous, unit);
                if (_overlap is not null && TryAppend(unit))
                {
                    return;
                }

                _overlap = null;
            }

            if (TryAppend(unit))
            {
                return;
            }

            // A unit larger than a whole budget: cut at the last whitespace before the limit.
            var room = Budget - owner.Tokens.Count(prefix) - 2;
            var rest = unit.Text;
            while (rest.Length > 0)
            {
                var cut = TokenCuts.PrefixLength(owner.Tokens, rest, Math.Max(1, room));
                var piece = rest[..cut].Trim();
                rest = rest[cut..].TrimStart();
                _current = [unit with { Text = piece }];
                _overlap = null;
                _tokens = null;
                if (rest.Length > 0)
                {
                    Close();
                }
            }
        }

        /// <summary>Whole trailing sentences up to the overlap; one longer sentence: its last tokens at a word boundary; a list continues with its lead-in.</summary>
        private string? OverlapFor(List<Unit> previous, Unit next)
        {
            var limit = options.OverlapTokens;
            if (limit <= 0)
            {
                return null;
            }

            if (next.Kind == UnitKind.ListItem && previous[^1].Kind == UnitKind.ListItem)
            {
                var lead = previous.LastOrDefault(u => u.LeadIn is not null)?.LeadIn;
                return lead is not null && owner.Tokens.Count(lead) <= limit ? lead : null;
            }

            if (next.Kind != UnitKind.Sentence || previous[^1].Kind != UnitKind.Sentence)
            {
                return null;
            }

            var taken = new List<string>();
            for (var i = previous.Count - 1; i >= 0 && previous[i].Kind == UnitKind.Sentence; i--)
            {
                var candidate = string.Join(' ', taken.Prepend(previous[i].Text));
                if (owner.Tokens.Count(candidate) > limit)
                {
                    break;
                }

                taken.Insert(0, previous[i].Text);
            }

            if (taken.Count > 0)
            {
                return string.Join(' ', taken);
            }

            var last = previous[^1].Text;
            var start = TokenCuts.SuffixStart(owner.Tokens, last, limit);
            return start < last.Length ? last[start..] : null;
        }

        private void Close()
        {
            if (_current.Count == 0)
            {
                _overlap = null;
                return;
            }

            _closed.Add(_current);
            _overlaps.Add(_overlap);
            _current = [];
            _overlap = null;
            _tokens = null;
        }

        /// <summary>Rule 4 on the section's last chunk, then the pending chunks.</summary>
        private void Drain()
        {
            if (_closed.Count >= 2)
            {
                var lastUnits = _closed[^1];
                var lastBody = Body(lastUnits, null);
                if (owner.Tokens.Count(lastBody) < 0.25 * Budget)
                {
                    var merged = _closed[^2].Concat(lastUnits).ToList();
                    if (Fits(merged, _overlaps[^2], 1.15))
                    {
                        _closed.RemoveAt(_closed.Count - 1);
                        _overlaps.RemoveAt(_overlaps.Count - 1);
                        _closed[^1] = merged;
                    }
                    else
                    {
                        Rebalance(merged);
                    }
                }
            }

            for (var i = 0; i < _closed.Count; i++)
            {
                var units = _closed[i];
                var overlap = _overlaps[i];
                var body = Body(units, overlap);
                var pages = units.Where(u => u.Page is not null).ToList();
                _done.Add(new Pending(prefix, section.HeadingPath, body, overlap?.Length ?? 0,
                    pages.Count == 0 ? null : pages.Min(u => u.Page), pages.Count == 0 ? null : pages.Max(u => u.PageEnd), KindOf(units, body)));
            }

            _closed.Clear();
            _overlaps.Clear();
        }

        /// <summary><c>list</c> when the list items hold at least half of the body's tokens (#129), else <c>text</c>.</summary>
        private string KindOf(IReadOnlyList<Unit> units, string body)
        {
            var listTokens = units.Where(u => u.Kind == UnitKind.ListItem).Sum(u => owner.Tokens.Count(u.Text));
            return listTokens > 0 && 2 * listTokens >= owner.Tokens.Count(body) ? ChunkContentKinds.List : ChunkContentKinds.Text;
        }

        /// <summary>Re-splits the last two chunks evenly at a unit boundary, both within the budget.</summary>
        private void Rebalance(List<Unit> units)
        {
            var firstOverlap = _overlaps[^2];
            var best = -1;
            var bestCost = int.MaxValue;
            for (var k = 1; k < units.Count; k++)
            {
                var a = units.Take(k).ToList();
                var b = units.Skip(k).ToList();
                if (!Fits(a, firstOverlap) || !Fits(b, null))
                {
                    continue;
                }

                var cost = Math.Abs(owner.Tokens.Count(Body(a, firstOverlap)) - owner.Tokens.Count(Body(b, null)));
                if (cost < bestCost)
                {
                    (best, bestCost) = (k, cost);
                }
            }

            if (best < 0)
            {
                return; // no even split within the budget: keep the chunks as packed
            }

            var head = units.Take(best).ToList();
            var tail = units.Skip(best).ToList();
            var overlap = OverlapFor(head, tail[0]);
            _closed[^2] = head;
            _closed[^1] = tail;
            _overlaps[^1] = overlap is not null && Fits(tail, overlap) ? overlap : null;
        }
    }
}
