using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Ume.Rag.Worker.Evaluation.Generation;

/// <summary>An eligible chunk as the sampler sees it (processing design §13.1): no text, so a large collection stays cheap.</summary>
public sealed record SampleChunk(Guid ChunkId, Guid DocumentId, string DocumentType, int Ordinal);

/// <summary>
/// The sample of a generation round (processing design §13.1). The size is <c>N = min(max, 3 × D, eligible chunks)</c>,
/// over-generated 1.6 × to survive filtering; the order is round-robin over the documents in seeded random order with at
/// most <c>⌈N/D⌉ + 1</c> chunks per document, first within per-type quotas proportional to the types' chunk shares (a
/// type with ≥ 5 % of the chunks gets ≥ 2), then without them. The seed is <c>SHA-256(setId ‖ round)</c>, so the same
/// seed and inputs give the same order.
/// </summary>
public static class ChunkSampler
{
    public const double OverGeneration = 1.6;
    public const double MinTypeShare = 0.05;
    public const int MinPerType = 2;

    /// <summary><c>N = min(max, 3 × D, eligible chunks)</c>.</summary>
    public static int Target(int maxQuestions, int liveDocuments, int eligibleChunks) =>
        Math.Max(0, Math.Min(maxQuestions, Math.Min(3 * liveDocuments, eligibleChunks)));

    /// <summary>The chunks to generate from for <paramref name="needed"/> questions: <c>⌈1.6 × needed⌉</c>.</summary>
    public static int Candidates(int needed) => needed <= 0 ? 0 : (int)Math.Ceiling(OverGeneration * needed);

    /// <summary><c>⌈N/D⌉ + 1</c> chunks per document.</summary>
    public static int PerDocumentCap(int target, int liveDocuments) =>
        liveDocuments <= 0 ? 0 : ((target + liveDocuments - 1) / liveDocuments) + 1;

    /// <summary>
    /// <c>SHA-256(setId ‖ round)</c>: the set id's 16 bytes in RFC 4122 (big-endian) order, then the round as a 4-byte
    /// big-endian integer.
    /// </summary>
    public static byte[] Seed(Guid setId, int round)
    {
        Span<byte> input = stackalloc byte[20];
        setId.TryWriteBytes(input[..16], bigEndian: true, out _);
        BinaryPrimitives.WriteInt32BigEndian(input[16..], round);
        return SHA256.HashData(input);
    }

    /// <summary>
    /// Every eligible chunk within the per-document cap, in sample order: <paramref name="candidates"/> is the size the
    /// type quotas are computed for. The caller takes chunks from the front until it has as many as it needs, so a chunk it
    /// skips (body too short) is replaced by the next one.
    /// </summary>
    public static IReadOnlyList<SampleChunk> Rank(IReadOnlyList<SampleChunk> eligible, byte[] seed, int target, int liveDocuments, int candidates)
    {
        ArgumentNullException.ThrowIfNull(eligible);
        ArgumentNullException.ThrowIfNull(seed);
        var cap = PerDocumentCap(target, liveDocuments);
        if (eligible.Count == 0 || cap == 0 || candidates <= 0)
        {
            return [];
        }

        var random = new SeededRandom(seed);
        var documents = eligible.Select(c => c.DocumentId).Distinct().Order().ToList();
        random.Shuffle(documents);
        var byDocument = eligible.GroupBy(c => c.DocumentId).ToDictionary(g => g.Key, g => g.OrderBy(c => c.Ordinal).ThenBy(c => c.ChunkId).ToList());
        var queues = documents.Select(d =>
        {
            var chunks = byDocument[d];
            random.Shuffle(chunks);
            return chunks;
        }).ToList();

        var quotas = Quotas(eligible, candidates);
        var taken = new HashSet<Guid>();
        var perDocument = new int[queues.Count];
        var order = new List<SampleChunk>();
        RoundRobin(withQuotas: true);
        RoundRobin(withQuotas: false);
        return order;

        void RoundRobin(bool withQuotas)
        {
            bool progress;
            do
            {
                progress = false;
                for (var d = 0; d < queues.Count; d++)
                {
                    if (perDocument[d] >= cap)
                    {
                        continue;
                    }

                    var next = queues[d].FirstOrDefault(c => !taken.Contains(c.ChunkId) && (!withQuotas || quotas[c.DocumentType] > 0));
                    if (next is null)
                    {
                        continue;
                    }

                    taken.Add(next.ChunkId);
                    perDocument[d]++;
                    order.Add(next);
                    progress = true;
                    if (withQuotas)
                    {
                        quotas[next.DocumentType]--;
                    }
                }
            }
            while (progress);
        }
    }

    /// <summary>
    /// Chunks per document type for <paramref name="candidates"/>: the floor of each type's share, at least
    /// <see cref="MinPerType"/> (or all it has) for a type with ≥ 5 % of the chunks, any excess taken back from the largest
    /// types, then the rest by the largest remainder.
    /// </summary>
    internal static Dictionary<string, int> Quotas(IReadOnlyList<SampleChunk> eligible, int candidates)
    {
        var types = eligible.GroupBy(c => c.DocumentType, StringComparer.Ordinal)
            .Select(g => (Type: g.Key, Count: g.Count(), Exact: (double)g.Count() / eligible.Count * candidates))
            .OrderBy(t => t.Type, StringComparer.Ordinal)
            .ToList();
        var quotas = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (type, count, exact) in types)
        {
            var quota = (int)Math.Floor(exact);
            if ((double)count / eligible.Count >= MinTypeShare)
            {
                quota = Math.Max(quota, Math.Min(MinPerType, count));
            }

            quotas[type] = Math.Min(quota, count);
        }

        // The minimums may exceed the candidates: take the excess from the largest types, never below their own minimum.
        var minimums = types.ToDictionary(t => t.Type, t => (double)t.Count / eligible.Count >= MinTypeShare ? Math.Min(MinPerType, t.Count) : 0,
            StringComparer.Ordinal);
        while (quotas.Values.Sum() > candidates && quotas.Where(q => q.Value > minimums[q.Key]).OrderByDescending(q => q.Value)
                   .ThenBy(q => q.Key, StringComparer.Ordinal).Select(q => q.Key).FirstOrDefault() is { } largest)
        {
            quotas[largest]--;
        }

        var rest = candidates - quotas.Values.Sum();
        foreach (var (type, count, _) in types.OrderByDescending(t => t.Exact - Math.Floor(t.Exact)).ThenBy(t => t.Type, StringComparer.Ordinal))
        {
            if (rest <= 0)
            {
                break;
            }

            if (quotas[type] < count)
            {
                quotas[type]++;
                rest--;
            }
        }

        return quotas;
    }
}

/// <summary>
/// A reproducible random sequence from a seed: value <c>i</c> is the first 8 bytes of <c>SHA-256(seed ‖ i)</c>, so the
/// order does not depend on the runtime's <see cref="Random"/> algorithm.
/// </summary>
internal sealed class SeededRandom(byte[] seed)
{
    private readonly byte[] _input = [.. seed, 0, 0, 0, 0, 0, 0, 0, 0];
    private ulong _counter;

    public ulong NextUInt64()
    {
        BinaryPrimitives.WriteUInt64BigEndian(_input.AsSpan(seed.Length), _counter++);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(_input, hash);
        return BinaryPrimitives.ReadUInt64BigEndian(hash);
    }

    /// <summary>A value in <c>[0, n)</c>, without modulo bias.</summary>
    public int Next(int n)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(n);
        var limit = ulong.MaxValue - (ulong.MaxValue % (ulong)n);
        ulong value;
        do
        {
            value = NextUInt64();
        }
        while (value >= limit);

        return (int)(value % (ulong)n);
    }

    /// <summary>Fisher–Yates.</summary>
    public void Shuffle<T>(IList<T> items)
    {
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }
}
