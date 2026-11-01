using System.Text.Json;

namespace Ume.Rag.TestKit;

/// <summary>One question of the smoke set: the documents (file names) a correct answer cites.</summary>
public sealed record SmokeQuestion(string Id, string Question, IReadOnlyList<string> Expected);

/// <summary>The seeded collection: the document id of each file name.</summary>
public sealed record SmokeSeed(Guid CollectionId, Guid SourceId, IReadOnlyDictionary<string, Guid> Documents);

/// <summary>
/// The evaluation smoke set (<c>tests/evaluation/smoke</c>; P1 item 17, knowledge design §16.4): 13 fictional Markdown
/// pages and 30 questions. <see cref="SeedAsync"/> writes the pages as one public upload collection through
/// <see cref="ContentFixtureWriter"/>, without worker or parser: one chunk per paragraph, heading path
/// <c>Title › Section</c>, the writer's embedding function (the FakeGateway's in tests). The evaluation-smoke CI job
/// ingests the same files through the pipeline instead.
/// </summary>
public static class SmokeSet
{
    /// <summary>Fixed ids, so the evaluator and the API can be pointed at the same collection.</summary>
    public static readonly Guid CollectionId = Guid.Parse("01929b6e-0000-7000-8000-00000005e0e0");

    public static readonly Guid SourceId = Guid.Parse("01929b6e-0000-7000-8000-00000005e0e1");

    /// <summary>
    /// The collection's <c>RetrievalSettings</c> for FakeGateway embeddings: feature hashing puts a question's best chunk at
    /// a cosine distance of 0.33–0.78 on this set (the platform default <c>coverageMaxDistance</c> 0.50 is BGE-M3's), so the
    /// coverage threshold is calibrated for this collection (knowledge design §7) and every question is answered.
    /// </summary>
    public const string RetrievalSettings = """{"coverageMaxDistance":0.85}""";

    public static string Directory => Path.GetDirectoryName(TestPostgres.RepositoryFile(Path.Combine("tests", "evaluation", "smoke", "questions.json")))!;

    public static IReadOnlyList<SmokeQuestion> Questions()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(Directory, "questions.json")));
        return [.. json.RootElement.GetProperty("questions").EnumerateArray().Select(q => new SmokeQuestion(
            q.GetProperty("id").GetString()!, q.GetProperty("question").GetString()!,
            [.. q.GetProperty("expected").EnumerateArray().Select(e => e.GetString()!)]))];
    }

    /// <summary>The pages by file name, in name order.</summary>
    public static IReadOnlyList<(string FileName, string Markdown)> Documents() =>
        [.. System.IO.Directory.GetFiles(Path.Combine(Directory, "documents"), "*.md").Order(StringComparer.Ordinal)
            .Select(path => (Path.GetFileName(path), File.ReadAllText(path)))];

    /// <summary>
    /// Writes the smoke collection's documents (upload documents named by their file, <c>text/markdown</c>, level
    /// <paramref name="classification"/>) with <paramref name="embeddingProfileId"/>. The collection and source exist in
    /// <c>content</c> only; a test puts them into its snapshot under <see cref="CollectionId"/> and <see cref="SourceId"/>.
    /// </summary>
    public static async Task<SmokeSeed> SeedAsync(ContentFixtureWriter writer, Guid embeddingProfileId, string classification = "public",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var (fileName, markdown) in Documents())
        {
            var (title, chunks) = Chunk(markdown);
            var document = await writer.AddDocumentAsync(new FixtureDocumentSpec
            {
                CollectionId = CollectionId, EmbeddingProfileId = embeddingProfileId, SourceId = SourceId, Classification = classification,
                IdentityKind = "upload", CanonicalIdentity = "upload:smoke/" + fileName, Title = title, DisplayName = fileName, MediaType = "text/markdown",
                SourceModifiedAt = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
            }, chunks, cancellationToken);
            ids[fileName] = document.Id;
        }

        return new SmokeSeed(CollectionId, SourceId, ids);
    }

    /// <summary>The page's <c># </c> title and one chunk per paragraph under its <c>## </c> section.</summary>
    public static (string Title, IReadOnlyList<FixtureChunk> Chunks) Chunk(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        string? title = null;
        string? section = null;
        var chunks = new List<FixtureChunk>();
        foreach (var block in markdown.ReplaceLineEndings("\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (block.StartsWith("# ", StringComparison.Ordinal))
            {
                title = block[2..].Trim();
            }
            else if (block.StartsWith("## ", StringComparison.Ordinal))
            {
                section = block[3..].Trim();
            }
            else
            {
                var text = string.Join(' ', block.Split('\n', StringSplitOptions.TrimEntries));
                chunks.Add(new FixtureChunk(text)
                {
                    HeadingPath = section is null ? title : $"{title} › {section}",
                    TokenCount = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length * 2,
                });
            }
        }

        return (title ?? throw new InvalidDataException("A smoke page starts with a '# ' title."), chunks);
    }
}
