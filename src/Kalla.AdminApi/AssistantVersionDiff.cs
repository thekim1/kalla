using System.Text.Json;
using System.Text.Json.Nodes;
using Kalla.Domain.Entities;

namespace Kalla.AdminApi;

/// <summary>One line of a line diff: <c>equal</c>, <c>insert</c> (only in the newer text) or <c>delete</c> (only in the older).</summary>
public sealed record DiffLine(string Op, string Text)
{
    public const string Equal = "equal";
    public const string Insert = "insert";
    public const string Delete = "delete";
}

/// <summary>
/// A text's line diff. <c>tooLong</c>: one side is over <see cref="AssistantVersionDiff.MaxTextLength"/> characters, so no
/// lines are given, only whether it changed.
/// </summary>
public sealed record TextDiff(bool Changed, bool TooLong, IReadOnlyList<DiffLine> Lines);

/// <summary>A setting by its name (<c>model</c>, <c>querySteps.retrieval.topK</c>, <c>notFound.links</c>): its value before and after; null when absent.</summary>
public sealed record SettingChange(string Name, JsonNode? From, JsonNode? To);

/// <summary>A collection link added or removed, with the knowledge base's name (also when it was deleted since) and its topics.</summary>
public sealed record LinkChange(Guid CollectionId, string? Name, IReadOnlyList<string> Tags);

/// <summary>A collection linked on both sides whose topics changed.</summary>
public sealed record TopicChange(Guid CollectionId, string? Name, IReadOnlyList<string> From, IReadOnlyList<string> To);

/// <summary>
/// <c>GET /api/assistants/{id}/versions/{n}/diff?against={m|draft}</c> (admin design §4.5, P2): what changed from version
/// <see cref="From"/> to <see cref="To"/> (a number, or the draft's when <see cref="ToDraft"/>). Skills come with P2's skills
/// and have no fields yet.
/// </summary>
public sealed record VersionDiffDto(
    int From, int To, bool ToDraft, IReadOnlyList<SettingChange> Settings, IReadOnlyList<LinkChange> CollectionsAdded,
    IReadOnlyList<LinkChange> CollectionsRemoved, IReadOnlyList<TopicChange> TopicsChanged, TextDiff Instructions, TextDiff NotFoundSv, TextDiff NotFoundEn)
{
    /// <summary>Whether anything at all differs.</summary>
    public bool Changed => Settings.Count > 0 || CollectionsAdded.Count > 0 || CollectionsRemoved.Count > 0 || TopicsChanged.Count > 0
        || Instructions.Changed || NotFoundSv.Changed || NotFoundEn.Changed;
}

/// <summary>
/// The structured diff of two assistant versions (admin design §4.5, B-56): changed settings by name, collections added or
/// removed (and changed topics), and a line diff of the instructions and the not-found texts. The line diff is an in-house
/// Myers diff (no package) over texts of at most <see cref="MaxTextLength"/> characters.
/// </summary>
public static class AssistantVersionDiff
{
    /// <summary>The longest text that gets a line diff (admin design §4.5).</summary>
    public const int MaxTextLength = 20_000;

    /// <summary>
    /// The most differing lines the diff looks for before it gives the whole text as replaced: keeps the work and memory
    /// bounded (Myers is O((N+M)·D) time and, with the kept trace, O(D²) memory) for texts that have little in common.
    /// </summary>
    public const int MaxEdits = 2_000;

    private static readonly string[] TextMembers = ["sv", "en"];

    public static VersionDiffDto Of(AssistantVersion from, AssistantVersion to, bool toDraft, IReadOnlyDictionary<Guid, string> collectionNames)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(collectionNames);
        var settings = new List<SettingChange>();
        if (!string.Equals(from.GatewayModel, to.GatewayModel, StringComparison.Ordinal))
        {
            settings.Add(new SettingChange("model", from.GatewayModel is null ? null : JsonValue.Create(from.GatewayModel), to.GatewayModel is null ? null : JsonValue.Create(to.GatewayModel)));
        }

        Compare("querySteps", Parse(from.QuerySteps), Parse(to.QuerySteps), settings, skip: []);
        var notFoundFrom = Parse(from.NotFound);
        var notFoundTo = Parse(to.NotFound);
        Compare("notFound", notFoundFrom, notFoundTo, settings, skip: TextMembers);

        var before = from.CollectionLinks.ToDictionary(l => l.CollectionId);
        var after = to.CollectionLinks.ToDictionary(l => l.CollectionId);
        string? NameOf(Guid id) => collectionNames.GetValueOrDefault(id);
        var added = to.CollectionLinks.Where(l => !before.ContainsKey(l.CollectionId)).Select(l => new LinkChange(l.CollectionId, NameOf(l.CollectionId), l.Tags)).ToList();
        var removed = from.CollectionLinks.Where(l => !after.ContainsKey(l.CollectionId)).Select(l => new LinkChange(l.CollectionId, NameOf(l.CollectionId), l.Tags)).ToList();
        var topics = to.CollectionLinks.Where(l => before.TryGetValue(l.CollectionId, out var old) && !old.Tags.SequenceEqual(l.Tags, StringComparer.Ordinal))
            .Select(l => new TopicChange(l.CollectionId, NameOf(l.CollectionId), before[l.CollectionId].Tags, l.Tags)).ToList();

        return new VersionDiffDto(from.Number, to.Number, toDraft, settings, added, removed, topics, Text(from.SystemPrompt, to.SystemPrompt),
            Text(TextOf(notFoundFrom, "sv"), TextOf(notFoundTo, "sv")), Text(TextOf(notFoundFrom, "en"), TextOf(notFoundTo, "en")));
    }

    /// <summary>The line diff of two texts; line ends are <c>\n</c> or <c>\r\n</c>, and an empty text has no lines.</summary>
    public static TextDiff Text(string? from, string? to)
    {
        var a = from ?? "";
        var b = to ?? "";
        var changed = !string.Equals(Normalise(a), Normalise(b), StringComparison.Ordinal);
        if (a.Length > MaxTextLength || b.Length > MaxTextLength)
        {
            return new TextDiff(changed, TooLong: true, []);
        }

        return new TextDiff(changed, TooLong: false, Lines(SplitLines(a), SplitLines(b)));
    }

    /// <summary>
    /// Myers's O((N+M)·D) diff (E. Myers, "An O(ND) Difference Algorithm and Its Variations", 1986) over lines, after the
    /// common head and tail are taken off. Beyond <see cref="MaxEdits"/> differing lines the middle is given as deleted and
    /// inserted whole.
    /// </summary>
    public static IReadOnlyList<DiffLine> Lines(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var head = 0;
        while (head < a.Count && head < b.Count && a[head] == b[head])
        {
            head++;
        }

        var tail = 0;
        while (tail < a.Count - head && tail < b.Count - head && a[a.Count - 1 - tail] == b[b.Count - 1 - tail])
        {
            tail++;
        }

        var result = new List<DiffLine>(a.Count + b.Count);
        for (var i = 0; i < head; i++)
        {
            result.Add(new DiffLine(DiffLine.Equal, a[i]));
        }

        var middleA = a.Skip(head).Take(a.Count - head - tail).ToArray();
        var middleB = b.Skip(head).Take(b.Count - head - tail).ToArray();
        result.AddRange(Middle(middleA, middleB));
        for (var i = a.Count - tail; i < a.Count; i++)
        {
            result.Add(new DiffLine(DiffLine.Equal, a[i]));
        }

        return result;
    }

    private static List<DiffLine> Middle(string[] a, string[] b)
    {
        int n = a.Length, m = b.Length, max = n + m;
        if (max == 0)
        {
            return [];
        }

        // v[k] is the furthest x on diagonal k; the trace keeps v's live band [-d, d] for each d, so the path can be walked back.
        var offset = max + 1;
        var v = new int[2 * max + 3];
        var trace = new List<int[]>();
        var found = -1;
        for (var d = 0; d <= Math.Min(max, MaxEdits); d++)
        {
            for (var k = -d; k <= d; k += 2)
            {
                var x = k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1]) ? v[offset + k + 1] : v[offset + k - 1] + 1;
                var y = x - k;
                while (x < n && y < m && a[x] == b[y])
                {
                    x++;
                    y++;
                }

                v[offset + k] = x;
                if (x >= n && y >= m)
                {
                    found = d;
                    break;
                }
            }

            var band = new int[2 * d + 3];
            Array.Copy(v, offset - d - 1, band, 0, band.Length);
            trace.Add(band);
            if (found >= 0)
            {
                break;
            }
        }

        if (found < 0)
        {
            return [.. a.Select(l => new DiffLine(DiffLine.Delete, l)), .. b.Select(l => new DiffLine(DiffLine.Insert, l))];
        }

        // Walk back from (n, m): at each d the previous snake ends on the diagonal it came from.
        var edits = new List<DiffLine>(n + m);
        int cx = n, cy = m;
        for (var d = found; d >= 0; d--)
        {
            int At(int[] bandOf, int dOf, int k) => bandOf[k + dOf + 1];
            var k = cx - cy;
            int previousK, previousX;
            if (d == 0)
            {
                previousK = 0;
                previousX = 0;
            }
            else
            {
                var band = trace[d - 1];
                previousK = k == -d || (k != d && At(band, d - 1, k - 1) < At(band, d - 1, k + 1)) ? k + 1 : k - 1;
                previousX = At(band, d - 1, previousK);
            }

            var previousY = previousX - previousK;
            var startX = d == 0 ? 0 : previousK == k + 1 ? previousX : previousX + 1;
            var startY = startX - k;
            while (cx > startX && cy > startY)
            {
                edits.Add(new DiffLine(DiffLine.Equal, a[cx - 1]));
                cx--;
                cy--;
            }

            if (d > 0)
            {
                if (previousK == k + 1)
                {
                    edits.Add(new DiffLine(DiffLine.Insert, b[cy - 1]));
                }
                else
                {
                    edits.Add(new DiffLine(DiffLine.Delete, a[cx - 1]));
                }
            }

            cx = previousX;
            cy = previousY;
        }

        edits.Reverse();
        return edits;
    }

    private static string Normalise(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string[] SplitLines(string text) => text.Length == 0 ? [] : Normalise(text).Split('\n');

    private static JsonObject Parse(string json) =>
        (string.IsNullOrWhiteSpace(json) ? null : JsonNode.Parse(json)) as JsonObject ?? [];

    private static string? TextOf(JsonObject json, string member) => json[member] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    /// <summary>Every leaf that differs, by its dotted name; an object is walked into, anything else (arrays too) compared whole.</summary>
    private static void Compare(string prefix, JsonObject? from, JsonObject? to, List<SettingChange> changes, IReadOnlyList<string> skip)
    {
        var names = (from?.Select(p => p.Key) ?? []).Concat(to?.Select(p => p.Key) ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        foreach (var name in names.Where(n => !skip.Contains(n)))
        {
            var a = from?[name];
            var b = to?[name];
            var path = $"{prefix}.{name}";
            if (a is JsonObject oa && b is JsonObject ob)
            {
                Compare(path, oa, ob, changes, []);
            }
            else if (a is JsonObject || b is JsonObject)
            {
                // An object on one side only: its leaves against nothing.
                Compare(path, a as JsonObject, b as JsonObject, changes, []);
                if (a is not JsonObject && a is not null)
                {
                    changes.Add(new SettingChange(path, a.DeepClone(), null));
                }

                if (b is not JsonObject && b is not null)
                {
                    changes.Add(new SettingChange(path, null, b.DeepClone()));
                }
            }
            else if (!JsonNode.DeepEquals(a, b))
            {
                changes.Add(new SettingChange(path, a?.DeepClone(), b?.DeepClone()));
            }
        }
    }
}
