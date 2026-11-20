using Kalla.Retrieval.Postgres;

namespace Kalla.Retrieval;

/// <summary>One hit to expand, in rank order, with the section bound of its collection (knowledge design §9.2).</summary>
/// <param name="HitId">The hit's chunk id.</param>
/// <param name="DocumentId">The hit's document.</param>
/// <param name="ExpandMaxTokens">The collection's <c>expandMaxTokens</c> (1,500 by default).</param>
public sealed record SectionHit(Guid HitId, Guid DocumentId, int ExpandMaxTokens);

/// <summary>An assembled section (knowledge design §9.3); <see cref="Id"/> is its 0-based index in the response.</summary>
public sealed record AssembledSection(
    int Id, Guid DocumentId, string HeadingPath, string Text, int TokenEstimate, int? PageStart, int? PageEnd, IReadOnlyList<Guid> ChunkIds,
    bool Truncated);

/// <summary>The sections and, per hit, the index of its section (absent: no section, the budget was spent or the hit was stale).</summary>
public sealed record SectionAssembly(IReadOnlyList<AssembledSection> Sections, IReadOnlyDictionary<Guid, int> SectionOf)
{
    public static SectionAssembly Empty { get; } = new([], new Dictionary<Guid, int>());
}

/// <summary>
/// Section expansion from the neighbouring chunks that statement H fetched (knowledge design §9). Pure: no I/O.
/// <list type="bullet">
/// <item>The section of a hit is the run of chunks contiguous in <c>Ordinal</c> around it whose heading path is the hit's
/// or below it (H already filters the path); a run reaching the ±radius window edge is <c>truncated</c>.</item>
/// <item>Growth starts at the hit and alternates after, before, while Σ <c>TokenCount</c> ≤ the collection's
/// <c>expandMaxTokens</c>; the hit alone is always kept.</item>
/// <item>Sections are built in rank order of their best hit until <c>expandTotalMaxTokens</c> is spent; a hit inside an
/// existing section shares it, and a window overlapping an existing one of the same chunk set is merged into it.</item>
/// <item>Text: the heading path line, then each chunk's text without its leading <c>OverlapChars</c> (all but the first
/// of a run), a <c>### Heading</c> line where a subsection starts.</item>
/// </list>
/// </summary>
public static class SectionAssembler
{
    /// <summary>The window fetched around each hit (<c>Retrieval:ExpandNeighbourChunks</c>, §9.2).</summary>
    public const int DefaultNeighbourRadius = 8;

    public const string PathSeparator = " › ";

    public static SectionAssembly Assemble(IReadOnlyList<SectionHit> hits, IReadOnlyList<HydratedChunk> chunks, int neighbourRadius, int totalMaxTokens)
    {
        ArgumentNullException.ThrowIfNull(hits);
        ArgumentNullException.ThrowIfNull(chunks);
        ArgumentOutOfRangeException.ThrowIfNegative(neighbourRadius);
        var byHit = chunks.GroupBy(c => c.HitId).ToDictionary(g => g.Key, g => g.OrderBy(c => c.Ordinal).ToList());
        var drafts = new List<Draft>();
        var sectionOf = new Dictionary<Guid, Draft>();
        var used = 0;
        var spent = false;

        foreach (var hit in hits)
        {
            if (sectionOf.ContainsKey(hit.HitId) || !byHit.TryGetValue(hit.HitId, out var window))
            {
                continue;
            }

            var at = window.FindIndex(c => c.ChunkId == hit.HitId);
            if (at < 0)
            {
                continue; // stale hit
            }

            // A hit already inside a section shares it at no cost (§9.3 merging).
            if (drafts.FirstOrDefault(d => d.Chunks.ContainsKey(hit.HitId)) is { } containing)
            {
                sectionOf[hit.HitId] = containing;
                continue;
            }

            if (spent)
            {
                continue;
            }

            var self = window[at];
            var (run, reachesEdge) = Run(window, at, self.Ordinal, neighbourRadius);
            var chosen = Grow(run, run.IndexOf(self), hit.ExpandMaxTokens, out var cutByBudget);
            var overlapping = drafts.FirstOrDefault(d => d.ChunkSetId == self.ChunkSetId && d.Overlaps(chosen));
            var added = chosen.Where(c => overlapping is null || !overlapping.Chunks.ContainsKey(c.ChunkId)).Sum(c => c.TokenCount);
            if (used + added > totalMaxTokens)
            {
                spent = true; // lower-ranked results keep their own text only (§9.2 response budget)
                continue;
            }

            used += added;
            var draft = overlapping ?? new Draft(hit.DocumentId, self.ChunkSetId, self.HeadingPath ?? "");
            if (overlapping is null)
            {
                drafts.Add(draft);
            }

            foreach (var chunk in chosen)
            {
                draft.Chunks.TryAdd(chunk.ChunkId, chunk);
            }

            draft.Truncated |= reachesEdge || cutByBudget;
            draft.HitIds.Add(hit.HitId);
            sectionOf[hit.HitId] = draft;
            foreach (var other in hits.Where(h => !sectionOf.ContainsKey(h.HitId) && draft.Chunks.ContainsKey(h.HitId)))
            {
                sectionOf[other.HitId] = draft;
            }
        }

        var sections = drafts.Select((d, i) => d.Build(i)).ToList();
        var index = drafts.Select((d, i) => (d, i)).ToDictionary(x => x.d, x => x.i);
        return new SectionAssembly(sections, sectionOf.ToDictionary(e => e.Key, e => index[e.Value]));
    }

    /// <summary>The maximal run of consecutive ordinals around <paramref name="at"/>; whether it touches the window edge.</summary>
    private static (List<HydratedChunk> Run, bool ReachesEdge) Run(List<HydratedChunk> window, int at, int hitOrdinal, int radius)
    {
        var first = at;
        while (first > 0 && window[first - 1].Ordinal == window[first].Ordinal - 1)
        {
            first--;
        }

        var last = at;
        while (last < window.Count - 1 && window[last + 1].Ordinal == window[last].Ordinal + 1)
        {
            last++;
        }

        var run = window.GetRange(first, last - first + 1);
        var reachesEdge = radius > 0 && (run[0].Ordinal <= hitOrdinal - radius || run[^1].Ordinal >= hitOrdinal + radius);
        return (run, reachesEdge);
    }

    /// <summary>Hit first, then after, before, alternating, while the sum fits; stops at the first chunk that does not.</summary>
    private static List<HydratedChunk> Grow(List<HydratedChunk> run, int at, int maxTokens, out bool cutByBudget)
    {
        var lo = at;
        var hi = at;
        var sum = run[at].TokenCount;
        var after = true;
        cutByBudget = false;
        while (lo > 0 || hi < run.Count - 1)
        {
            var takeAfter = after ? hi < run.Count - 1 : lo == 0;
            var next = takeAfter ? run[hi + 1] : run[lo - 1];
            if (sum + next.TokenCount > maxTokens)
            {
                cutByBudget = true;
                break;
            }

            sum += next.TokenCount;
            if (takeAfter)
            {
                hi++;
            }
            else
            {
                lo--;
            }

            after = !after;
        }

        return run.GetRange(lo, hi - lo + 1);
    }

    private sealed class Draft(Guid documentId, Guid chunkSetId, string headingPath)
    {
        public Guid ChunkSetId { get; } = chunkSetId;

        public Dictionary<Guid, HydratedChunk> Chunks { get; } = [];

        public List<Guid> HitIds { get; } = [];

        public bool Truncated { get; set; }

        public bool Overlaps(List<HydratedChunk> other)
        {
            var min = Chunks.Values.Min(c => c.Ordinal);
            var max = Chunks.Values.Max(c => c.Ordinal);
            return other[0].Ordinal <= max && other[^1].Ordinal >= min;
        }

        public AssembledSection Build(int id)
        {
            var ordered = Chunks.Values.OrderBy(c => c.Ordinal).ToList();
            var text = new System.Text.StringBuilder(headingPath);
            var previousPath = headingPath;
            for (var i = 0; i < ordered.Count; i++)
            {
                var chunk = ordered[i];
                var path = chunk.HeadingPath ?? "";
                if (path != previousPath && path != headingPath && path.Length > 0)
                {
                    var heading = path[(path.LastIndexOf(PathSeparator, StringComparison.Ordinal) is var cut and >= 0 ? cut + PathSeparator.Length : 0)..];
                    Line(text, "### " + heading);
                }

                previousPath = path;
                var body = i > 0 && ordered[i - 1].Ordinal == chunk.Ordinal - 1 ? chunk.Text[Math.Min(chunk.OverlapChars, chunk.Text.Length)..] : chunk.Text;
                Line(text, body);
            }

            var pageStart = ordered.Where(c => c.Page is not null).Select(c => c.Page).Min();
            var pageEnd = ordered.Where(c => (c.PageEnd ?? c.Page) is not null).Select(c => c.PageEnd ?? c.Page).Max();
            return new AssembledSection(id, documentId, headingPath, text.ToString(), ordered.Sum(c => c.TokenCount), pageStart, pageEnd,
                [.. ordered.Select(c => c.ChunkId)], Truncated);
        }

        private static void Line(System.Text.StringBuilder text, string line)
        {
            if (text.Length > 0)
            {
                text.Append('\n');
            }

            text.Append(line);
        }
    }
}
