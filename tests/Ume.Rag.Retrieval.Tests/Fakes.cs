using System.Security.Cryptography;
using System.Text;
using Ume.Rag.Domain.Retrieval;

namespace Ume.Rag.Retrieval.Tests;

/// <summary>Candidates and scopes for the pure search-core tests.</summary>
internal static class Make
{
    public static readonly Guid Profile = Guid.Parse("01929b6e-0000-7000-8000-00000000b6e3");
    public static readonly Guid CollectionA = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    public static readonly Guid CollectionB = Guid.Parse("00000000-0000-0000-0000-00000000000b");

    /// <summary>A chunk id whose uuid order is <paramref name="n"/>.</summary>
    public static Guid Id(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:x12}");

    public static Candidate Chunk(
        int id, double? distance = null, double? keyword = null, int document = 0, string? text = null, string? identity = null,
        Guid? collection = null) =>
        new(
            ChunkId: Id(id),
            DocumentId: Id(1000 + (document == 0 ? id : document)),
            ChunkSetId: Id(2000 + (document == 0 ? id : document)),
            Ordinal: id,
            TextHash: SHA256.HashData(Encoding.UTF8.GetBytes(text ?? "text " + id)),
            CollectionId: collection ?? CollectionA,
            Level: "public",
            DocumentType: "html",
            SourceModifiedAt: null,
            IdentityKind: "url",
            IdentityHash: SHA256.HashData(Encoding.UTF8.GetBytes(identity ?? "https://www.umea.se/doc/" + (document == 0 ? id : document))),
            SourceId: Id(3000),
            Url: "https://www.umea.se/doc/" + (document == 0 ? id : document),
            Distance: distance,
            KeywordScore: keyword);

    public static SearchScope Scope(params (Guid Collection, RetrievalSettings Settings)[] collections) =>
        Scope(RetrievalPlatformSettings.Default, collections);

    public static SearchScope Scope(RetrievalPlatformSettings platform, params (Guid Collection, RetrievalSettings Settings)[] collections) =>
        new(Profile, 3, [.. collections.Select(c => new CollectionInScope(c.Collection, c.Settings))], ["public"], platform);

    public static SearchScope OneCollection(RetrievalSettings? settings = null) => Scope((CollectionA, settings ?? RetrievalSettings.Default));
}

/// <summary>A search index answering from fixed lists, recording what it was asked.</summary>
internal sealed class FakeIndex : ISearchIndex, IScopeSizeSource
{
    public IReadOnlyList<Candidate> Keyword { get; set; } = [];

    public IReadOnlyList<Candidate> Vector { get; set; } = [];

    public IReadOnlyList<CollectionChunkCount> Counts { get; set; } = [];

    public List<string> KeywordQueries { get; } = [];

    /// <summary>The stop list each keyword call was given (<see cref="SearchScope.KeywordStopLexemes"/>).</summary>
    public List<IReadOnlyList<string>> KeywordStopLists { get; } = [];

    /// <summary>The frequent-lexeme rows; a thrown exception when <see cref="LexemeFailure"/> is set.</summary>
    public IReadOnlyList<FrequentLexeme> Lexemes { get; set; } = [];

    public Exception? LexemeFailure { get; set; }

    public int LexemeReads { get; private set; }

    /// <summary>When set, a lexeme read waits for it (to observe a background refresh).</summary>
    public TaskCompletionSource? HoldLexemes { get; set; }

    public List<VectorSearch> VectorSearches { get; } = [];

    public TaskCompletionSource? HoldKeyword { get; set; }

    public async Task<IReadOnlyList<Candidate>> KeywordAsync(SearchScope scope, string querySearchText, RetrievalFilters? filters, int limit, CancellationToken cancellationToken)
    {
        KeywordQueries.Add(querySearchText);
        KeywordStopLists.Add(scope.KeywordStopLexemes);
        if (HoldKeyword is not null)
        {
            await HoldKeyword.Task.WaitAsync(cancellationToken);
        }

        return Keyword;
    }

    public Task<IReadOnlyList<Candidate>> VectorAsync(SearchScope scope, ReadOnlyMemory<float> vector, RetrievalFilters? filters, int limit, VectorSearch search, CancellationToken cancellationToken)
    {
        VectorSearches.Add(search);
        return Task.FromResult(Vector);
    }

    public Task<IReadOnlyList<CollectionChunkCount>> CountChunksAsync(CancellationToken cancellationToken) => Task.FromResult(Counts);

    public async Task<IReadOnlyList<FrequentLexeme>> FrequentLexemesAsync(long minChunks, double minShare, CancellationToken cancellationToken)
    {
        LexemeReads++;
        if (HoldLexemes is not null)
        {
            await HoldLexemes.Task.WaitAsync(cancellationToken);
        }

        return LexemeFailure is null ? Lexemes : throw LexemeFailure;
    }
}

/// <summary>A reranker with fixed scores per chunk id, or one that fails.</summary>
internal sealed class FakeReranker(IReadOnlyDictionary<Guid, double>? scores, bool fail = false) : IReranker
{
    public int Calls { get; private set; }

    public Task<IReadOnlyList<double>?> ScoreAsync(string query, IReadOnlyList<RerankPassage> passages, CancellationToken cancellationToken)
    {
        Calls++;
        if (fail)
        {
            throw new HttpRequestException("rerank endpoint down");
        }

        return Task.FromResult<IReadOnlyList<double>?>([.. passages.Select(p => scores!.GetValueOrDefault(p.ChunkId))]);
    }
}

internal sealed class FakeTexts : IPassageTextReader
{
    public Task<IReadOnlyDictionary<Guid, string>> TextsAsync(SearchScope scope, IReadOnlyList<Guid> chunkIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(chunkIds.ToDictionary(id => id, id => "text " + id));
}
