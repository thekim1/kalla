using System.Security.Cryptography;
using System.Text;
using Kalla.Retrieval;

namespace Kalla.Benchmarks;

/// <summary>Deterministic inputs shared by the benchmarks: candidate lists shaped like the pilot's, scopes, files.</summary>
public static class BenchData
{
    public const int Dimensions = 1024;

    /// <summary>The repository root (the directory holding <c>Kalla.slnx</c>), for fixture files.</summary>
    public static string RepositoryRoot { get; } = FindRoot();

    public static string Fixture(params string[] parts) => Path.Combine([RepositoryRoot, "tests", "fixtures", .. parts]);

    /// <summary>
    /// A vector list and a keyword list of <paramref name="perList"/> candidates each, over documents of five chunks,
    /// with <paramref name="overlap"/> of the keyword candidates also in the vector list (hybrid search finds many of the
    /// same chunks twice), every tenth chunk a duplicate text of another document's (§8 dedup), and every
    /// twentieth document sharing an identity with another (two sources crawling the same page).
    /// </summary>
    public static (List<Candidate> Vector, List<Candidate> Keyword) CandidateLists(int perList, double overlap, Guid collection, Guid source, int seed = 7)
    {
        var random = new Random(seed);
        var pool = Candidates(perList * 2, collection, source, random);
        var vector = pool.Take(perList).Select((c, i) => c with { Distance = 0.2 + (i * 0.003) + (random.NextDouble() * 0.001) }).ToList();
        var shared = (int)(perList * overlap);
        var keyword = vector.Take(shared).Concat(pool.Skip(perList).Take(perList - shared))
            .Select(c => c with { Distance = null, KeywordScore = random.NextDouble() })
            .ToList();
        return (vector, keyword);
    }

    public static List<Candidate> Candidates(int count, Guid collection, Guid source, Random random)
    {
        var documents = Enumerable.Range(0, (count / 5) + 1).Select(_ => Guid.NewGuid()).ToArray();
        var result = new List<Candidate>(count);
        for (var i = 0; i < count; i++)
        {
            var document = documents[i / 5];
            var text = i % 10 == 9 ? $"delad text {i / 10}" : $"text {i}";
            var identity = (i / 5) % 20 == 19 ? $"https://www.umea.se/sida/{(i / 5) - 1}" : $"https://www.umea.se/sida/{i / 5}";
            result.Add(new Candidate(
                Guid.NewGuid(), document, Guid.NewGuid(), i % 5, SHA256.HashData(Encoding.UTF8.GetBytes(text)), collection, "public",
                i % 3 == 0 ? "pdf" : "page", DateTimeOffset.UtcNow.AddDays(-random.Next(0, 3000)), "url",
                SHA256.HashData(Encoding.UTF8.GetBytes(identity)), source, identity + (i % 4 == 0 ? "/arkiv/gammal" : "")));
        }

        return result;
    }

    /// <summary>A one-collection scope with the website template's age decay on and two demote patterns on the source.</summary>
    public static SearchScope Scope(Guid collection, Guid source, Guid profile)
    {
        var settings = RetrievalSettings.Default with { Recency = new RecencySettings(730, 0.15, ["pdf"]) };
        var demote = DemoteAdjuster.Compile("""[{"pattern":"www.umea.se/**/arkiv/**","factor":0.5},{"pattern":"www.umea.se/nyheter/**","factor":0.8}]""");
        return new SearchScope(profile, Dimensions, [new CollectionInScope(collection, settings)], ["public", "internal"], RetrievalPlatformSettings.Default)
        {
            Demote = new Dictionary<Guid, IReadOnlyList<DemotePattern>> { [source] = demote },
        };
    }

    public static float[] UnitVector(Random random, int dimensions = Dimensions)
    {
        var v = new float[dimensions];
        double norm = 0;
        for (var i = 0; i < dimensions; i++)
        {
            v[i] = (float)((random.NextDouble() * 2) - 1);
            norm += v[i] * v[i];
        }

        var scale = (float)(1 / Math.Sqrt(norm));
        for (var i = 0; i < dimensions; i++)
        {
            v[i] *= scale;
        }

        return v;
    }

    /// <summary>About <paramref name="words"/> words of Swedish municipal prose with compounds, as a crawled page reads.</summary>
    public static string SwedishText(int words, int seed = 3)
    {
        string[] vocabulary =
        [
            "bygglov", "ansökan", "handläggningstid", "kommunen", "förskoleplats", "avfallshantering", "detaljplan", "fastighetsägare",
            "du", "kan", "söka", "om", "det", "är", "för", "att", "och", "i", "på", "med", "en", "ett", "som", "inte", "måste", "behöver",
            "bostadsanpassningsbidrag", "äldreomsorg", "hemtjänst", "parkeringstillstånd", "vattenförbrukning", "byggnadsnämnden",
            "attefallshus", "tillbyggnad", "skolskjuts", "e-tjänsten", "blankett", "avgift", "kronor", "veckor", "Umeå", "kontakta",
            "kundtjänst", "telefon", "information", "beslut", "överklaga", "länsstyrelsen", "miljöförvaltningen", "livsmedelskontroll",
        ];
        var random = new Random(seed);
        var text = new StringBuilder();
        for (var i = 0; i < words; i++)
        {
            text.Append(vocabulary[random.Next(vocabulary.Length)]);
            text.Append(i % 14 == 13 ? ". " : " ");
        }

        return text.ToString().Trim();
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Kalla.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Kalla.slnx not found above the benchmark output directory.");
    }
}
