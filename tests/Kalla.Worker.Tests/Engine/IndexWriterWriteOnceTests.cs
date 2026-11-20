using System.Security.Cryptography;
using Kalla.Domain;
using Kalla.TestKit;
using Kalla.Worker.Engine;
using Kalla.Worker.Processing;
using Kalla.Worker.Processing.Persistence;
using Kalla.Worker.Tests.CommandJobs;

namespace Kalla.Worker.Tests.Engine;

/// <summary>
/// A-25 (engine design §14 rule 2a, #501): a set of at most <see cref="IndexWriter.BatchSize"/> chunks is inserted inside the
/// swap transaction, as <c>live</c> or <c>held</c>, with its chunks written once (no update); only the previous set's chunks
/// are updated. A profile changed meanwhile writes nothing. A larger set keeps rule 2: written invisible in batches, then
/// swapped. A test trigger records every insert and update on <c>content."Chunks"</c> by set.
/// </summary>
[Collection(RagDatabaseGroup1.Name)]
public sealed class IndexWriterWriteOnceTests(RagDatabaseFixture database) : IAsyncLifetime
{
    private const string InstallRecorder = """
        CREATE TABLE IF NOT EXISTS public.chunk_writes (set_id uuid, op text);
        TRUNCATE public.chunk_writes;
        CREATE OR REPLACE FUNCTION public.record_chunk_write() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER AS $$
        BEGIN INSERT INTO public.chunk_writes VALUES (NEW."ChunkSetId", TG_OP); RETURN NULL; END $$;
        DROP TRIGGER IF EXISTS record_chunk_write ON content."Chunks";
        CREATE TRIGGER record_chunk_write AFTER INSERT OR UPDATE ON content."Chunks" FOR EACH ROW EXECUTE FUNCTION public.record_chunk_write();
        """;

    private const string RemoveRecorder = """
        DROP TRIGGER IF EXISTS record_chunk_write ON content."Chunks";
        DROP FUNCTION IF EXISTS public.record_chunk_write();
        DROP TABLE IF EXISTS public.chunk_writes;
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await database.ResetAsync();
        await Sql.ExecuteAsync(database.Connection(null), InstallRecorder);
    }

    public async ValueTask DisposeAsync() => await Sql.ExecuteAsync(database.Connection(null), RemoveRecorder);

    [Fact]
    public async Task A_set_of_at_most_1000_chunks_is_written_once_in_the_swap_and_only_the_previous_set_is_updated()
    {
        var seed = await EngineKit.SeedUrlListAsync(database, [], Classification.Public);
        try
        {
            var document = await CommandSeeds.DocumentAsync(database, seed, $"https://site.test/{Guid.NewGuid():N}");
            var writer = new IndexWriter(database.Database(RagDatabase.Worker));
            var profile = (await new PostgresEmbeddingProfileStore(database.Database(RagDatabase.Worker)).GetAsync(EngineKit.ProfileId, Ct))!;

            var (first, firstChunks) = Set(seed, document, 3);
            (await WriteAndSwapAsync(writer, first, ChunkSetState.Live, firstChunks, profile)).ShouldBeTrue();
            var (second, secondChunks) = Set(seed, document, 4);
            (await WriteAndSwapAsync(writer, second, ChunkSetState.Live, secondChunks, profile)).ShouldBeTrue();

            // Each chunk of the new set inserted once and never updated; the previous set's chunks updated once (hidden).
            (await WritesAsync(second.Id)).ShouldBe(("INSERT", 4L, "UPDATE", 0L));
            (await WritesAsync(first.Id)).ShouldBe(("INSERT", 3L, "UPDATE", 3L));
            (await Scalar<long>("""SELECT count(*) FROM content."Chunks" WHERE "ChunkSetId" = $1 AND "DeletedAt" IS NULL""", second.Id)).ShouldBe(4);
            (await Scalar<long>("""SELECT count(*) FROM content."Chunks" WHERE "ChunkSetId" = $1 AND "DeletedAt" IS NULL""", first.Id)).ShouldBe(0);
            (await Scalar<string>("""SELECT "State" || ',' || ("LiveAt" IS NOT NULL) FROM content."ChunkSets" WHERE "Id" = $1""", second.Id)).ShouldBe("live,true");
            (await Scalar<string>("""SELECT "State" || ',' || ("SupersededAt" IS NOT NULL) FROM content."ChunkSets" WHERE "Id" = $1""", first.Id))
                .ShouldBe("superseded,true");
            (await Scalar<Guid>("""SELECT "LiveChunkSetId" FROM content."Documents" WHERE "Id" = $1""", document)).ShouldBe(second.Id);

            // Held (personal data in a public collection): the chunks go in deleted, still once.
            var (held, heldChunks) = Set(seed, document, 2);
            (await WriteAndSwapAsync(writer, held, ChunkSetState.Held, heldChunks, profile)).ShouldBeTrue();
            (await WritesAsync(held.Id)).ShouldBe(("INSERT", 2L, "UPDATE", 0L));
            (await Scalar<long>("""SELECT count(*) FROM content."Chunks" WHERE "ChunkSetId" = $1 AND "DeletedAt" IS NOT NULL""", held.Id)).ShouldBe(2);
            (await Scalar<string>("""SELECT "State" || ',' || ("LiveAt" IS NULL) FROM content."ChunkSets" WHERE "Id" = $1""", held.Id)).ShouldBe("held,true");

            // More than one batch is rule 2's: refused here.
            var (large, largeChunks) = Set(seed, document, IndexWriter.BatchSize + 1);
            await Should.ThrowAsync<ArgumentException>(() => WriteAndSwapAsync(writer, large, ChunkSetState.Live, largeChunks, profile));
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task A_profile_changed_before_the_swap_writes_nothing_and_leaves_the_live_set()
    {
        var seed = await EngineKit.SeedUrlListAsync(database, [], Classification.Public);
        var model = await Scalar<string>("""SELECT "GatewayModel" FROM config."EmbeddingProfiles" WHERE "Id" = $1""", EngineKit.ProfileId);
        try
        {
            var document = await CommandSeeds.DocumentAsync(database, seed, $"https://site.test/{Guid.NewGuid():N}");
            var writer = new IndexWriter(database.Database(RagDatabase.Worker));
            var profile = (await new PostgresEmbeddingProfileStore(database.Database(RagDatabase.Worker)).GetAsync(EngineKit.ProfileId, Ct))!;
            var (live, liveChunks) = Set(seed, document, 2);
            (await WriteAndSwapAsync(writer, live, ChunkSetState.Live, liveChunks, profile)).ShouldBeTrue();

            await Sql.ExecuteAsync(database.Connection(null), """UPDATE config."EmbeddingProfiles" SET "GatewayModel" = 'another-model' WHERE "Id" = $1""",
                EngineKit.ProfileId);
            var (refused, refusedChunks) = Set(seed, document, 2);
            (await WriteAndSwapAsync(writer, refused, ChunkSetState.Live, refusedChunks, profile)).ShouldBeFalse();

            (await Scalar<long>("""SELECT count(*) FROM content."ChunkSets" WHERE "Id" = $1""", refused.Id)).ShouldBe(0);
            (await Scalar<long>("""SELECT count(*) FROM public.chunk_writes WHERE set_id = $1""", refused.Id)).ShouldBe(0);
            (await Scalar<Guid>("""SELECT "LiveChunkSetId" FROM content."Documents" WHERE "Id" = $1""", document)).ShouldBe(live.Id);
            (await Scalar<long>("""SELECT count(*) FROM content."Chunks" WHERE "ChunkSetId" = $1 AND "DeletedAt" IS NULL""", live.Id)).ShouldBe(2);
        }
        finally
        {
            await Sql.ExecuteAsync(database.Connection(null), """UPDATE config."EmbeddingProfiles" SET "GatewayModel" = $2 WHERE "Id" = $1""",
                EngineKit.ProfileId, model);
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task A_set_of_more_than_1000_chunks_is_written_invisible_in_batches_then_swapped()
    {
        var seed = await EngineKit.SeedUrlListAsync(database, [], Classification.Public);
        try
        {
            var document = await CommandSeeds.DocumentAsync(database, seed, $"https://site.test/{Guid.NewGuid():N}");
            var writer = new IndexWriter(database.Database(RagDatabase.Worker));
            var profile = (await new PostgresEmbeddingProfileStore(database.Database(RagDatabase.Worker)).GetAsync(EngineKit.ProfileId, Ct))!;
            var (set, chunks) = Set(seed, document, IndexWriter.BatchSize + 1);

            await writer.WriteInvisibleAsync(set, chunks, profile, Ct);
            (await Scalar<long>("""SELECT count(*) FROM content."Chunks" WHERE "ChunkSetId" = $1 AND "DeletedAt" IS NOT NULL""", set.Id))
                .ShouldBe(IndexWriter.BatchSize + 1);
            await using (var connection = await database.Database(RagDatabase.Worker).OpenAsync(Ct))
            await using (var transaction = await connection.BeginTransactionAsync(Ct))
            {
                await IndexWriter.SwapInAsync(connection, transaction, document, set.Id, ChunkSetState.Live, set.Key.PipelineVersionId, DateTimeOffset.UtcNow, Ct);
                await transaction.CommitAsync(Ct);
            }

            (await Scalar<long>("""SELECT count(*) FROM content."Chunks" WHERE "ChunkSetId" = $1 AND "DeletedAt" IS NULL""", set.Id))
                .ShouldBe(IndexWriter.BatchSize + 1);
            (await WritesAsync(set.Id)).ShouldBe(("INSERT", IndexWriter.BatchSize + 1L, "UPDATE", IndexWriter.BatchSize + 1L));
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    /// <summary>
    /// The done-when measurement (#501): a re-crawl that changes every one of 200 documents (<c>UME_RAG_MEASURE_DOCUMENTS</c>)
    /// of 20 chunks, alternately through rule 2 (invisible, then swapped) and rule 2a, twice each. WAL bytes are cluster-wide, so
    /// run it alone: <c>dotnet test --project tests/Kalla.Worker.Tests -- --filter-method "*Rewrite_cost*" --explicit only
    /// --output Detailed</c>.
    /// </summary>
    [Fact(Explicit = true)]
    public async Task Rewrite_cost_of_a_recrawl_through_rule_2_and_rule_2a()
    {
        var documents = int.TryParse(Environment.GetEnvironmentVariable("UME_RAG_MEASURE_DOCUMENTS"), out var n) ? n : 200;
        const int chunksPerDocument = 20;
        var seed = await EngineKit.SeedUrlListAsync(database, [], Classification.Public);
        var output = TestContext.Current.TestOutputHelper!;
        try
        {
            var writer = new IndexWriter(database.Database(RagDatabase.Worker));
            var profile = (await new PostgresEmbeddingProfileStore(database.Database(RagDatabase.Worker)).GetAsync(EngineKit.ProfileId, Ct))!;
            var ids = new List<Guid>();
            for (var i = 0; i < documents; i++)
            {
                var document = await CommandSeeds.DocumentAsync(database, seed, $"https://site.test/{Guid.NewGuid():N}");
                ids.Add(document);
                var (set, chunks) = Set(seed, document, chunksPerDocument);
                (await WriteAndSwapAsync(writer, set, ChunkSetState.Live, chunks, profile)).ShouldBeTrue();
            }

            foreach (var (name, twoPhase) in new[] { ("rule 2 (invisible, then swap)", true), ("rule 2a (in the swap)", false), ("rule 2 (invisible, then swap)", true), ("rule 2a (in the swap)", false) })
            {
                await Sql.ExecuteAsync(database.Connection(null), "TRUNCATE public.chunk_writes");
                var before = await Scalar<string>("SELECT pg_current_wal_lsn()::text");
                foreach (var document in ids)
                {
                    var (set, chunks) = Set(seed, document, chunksPerDocument);
                    if (twoPhase)
                    {
                        await writer.WriteInvisibleAsync(set, chunks, profile, Ct);
                        await using var connection = await database.Database(RagDatabase.Worker).OpenAsync(Ct);
                        await using var transaction = await connection.BeginTransactionAsync(Ct);
                        await IndexWriter.SwapInAsync(connection, transaction, document, set.Id, ChunkSetState.Live, set.Key.PipelineVersionId, DateTimeOffset.UtcNow, Ct);
                        await transaction.CommitAsync(Ct);
                    }
                    else
                    {
                        (await WriteAndSwapAsync(writer, set, ChunkSetState.Live, chunks, profile)).ShouldBeTrue();
                    }
                }

                var wal = await Scalar<decimal>("SELECT pg_wal_lsn_diff(pg_current_wal_lsn(), $1::pg_lsn)", before);
                var updates = await Scalar<long>("SELECT count(*) FROM public.chunk_writes WHERE op = 'UPDATE'");
                var inserts = await Scalar<long>("SELECT count(*) FROM public.chunk_writes WHERE op = 'INSERT'");
                output.WriteLine($"{name}: {documents} documents x {chunksPerDocument} chunks: WAL {wal / 1024 / 1024:F1} MiB, chunk inserts {inserts}, " +
                    $"chunk updates (dead tuples) {updates}");
            }
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    private async Task<bool> WriteAndSwapAsync(IndexWriter writer, ChunkSetRecord set, string state, IReadOnlyList<ChunkRecord> chunks,
        EmbeddingProfileRecord profile)
    {
        await using var connection = await database.Database(RagDatabase.Worker).OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        if (!await writer.WriteAndSwapInAsync(connection, transaction, set, state, chunks, profile, DateTimeOffset.UtcNow, Ct))
        {
            await transaction.RollbackAsync(Ct);
            return false;
        }

        await transaction.CommitAsync(Ct);
        writer.Committed(profile.Id);
        return true;
    }

    private async Task<(string, long, string, long)> WritesAsync(Guid setId) =>
        ("INSERT", await Scalar<long>("SELECT count(*) FROM public.chunk_writes WHERE set_id = $1 AND op = 'INSERT'", setId),
         "UPDATE", await Scalar<long>("SELECT count(*) FROM public.chunk_writes WHERE set_id = $1 AND op = 'UPDATE'", setId));

    private Task<T> Scalar<T>(string sql, params object[] parameters) => Sql.ScalarAsync<T>(database.Connection(null), sql, parameters);

    private static (ChunkSetRecord Set, IReadOnlyList<ChunkRecord> Chunks) Set(SourceSeed seed, Guid document, int count)
    {
        var set = new ChunkSetRecord(Guid.CreateVersion7(), new ChunkSetKey(document, seed.VersionId, null, EngineKit.ProfileId, RandomNumberGenerator.GetBytes(32)),
            seed.Classification, seed.CollectionId, RandomNumberGenerator.GetBytes(32)) { ChunkCount = count };
        var chunks = Enumerable.Range(0, count).Select(i =>
        {
            var text = $"Text {i} {Guid.NewGuid():N}";
            return new ChunkRecord(Guid.CreateVersion7(), set.Id, document, seed.CollectionId, seed.Classification, i, new ChunkDraft
            {
                Ordinal = i, Prefix = "x", HeadingPath = "x", Text = text, SearchText = text, OverlapChars = 0, TextHash = ProcessingJson.Sha256Hex(text),
                TokenCount = 4, DocumentType = "page",
            }, EngineKit.ProfileId, Enumerable.Repeat(0.5f, EngineKit.Dimensions).ToArray()) { DeletedAt = DateTimeOffset.UtcNow };
        }).ToList();
        return (set, chunks);
    }
}
