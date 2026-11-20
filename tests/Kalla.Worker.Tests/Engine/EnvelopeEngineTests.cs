using System.Text;
using Kalla.Domain;
using Kalla.Domain.Entities;
using Kalla.Worker.Tests.Processing;

namespace Kalla.Worker.Tests.Engine;

/// <summary>
/// Blob envelope encryption in the engine (data-model §7 "Blobs", §9): an <c>internal</c> URL-list source stores its raw
/// blob, parse output and stage outputs as <c>URB2</c> with no plaintext on disk and still goes live; a <c>public</c>
/// source's blobs stay plaintext.
/// </summary>
[Collection(RagDatabaseGroup4.Name)]
public sealed class EnvelopeEngineTests(RagDatabaseFixture database, FakeGatewayFactory gateway) : IClassFixture<FakeGatewayFactory>, IAsyncLifetime
{
    private readonly DirectoryInfo _blobs = Directory.CreateTempSubdirectory("kalla-envelope-engine-");

    public async ValueTask InitializeAsync() => await database.ResetAsync();

    public ValueTask DisposeAsync()
    {
        _blobs.Delete(recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task An_internal_source_stores_URB2_blobs_without_plaintext_and_goes_live()
    {
        var marker = $"kuvertmarkör{Guid.NewGuid():N}";
        await using var site = await EngineKit.SiteAsync((name, _) => Task.FromResult(EngineKit.Html(EngineKit.Page($"{marker} {name}"))));
        var seed = await EngineKit.SeedUrlListAsync(database, [site.Url("/p/a")], Classification.Internal);
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs);
        try
        {
            var run = await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, seed));

            run.ShouldBe(new RunCounters(RunStates.Succeeded, 1, 0, 0, 0, 0, 0));
            (await EngineKit.LiveChunksAsync(database, seed)).ShouldBeGreaterThan(0);
            (await LiveTextAsync(seed)).ShouldContain(marker, Case.Sensitive, "the stages read the plaintext back through the envelope");
            var stored = Blobs("raw/internal", "parse/internal", "stages/internal");
            stored.ShouldContain(f => f.Key.StartsWith("raw/internal/", StringComparison.Ordinal));
            stored.ShouldContain(f => f.Key.StartsWith("parse/internal/", StringComparison.Ordinal));
            stored.ShouldContain(f => f.Key.StartsWith("stages/internal/", StringComparison.Ordinal));
            foreach (var (key, bytes) in stored)
            {
                Encoding.ASCII.GetString(bytes, 0, 4).ShouldBe("URB2", key);
                Contains(bytes, marker).ShouldBeFalse($"{key} holds the page's text in plaintext");
            }

            Blobs("raw/public", "parse/public", "stages/public").ShouldBeEmpty();
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task A_public_sources_blobs_stay_plaintext()
    {
        var marker = $"öppenmarkör{Guid.NewGuid():N}";
        await using var site = await EngineKit.SiteAsync((name, _) => Task.FromResult(EngineKit.Html(EngineKit.Page($"{marker} {name}"))));
        var seed = await EngineKit.SeedUrlListAsync(database, [site.Url("/p/a")], Classification.Public);
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs);
        try
        {
            var run = await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, seed));

            run.ShouldBe(new RunCounters(RunStates.Succeeded, 1, 0, 0, 0, 0, 0));
            (await LiveTextAsync(seed)).ShouldContain(marker, Case.Sensitive);
            var stored = Blobs("raw/public", "parse/public", "stages/public");
            stored.ShouldNotBeEmpty();
            stored.ShouldAllBe(f => !(f.Bytes.Length >= 4 && f.Bytes[0] == 'U' && f.Bytes[1] == 'R' && f.Bytes[2] == 'B' && f.Bytes[3] == '2'));
            var raw = stored.Where(f => f.Key.StartsWith("raw/public/", StringComparison.Ordinal) && !f.Key.EndsWith(".gz", StringComparison.Ordinal)).ToList();
            raw.ShouldNotBeEmpty();
            raw.ShouldAllBe(f => Contains(f.Bytes, marker), "the raw page is stored as fetched");
            Blobs("raw/internal", "parse/internal", "stages/internal").ShouldBeEmpty();
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    /// <summary>The stored files under the given blob prefixes, read from disk as they are.</summary>
    private List<(string Key, byte[] Bytes)> Blobs(params string[] prefixes) =>
    [
        .. prefixes.Select(p => new DirectoryInfo(Path.Combine(_blobs.FullName, p))).Where(d => d.Exists)
            .SelectMany(d => d.EnumerateFiles("*", SearchOption.AllDirectories))
            .Select(f => (Path.GetRelativePath(_blobs.FullName, f.FullName).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllBytes(f.FullName)))
            .OrderBy(f => f.Item1, StringComparer.Ordinal),
    ];

    private static bool Contains(byte[] bytes, string text) => bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(text)) >= 0;

    private Task<string> LiveTextAsync(SourceSeed seed) =>
        Sql.ScalarAsync<string>(database.Connection(null), """
            SELECT coalesce(string_agg(c."Text", E'\n' ORDER BY c."Ordinal"), '') FROM content."Chunks" c
            JOIN content."Documents" d ON d."LiveChunkSetId" = c."ChunkSetId" WHERE d."SourceId" = $1
            """, seed.SourceId);
}
