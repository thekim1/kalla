using System.Security.Cryptography;
using Ume.Rag.Domain;
using Ume.Rag.TestKit;
using Ume.Rag.Worker.Engine;
using Ume.Rag.Worker.Processing;
using Ume.Rag.Worker.Processing.Persistence;
using Ume.Rag.Worker.Tests.CommandJobs;

namespace Ume.Rag.Worker.Tests.Engine;

/// <summary>
/// #764 item 4: the chunk insert and the embedding cache send vectors as a binary <c>vector[]</c>, not as <c>'[…]'</c> text.
/// What is read back is the same floats, bit for bit, including values whose shortest text form needs nine digits.
/// </summary>
[Collection(RagDatabaseGroup1.Name)]
public sealed class VectorRoundTripTests(RagDatabaseFixture database) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await database.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Unit-length-like values plus the awkward ones: subnormal, extremes, negative zero, one ulp apart.</summary>
    private static float[] Vector(int seed)
    {
        var random = new Random(seed);
        var vector = new float[EngineKit.Dimensions];
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = (float)((random.NextDouble() * 2) - 1) / 32f;
        }

        vector[0] = float.Epsilon;
        vector[1] = float.MaxValue;
        vector[2] = float.MinValue;
        vector[3] = -0f;
        vector[4] = 0.1f;
        vector[5] = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(0.1f) + 1);
        vector[6] = 1.17549435E-38f;
        return vector;
    }

    private static void ShouldBeBitIdentical(float[] actual, float[] expected)
    {
        actual.Length.ShouldBe(expected.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            BitConverter.SingleToInt32Bits(actual[i]).ShouldBe(BitConverter.SingleToInt32Bits(expected[i]), $"element {i}");
        }
    }

    [Fact]
    public async Task A_chunk_written_by_the_index_writer_reads_back_with_the_same_floats()
    {
        var seed = await EngineKit.SeedUrlListAsync(database, [], Classification.Public);
        try
        {
            var document = await CommandSeeds.DocumentAsync(database, seed, $"https://site.test/{Guid.NewGuid():N}");
            var embeddedWith = (await new PostgresEmbeddingProfileStore(database.Database(RagDatabase.Worker)).GetAsync(EngineKit.ProfileId, Ct))!;
            var set = new ChunkSetRecord(Guid.CreateVersion7(), new ChunkSetKey(document, seed.VersionId, null, EngineKit.ProfileId, RandomNumberGenerator.GetBytes(32)),
                seed.Classification, seed.CollectionId, RandomNumberGenerator.GetBytes(32)) { ChunkCount = 2 };
            float[][] vectors = [Vector(1), Vector(2)];
            var chunks = vectors.Select((vector, i) =>
            {
                var text = $"Text {i} {Guid.NewGuid():N}";
                return new ChunkRecord(Guid.CreateVersion7(), set.Id, document, seed.CollectionId, seed.Classification, i, new ChunkDraft
                {
                    Ordinal = i, Prefix = "x", HeadingPath = "x", Text = text, SearchText = text, OverlapChars = 0, TextHash = ProcessingJson.Sha256Hex(text),
                    TokenCount = 4, DocumentType = "page",
                }, EngineKit.ProfileId, vector) { DeletedAt = DateTimeOffset.UtcNow };
            }).ToList();

            await new IndexWriter(database.Database(RagDatabase.Worker)).WriteInvisibleAsync(set, chunks, embeddedWith, Ct);

            var read = await new PostgresChunkSetStore(database.Database(RagDatabase.Worker)).ChunksAsync(set.Id, Ct);
            read.Count.ShouldBe(2);
            ShouldBeBitIdentical(read[0].Embedding, vectors[0]);
            ShouldBeBitIdentical(read[1].Embedding, vectors[1]);
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task An_embedding_cache_put_reads_back_with_the_same_floats()
    {
        var seed = await EngineKit.SeedUrlListAsync(database, [], Classification.Public);
        try
        {
            var cache = new PostgresEmbeddingCache(database.Database(RagDatabase.Worker));
            var embeddedWith = (await new PostgresEmbeddingProfileStore(database.Database(RagDatabase.Worker)).GetAsync(EngineKit.ProfileId, Ct))!;
            var vectors = Enumerable.Range(10, 3).ToDictionary(_ => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)), Vector);

            await cache.PutAsync(Classification.Public, embeddedWith, vectors, DateTimeOffset.UtcNow, Ct);

            var read = await cache.GetAsync(Classification.Public, EngineKit.ProfileId, vectors.Keys, Ct);
            read.Keys.ShouldBe(vectors.Keys, ignoreOrder: true);
            foreach (var (hash, vector) in vectors)
            {
                ShouldBeBitIdentical(read[hash], vector);
            }
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }
}
