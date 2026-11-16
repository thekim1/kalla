using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Pgvector;
using Ume.Rag.Answering.Chat;
using Ume.Rag.Api;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Domain.Retrieval;
using Ume.Rag.Infrastructure.Access;
using Ume.Rag.Infrastructure.Config;
using Ume.Rag.Infrastructure.Jobs;
using Ume.Rag.Retrieval;
using Ume.Rag.Retrieval.Postgres;
using Ume.Rag.TestKit;

namespace Ume.Rag.Benchmarks.Measurements;

/// <summary>
/// One-off measurements of the hot-path findings of 2026-11-15, current code against a candidate where the candidate fits in
/// the harness: <c>dotnet run -c Release --project tests/Ume.Rag.Benchmarks -- measure slots|vector|jobs|vectortext|hydrate|stream|vectorinsert|snapshot</c>.
/// Stopwatch medians, not BenchmarkDotNet: several count outcomes (503s, recall, rows) as well as time. The Postgres ones need Docker.
/// </summary>
public static class HotPathMeasurements
{
    public static async Task<int> RunAsync(string[] args)
    {
        var which = args.Length > 0 ? args[0] : "all";
        if (which is "slots" or "all") { await SlotsAsync(); }
        if (which is "vectortext" or "all") { VectorText(); }
        if (which is "stream" or "all") { await StreamAsync(); }
        if (which is "vector" or "all") { await VectorAsync(); }
        if (which is "hydrate" or "all") { await HydrateAsync(); }
        if (which is "jobs" or "all") { await JobsAsync(); }
        if (which is "vectorinsert" or "all") { await VectorInsertAsync(); }
        if (which is "snapshot" or "all") { await SnapshotAsync(); }
        return 0;
    }

    // ---------------------------------------------------------------- 1: search slots

    /// <summary>Closed loop: <c>clients</c> callers search for 10 s; statements take 5 ms, the embedding and the rerank call their delays.</summary>
    private static async Task SlotsAsync()
    {
        Console.WriteLine("## 1 Search slots (RetrievalEngine, in-memory index: K and V 5 ms each, MaxConcurrentSearches 4, SlotWait 200 ms)");
        Console.WriteLine("| embed ms | rerank ms | clients | ok/s | 503 search_unavailable | ok p50 ms | ok p95 ms | peak statements at once |");
        Console.WriteLine("|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var (embedMs, rerankMs) in new[] { (100, 0), (100, 800), (300, 0) })
        {
            foreach (var clients in new[] { 4, 8, 16, 32 })
            {
                var collection = Guid.NewGuid();
                var source = Guid.NewGuid();
                var profile = Guid.NewGuid();
                var (vector, keyword) = BenchData.CandidateLists(100, 0.4, collection, source);
                var index = new DelayedIndex(vector, keyword, profile, collection, TimeSpan.FromMilliseconds(5));
                using var sizes = new ScopeSizeCache(index, TimeProvider.System);
                using var engine = new RetrievalEngine(index, sizes, reranker: new DelayedReranker(TimeSpan.FromMilliseconds(rerankMs)), texts: index);
                var scope = new SearchScope(profile, BenchData.Dimensions, [new CollectionInScope(collection, RetrievalSettings.Default)], ["public"],
                    RetrievalPlatformSettings.Default with { RerankModel = "rerank" });
                var query = BenchData.UnitVector(new Random(1));
                var request = new SearchRequest("Hur ansöker jag om bygglov?", scope,
                    Embed: async (_, ct) => { await Task.Delay(embedMs, ct); return query; },
                    Rerank: rerankMs > 0 ? new RerankCall(Residency.Eu) : null);
                await engine.SearchAsync(request, CancellationToken.None); // warm
                var duration = TimeSpan.FromSeconds(10);
                var latencies = new System.Collections.Concurrent.ConcurrentBag<double>();
                var refused = 0;
                var clock = Stopwatch.StartNew();
                await Task.WhenAll(Enumerable.Range(0, clients).Select(async _ =>
                {
                    while (clock.Elapsed < duration)
                    {
                        var started = Stopwatch.GetTimestamp();
                        try
                        {
                            await engine.SearchAsync(request, CancellationToken.None);
                            latencies.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                        }
                        catch (SearchUnavailableException)
                        {
                            Interlocked.Increment(ref refused);
                            await Task.Delay(50); // a client that retries shortly
                        }
                    }
                }));
                var sorted = latencies.Order().ToArray();
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"| {embedMs} | {rerankMs} | {clients} | {sorted.Length / clock.Elapsed.TotalSeconds:F1} | {refused} | {Pct(sorted, 0.5):F0} | {Pct(sorted, 0.95):F0} | {index.Peak} |"));
            }
        }
    }

    private sealed class DelayedIndex(List<Candidate> vector, List<Candidate> keyword, Guid profile, Guid collection, TimeSpan statement)
        : ISearchIndex, IScopeSizeSource, IPassageTextReader
    {
        public int Peak;
        private int _running;

        private async Task<IReadOnlyList<Candidate>> RunAsync(IReadOnlyList<Candidate> rows, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref _running);
            int peak;
            while (now > (peak = Volatile.Read(ref Peak)) && Interlocked.CompareExchange(ref Peak, now, peak) != peak)
            {
            }

            try
            {
                await Task.Delay(statement, ct);
                return rows;
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }

        public Task<IReadOnlyList<Candidate>> KeywordAsync(SearchScope scope, string querySearchText, RetrievalFilters? filters, int limit, CancellationToken cancellationToken) =>
            RunAsync(keyword, cancellationToken);

        public Task<IReadOnlyList<Candidate>> VectorAsync(SearchScope scope, ReadOnlyMemory<float> vector1, RetrievalFilters? filters, int limit, VectorSearch search,
            CancellationToken cancellationToken) => RunAsync(vector, cancellationToken);

        public Task<IReadOnlyList<CollectionChunkCount>> CountChunksAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CollectionChunkCount>>([new CollectionChunkCount(profile, collection, 50_000)]);

        public Task<IReadOnlyList<FrequentLexeme>> KeywordStatisticsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FrequentLexeme>>([]);

        public Task<IReadOnlyDictionary<Guid, string>> TextsAsync(SearchScope scope, IReadOnlyList<Guid> chunkIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(chunkIds.ToDictionary(id => id, _ => "text"));
    }

    private sealed class DelayedReranker(TimeSpan delay) : IReranker
    {
        public async Task<RerankScores?> ScoreAsync(RerankRequest request, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return new RerankScores([.. request.Passages.Select((_, i) => (double?)(1.0 - (i * 0.01)))]);
        }
    }

    // ---------------------------------------------------------------- 4: vectors as text

    private static void VectorText()
    {
        Console.WriteLine("## 4 Vector text (1,000 vectors of 1,024 floats, as one chunk-set insert formats them)");
        var random = new Random(5);
        var vectors = Enumerable.Range(0, 1000).Select(_ => BenchData.UnitVector(random)).ToArray();
        Console.WriteLine("| variant | ms per 1,000 vectors | allocated MB | text MB |");
        Console.WriteLine("|---|---:|---:|---:|");
        foreach (var (name, format) in new (string, Func<float[], string>)[] { ("current: ToString(\"R\") per float", FormatCurrent), ("candidate: span-formatted append", FormatCandidate) })
        {
            if (vectors.Any(v => FormatCurrent(v) != format(v)))
            {
                throw new InvalidOperationException($"{name} formats differently");
            }

            var times = new List<double>();
            long allocated = 0;
            long chars = 0;
            for (var run = 0; run < 25; run++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                var started = Stopwatch.GetTimestamp();
                chars = 0;
                foreach (var v in vectors)
                {
                    chars += format(v).Length;
                }

                times.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            }

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"| {name} | {Median(times.Skip(5)):F1} | {allocated / 1e6:F1} | {chars / 1e6:F1} |"));
        }
    }

    /// <summary><c>VectorText.Format</c> of the worker (PostgresProcessingStores.cs), verbatim.</summary>
    private static string FormatCurrent(float[] vector)
    {
        var builder = new StringBuilder(vector.Length * 10 + 2).Append('[');
        for (var i = 0; i < vector.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(vector[i].ToString("R", CultureInfo.InvariantCulture));
        }

        return builder.Append(']').ToString();
    }

    private static string FormatCandidate(float[] vector)
    {
        var builder = new StringBuilder(vector.Length * 13 + 2).Append('[');
        for (var i = 0; i < vector.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(CultureInfo.InvariantCulture, $"{vector[i]:R}");
        }

        return builder.Append(']').ToString();
    }

    // ---------------------------------------------------------------- 6: stream flushes and the user JWT

    private static async Task StreamAsync()
    {
        Console.WriteLine("## 6a Flush per token (Kestrel on loopback, 400 content chunks per answer, 200 answers, 8 at a time)");
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapGet("/{mode}", async (string mode, HttpContext http) =>
        {
            http.Response.ContentType = "text/event-stream";
            await http.Response.StartAsync();
            using var chunk = new SseChunk();
            for (var i = 0; i < 400; i++)
            {
                await http.Response.Body.WriteAsync(chunk.Content("chatcmpl-0123456789abcdef0123456789abcdef", 1_763_000_000, "turkos", " ord"));
                if (mode == "each" || (mode == "8" && i % 8 == 7))
                {
                    await http.Response.Body.FlushAsync();
                }
            }
        });
        await app.StartAsync();
        var address = app.Urls.First();
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        Console.WriteLine("| variant | wall ms per answer | process CPU ms per answer |");
        Console.WriteLine("|---|---:|---:|");
        foreach (var (name, mode) in new[] { ("current: flush after every chunk", "each"), ("flush every 8 chunks", "8"), ("one flush at the end (lower bound)", "end") })
        {
            async Task RoundAsync(int answers)
            {
                using var gate = new SemaphoreSlim(8);
                await Task.WhenAll(Enumerable.Range(0, answers).Select(async _ =>
                {
                    await gate.WaitAsync();
                    try
                    {
                        using var response = await client.GetAsync(new Uri("/" + mode, UriKind.Relative), HttpCompletionOption.ResponseHeadersRead);
                        await using var body = await response.Content.ReadAsStreamAsync();
                        var buffer = new byte[4096];
                        while (await body.ReadAsync(buffer) > 0)
                        {
                        }
                    }
                    finally
                    {
                        gate.Release();
                    }
                }));
            }

            await RoundAsync(50);
            var cpu = Process.GetCurrentProcess().TotalProcessorTime;
            var wall = Stopwatch.StartNew();
            await RoundAsync(200);
            wall.Stop();
            var used = Process.GetCurrentProcess().TotalProcessorTime - cpu;
            // Server and client share the process, so the CPU is both sides': an upper bound for the server's share.
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"| {name} | {wall.Elapsed.TotalMilliseconds * 8 / 200:F2} | {used.TotalMilliseconds / 200:F2} |"));
        }

        await app.StopAsync();

        Console.WriteLine();
        Console.WriteLine("## 6b Open WebUI user JWT per request");
        var secret = new string('s', 48);
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var handler = new JsonWebTokenHandler();
        var now = DateTimeOffset.UtcNow;
        var token = handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "open-webui",
            IssuedAt = now.UtcDateTime, NotBefore = now.UtcDateTime, Expires = now.UtcDateTime.AddMinutes(5),
            Claims = new Dictionary<string, object> { ["sub"] = "user-1", ["name"] = "Test", ["email"] = "t@example.org", ["role"] = "user" },
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
        });
        var options = new OpenWebUiOptions { JwtSecret = secret };
        var http = new DefaultHttpContext();
        http.Request.Headers[OpenWebUiUserJwt.Header] = token;
        var (user, refusal) = await OpenWebUiUserJwt.ValidateAsync(http.Request, options, now);
        if (user is null)
        {
            throw new InvalidOperationException("The measured token is refused: " + refusal);
        }

        var cached = new TokenValidationParameters
        {
            IssuerSigningKey = key, ValidAlgorithms = [SecurityAlgorithms.HmacSha256], ValidIssuer = "open-webui", ValidateAudience = false, ValidateLifetime = false,
        };
        Console.WriteLine("| variant | µs per validation | allocated B |");
        Console.WriteLine("|---|---:|---:|");
        foreach (var (name, run) in new (string, Func<Task>)[]
        {
            ("current: OpenWebUiUserJwt.ValidateAsync (new parameters and key per request)", async () => await OpenWebUiUserJwt.ValidateAsync(http.Request, options, now)),
            ("candidate: the same validation with parameters and key built once", async () => await handler.ValidateTokenAsync(token, cached)),
        })
        {
            for (var i = 0; i < 2000; i++)
            {
                await run();
            }

            const int n = 20000;
            var before = GC.GetAllocatedBytesForCurrentThread();
            var started = Stopwatch.GetTimestamp();
            for (var i = 0; i < n; i++)
            {
                await run();
            }

            var elapsed = Stopwatch.GetElapsedTime(started);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"| {name} | {elapsed.TotalMicroseconds / n:F1} | {allocated / n} |"));
        }
    }

    // ---------------------------------------------------------------- shared seed

    private sealed record Seeded(RagDatabase Database, NpgsqlDataSource Api, PostgresSearchIndex Index, PostgresHydrator Hydrator, Guid Profile, Guid Collection,
        Guid[] Sources, float[][] Centroids);

    private const int Topics = 200;

    private static async Task<Seeded> SeedAsync(int chunks, int chunksPerDocument, int wordsPerChunk, int sectionLength)
    {
        var database = new RagDatabase();
        await database.InitializeAsync();
        await using var writer = new ContentFixtureWriter(database.Connection(RagDatabase.Worker), database.Connection(RagDatabase.Admin));
        var profile = await writer.AddEmbeddingProfileAsync(BenchData.Dimensions);
        var collection = Guid.NewGuid();
        Guid[] sources = [.. Enumerable.Range(0, 10).Select(_ => Guid.NewGuid())];
        var random = new Random(23);
        var centroids = Enumerable.Range(0, Topics).Select(_ => BenchData.UnitVector(random)).ToArray();
        for (var d = 0; d < chunks / chunksPerDocument; d++)
        {
            var topic = d % Topics;
            await writer.AddDocumentAsync(
                new FixtureDocumentSpec
                {
                    CollectionId = collection, EmbeddingProfileId = profile, SourceId = sources[d % sources.Length], Url = $"https://www.umea.se/omrade{d % 37}/sida-{d}",
                    Title = $"Sida {d}", SourceModifiedAt = DateTimeOffset.UtcNow.AddDays(-(d % 900)),
                },
                [.. Enumerable.Range(0, chunksPerDocument).Select(i =>
                {
                    var text = $"ämne{topic} ämne{(topic * 7) % Topics} ord{random.Next(2000)} ord{random.Next(2000)} ord{random.Next(2000)} " +
                        BenchData.SwedishText(wordsPerChunk, seed: (d * 31) + i);
                    return new FixtureChunk(text)
                    {
                        SearchText = text, Embedding = Near(centroids[topic], random, 0.25f), TokenCount = (int)(wordsPerChunk * 1.9),
                        HeadingPath = $"Sida {d} › Avsnitt {i / sectionLength}",
                    };
                })]);
        }

        await writer.RefreshKeywordStatisticsAsync(collection);
        await using (var superuser = new NpgsqlConnection(database.Connection(null)))
        {
            await superuser.OpenAsync();
            foreach (var table in new[] { "Chunks", "Documents" })
            {
                await using var analyze = new NpgsqlCommand($"""VACUUM ANALYZE content."{table}" """, superuser);
                await analyze.ExecuteNonQueryAsync();
            }
        }

        var api = RetrievalDataSource.Create(database.Connection(RagDatabase.Api));
        return new Seeded(database, api, new PostgresSearchIndex(api), new PostgresHydrator(api), profile, collection, sources, centroids);
    }

    private static float[] Near(float[] centroid, Random random, float noise)
    {
        var v = new float[centroid.Length];
        double norm = 0;
        for (var i = 0; i < v.Length; i++)
        {
            v[i] = centroid[i] + (noise * (float)((random.NextDouble() * 2) - 1) / MathF.Sqrt(v.Length / 16f));
            norm += v[i] * v[i];
        }

        var scale = (float)(1 / Math.Sqrt(norm));
        for (var i = 0; i < v.Length; i++)
        {
            v[i] *= scale;
        }

        return v;
    }

    // ---------------------------------------------------------------- 2: exact vector scan

    private static async Task VectorAsync()
    {
        Console.WriteLine("## 2 Statement V on a scope under ExactScanMaxChunks (one collection = the whole profile, limit 100, 40 queries)");
        Console.WriteLine("| chunks | V exact (current) ms | V exact on halfvec (candidate) ms | V HNSW (candidate: lower exactScanMaxChunks) ms | HNSW recall@10 | HNSW recall@100 | halfvec-exact recall@10 |");
        Console.WriteLine("|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var chunks in new[] { 2_000, 5_000, 10_000 })
        {
            var db = await SeedAsync(chunks, 10, 120, 10);
            var scope = new SearchScope(db.Profile, BenchData.Dimensions, [new CollectionInScope(db.Collection, RetrievalSettings.Default)], ["public"],
                RetrievalPlatformSettings.Default);
            var random = new Random(99);
            var queries = Enumerable.Range(0, 40).Select(i => Near(db.Centroids[(i * 7) % Topics], random, 0.15f)).ToArray();
            var halfSql = RetrievalStatements.VectorExact(db.Profile, BenchData.Dimensions, 100, FilterFragments.None)
                .Replace($"""(c."Embedding"::vector({BenchData.Dimensions}) <=> $1) + 0""",
                    $"""(c."Embedding"::halfvec({BenchData.Dimensions}) <=> $1::halfvec({BenchData.Dimensions})) + 0""", StringComparison.Ordinal);
            if (!halfSql.Contains("halfvec", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The exact statement's distance expression changed; the candidate was not built.");
            }

            async Task<List<Guid>> HalfAsync(float[] query)
            {
                await using var command = db.Api.CreateCommand(halfSql);
                command.Parameters.Add(new NpgsqlParameter { Value = new Vector(query) });
                command.Parameters.Add(new NpgsqlParameter { Value = new[] { db.Collection } });
                command.Parameters.Add(new NpgsqlParameter { Value = new[] { "public" } });
                var ids = new List<Guid>();
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    ids.Add(reader.GetGuid(0));
                }

                return ids;
            }

            async Task<List<Guid>> IndexAsync(float[] query, VectorSearch search) =>
                [.. (await db.Index.VectorAsync(scope, query, null, 100, search, CancellationToken.None)).Select(c => c.ChunkId)];

            var exact = new List<double>();
            var half = new List<double>();
            var hnsw = new List<double>();
            double recall10 = 0, recall100 = 0, halfRecall10 = 0;
            for (var round = 0; round < 3; round++)
            {
                foreach (var query in queries)
                {
                    var t = Stopwatch.GetTimestamp();
                    var e = await IndexAsync(query, VectorSearch.Exact);
                    var exactMs = Stopwatch.GetElapsedTime(t).TotalMilliseconds;
                    t = Stopwatch.GetTimestamp();
                    var h = await HalfAsync(query);
                    var halfMs = Stopwatch.GetElapsedTime(t).TotalMilliseconds;
                    t = Stopwatch.GetTimestamp();
                    var a = await IndexAsync(query, VectorSearch.Approximate);
                    var hnswMs = Stopwatch.GetElapsedTime(t).TotalMilliseconds;
                    if (round == 0)
                    {
                        continue; // warm: caches and auto-prepare
                    }

                    exact.Add(exactMs);
                    half.Add(halfMs);
                    hnsw.Add(hnswMs);
                    if (round == 1)
                    {
                        recall10 += e.Take(10).Intersect(a).Count() / 10.0;
                        recall100 += e.Intersect(a).Count() / (double)e.Count;
                        halfRecall10 += e.Take(10).Intersect(h.Take(10)).Count() / 10.0;
                    }
                }
            }

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"| {chunks} | {Median(exact):F1} | {Median(half):F1} | {Median(hnsw):F1} | {recall10 / queries.Length:F3} | {recall100 / queries.Length:F3} | {halfRecall10 / queries.Length:F3} |"));
            if (chunks == 10_000)
            {
                await using var explain = db.Api.CreateCommand("EXPLAIN (ANALYZE, BUFFERS) " + RetrievalStatements.VectorExact(db.Profile, BenchData.Dimensions, 100, FilterFragments.None));
                explain.Parameters.Add(new NpgsqlParameter { Value = new Vector(queries[0]) });
                explain.Parameters.Add(new NpgsqlParameter { Value = new[] { db.Collection } });
                explain.Parameters.Add(new NpgsqlParameter { Value = new[] { "public" } });
                Console.WriteLine();
                Console.WriteLine("```");
                await using var reader = await explain.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    Console.WriteLine(reader.GetString(0));
                }

                Console.WriteLine("```");
            }

            await db.Api.DisposeAsync();
            await db.Database.DisposeAsync();
        }
    }

    // ---------------------------------------------------------------- 5: hydration window

    private static async Task HydrateAsync()
    {
        Console.WriteLine("## 5 Hydration (H+D) of 10 hits in 10 documents; 10,000 chunks of ≈ 230 words in documents of 40 chunks, sections of 20 chunks");
        var db = await SeedAsync(10_000, 40, 230, 20);
        var scope = new SearchScope(db.Profile, BenchData.Dimensions, [new CollectionInScope(db.Collection, RetrievalSettings.Default)], ["public"],
            RetrievalPlatformSettings.Default);
        var random = new Random(99);
        // One hit per document, as a diversified result has them; and the clustered case: the cap's 3 chunks per document.
        foreach (var (label, perDocument) in new[] { ("10 hits in 10 documents", 1), ("10 hits in 4 documents", 3) })
        {
            var hits = new List<Candidate>();
            for (var topic = 0; hits.Count < 10; topic++)
            {
                var found = await db.Index.VectorAsync(scope, Near(db.Centroids[topic], random, 0.15f), null, 100, VectorSearch.Exact, CancellationToken.None);
                foreach (var group in found.GroupBy(c => c.DocumentId).Take(perDocument == 1 ? 10 : 4))
                {
                    hits.AddRange(group.Take(perDocument).Where(_ => hits.Count < 10));
                }
            }

            Console.WriteLine();
            Console.WriteLine($"### {label}");
            Console.WriteLine("| radius | median ms | rows read | text read KB | sections | text kept in sections KB | kept share |");
            Console.WriteLine("|---:|---:|---:|---:|---:|---:|---:|");
            foreach (var radius in new[] { 0, 1, 2, 4, 8 })
            {
                var request = new HydrationRequest([.. hits.Select(h => h.ChunkId)], [.. hits.Select(h => h.DocumentId).Distinct()], ["public"], [db.Collection], db.Sources,
                    NeighbourRadius: radius);
                var times = new List<double>();
                Hydration hydration = null!;
                for (var i = 0; i < 45; i++)
                {
                    var t = Stopwatch.GetTimestamp();
                    hydration = await db.Hydrator.HydrateAsync(request, CancellationToken.None);
                    times.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
                }

                var read = hydration.Chunks.Sum(c => (long)c.Text.Length);
                var assembly = radius == 0
                    ? SectionAssembly.Empty
                    : SectionAssembler.Assemble([.. hits.Select(h => new SectionHit(h.ChunkId, h.DocumentId, RetrievalSettings.Default.ExpandMaxTokens))], hydration.Chunks, radius,
                        RetrievalPlatformSettings.Default.ExpandTotalMaxTokens);
                var kept = assembly.Sections.Sum(s => (long)s.Text.Length);
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"| {radius} | {Median(times.Skip(5)):F2} | {hydration.Chunks.Count} | {read / 1024.0:F0} | {assembly.Sections.Count} | {kept / 1024.0:F0} | {(read == 0 ? 0 : (double)kept / read):P0} |"));
            }
        }

        await db.Api.DisposeAsync();
        await db.Database.DisposeAsync();
    }

    // ---------------------------------------------------------------- 3: job claim

    private static async Task JobsAsync()
    {
        Console.WriteLine("## 3 Job claim (PostgresJobQueue as the dispatcher uses it: ReadyDepartmentsAsync, then DequeueAsync), class fetch, 3 departments");
        Console.WriteLine("| queued jobs | ReadyDepartments ms | Dequeue ms | claim, current (probe + dequeue) ms | claim, candidate (dequeue on a cached probe) ms | probe share |");
        Console.WriteLine("|---:|---:|---:|---:|---:|---:|");
        var database = new RagDatabase();
        await database.InitializeAsync();
        await using var worker = new NpgsqlDataSourceBuilder(database.Connection(RagDatabase.Worker)).Build();
        var queue = new PostgresJobQueue(new JobDatabase(worker), new JobWakeUps(TimeProvider.System));
        Guid[] departments = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        var owner = Guid.NewGuid();
        var queued = 0;
        foreach (var target in new[] { 1_000, 10_000, 50_000, 100_000 })
        {
            while (queued < target)
            {
                var batch = Math.Min(5_000, target - queued);
                await queue.EnqueueAsync([.. Enumerable.Range(0, batch).Select(i => new JobRequest(JobClasses.Fetch, departments[(queued + i) % 3], "public",
                    $$"""{"url":"https://www.umea.se/sida-{{queued + i}}"}""") { Priority = (short)((queued + i) % 2) })], null, CancellationToken.None);
                queued += batch;
            }

            await using (var superuser = new NpgsqlConnection(database.Connection(null)))
            {
                await superuser.OpenAsync();
                await using var analyze = new NpgsqlCommand("""VACUUM ANALYZE content."Jobs" """, superuser);
                await analyze.ExecuteNonQueryAsync();
            }

            var probe = new List<double>();
            var dequeue = new List<double>();
            for (var i = 0; i < 60; i++)
            {
                var t = Stopwatch.GetTimestamp();
                var ready = await queue.ReadyDepartmentsAsync(JobClasses.Fetch, CancellationToken.None);
                probe.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
                t = Stopwatch.GetTimestamp();
                var job = await queue.DequeueAsync(new DequeueRequest(JobClasses.Fetch, ready.Departments[i % ready.Departments.Count].DepartmentId, owner, TimeSpan.FromMinutes(5)),
                    CancellationToken.None);
                dequeue.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
                await queue.CompleteAsync(job!.Id, owner, null, null, CancellationToken.None);
                queued--;
            }

            var (p, d) = (Median(probe.Skip(10)), Median(dequeue.Skip(10)));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"| {target} | {p:F2} | {d:F2} | {p + d:F2} | {d:F2} | {p / (p + d):P0} |"));
        }

        await database.DisposeAsync();
    }

    // ---------------------------------------------------------------- 4b: the vectors' way into Postgres

    /// <summary>1,000 vectors into a table through unnest, as the chunk insert and the embedding cache do: text[] cast to vector, against a binary vector[].</summary>
    private static async Task VectorInsertAsync()
    {
        Console.WriteLine("## 4b Insert of 1,000 vector(1024) through unnest (format + round trip), worker role");
        var database = new RagDatabase();
        await database.InitializeAsync();
        var builder = new NpgsqlDataSourceBuilder(database.Connection(null));
        builder.UseVector();
        await using var data = builder.Build();
        await using var connection = await data.OpenConnectionAsync();
        await using (var create = new NpgsqlCommand("CREATE TEMP TABLE v (id int, e vector(1024))", connection))
        {
            await create.ExecuteNonQueryAsync();
        }

        var random = new Random(5);
        var vectors = Enumerable.Range(0, 1000).Select(_ => BenchData.UnitVector(random)).ToArray();
        var ids = Enumerable.Range(0, 1000).ToArray();
        Console.WriteLine("| variant | median ms | of which formatting in .NET ms |");
        Console.WriteLine("|---|---:|---:|");
        foreach (var name in new[] { "current: text[] of '[…]' cast to vector", "candidate: binary vector[] (Pgvector)" })
        {
            var total = new List<double>();
            var format = new List<double>();
            for (var run = 0; run < 25; run++)
            {
                await using (var truncate = new NpgsqlCommand("TRUNCATE v", connection))
                {
                    await truncate.ExecuteNonQueryAsync();
                }

                var t = Stopwatch.GetTimestamp();
                NpgsqlCommand command;
                if (name.StartsWith("current", StringComparison.Ordinal))
                {
                    var texts = vectors.Select(FormatCurrent).ToArray();
                    format.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
                    command = new NpgsqlCommand("INSERT INTO v SELECT u.id, u.e::vector FROM unnest($1::int[], $2::text[]) AS u(id, e)", connection)
                    {
                        Parameters = { new() { Value = ids }, new() { Value = texts } },
                    };
                }
                else
                {
                    var typed = vectors.Select(v => new Vector(v)).ToArray();
                    format.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
                    command = new NpgsqlCommand("INSERT INTO v SELECT u.id, u.e FROM unnest($1::int[], $2::vector[]) AS u(id, e)", connection)
                    {
                        Parameters = { new() { Value = ids }, new() { Value = typed } },
                    };
                }

                await using (command)
                {
                    await command.ExecuteNonQueryAsync();
                }

                total.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
            }

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"| {name} | {Median(total.Skip(5)):F1} | {Median(format.Skip(5)):F1} |"));
        }

        await database.DisposeAsync();
    }

    // ---------------------------------------------------------------- 7: config snapshot reload

    /// <summary>
    /// to-verify #164 (data-model §5, §14): rag-api's whole-snapshot reload (<see cref="PolicySnapshotStore.LoadAsync"/>, as
    /// <c>ume_rag_api</c>) with N published assistant versions whose instructions are 20 KB each (three collection links, two
    /// audiences per assistant). 50 reloads after 5 warm-ups; allocated bytes per reload over all threads, and the instruction
    /// text one snapshot keeps alive. The design moves to reloading by outbox EntityId when a reload takes more than 50 ms.
    /// </summary>
    private static async Task SnapshotAsync()
    {
        Console.WriteLine("## 7 Config snapshot reload (PolicySnapshotStore.LoadAsync as ume_rag_api), 20 KB instructions per published version");
        Console.WriteLine("| published assistants | instructions MB in the database | reload p50 ms | p95 ms | max ms | allocated MB per reload | instructions held by the snapshot MB (UTF-16) |");
        Console.WriteLine("|---:|---:|---:|---:|---:|---:|---:|");
        var database = new RagDatabase();
        await database.InitializeAsync();
        await using var api = NpgsqlDataSource.Create(database.Connection(RagDatabase.Api));
        var store = new PolicySnapshotStore(api,
            new ConfigVersionState(Microsoft.Extensions.Options.Options.Create(new ConfigOptions()), TimeProvider.System,
                new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { EnvironmentName = "Development" },
                Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PolicySnapshotStore>.Instance);
        // About 20 KB of Swedish instructions (UTF-8, å/ä/ö included), different per assistant so no string is shared.
        const string Sentence = "Du är kommunens assistent för bygglov och svarar på svenska med hänvisning till källorna. ";
        var seeded = 0;
        foreach (var target in new[] { 1, 300, 1_000 })
        {
            await SeedAssistantsAsync(database, seeded, target - seeded, Sentence);
            seeded = target;
            for (var i = 0; i < 5; i++)
            {
                await store.LoadAsync(i, CancellationToken.None);
            }

            var times = new List<double>();
            var allocated = new List<double>();
            for (var i = 0; i < 50; i++)
            {
                var before = GC.GetTotalAllocatedBytes(precise: true);
                var t = Stopwatch.GetTimestamp();
                var snapshot = await store.LoadAsync(i, CancellationToken.None);
                times.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
                allocated.Add(GC.GetTotalAllocatedBytes(precise: true) - before);
                if (snapshot.Assistants.Count != target || snapshot.Assistants.Values.Any(a => a.Published is null))
                {
                    throw new InvalidOperationException($"The snapshot holds {snapshot.Assistants.Count} assistants, not {target} published ones.");
                }
            }

            // What a snapshot keeps alive is mostly its instructions: counted exactly (UTF-16) rather than by heap deltas,
            // which the server GC's many heaps make unreliable here.
            var instructions = (await store.LoadAsync(0, CancellationToken.None)).Assistants.Values.Sum(a => (long)a.Published!.SystemPrompt.Length * sizeof(char));
            long databaseBytes;
            await using (var admin = new NpgsqlConnection(database.Connection(RagDatabase.Admin)))
            {
                await admin.OpenAsync();
                await using var size = new NpgsqlCommand("""SELECT coalesce(sum(octet_length("SystemPrompt")), 0)::bigint FROM config."AssistantVersions" """, admin);
                databaseBytes = (long)(await size.ExecuteScalarAsync())!;
            }

            var sorted = times.Order().ToArray();
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"| {target} | {databaseBytes / 1e6:F1} | {Pct(sorted, 0.5):F1} | {Pct(sorted, 0.95):F1} | {sorted[^1]:F1} | {Median(allocated) / 1e6:F1} | {instructions / 1e6:F1} |"));
        }

        await database.DisposeAsync();
    }

    /// <summary><paramref name="count"/> more assistants, each with one published version, as the admin's publish leaves them.</summary>
    private static async Task SeedAssistantsAsync(RagDatabase database, int offset, int count, string sentence)
    {
        await using var db = TestPostgres.Context(database.Connection(RagDatabase.Admin));
        var now = DateTimeOffset.UtcNow;
        var department = Guid.NewGuid();
        Guid[] collections = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        var assistants = Enumerable.Range(offset, count).Select(n => new Assistant
        {
            Slug = $"assistent-{n}", Name = $"Assistent {n}", DepartmentId = department, Classification = Classification.Internal, UpdatedAt = now,
            Description = "Svarar om bygglov.", ContactName = "Bygglov", ContactEmail = "bygglov@umea.se",
        }).ToList();
        db.Assistants.AddRange(assistants);
        await db.SaveChangesAsync();
        var versions = assistants.Select((a, n) =>
        {
            var prompt = new StringBuilder("Instruktion ").Append((offset + n).ToString(CultureInfo.InvariantCulture)).Append(". ");
            while (Encoding.UTF8.GetByteCount(prompt.ToString()) + Encoding.UTF8.GetByteCount(sentence) <= 20 * 1024)
            {
                prompt.Append(sentence);
            }

            return new AssistantVersion
            {
                AssistantId = a.Id, Number = 1, GatewayModel = "ume/chat-eu", SystemPrompt = prompt.ToString(), NotFound = """{"sv":"Inget svar i källorna."}""",
                CollectionLinks = [.. collections.Select(CollectionLink.Whole)], CreatedAt = now, UpdatedAt = now,
            };
        }).ToList();
        db.AssistantVersions.AddRange(versions);
        await db.SaveChangesAsync();
        for (var i = 0; i < assistants.Count; i++)
        {
            versions[i].State = AssistantVersionStates.Published;
            versions[i].PublishedAt = now;
            versions[i].PublishedBy = "measure";
            assistants[i].PublishedVersionId = versions[i].Id;
            db.AssistantAudiences.AddRange(
                new AssistantAudience { AssistantId = assistants[i].Id, SubjectId = Guid.NewGuid() },
                new AssistantAudience { AssistantId = assistants[i].Id, SubjectId = Guid.NewGuid() });
        }

        await db.SaveChangesAsync();
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? 0 : sorted[sorted.Length / 2];
    }

    private static double Pct(double[] sorted, double p) => sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * p))];
}
